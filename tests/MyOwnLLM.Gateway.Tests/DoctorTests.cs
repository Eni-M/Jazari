using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class DoctorTests : IDisposable
{
    static readonly JazariConfig Config = new(@"C:\src", @"C:\J\gateway\g.exe", @"C:\J\llama\llama-server.exe", @"C:\J\models\m.gguf");
    readonly string _logs = Directory.CreateTempSubdirectory("jazari doctor").FullName;
    readonly FakeGateway _gateway = new();

    public void Dispose() => Directory.Delete(_logs, recursive: true);

    Task<List<Check>> Run(JazariConfig? config = null, string? key = "k", bool llama = true, Func<string, bool>? exists = null, bool update = false) =>
        new Doctor(config ?? Config, key, _gateway, _ => Task.FromResult(llama), exists ?? (_ => true), _logs, @"C:\law-book.db", measured: true,
            _ => Task.FromResult(update)).RunAsync(default);

    static Check Named(List<Check> checks, string name) => checks.Single(c => c.Name == name);

    [Fact]
    public async Task A_healthy_install_has_nothing_to_fix() =>
        Assert.All(await Run(), c => Assert.True(c.Ok && c.Fix is null, $"{c.Name}: {c.Detail}"));

    [Fact]
    public async Task No_install_says_how_to_install()
    {
        var checks = await new Doctor(null, null, _gateway, _ => Task.FromResult(false), _ => false, _logs, "", false, _ => Task.FromResult(false)).RunAsync(default);
        Assert.Contains("install-jazari.ps1", Assert.Single(checks).Fix);
    }

    [Fact]
    public async Task Missing_files_point_to_update()
    {
        var check = Named(await Run(exists: f => !f.EndsWith(".gguf")), "Install");
        Assert.False(check.Ok);
        Assert.Contains("m.gguf", check.Detail);
        Assert.Equal("Run jazari update.", check.Fix);
    }

    [Fact]
    public async Task A_stopped_model_server_shows_the_logs_last_error()
    {
        File.WriteAllText(Path.Combine(_logs, "llama-server.log"), "loading\nerror: failed to load model\nbye\n");
        var check = Named(await Run(llama: false), "Model server");
        Assert.False(check.Ok);
        Assert.Contains("error: failed to load model", check.Detail);
    }

    [Fact]
    public async Task A_rejected_key_and_a_stopped_gateway_are_told_apart()
    {
        _gateway.Info = _ => throw new GatewayException("no", null, 401);
        Assert.Contains("doesn't know this key", Named(await Run(), "Gateway").Detail);
        _gateway.Info = _ => throw new GatewayException("no", null, null);
        Assert.StartsWith("not running", Named(await Run(), "Gateway").Detail);
    }

    [Fact]
    public async Task No_key_and_an_update_are_reported()
    {
        var checks = await Run(key: null, update: true);
        Assert.False(Named(checks, "Key").Ok);
        Assert.Equal("Run jazari update.", Named(checks, "Version").Fix);
    }

    [Fact]
    public void Doctor_parses() => Assert.Equal("doctor", CliArgs.Parse(["doctor"]).Command);
}
