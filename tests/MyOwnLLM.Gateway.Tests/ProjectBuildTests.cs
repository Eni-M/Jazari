using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class ProjectBuildTests
{
    sealed class FakeBuild(int exit, string output, bool hang = false) : IProcessRunner
    {
        public string? Exe, Folder;
        public string[]? Args;
        public IReadOnlyDictionary<string, string>? Environment;

        public Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct) => throw new InvalidOperationException("the build must run in the project's folder");
        public void StartDetached(string exe, string[] args, string logFile) => throw new InvalidOperationException();

        public async Task<(int ExitCode, string Output)> RunInAsync(string exe, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
        {
            (Exe, Args, Folder, Environment) = (exe, args, workingDirectory, environment);
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return (exit, output);
        }
    }

    const string Project = @"C:\src\App\App.csproj";

    [Fact] public async Task The_build_runs_offline_and_in_the_projects_folder()
    {
        var runner = new FakeBuild(0, "");
        await ProjectBuild.RunAsync(Project, runner, @"C:\dotnet\dotnet.exe", default);
        Assert.Equal(@"C:\dotnet\dotnet.exe", runner.Exe);
        Assert.Equal(["build", Project, "--no-restore", "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false", "-p:UseSharedCompilation=false"], runner.Args);
        Assert.Equal(@"C:\src\App", runner.Folder);
        Assert.Equal("1", runner.Environment!["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact] public async Task A_failed_build_returns_its_errors()
    {
        var result = await ProjectBuild.RunAsync(Project, new FakeBuild(1, @"C:\src\App\A.cs(1,1): error CS0103: nope [C:\src\App\App.csproj]"), "dotnet.exe", default);
        Assert.False(result!.Succeeded);
        Assert.Equal("CS0103", Assert.Single(result.Errors).Code);
    }

    [Fact] public async Task A_passing_build_has_no_errors()
    {
        var result = await ProjectBuild.RunAsync(Project, new FakeBuild(0, "warning only"), "dotnet.exe", default);
        Assert.True(result!.Succeeded);
        Assert.Empty(result.Errors);
    }

    [Fact] public async Task A_build_that_runs_too_long_is_stopped()
    {
        var result = await ProjectBuild.RunAsync(Project, new FakeBuild(0, "", hang: true), "dotnet.exe", default, TimeSpan.FromMilliseconds(50));
        Assert.False(result!.Succeeded);
        Assert.Empty(result.Errors);
        Assert.Contains("minutes", result.Output);
    }

    [Fact] public async Task Stopping_the_build_reaches_the_caller()
    {
        using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectBuild.RunAsync(Project, new FakeBuild(0, "", hang: true), "dotnet.exe", stop.Token));
    }

    [Fact] public async Task No_dotnet_on_the_path_gives_nothing() =>
        Assert.Null(await ProjectBuild.RunAsync(Project, new FakeBuild(0, ""), null, default));

    [Fact] public void A_program_is_only_found_on_absolute_path_entries()
    {
        var dir = Directory.CreateTempSubdirectory("mollm-path").FullName;
        try
        {
            var exe = Path.Combine(dir, "dotnet.exe");
            File.WriteAllText(exe, "");
            Assert.Equal(exe, UpdateCheck.FromAbsolutePath("dotnet.exe", $".;relative;{dir}"));
            Assert.Null(UpdateCheck.FromAbsolutePath("dotnet.exe", ".;relative"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
