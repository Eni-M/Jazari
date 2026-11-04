namespace MyOwnLLM.Cli;

/// <param name="Fix">What to do about it; null when there's nothing to do.</param>
public sealed record Check(string Name, bool Ok, string Detail, string? Fix = null);

/// <summary>
/// <c>jazari doctor</c>: looks at every piece Jazari needs and says what to do about what's wrong. Only looks: it starts
/// no server, makes no key and changes no file, so it's safe to run whatever state the install is in. <c>jazari update</c>
/// is the one that repairs.
/// </summary>
public sealed class Doctor(
    JazariConfig? config, string? key, IGatewayClient gateway, Func<CancellationToken, Task<bool>> llamaReady, Func<string, bool> fileExists,
    string logDir, string lawBookFile, bool measured, Func<CancellationToken, Task<bool>> updateAvailable)
{
    public async Task<List<Check>> RunAsync(CancellationToken ct)
    {
        if (config is null) return [new("Install", false, "Jazari isn't installed on this PC.", @"Run scripts\install-jazari.ps1 from your MyOwnLLM clone.")];

        List<Check> checks = [];
        var missing = new[] { config.GatewayExe, config.LlamaServer, config.Model }.Where(f => !fileExists(f)).ToList();
        checks.Add(missing.Count == 0
            ? new("Install", true, "the gateway, llama-server and the model are in place")
            : new("Install", false, $"missing: {string.Join(", ", missing)}", "Run jazari update."));

        checks.Add(key is null
            ? new("Key", false, "no API key is set", "Run jazari: it makes one.")
            : new("Key", true, "set"));

        checks.Add(await llamaReady(ct)
            ? new("Model server", true, "answering on 127.0.0.1:8080")
            : new("Model server", false, "not running" + LastError("llama-server.log"), "Run jazari: it starts the servers."));

        try
        {
            var info = await gateway.InfoAsync(ct);
            checks.Add(new("Gateway", true, $"{info.Name} {info.Version}, model {info.Model}"));
        }
        catch (GatewayException e) when (e.Status == 401)
        {
            checks.Add(new("Gateway", false, "running, but it doesn't know this key", "Run jazari: it replaces a rejected key."));
        }
        catch (GatewayException)
        {
            checks.Add(new("Gateway", false, "not running" + LastError("gateway.log"), "Run jazari: it starts the servers."));
        }

        checks.Add(measured
            ? new("Tuning", true, "measured on this PC")
            : new("Tuning", true, "safe CPU settings", "jazari tune measures this PC for faster ones (a few minutes)."));
        checks.Add(fileExists(lawBookFile)
            ? new("Law book", true, "installed")
            : new("Law book", true, "not installed; answers work without it"));
        checks.Add(await updateAvailable(ct)
            ? new("Version", true, "an update is available", "Run jazari update.")
            : new("Version", true, "up to date, or GitHub couldn't be reached"));
        return checks;
    }

    /// <summary>": " and the log's last line that mentions an error, so the cause shows without opening the file.</summary>
    string LastError(string log)
    {
        var path = Path.Combine(logDir, log);
        var line = ServerLauncher.ReadLog(path).Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase));
        return line is null ? "" : $"; {path} says: {(line.Length > 160 ? line[..160] + "..." : line)}";
    }
}
