using System.Text.Json;

namespace MyOwnLLM.Cli;

/// <summary>
/// Where this machine's Jazari pieces live, written by scripts/install-jazari.ps1 and scripts/update-jazari.ps1 so
/// nothing is hard-coded: the published gateway, llama-server, the model file and what the updater needs.
/// </summary>
public sealed record JazariConfig(
    string Home, string GatewayExe, string LlamaServer, string Model,
    string? RepoUrl = null, string? InstallDir = null, string? SourceDir = null, string? InstalledCommit = null, string? DeepModel = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "config.json");

    public static JazariConfig? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<JazariConfig>(File.ReadAllText(path), JsonSerializerOptions.Web) : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; } // like no install: jazari says to repair it
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
    }
}
