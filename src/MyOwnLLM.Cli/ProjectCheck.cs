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
/// Which projects the user said may have their build scripts run, as they were then: a change to those scripts asks again. A
/// damaged or missing file means none. Covered: each project file (following ProjectReference and Import paths written out
/// literally), its .user file, the .props and .targets MSBuild imports from obj, and the .props, .targets, global.json,
/// nuget.config and Directory.Build.rsp in its folder and those above it up to the project root. A restore that changes
/// packages asks again too. ponytail: a path built from MSBuild properties isn't followed, because working it out means
/// evaluating the project, which runs the scripts this exists to ask about.
/// </summary>
public sealed class ProjectTrust(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "trusted-projects.json");

    static string Key(string projectFile) => Path.GetFullPath(projectFile).ToLowerInvariant();

    /// <param name="remote">Set when a file names a UNC or device path (\\server\share). It's never opened, since opening one
    /// before the user's yes would send their Windows credentials to that server, and the project is never trusted.</param>
    static IEnumerable<string> BuildFiles(string projectFile, string root, out bool remote)
    {
        remote = false;
        var top = Path.GetFullPath(root).TrimEnd('\\', '/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Stack<string>([Path.GetFullPath(projectFile)]);
        while (waiting.TryPop(out var file))
        {
            if (!seen.Add(file) || !File.Exists(file)) continue;
            files.Add(file);
            foreach (var linked in LiteralPaths(file))
                if (linked.StartsWith(@"\\", StringComparison.Ordinal) || linked.StartsWith("//", StringComparison.Ordinal)) remote = true;
                else waiting.Push(linked);
            if (Path.GetExtension(file).ToLowerInvariant() is ".props" or ".targets") continue;

            var folder = Path.GetDirectoryName(file)!;
            files.Add(file + ".user");
            var obj = Path.Combine(folder, "obj");
            if (Directory.Exists(obj)) foreach (var generated in Directory.GetFiles(obj).Where(IsScript)) waiting.Push(generated);
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                foreach (var nearby in dir.GetFiles().Where(f => IsScript(f.Name) || f.Name.ToLowerInvariant() is "global.json" or "nuget.config" or "directory.build.rsp")) waiting.Push(nearby.FullName);
                if (string.Equals(dir.FullName.TrimEnd('\\', '/'), top, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return files.Where(File.Exists);
    }

    static bool IsScript(string file) => Path.GetExtension(file).ToLowerInvariant() is ".props" or ".targets";

    /// <summary>Other files this one names outright. Read as plain XML, so nothing in it runs; a file that isn't XML names nothing.</summary>
    static IEnumerable<string> LiteralPaths(string file)
    {
        if (Path.GetExtension(file).ToLowerInvariant() is not (".csproj" or ".props" or ".targets")) return [];
        try
        {
            var folder = Path.GetDirectoryName(file)!;
            return System.Xml.Linq.XDocument.Load(file).Descendants()
                .Select(e => e.Name.LocalName switch { "ProjectReference" => e.Attribute("Include")?.Value, "Import" => e.Attribute("Project")?.Value, _ => null })
                .Where(p => p is { Length: > 0 } && !p.Contains('$') && !p.Contains('*') && p.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                .Select(p => Path.GetFullPath(Path.Combine(folder, p!.Replace('\\', Path.DirectorySeparatorChar)))).ToList();
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// <summary>Each file as its path and contents, both with their lengths, so bytes moved from one file to another change the hash.</summary>
    /// <returns>Null for a project that names a remote file: it asks every time.</returns>
    static string? BuildInputsHash(string projectFile, string root)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Framed(byte[] bytes)
        {
            hash.AppendData(BitConverter.GetBytes((long)bytes.Length));
            hash.AppendData(bytes);
        }
        var files = BuildFiles(projectFile, root, out var remote).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (remote) return null;
        foreach (var file in files)
        {
            Framed(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).ToLowerInvariant()));
            Framed(File.ReadAllBytes(file));
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
        try { return Read().TryGetValue(Key(projectFile), out var trusted) && BuildInputsHash(projectFile, root) is { } now && trusted == now; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Trust(string projectFile, string root)
    {
        try
        {
            if (BuildInputsHash(projectFile, root) is not { } now) return;
            var trusted = Read();
            trusted[Key(projectFile)] = now;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(trusted));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a file in use: this yes still counts, and the next edit asks again
    }
}
