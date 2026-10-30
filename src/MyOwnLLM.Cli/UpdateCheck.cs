using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>Is GitHub's main newer than what's installed? Any doubt (offline, slow, git missing) means "no", silently.</summary>
public static partial class UpdateCheck
{
    public static async Task<bool> IsUpdateAvailableAsync(IProcessRunner runner, JazariConfig? config, TimeSpan timeout, CancellationToken ct)
    {
        if (config is not { RepoUrl: { Length: > 0 } repo, InstalledCommit: { Length: > 0 } installed }) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var (exitCode, output) = await runner.RunAsync("git", ["ls-remote", repo, "refs/heads/main"], cts.Token).WaitAsync(cts.Token);
            var latest = output.Split('\t', '\n', ' ').FirstOrDefault()?.Trim() ?? "";
            return exitCode == 0 && Commit().IsMatch(latest) && !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; } // never let a check get in the way of starting Jazari
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase)] private static partial Regex Commit();
}
