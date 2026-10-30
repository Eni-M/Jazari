using System.Text.Json;

namespace Jazari.Launcher;

/// <summary>What the `jazari` command should do: run the app, run the updater, or explain what's missing.</summary>
public sealed record LaunchPlan(string Kind, string Exe, string[] Args, string? Message)
{
    const string NotInstalled = @"Jazari isn't installed on this PC yet. Run scripts\install-jazari.ps1 from your MyOwnLLM clone.";

    public static LaunchPlan Decide(string[] args, string? configJson, Func<string, bool> fileExists)
    {
        if (Read(configJson) is not var (installDir, sourceDir)) return Fail(NotInstalled);

        if (args is ["update"])
        {
            var updater = Path.Combine(sourceDir, "scripts", "update-jazari.ps1");
            return fileExists(updater)
                ? new("update", "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", updater], null)
                : Fail(@"Jazari's updater is missing. Run scripts\install-jazari.ps1 again from your MyOwnLLM clone.");
        }

        var app = Path.Combine(installDir, "app", "jazari.exe");
        return fileExists(app) ? new("run", app, args, null) : Fail("Jazari isn't fully installed. Run: jazari update");
    }

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
