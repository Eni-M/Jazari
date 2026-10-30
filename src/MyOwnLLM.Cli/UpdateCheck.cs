using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>Is GitHub's main newer than what's installed? Any doubt (offline, slow, git missing) means "no", silently.</summary>
public static partial class UpdateCheck
{
    public static async Task<bool> IsUpdateAvailableAsync(
        IProcessRunner runner, JazariConfig? config, TimeSpan timeout, CancellationToken ct, Func<string?>? findGit = null)
    {
        if (config is not { RepoUrl: { Length: > 0 } repo, InstalledCommit: { Length: > 0 } installed }) return false;
        // jazari runs inside arbitrary (possibly cloned, untrusted) folders: only https, git from PATH (never the current
        // folder), run from Jazari's own folder so no repo config there can change how it connects.
        if (!repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if ((findGit ?? (() => FindGit(Environment.GetEnvironmentVariable("PATH"))))() is not { } git) return false;
        var trustedDir = config.InstallDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var (exitCode, output) = await runner.RunAsync(git,
                ["-C", trustedDir, "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "ls-remote", repo, "refs/heads/main"],
                cts.Token).WaitAsync(cts.Token);
            var latest = output.Split('\t', '\n', ' ').FirstOrDefault()?.Trim() ?? "";
            return exitCode == 0 && Commit().IsMatch(latest) && !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; } // never let a check get in the way of starting Jazari
    }

    /// <summary>git.exe from absolute PATH entries only; "." and relative entries (current-folder lookups) are skipped.</summary>
    public static string? FindGit(string? path) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(dir => Path.Combine(dir, "git.exe"))
            .FirstOrDefault(File.Exists);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase)] private static partial Regex Commit();
}
