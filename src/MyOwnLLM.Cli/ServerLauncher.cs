using System.Diagnostics;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>
/// Makes sure llama-server and the gateway are running before Jazari opens: starts only what's missing,
/// in the background, and waits until both answer.
/// </summary>
public sealed class ServerLauncher(
    IProcessRunner runner, IGatewayClient gateway, Func<CancellationToken, Task<bool>> llamaReady, Ui ui, string logDir,
    Func<TimeSpan, CancellationToken, Task> delay, int maxWaits = 180, Func<string, bool>? fileExists = null,
    Func<JazariConfig, CancellationToken, Task<string[]>>? llamaArgs = null,
    Func<JazariConfig, CancellationToken, Task<string[]>>? fallbackArgs = null, Func<bool>? llamaAlive = null, bool quiet = false, Func<CancellationToken, Task<bool>>? llamaListening = null)
{
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    public static string DefaultLogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "logs");

    /// <summary>llama-server's key, in the gateway's data folder, where the gateway reads it (GatewayOptions.LlamaKeyFile).</summary>
    /// <summary>The models llama-server's router runs, written at each start from this PC's tuning.</summary>
    public static string LanesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "llama-lanes.ini");

    public static string LlamaKeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "llama-server.key");

    /// <summary>Makes the key once, at random; later starts reuse it, so a running gateway and a restarted server still agree.</summary>
    public static string EnsureLlamaKey(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }
        return path;
    }

    public async Task<ConnectionState> EnsureRunningAsync(JazariConfig? config, CancellationToken ct)
    {
        var (gatewayState, llamaUp) = (await GatewayStateAsync(ct), await llamaReady(ct));
        if (gatewayState is not ConnectionState.GatewayNotRunning && llamaUp) return gatewayState;
        // Up and listening but with no model ready (a swap, or the first load): starting another server could only fail.
        var listening = llamaUp || (llamaListening is not null && await llamaListening(ct));
        if (config is null) return gatewayState;

        // Say what's missing straight away instead of waiting for servers that can never start.
        var missing = new[] { config.LlamaServer, config.Model, config.GatewayExe }.Where(f => !(fileExists ?? File.Exists)(f)).ToList();
        if (missing.Count > 0)
        {
            ui.Error("Some of Jazari's files are missing.", string.Join(", ", missing), "Run: jazari update");
            return ConnectionState.GatewayNotRunning;
        }

        if (!quiet) ui.Label("starting"); // the interactive session says so on its panel
        if (!listening)
            // The settings measured best on this PC (see DeviceTuner), or the T480's measured CPU setup.
            runner.StartDetached(config.LlamaServer, llamaArgs is null
                ?
                [
                    "-m", config.Model, "-dev", "none", "-t", "6", "--poll", "0", "-c", "16384", "--cache-ram", "1024", "-ctxcp", "8",
                    "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", "8080",
                ]
                : await llamaArgs(config, ct), Path.Combine(logDir, "llama-server.log"));
        if (gatewayState is ConnectionState.GatewayNotRunning)
            runner.StartDetached(config.GatewayExe, [], Path.Combine(logDir, "gateway.log"));

        var waited = Stopwatch.StartNew(); // checks themselves can take ~2 s while a port refuses, so cap the total wait too
        var fellBack = false;
        for (var i = 0; i < maxWaits && waited.Elapsed < TimeSpan.FromMinutes(2); i++)
        {
            await delay(PollEvery, ct);
            gatewayState = await GatewayStateAsync(ct);
            var ready = await llamaReady(ct);
            if (gatewayState is not ConnectionState.GatewayNotRunning && ready) return gatewayState;

            // The tuned settings can stop working (a GPU driver update, say): once our llama-server has had a few seconds
            // and is gone, start it again on safe CPU settings rather than waiting for a server that crashed.
            if (!llamaUp && !ready && !fellBack && fallbackArgs is not null && i >= 6 && !(llamaAlive ?? (() => Running(config.LlamaServer) > 0))())
            {
                var log = Path.Combine(logDir, "llama-server.log");
                var why = ReadLog(log);
                // A port taken by another program isn't the settings' fault: CPU settings would fail the same way.
                if (why.Contains("couldn't bind", StringComparison.OrdinalIgnoreCase) || why.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                {
                    ui.Error("The model server couldn't start: port 8080 is used by another program.", $"see {log}", "Close that program, then run jazari again.");
                    return ConnectionState.GatewayNotRunning;
                }
                fellBack = true;
                try { if (why.Length > 0) File.Copy(log, Path.Combine(logDir, "llama-server.failed.log"), overwrite: true); } // the restart overwrites it
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                ui.Metadata("The model server stopped with this PC's tuned settings, so it's starting on safe CPU settings for now. Run jazari tune to measure again.");
                StopOurs(config.LlamaServer); // a router outlives a model that fails to load, and would hold the port
                runner.StartDetached(config.LlamaServer, await fallbackArgs(config, ct), log);
            }
        }

        ui.Error("Jazari's servers didn't start in time.", $"see the logs in {logDir}", "Run jazari again once the model has loaded, or check the logs.");
        return ConnectionState.GatewayNotRunning;
    }

    internal static string ReadLog(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>
    /// The running processes started from exactly this file. Another tool's llama-server has the same name; a process
    /// whose path can't be read (elevated, already exiting) isn't ours to touch either.
    /// </summary>
    static List<Process> Ours(string exe)
    {
        var mine = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) mine.Add(p);
            else p.Dispose();
        }
        return mine;
    }

    static void StopOurs(string exe)
    {
        foreach (var p in Ours(exe))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            }
        }
    }

    public static bool IsRunning(string exe) => Running(exe) > 0;

    static int Running(string exe)
    {
        var running = Ours(exe);
        foreach (var p in running) p.Dispose();
        return running.Count;
    }

    async Task<ConnectionState> GatewayStateAsync(CancellationToken ct)
    {
        try
        {
            await gateway.InfoAsync(ct);
            return ConnectionState.Available;
        }
        catch (GatewayException e) when (e.Status == 401) { return ConnectionState.KeyRejected; }
        catch (GatewayException) { return ConnectionState.GatewayNotRunning; }
    }

    /// <summary><c>jazari stop</c>: ends the background servers to free their RAM. Returns how many were stopped.</summary>
    public static int StopServers(JazariConfig config)
    {
        var stopped = 0;
        foreach (var p in Ours(config.LlamaServer).Concat(Ours(config.GatewayExe)))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); stopped++; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { } // gone, or not ours to stop
            }
        }
        return stopped;
    }
}

/// <summary>
/// What llama-server's router says about the fast model. /health only says the router is up, so a model that failed to load
/// would look fine; and asking for a load here means a router started by any window ends up with a model.
/// </summary>
public sealed class RouterHealth(HttpClient http, Func<string?> key)
{
    /// <summary>The router gave up on the fast model: its settings don't work on this PC.</summary>
    public bool FastFailed { get; private set; }
    /// <summary>Off for jazari doctor, which only looks.</summary>
    public bool AsksForLoad { get; init; } = true;
    bool _asked;
    int _unloadedAfterAsking;
    const int UnloadedChecksBeforeFailing = 10; // the state can take a moment to change after asking

    public async Task<bool> ListeningAsync(CancellationToken ct)
    {
        try { using var response = await http.GetAsync("/health", ct); return true; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            using var models = await http.SendAsync(WithKey(new(HttpMethod.Get, "/models")), ct);
            if (!models.IsSuccessStatusCode) return await PlainReadyAsync(ct);
            var read = Read(await models.Content.ReadAsStringAsync(ct));
            if (read is null) return await PlainReadyAsync(ct);
            if (read.AnyLoaded) return true;
            if (read.AnyLoading) return false;
            if (read.FastFailed || (_asked && ++_unloadedAfterAsking > UnloadedChecksBeforeFailing)) { FastFailed = true; return false; }
            if (!_asked && AsksForLoad)
            {
                _asked = true;
                using var load = await http.SendAsync(WithKey(new(HttpMethod.Post, "/models/load") { Content = System.Net.Http.Json.JsonContent.Create(new { model = "fast" }) }), ct);
            }
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    async Task<bool> PlainReadyAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/health", ct);
        return response.IsSuccessStatusCode;
    }

    HttpRequestMessage WithKey(HttpRequestMessage request)
    {
        if (key() is { Length: > 0 } value) request.Headers.Authorization = new("Bearer", value);
        return request;
    }

    public sealed record RouterState(bool AnyLoaded, bool AnyLoading, bool FastFailed);

    /// <summary>Null from a plain llama-server, whose models have no status.</summary>
    public static RouterState? Read(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var (loaded, loading, failed) = (false, false, false);
        foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("status", out var status) || status.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var value = status.GetProperty("value").GetString();
            loaded |= value is "loaded" or "sleeping";
            loading |= value == "loading";
            failed |= model.GetProperty("id").GetString() == "fast" && status.TryGetProperty("failed", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        return new(loaded, loading, failed);
    }
}
