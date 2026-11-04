using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace MyOwnLLM.Cli;

/// <summary>
/// What an edit breaks in the whole project, for /deep /edit: the project's errors before and after, and only the new ones, so
/// errors that were already there or come from source generators never count. Loading a project runs its build scripts,
/// so the caller asks first (<see cref="ProjectTrust"/>). Loaded per edit and nothing is kept: it's rare, and that frees the RAM.
/// </summary>
public static class ProjectCheck
{
    /// <summary>The nearest project file above <paramref name="file"/>, no higher than <paramref name="root"/>; null when there's none.</summary>
    public static string? FindProject(string file, string root)
    {
        var top = Path.GetFullPath(root).TrimEnd('\\', '/');
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!); dir is not null; dir = dir.Parent)
        {
            if (dir.GetFiles("*.csproj") is [var project, ..]) return project.FullName;
            if (string.Equals(dir.FullName.TrimEnd('\\', '/'), top, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return null;
    }

    /// <summary>Errors the edit adds; empty when it adds none. Null: no project, or it wouldn't load, so nothing can be said.</summary>
    public static async Task<IReadOnlyList<string>?> NewErrorsAsync(string file, string root, string before, string after, CancellationToken ct)
    {
        if (FindProject(file, root) is not { } projectFile) return null;
        try
        {
            using var workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
            var full = Path.GetFullPath(file);
            if (project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase)) is not { } document) return null;
            var known = (await ErrorsAsync(document, before, ct)).ToHashSet();
            return [.. (await ErrorsAsync(document, after, ct)).Where(e => !known.Contains(e)).Distinct()];
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { return null; } // a project that won't load is no reason to stop the edit
    }

    /// <summary>"CS0103 message", without places: an edit moves every line below it, and those errors aren't new.</summary>
    static async Task<IEnumerable<string>> ErrorsAsync(Document document, string text, CancellationToken ct)
    {
        var compilation = await document.WithText(SourceText.From(text)).Project.GetCompilationAsync(ct);
        if (compilation is null) throw new InvalidOperationException("the project gave no compilation"); // can't tell isn't the same as no errors
        return compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id} {d.GetMessage()}");
    }
}

/// <summary>
/// Which projects the user said may have their build scripts run, as they were then: a pull that changes those scripts asks
/// again. A damaged or missing file means none. ponytail: covers the project file and the .props and .targets files in its
/// folder and those above it up to the project root, plus global.json and nuget.config; a script imported from elsewhere
/// isn't seen. Evaluating the project to list every import would run the scripts this exists to ask about.
/// </summary>
public sealed class ProjectTrust(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "trusted-projects.json");

    static string Key(string projectFile) => Path.GetFullPath(projectFile).ToLowerInvariant();

    static string BuildInputsHash(string projectFile, string root)
    {
        var top = Path.GetFullPath(root).TrimEnd('\\', '/');
        var files = new List<string> { Path.GetFullPath(projectFile) };
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectFile))!); dir is not null; dir = dir.Parent)
        {
            files.AddRange(dir.GetFiles().Where(f => f.Extension.ToLowerInvariant() is ".props" or ".targets" || f.Name.ToLowerInvariant() is "global.json" or "nuget.config").Select(f => f.FullName));
            if (string.Equals(dir.FullName.TrimEnd('\\', '/'), top, StringComparison.OrdinalIgnoreCase)) break;
        }
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.ToLowerInvariant() + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    Dictionary<string, string> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    public bool IsTrusted(string projectFile, string root)
    {
        try { return Read().TryGetValue(Key(projectFile), out var trusted) && trusted == BuildInputsHash(projectFile, root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Trust(string projectFile, string root)
    {
        var trusted = Read();
        trusted[Key(projectFile)] = BuildInputsHash(projectFile, root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(trusted));
    }
}
