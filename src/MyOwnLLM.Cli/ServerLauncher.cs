using System.Diagnostics;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>
/// Makes sure llama-server and the gateway are running before Jazari opens: starts only what's missing,
/// in the background, and waits until both answer.
/// </summary>
public sealed class ServerLauncher(
    IProcessRunner runner, IGatewayClient gateway, Func<CancellationToken, Task<bool>> llamaReady, Ui ui, string logDir,
    Func<TimeSpan, CancellationToken, Task> delay, int maxWaits = 180)
{
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    public static string DefaultLogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "logs");

    public async Task<ConnectionState> EnsureRunningAsync(JazariConfig? config, CancellationToken ct)
    {
        var (gatewayState, llamaUp) = (await GatewayStateAsync(ct), await llamaReady(ct));
        if (gatewayState is not ConnectionState.GatewayNotRunning && llamaUp) return gatewayState;
        if (config is null) return gatewayState;

        ui.Label("starting");
        if (!llamaUp)
            runner.StartDetached(config.LlamaServer,
            [
                "-m", config.Model, "-dev", "none", "-t", "6", "--poll", "0", "-c", "16384", "--cache-ram", "2048",
                "-np", "1", "--cache-reuse", "256", "--host", "127.0.0.1", "--port", "8080",
            ], Path.Combine(logDir, "llama-server.log"));
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
