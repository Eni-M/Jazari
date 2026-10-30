using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

sealed class FakeRunner : IProcessRunner
{
    public List<(string Exe, string[] Args)> Ran { get; } = [];
    public List<(string Exe, string[] Args, string LogFile)> Started { get; } = [];
    public Func<string, string[], (int ExitCode, string Output)> Result { get; set; } = (_, _) => (0, "");

    public Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        Ran.Add((exe, args));
        return Task.FromResult(Result(exe, args));
    }

    public void StartDetached(string exe, string[] args, string logFile) => Started.Add((exe, args, logFile));
}

public class KeyProvisionerTests
{
    static readonly JazariConfig Config = new(@"C:\Repo", @"C:\gw\MyOwnLLM.Gateway.exe", @"C:\llama\llama-server.exe", @"C:\m\granite.gguf");
    readonly FakeRunner _runner = new();
    readonly List<string> _saved = [];
    KeyProvisioner Provisioner => new(_runner, _saved.Add);

    [Fact]
    public async Task Existing_key_is_kept_and_nothing_runs()
    {
        Assert.Equal("mollm_have", await Provisioner.EnsureKeyAsync("mollm_have", Config, CancellationToken.None));
        Assert.Empty(_runner.Ran);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Missing_key_runs_add_key_and_saves_it()
    {
        _runner.Result = (_, _) => (0, "mollm_new123\r\nStore this key now; only its hash is kept.\r\n");

        var key = await Provisioner.EnsureKeyAsync(null, Config, CancellationToken.None);

        Assert.Equal("mollm_new123", key);
        Assert.Equal(Config.GatewayExe, _runner.Ran.Single().Exe);
        Assert.Equal(["add-key"], _runner.Ran.Single().Args);
        Assert.Equal(["mollm_new123"], _saved);
    }

    [Fact]
    public async Task No_config_means_no_key() => Assert.Null(await Provisioner.EnsureKeyAsync(null, null, CancellationToken.None));

    [Theory]
    [InlineData(0, "something unexpected")]
    [InlineData(1, "mollm_but_failed")]
    public async Task Failed_add_key_means_no_key(int exitCode, string output)
    {
        _runner.Result = (_, _) => (exitCode, output);

        Assert.Null(await Provisioner.EnsureKeyAsync(null, Config, CancellationToken.None));
        Assert.Empty(_saved);
    }
}
