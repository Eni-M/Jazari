namespace MyOwnLLM.Cli;

/// <summary>What /fix works on: which project to build, and which of its errors the model may change.</summary>
public static class FixTarget
{
    public const int MaxErrors = 5;

    /// <summary>The project named, or the only one in the root or a folder directly under it. A solution isn't accepted:
    /// <see cref="ProjectTrust"/> follows a project's references but can't see what a solution holds.</summary>
    public static (string? Project, string? Problem) Find(string projectRoot, string? argument)
    {
        if (argument is not null)
        {
            var named = Path.GetFullPath(argument, projectRoot);
            if (!Inside(named, projectRoot)) return (null, "Jazari only builds projects inside this folder.");
            if (!named.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return (null, "Name a .csproj file: /fix path\\to\\App.csproj");
            return File.Exists(named) ? (named, null) : (null, $"Can't find {named}.");
        }
        var found = Directory.GetFiles(projectRoot, "*.csproj").Concat(Directory.GetDirectories(projectRoot)
            .Where(d => !IsBuildOutput(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.'))
            .SelectMany(d => Directory.GetFiles(d, "*.csproj"))).ToList();
        return found switch
        {
            [] => (null, "No .csproj here or one folder down. Name one: /fix path\\to\\App.csproj"),
            [var only] => (only, null),
            _ => (null, "More than one project. Name one: /fix " + string.Join(" or /fix ", found.Select(p => Path.GetRelativePath(projectRoot, p)))),
        };
    }

    /// <summary>The first file with errors the model may change, and at most <see cref="MaxErrors"/> of its errors. Not generated
    /// code, and nothing outside the project such as a NuGet package's source.</summary>
    public static IReadOnlyList<BuildError> Editable(IReadOnlyList<BuildError> errors, string projectRoot)
    {
        var editable = errors.Where(e => e.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && Inside(e.File, projectRoot)
            && !Path.GetRelativePath(projectRoot, e.File).Split(Path.DirectorySeparatorChar).Any(IsBuildOutput)).ToList();
        return editable.Count == 0 ? [] :
            [.. editable.Where(e => string.Equals(e.File, editable[0].File, StringComparison.OrdinalIgnoreCase)).Take(MaxErrors)];
    }

    static bool IsBuildOutput(string folder) => folder.Equals("obj", StringComparison.OrdinalIgnoreCase) || folder.Equals("bin", StringComparison.OrdinalIgnoreCase);

    static bool Inside(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
