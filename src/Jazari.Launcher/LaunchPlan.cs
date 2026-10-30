using System.Text.Json;

namespace Jazari.Launcher;

/// <summary>What the `jazari` command should do: run the app, run the updater, or explain what's missing.</summary>
/// <param name="WorkingDirectory">Where to start it; null keeps the user's current folder (the app's project).</param>
public sealed record LaunchPlan(string Kind, string Exe, string[] Args, string? Message, string? WorkingDirectory = null)
{
    const string NotInstalled = @"Jazari isn't installed on this PC yet. Run scripts\install-jazari.ps1 from your MyOwnLLM clone.";

    // Full path, never "powershell.exe": a bare name could pick up a planted copy in the folder jazari runs in.
    static readonly string PowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static LaunchPlan Decide(string[] args, string? configJson, Func<string, bool> fileExists)
    {
        if (Read(configJson) is not var (installDir, sourceDir)) return Fail(NotInstalled);

        if (args is ["update"])
        {
            var updater = Path.Combine(sourceDir, "scripts", "update-jazari.ps1");
            return fileExists(updater)
                ? new("update", PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", updater], null, sourceDir)
                : Fail(@"Jazari's updater is missing. Run scripts\install-jazari.ps1 again from your MyOwnLLM clone.");
        }

        var app = Path.Combine(installDir, "app", "jazari.exe");
        return fileExists(app) ? new("run", app, args, null) : Fail("Jazari isn't fully installed. Run: jazari update");
    }

    /// <summary>A failed update leaves the previous version working, so Jazari reopens; "n" (exit 2) or Ctrl+C means stop here.</summary>
    public static bool ReopensAfterUpdate(int updaterExit) => updaterExit is 0 or 1;

    static LaunchPlan Fail(string message) => new("fail", "", [], message);

    static (string InstallDir, string SourceDir)? Read(string? json)
    {
        if (json is null) return null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.TryGetProperty("installDir", out var i) && root.TryGetProperty("sourceDir", out var s)
                   && i.GetString() is { Length: > 0 } installDir && s.GetString() is { Length: > 0 } sourceDir
                ? (installDir, sourceDir)
                : null;
        }
        catch (JsonException) { return null; }
    }
}
