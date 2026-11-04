using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <param name="FirstLine">The file's line number of the text's first line: a large file is reviewed in pieces.</param>
public sealed record ProjectFile(string Path, string Text, int FirstLine = 1);

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
        var ignored = IgnorePatterns(target);
        foreach (var path in Walk(target))
        {
            // Relative: the folders above the project (it may sit in a .azure) say nothing about its files.
            var relative = Path.GetRelativePath(target, path).Replace('\\', '/');
            if (!Wanted(relative) || IsIgnored(relative, ignored) || TryRead(path) is not { } text) { skipped++; continue; }
            files.Add(new(relative, text));
        }
        return ([.. files.OrderByDescending(f => Risk(f.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)], skipped);
    }

    static IEnumerable<string> Walk(string folder)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }; // a folder we can't open is no reason to stop
        // A link can point anywhere: notes.txt linked to ~/.ssh/id_rsa passes every name rule. Only links are skipped, not
        // every reparse point, since OneDrive marks its ordinary files as reparse points too.
        foreach (var file in Directory.EnumerateFiles(folder, "*", options).Where(f => !IsLink(new FileInfo(f)))) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder, "*", options))
        {
            if (SkippedFolders.Contains(Path.GetFileName(sub)) || new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var file in Walk(sub)) yield return file;
        }
    }

    /// <summary>
    /// The project's .jazariignore, like Crush's .crushignore: one pattern a line, # for comments. "secrets/" leaves out a
    /// folder anywhere in the project, "*.generated.cs" a kind of file, "src/legacy/*" a path. * and ? are the wildcards.
    /// </summary>
    static string[] IgnorePatterns(string root)
    {
        try
        {
            var file = Path.Combine(root, ".jazariignore");
            return File.Exists(file)
                ? [.. File.ReadAllLines(file).Select(l => l.Trim().Replace('\\', '/')).Where(l => l.Length > 0 && !l.StartsWith('#'))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The files the project's .jazariignore kept out that would otherwise have been read. A cloned repository's own
    /// .jazariignore could hide a file from /review, so the report names every one of them.
    /// </summary>
    public static List<string> IgnoredFiles(string target)
    {
        if (!Directory.Exists(target) || IgnorePatterns(target) is not { Length: > 0 } patterns) return [];
        return [.. Walk(target).Select(p => Path.GetRelativePath(target, p).Replace('\\', '/'))
            .Where(r => Wanted(r) && IsIgnored(r, patterns)).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static bool IsIgnored(string relative, IReadOnlyList<string> patterns) => patterns.Any(pattern =>
        pattern.EndsWith('/')
            ? relative.Split('/').SkipLast(1).Any(folder => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern.TrimEnd('/'), folder))
            : System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, pattern.Contains('/') ? relative : Path.GetFileName(relative)));

    static bool Wanted(string path)
    {
        var name = Path.GetFileName(path);
        return CodeExtensions.Contains(Path.GetExtension(path)) && !Noise().IsMatch(name) && !IsSecret(path);
    }

    static bool IsLink(FileInfo file)
    {
        try { return file.LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; } // can't tell: don't follow it
    }

    static readonly HashSet<string> SecretFolders = new(StringComparer.OrdinalIgnoreCase) { ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".git" };

    /// <summary>
    /// Files that hold keys and passwords (.env, *.pem, appsettings.Production.json) or sit in a folder of them (.ssh, .aws,
    /// .git and the like): never sent to the model.
    /// </summary>
    public static bool IsSecret(string path) =>
        Secret().IsMatch(Path.GetFileName(path)) || path.Split('/', '\\').SkipLast(1).Any(SecretFolders.Contains);

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
