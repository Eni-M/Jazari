namespace MyOwnLLM.Cli;

public sealed record BuildResult(bool Succeeded, IReadOnlyList<BuildError> Errors, string Output);

/// <summary>
/// dotnet build for /fix. Offline (no restore, so a build can't change packages behind the trust check) and from the project's
/// folder, so its global.json applies. No build server stays behind. A build runs the project's scripts, so the caller asks first.
/// </summary>
public static class ProjectBuild
{
    public const int TimeoutSeconds = 300;

    /// <param name="dotnet">dotnet.exe's path, or null when it wasn't found: then this returns null.</param>
    /// <param name="timeout">How long it may take. Running out is a failed build; <paramref name="ct"/> is the user's stop, which throws.</param>
    public static async Task<BuildResult?> RunAsync(string projectFile, IProcessRunner runner, string? dotnet, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (dotnet is null) return null;
        string[] args = ["build", projectFile, "--no-restore", "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false", "-p:UseSharedCompilation=false"];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            var (exit, output) = await runner.RunInAsync(dotnet, args, Path.GetDirectoryName(projectFile)!,
                new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" }, limit.Token);
            return new BuildResult(exit == 0, exit == 0 ? [] : BuildErrors.Parse(output), output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BuildResult(false, [], $"The build was stopped after {TimeoutSeconds / 60} minutes.");
        }
    }
}
