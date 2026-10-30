using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class ServerLauncherTests
{
    static readonly JazariConfig Config = new(@"C:\Repo", @"C:\gw\MyOwnLLM.Gateway.exe", @"C:\llama\llama-server.exe", @"C:\m\granite.gguf");
    readonly FakeRunner _runner = new();
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();
    bool _gatewayUp, _llamaUp;
    Func<string, bool> _fileExists = _ => true;

    ServerLauncher NewLauncher(int startsAfterChecks = 0)
    {
        var checks = 0;
        _gateway.Info = _ => _gatewayUp || (_runner.Started.Count > 0 && ++checks > startsAfterChecks && (_gatewayUp = true))
            ? Task.FromResult(new GatewayInfo("Jazari", "Granite 4.1 3B", "0.2.0"))
            : throw new GatewayException("gateway not reachable", null, null);
        return new ServerLauncher(_runner, _gateway, _ => Task.FromResult(_llamaUp || (_runner.Started.Count > 0 && (_llamaUp = true))),
            new Ui(_output, new Theme(ColorMode.None, true), 80), @"C:\logs", (_, _) => Task.CompletedTask, maxWaits: 5, fileExists: _fileExists);
    }

    [Fact]
    public async Task Everything_running_starts_nothing()
    {
        _gatewayUp = _llamaUp = true;

        Assert.Equal(ConnectionState.Available, await NewLauncher().EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public async Task Nothing_running_starts_both_and_waits()
    {
        var state = await NewLauncher(startsAfterChecks: 2).EnsureRunningAsync(Config, CancellationToken.None);

        Assert.Equal(ConnectionState.Available, state);
        Assert.Equal([Config.LlamaServer, Config.GatewayExe], _runner.Started.Select(s => s.Exe));
        var llamaArgs = _runner.Started[0].Args;
        Assert.Equal(Config.Model, llamaArgs[Array.IndexOf(llamaArgs, "-m") + 1]);
        Assert.Contains("127.0.0.1", llamaArgs);
        Assert.Equal(@"C:\logs\llama-server.log", _runner.Started[0].LogFile);
        Assert.Contains("STARTING", _output.ToString());
    }

    [Fact]
    public async Task A_server_that_dies_on_tuned_settings_is_restarted_on_safe_ones()
    {
        _gatewayUp = true;
        var started = 0;
        var launcher = new ServerLauncher(_runner, _gateway, _ => Task.FromResult(_runner.Started.Count == 2 && ++started > 1),
            new Ui(_output, new Theme(ColorMode.None, true), 80), @"C:\logs", (_, _) => Task.CompletedTask, maxWaits: 20, fileExists: _ => true,
            llamaArgs: (_, _) => Task.FromResult(new[] { "-dev", "Vulkan0" }),
            fallbackArgs: (_, _) => Task.FromResult(new[] { "-dev", "none" }),
            llamaAlive: () => _runner.Started.Count == 2); // the GPU start crashed; the safe one runs

        Assert.Equal(ConnectionState.Available, await launcher.EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Equal([["-dev", "Vulkan0"], ["-dev", "none"]], _runner.Started.Select(s => s.Args));
        Assert.Contains("safe CPU settings", _output.ToString());
    }

    [Fact]
    public async Task Only_the_missing_server_is_started()
    {
        _gatewayUp = true;

        await NewLauncher().EnsureRunningAsync(Config, CancellationToken.None);

        Assert.Equal([Config.LlamaServer], _runner.Started.Select(s => s.Exe));
    }

    [Fact]
    public async Task Key_rejected_counts_as_running()
    {
        _llamaUp = true;
        var launcher = NewLauncher();
        _gateway.Info = _ => throw new GatewayException("the API key was rejected", "HTTP 401", 401);

        Assert.Equal(ConnectionState.KeyRejected, await launcher.EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public async Task Without_config_nothing_is_started()
    {
        Assert.Equal(ConnectionState.GatewayNotRunning, await NewLauncher().EnsureRunningAsync(null, CancellationToken.None));
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public async Task Servers_that_never_come_up_give_an_error_with_the_log_folder()
    {
        var launcher = NewLauncher(startsAfterChecks: 1000);

        Assert.Equal(ConnectionState.GatewayNotRunning, await launcher.EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Contains("ERROR", _output.ToString());
        Assert.Contains(@"C:\logs", _output.ToString());
    }

    [Fact]
    public async Task Missing_files_are_reported_right_away_without_starting_anything()
    {
        _fileExists = path => path != Config.GatewayExe;

        Assert.Equal(ConnectionState.GatewayNotRunning, await NewLauncher().EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Empty(_runner.Started);
        Assert.Contains("ERROR", _output.ToString());
        Assert.Contains(Config.GatewayExe, _output.ToString());
        Assert.Contains("jazari update", _output.ToString());
    }
}
