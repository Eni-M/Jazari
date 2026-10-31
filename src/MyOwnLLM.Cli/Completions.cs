namespace MyOwnLLM.Cli;

/// <summary>What Tab offers: command names after "/", and file and folder names after a command that takes a path.</summary>
public static class Completions
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea" };
    const int MaxItems = 50;

    /// <summary>Where the replaced text starts, and what can replace it. No items: nothing to offer.</summary>
    public static (int Start, IReadOnlyList<string> Items) For(string text, int caret, string directory)
    {
        var typed = text[..caret];
        if (!typed.StartsWith('/')) return (caret, []);
        if (!typed.Contains(' '))
            return (0, Session.CommandNames.Where(c => c.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList());

        var command = typed[..typed.IndexOf(' ')];
        var argument = typed[(command.Length + 1)..];
        // Only the first word after the command is a path (for /edit, the rest is the request).
        if (!Session.PathCommands.Contains(command, StringComparer.OrdinalIgnoreCase) || argument.Contains(' ')) return (caret, []);
        return (command.Length + 1, Paths(argument, directory));
    }

    static List<string> Paths(string partial, string directory)
    {
        var slash = partial.LastIndexOfAny(['/', '\\']);
        var folderPart = slash < 0 ? "" : partial[..(slash + 1)];
        var namePart = partial[(slash + 1)..];
        string folder;
        try { folder = Path.GetFullPath(Path.Combine(directory, folderPart)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return []; }
        if (!Directory.Exists(folder)) return [];

        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(e => e.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase))
                .Where(e => !Skipped.Contains(e.Name) && (namePart.StartsWith('.') || !e.Name.StartsWith('.')))
                .OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxItems)
                .Select(e => folderPart + e.Name + (e is DirectoryInfo ? "/" : ""))
                .ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }
}
