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
    Func<JazariConfig, CancellationToken, Task<string[]>>? llamaArgs = null)
{
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    public static string DefaultLogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "logs");

    public async Task<ConnectionState> EnsureRunningAsync(JazariConfig? config, CancellationToken ct)
    {
        var (gatewayState, llamaUp) = (await GatewayStateAsync(ct), await llamaReady(ct));
        if (gatewayState is not ConnectionState.GatewayNotRunning && llamaUp) return gatewayState;
        if (config is null) return gatewayState;

        // Say what's missing straight away instead of waiting for servers that can never start.
        var missing = new[] { config.LlamaServer, config.Model, config.GatewayExe }.Where(f => !(fileExists ?? File.Exists)(f)).ToList();
        if (missing.Count > 0)
        {
            ui.Error("Some of Jazari's files are missing.", string.Join(", ", missing), "Run: jazari update");
            return ConnectionState.GatewayNotRunning;
        }

        ui.Label("starting");
        if (!llamaUp)
            // The settings measured best on this PC (see DeviceTuner), or the T480's measured CPU setup.
            runner.StartDetached(config.LlamaServer, llamaArgs is null
                ?
                [
                    "-m", config.Model, "-dev", "none", "-t", "6", "--poll", "0", "-c", "16384", "--cache-ram", "2048",
                    "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", "8080",
                ]
                : await llamaArgs(config, ct), Path.Combine(logDir, "llama-server.log"));
        if (gatewayState is ConnectionState.GatewayNotRunning)
            runner.StartDetached(config.GatewayExe, [], Path.Combine(logDir, "gateway.log"));

        var waited = Stopwatch.StartNew(); // checks themselves can take ~2 s while a port refuses, so cap the total wait too
        for (var i = 0; i < maxWaits && waited.Elapsed < TimeSpan.FromMinutes(2); i++)
        {
            await delay(PollEvery, ct);
            gatewayState = await GatewayStateAsync(ct);
            if (gatewayState is not ConnectionState.GatewayNotRunning && await llamaReady(ct)) return gatewayState;
        }

        ui.Error("Jazari's servers didn't start in time.", $"see the logs in {logDir}", "Run jazari again once the model has loaded, or check the logs.");
        return ConnectionState.GatewayNotRunning;
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
    public static int StopServers()
    {
        var stopped = 0;
        foreach (var p in Process.GetProcessesByName("llama-server").Concat(Process.GetProcessesByName("MyOwnLLM.Gateway")))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); stopped++; }
                catch (InvalidOperationException) { } // already gone
            }
        }
        return stopped;
    }
}
