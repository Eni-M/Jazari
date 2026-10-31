using System.ComponentModel;
using System.Diagnostics;

namespace MyOwnLLM.Gateway.State;

/// <summary>Recent commits and working-tree changes, or "" when the folder isn't a git repo.</summary>
public static class GitSummary
{
    const int MaxChars = 2000;

    public static async Task<string> GetAsync(string repoPath, CancellationToken ct)
    {
        // This runs on its own after every conversation, in whatever folder the client names, which may be a clone of
        // someone else's code: nothing from that repo's config may run a program (see Hardening).
        var hardening = await HardeningAsync(repoPath, ct);
        var log = await GitAsync(repoPath, ct, [.. hardening, "log", "--oneline", "-10"]);
        if (log is null) return "";
        var status = await GitAsync(repoPath, ct, [.. hardening, "status", "--short"]) ?? "";
        var summary = $"Recent commits:\n{log.TrimEnd()}\nUncommitted changes:\n{(status.Length > 0 ? status.TrimEnd() : "(none)")}";
        return summary.Length > MaxChars ? summary[..MaxChars] : summary;
    }

    /// <summary>
    /// Settings that keep the repo's own config from running programs: no file-system monitor, no signature checker,
    /// and every clean/smudge/process filter it names emptied (an empty command is no filter). Reading the config runs nothing.
    /// </summary>
    public static async Task<string[]> HardeningAsync(string repoPath, CancellationToken ct)
    {
        string[] fixedOff = ["-c", "core.fsmonitor=false", "-c", "log.showSignature=false"];
        var filters = await GitAsync(repoPath, ct, ["config", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process)$"]) ?? "";
        return [.. fixedOff, .. filters.Split('\n').Select(k => k.Trim()).Where(k => k.Length > 0).SelectMany(k => new[] { "-c", k + "=" })];
    }

    static async Task<string?> GitAsync(string repoPath, CancellationToken ct, string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--no-optional-locks"); // never take .git/index.lock while the user may be committing
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repoPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            await using var killOnCancel = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? await output : null;
        }
        catch (Win32Exception) { return null; } // git not installed
    }
}
