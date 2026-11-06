using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MyOwnLLM.Cli;

/// <summary>
/// What `/review --changed` reads: only the code that changed since the last commit, as small parts. A changed C# member
/// (method, property, constructor) goes whole, so the model sees what the change does; elsewhere the changed lines go
/// with a few lines around them. Small parts are where review holds up: one study measured F1 0.657 on diffs under 10
/// lines and 0.043 over 150. Each part keeps the file's line numbers.
/// </summary>
public static partial class ChangedCode
{
    /// <summary>Lines around a change Jazari can't place in a member (another language, a using, a field).</summary>
    const int Around = 6;

    /// <summary>Each changed file and the lines its changes touch in the new version, from `git diff -U0`.</summary>
    public static Dictionary<string, List<(int From, int To)>> Ranges(string diff)
    {
        var ranges = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        List<(int, int)>? current = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ "))
            {
                var path = line[4..].TrimEnd('\r').Trim().Trim('"');
                current = path == "/dev/null" ? null : ranges[path.StartsWith("b/") ? path[2..] : path] = [];
                continue;
            }
            if (current is null || Hunk().Match(line) is not { Success: true } hunk) continue;
            var start = int.Parse(hunk.Groups[1].Value);
            var count = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;
            // Lines only taken away: the change sits between this line and the next, so both are looked at.
            current.Add(count == 0 ? (Math.Max(start, 1), start + 1) : (start, start + count - 1));
        }
        return ranges;
    }

    /// <summary>
    /// The parts to review, and the changed files the project's .jazariignore kept out (named in the report, as a full
    /// review names them). <paramref name="newFiles"/> are untracked files, reviewed whole.
    /// </summary>
    public static (List<ProjectFile> Parts, List<string> Ignored) Parts(string root, Dictionary<string, List<(int From, int To)>> ranges, IEnumerable<string> newFiles)
    {
        var patterns = ProjectFiles.IgnorePatterns(root);
        var parts = new List<ProjectFile>();
        var ignored = new List<string>();
        bool Reviewable(string path)
        {
            if (!ProjectFiles.Wanted(path)) return false;
            if (!ProjectFiles.IsIgnored(path, patterns)) return true;
            ignored.Add(path);
            return false;
        }

        foreach (var (path, changed) in ranges.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Reviewable(path) || Read(root, path) is not { } text) continue;
            var lines = text.Split('\n');
            var spans = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? MemberSpans(text, changed) : changed.Select(c => (c.From - Around, c.To + Around));
            foreach (var (from, to) in Merge(spans.Select(s => (Math.Max(s.Item1, 1), Math.Min(s.Item2, lines.Length)))))
                parts.Add(new(path, string.Join('\n', lines[(from - 1)..to]), from));
        }
        foreach (var path in newFiles.Where(p => !ranges.ContainsKey(p)).Order(StringComparer.OrdinalIgnoreCase))
            if (Reviewable(path) && Read(root, path) is { } text) parts.Add(new(path, text));
        return (parts, ignored);
    }

    /// <summary>
    /// A file git named, read only if it really is inside the project and no link leads to it: git tracks links as links,
    /// and a cloned repository's notes.txt linked to ~/.ssh/id_rsa must not reach the model, as the full review's walk
    /// already ensures. Null otherwise.
    /// </summary>
    static string? Read(string root, string path)
    {
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(top, path));
        if (!full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        for (var dir = new FileInfo(full).Directory; dir is not null && dir.FullName.Length > top.Length; dir = dir.Parent)
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && dir.LinkTarget is not null) return null;
        return ProjectFiles.IsLink(new FileInfo(full)) ? null : ProjectFiles.TryRead(full);
    }

    /// <summary>For each change, the members it touches, whole; or the change with lines around it when it touches none.</summary>
    static IEnumerable<(int, int)> MemberSpans(string text, List<(int From, int To)> changed)
    {
        var members = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
            // Types and namespaces hold the members: taking a whole class for a one-line change would undo the point.
            .Where(m => m is not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or DelegateDeclarationSyntax))
            .Select(m => m.GetLocation().GetLineSpan())
            .Select(s => (From: s.StartLinePosition.Line + 1, To: s.EndLinePosition.Line + 1)).ToList();
        foreach (var (from, to) in changed)
        {
            var touched = members.Where(m => m.From <= to && m.To >= from).ToList();
            if (touched.Count == 0) yield return (from - Around, to + Around);
            else foreach (var member in touched) yield return member;
        }
    }

    /// <summary>Spans sorted and joined where they overlap or touch, so no line is reviewed twice.</summary>
    static List<(int From, int To)> Merge(IEnumerable<(int From, int To)> spans)
    {
        var merged = new List<(int From, int To)>();
        foreach (var span in spans.Where(s => s.From <= s.To).OrderBy(s => s.From))
        {
            if (merged.Count > 0 && span.From <= merged[^1].To + 1) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, span.To));
            else merged.Add(span);
        }
        return merged;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")] private static partial Regex Hunk();
}
