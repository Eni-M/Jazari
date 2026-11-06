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

    /// <summary>
    /// Errors whose message only fills in names of code: types, members, parameters, keywords. Those go in the request as they are,
    /// which on the benchmark fixed 7 of 12 where moving every message into the data fixed 3. Any other message can carry the
    /// project's own text (an [Obsolete] reason in CS0619, an #error in CS1029, a generator's exception in CS8785), so it goes
    /// only with the files, which the prompt marks as data.
    /// </summary>
    public static bool OnlyNamesCode(BuildError error) => NamesOnly.Contains(error.Code);

    static readonly HashSet<string> NamesOnly =
    [
        "CS0029", "CS0030", "CS0103", "CS0117", "CS0120", "CS0161", "CS0165", "CS0234", "CS0246", "CS0266", "CS0535", "CS0236",
        "CS1002", "CS1003", "CS1026", "CS1061", "CS1501", "CS1503", "CS1513", "CS1525", "CS4014", "CS7036", "CS8618",
    ];

    static bool IsBuildOutput(string folder) => folder.Equals("obj", StringComparison.OrdinalIgnoreCase) || folder.Equals("bin", StringComparison.OrdinalIgnoreCase);

    static bool Inside(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
