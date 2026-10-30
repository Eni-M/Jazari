using System.ComponentModel;
using System.Diagnostics;

namespace MyOwnLLM.Gateway.State;

/// <summary>Recent commits and working-tree changes, or "" when the folder isn't a git repo.</summary>
public static class GitSummary
{
    const int MaxChars = 2000;

    public static async Task<string> GetAsync(string repoPath, CancellationToken ct)
    {
        var log = await GitAsync(repoPath, ct, "log", "--oneline", "-10");
        if (log is null) return "";
        var status = await GitAsync(repoPath, ct, "status", "--short") ?? "";
        var summary = $"Recent commits:\n{log.TrimEnd()}\nUncommitted changes:\n{(status.Length > 0 ? status.TrimEnd() : "(none)")}";
        return summary.Length > MaxChars ? summary[..MaxChars] : summary;
    }

    static async Task<string?> GitAsync(string repoPath, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--no-optional-locks"); // never take .git/index.lock while the user may be committing
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repoPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? await output : null;
        }
        catch (Win32Exception) { return null; } // git not installed
    }
}
