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
    public async Task A_router_with_no_model_ready_yet_is_not_started_again()
    {
        _gatewayUp = true;
        var checks = 0;
        var launcher = new ServerLauncher(_runner, _gateway, _ => Task.FromResult(++checks > 2), new Ui(_output, new Theme(ColorMode.None, true), 80), @"C:\logs",
            (_, _) => Task.CompletedTask, maxWaits: 5, fileExists: _fileExists, llamaListening: _ => Task.FromResult(true));
        _gateway.Info = _ => Task.FromResult(new GatewayInfo("Jazari", "m", "0"));

        Assert.Equal(ConnectionState.Available, await launcher.EnsureRunningAsync(Config, CancellationToken.None));
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public void A_preset_has_both_lanes_with_the_smart_one_on_the_cpu()
    {
        var text = new Tuning("f", "Vulkan0", 4, 99, true, 8192).PresetText(@"C:\m\fast.gguf", @"C:\m\deep.gguf");
        var deep = text[text.IndexOf("[deep]")..];
        Assert.Contains("model = C:/m/fast.gguf", text);
        Assert.Contains("ctx-size = 8192", text);
        Assert.Contains("device = Vulkan0", text[..text.IndexOf("[deep]")]);
        Assert.Contains("device = none", deep);
        Assert.Contains("n-gpu-layers = 0", deep);
    }

    [Fact]
    public void No_deep_model_means_a_preset_with_fast_only() =>
        Assert.DoesNotContain("[deep]", new Tuning("f", "none", 4, 0, false, 16384).PresetText(@"C:\m\fast.gguf", null));

    [Fact]
    public void The_router_keeps_the_key_and_port_and_loads_nothing_by_surprise()
    {
        var args = Tuning.RouterArgs("p.ini", "k.key");
        Assert.Equal("8080", args[Array.IndexOf(args, "--port") + 1]);
        Assert.Equal("k.key", args[Array.IndexOf(args, "--api-key-file") + 1]);
        Assert.Equal("1", args[Array.IndexOf(args, "--models-max") + 1]);
        Assert.Contains("--no-models-autoload", args);
    }

    [Theory]
    [InlineData("""{"data":[{"id":"fast","status":{"value":"loaded"}}]}""", true, false, false)]
    [InlineData("""{"data":[{"id":"fast","status":{"value":"sleeping"}}]}""", true, false, false)]
    [InlineData("""{"data":[{"id":"fast","status":{"value":"unloaded"}},{"id":"deep","status":{"value":"loading"}}]}""", false, true, false)]
    [InlineData("""{"data":[{"id":"fast","status":{"value":"unloaded","failed":true}}]}""", false, false, true)]
    public void Router_states_are_read(string json, bool loaded, bool loading, bool failed) =>
        Assert.Equal(new RouterHealth.RouterState(loaded, loading, failed), RouterHealth.Read(json));

    sealed class StubRouter(string models) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(models) });
        }
    }

    [Theory]
    [InlineData(true, new[] { "/models", "/models/load" })]
    [InlineData(false, new[] { "/models" })]
    public async Task Only_the_start_asks_for_a_load_and_the_doctor_only_looks(bool asks, string[] expected)
    {
        var stub = new StubRouter("""{"data":[{"id":"fast","status":{"value":"unloaded"}}]}""");
        var health = new RouterHealth(new HttpClient(stub) { BaseAddress = new Uri("http://127.0.0.1:8080") }, () => "k") { AsksForLoad = asks };

        Assert.False(await health.ReadyAsync(CancellationToken.None));

        Assert.Equal(expected, stub.Asked);
    }

    [Fact]
    public void A_plain_server_has_no_router_state() => Assert.Null(RouterHealth.Read("""{"data":[{"id":"m.gguf"}]}"""));

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
