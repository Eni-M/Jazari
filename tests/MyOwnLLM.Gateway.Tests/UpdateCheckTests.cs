using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class UpdateCheckTests
{
    static readonly string A = new('a', 40), B = new('b', 40);
    static readonly JazariConfig Config = new(@"C:\R", "g.exe", "l.exe", "m.gguf", "https://github.com/Eni-M/MyOwnLLM.git", @"C:\J", @"C:\J\source", B);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    readonly FakeRunner _runner = new();

    const string Git = @"C:\Program Files\Git\cmd\git.exe";

    Task<bool> Check(JazariConfig? config) => UpdateCheck.IsUpdateAvailableAsync(_runner, config, Timeout, CancellationToken.None, () => Git);

    [Fact]
    public async Task Newer_commit_means_update()
    {
        _runner.Result = (_, _) => (0, $"{A}\trefs/heads/main\n");

        Assert.True(await Check(Config));
        var ran = _runner.Ran.Single();
        Assert.Equal(Git, ran.Exe);
        Assert.Equal(["-C", Config.InstallDir!, "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
            "ls-remote", Config.RepoUrl!, "refs/heads/main"], ran.Args);
    }

    [Fact]
    public async Task Same_commit_means_no_update()
    {
        _runner.Result = (_, _) => (0, $"{B}\trefs/heads/main\n");

        Assert.False(await Check(Config));
    }

    [Theory]
    [InlineData(128, "fatal: repository not found")]
    [InlineData(0, "")]
    [InlineData(0, "not-a-commit\trefs/heads/main")]
    public async Task Git_failure_or_garbage_means_no_update(int exitCode, string output)
    {
        _runner.Result = (_, _) => (exitCode, output);

        Assert.False(await Check(Config));
    }

    [Fact]
    public async Task Runner_exception_means_no_update()
    {
        _runner.Result = (_, _) => throw new InvalidOperationException("git missing");

        Assert.False(await Check(Config));
    }

    [Fact]
    public async Task Missing_repo_or_commit_means_no_update()
    {
        Assert.False(await Check(null));
        Assert.False(await Check(Config with { RepoUrl = null }));
        Assert.False(await Check(Config with { InstalledCommit = null }));
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task Slow_git_times_out_as_no_update()
    {
        var slow = new SlowRunner();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await UpdateCheck.IsUpdateAvailableAsync(slow, Config, TimeSpan.FromMilliseconds(50), CancellationToken.None, () => Git);

        Assert.False(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    sealed class SlowRunner : IProcessRunner
    {
        public async Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return (0, "");
        }

        public void StartDetached(string exe, string[] args, string logFile) { }
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("ssh://evil/repo")]
    [InlineData("file:///C:/x")]
    [InlineData(@"C:\some\folder")]
    public async Task Only_https_repo_addresses_are_used(string repo)
    {
        Assert.False(await Check(Config with { RepoUrl = repo }));
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task No_git_on_PATH_means_no_update()
    {
        Assert.False(await UpdateCheck.IsUpdateAvailableAsync(_runner, Config, Timeout, CancellationToken.None, () => null));
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public void FindGit_ignores_relative_PATH_entries_and_the_current_folder()
    {
        var dir = Directory.CreateTempSubdirectory("jazari-git").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "git.exe"), "");
            Assert.Equal(Path.Combine(dir, "git.exe"), UpdateCheck.FindGit($@".;relative\bin;{dir}"));
            Assert.Null(UpdateCheck.FindGit(@".;relative\bin"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
