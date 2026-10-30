using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

public sealed record ProjectFile(string Path, string Text);

/// <summary>Finds the files of a project worth reading: real code and config, never generated code, data dumps, or secrets.</summary>
public static partial class ProjectFiles
{
    const int MaxFileBytes = 256 * 1024; // larger than this is generated or data, not something a person wrote

    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", "build", "out", "coverage", "packages", "artifacts", "Migrations", "TestResults", "wwwroot",
    };

    static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".razor", ".csproj", ".slnx", ".ts", ".tsx", ".js", ".jsx", ".vue", ".html", ".css", ".scss", ".py", ".java", ".go", ".rs", ".sql",
        ".ps1", ".sh", ".json", ".yml", ".yaml", ".xml", ".config", ".md", ".txt", ".toml",
    };

    /// <summary>
    /// The readable files under <paramref name="target"/>, riskiest first, with paths relative to it, and how many were
    /// skipped. A single named file is always read: asking for it by name overrides the folder rules, except for size.
    /// </summary>
    public static (List<ProjectFile> Files, int Skipped) Load(string target)
    {
        if (File.Exists(target))
            return TryRead(target) is { } text ? ([new(Path.GetFileName(target), text)], 0) : ([], 1);

        List<ProjectFile> files = [];
        var skipped = 0;
        foreach (var path in Walk(target))
        {
            if (!Wanted(path) || TryRead(path) is not { } text) { skipped++; continue; }
            files.Add(new(Path.GetRelativePath(target, path).Replace('\\', '/'), text));
        }
        return ([.. files.OrderByDescending(f => Risk(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)], skipped);
    }

    static IEnumerable<string> Walk(string folder)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }; // a folder we can't open is no reason to stop
        foreach (var file in Directory.EnumerateFiles(folder, "*", options)) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder, "*", options))
        {
            if (SkippedFolders.Contains(Path.GetFileName(sub)) || new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var file in Walk(sub)) yield return file;
        }
    }

    static bool Wanted(string path)
    {
        var name = Path.GetFileName(path);
        return CodeExtensions.Contains(Path.GetExtension(path)) && !Noise().IsMatch(name) && !Secret().IsMatch(name);
    }

    static string? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            var text = File.ReadAllText(path);
            return text.Contains('\0') ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Higher means read sooner: where security and money mistakes usually live.</summary>
    public static int Risk(string path) => Hot().Count(path) * 2 + Warm().Count(path);

    [GeneratedRegex(@"(\.Designer\.cs|\.g\.cs|\.g\.i\.cs|\.min\.(js|css)|\.lock|\.map|package-lock\.json|pnpm-lock\.yaml|ModelSnapshot\.cs)$|^seed", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();
    [GeneratedRegex(@"^(\.env.*|secrets?\..*|.*\.(pem|key|pfx|p12))$|^appsettings\.(Production|Secrets)\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
    [GeneratedRegex(@"auth|login|password|token|secret|crypt|payment|billing|invoice|upload|admin|permission|role|session|sql", RegexOptions.IgnoreCase)]
    private static partial Regex Hot();
    [GeneratedRegex(@"controller|endpoint|middleware|handler|filter|service|program\.cs|config|api", RegexOptions.IgnoreCase)]
    private static partial Regex Warm();
}
