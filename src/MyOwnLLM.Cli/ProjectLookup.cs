using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MyOwnLLM.Cli;

/// <summary>The code a question names, and the files it came from, for the line above the answer.</summary>
public sealed record LookedUp(string Text, IReadOnlyList<string> Files);

/// <summary>
/// The project's code a question names, found without /read: a file by its name (OrderService.cs, src/Api/Program.cs), or a
/// C# type, method or property by its name (OrderService, RefundAsync). A small model can't search a project; handed the code
/// it's asked about, it answers from the file instead of guessing. Only inside a git repository, so a question asked in the
/// home folder never walks the whole disk, and with the same rules as /read: no secrets, no generated code, no links.
/// </summary>
public static partial class ProjectLookup
{
    /// <summary>About 2,000 tokens. It goes inside the question, after the cached start of the prompt, so it's read once per question.</summary>
    public const int MaxChars = 8000;
    const int MaxFiles = 3;
    const int MaxWalked = 5000; // ponytail: a fresh walk per question; keep an index by write time if big repos make it slow

    public static LookedUp? For(string project, string question, string? alreadyRead)
    {
        try { return Find(project, question, alreadyRead); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; } // a lookup is only a help
    }

    /// <summary>The files declaring these names, however they're spelled: a compiler's message names 'Order' for certain,
    /// where a question's plain words need the inner capital that says they're code.</summary>
    public static LookedUp? ForNames(string project, IReadOnlyList<string> names, string? alreadyRead)
    {
        try { return Find(project, [], names, alreadyRead); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static LookedUp? Find(string project, string question, string? alreadyRead)
    {
        var fileNames = FileName().Matches(question).Select(m => m.Value.Replace('\\', '/').TrimStart('.', '/')).Where(n => ProjectFiles.Wanted(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = Identifier().Matches(question).Select(m => m.Value).Where(IsCodeName).Distinct().ToList();
        return Find(project, fileNames, names, alreadyRead);
    }

    static LookedUp? Find(string project, IReadOnlyList<string> fileNames, IReadOnlyList<string> names, string? alreadyRead)
    {
        if ((fileNames.Count == 0 && names.Count == 0) || !InRepository(project)) return null;

        var paths = ProjectFiles.Paths(project).Take(MaxWalked).ToList();
        var picked = new List<(string Path, string? Member)>();
        void Pick(string path, string? member)
        {
            if (picked.Count < MaxFiles && !picked.Any(p => p.Path == path) && !Read(alreadyRead, path)) picked.Add((path, member));
        }

        foreach (var name in fileNames)
            foreach (var path in paths.Where(p => p.Equals(name, StringComparison.OrdinalIgnoreCase) || p.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)).Take(2))
                Pick(path, null);

        // Each name in the question's order: the file that declares it. A plain mention elsewhere isn't what's asked about.
        var csharp = names.Count == 0 ? [] : paths.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var name in names)
        {
            var word = new Regex($@"\b{Regex.Escape(name)}\b");
            foreach (var path in csharp)
            {
                if (ProjectFiles.TryRead(Path.Combine(project, path)) is not { } text || !word.IsMatch(text) || !Declares(text, name)) continue;
                Pick(path, name);
                break;
            }
        }
        if (picked.Count == 0) return null;

        var result = new StringBuilder();
        var files = new List<string>();
        foreach (var (path, member) in picked)
        {
            if (ProjectFiles.TryRead(Path.Combine(project, path)) is not { } text) continue;
            var left = MaxChars - result.Length;
            var piece = $"--- {path} ---\n{text}\n";
            if (piece.Length > left) piece = Part(path, text, member, left);
            if (piece.Length == 0) continue;
            result.Append(piece);
            files.Add(path);
        }
        return files.Count == 0 ? null : new(result.ToString(), files);
    }

    /// <summary>What fits of a file too big to send whole: the member asked about with its line numbers, else the outline.</summary>
    static string Part(string path, string text, string? member, int left)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        if (member is not null && root.DescendantNodes().FirstOrDefault(n => NameOf(n) == member) is { } node)
        {
            var lines = node.SyntaxTree.GetLineSpan(node.Span);
            var header = $"--- {path} (lines {lines.StartLinePosition.Line + 1}-{lines.EndLinePosition.Line + 1}) ---\n";
            var code = node.ToString();
            var room = left - header.Length - 1;
            return room < 200 ? "" : header + (code.Length > room ? code[..room] : code) + "\n";
        }
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return "";
        var outline = $"--- {path} (outline: signatures only, the file is too big) ---\n{string.Join('\n', RepoMap.Members(text))}\n";
        return outline.Length <= left ? outline : "";
    }

    static bool Declares(string text, string name) => CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().Any(n => NameOf(n) == name);

    static string? NameOf(Microsoft.CodeAnalysis.SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        DelegateDeclarationSyntax d => d.Identifier.Text,
        _ => null,
    };

    /// <summary>OrderService, RefundAsync, IOrderRepository, getUser: a capital after the first letter, and a lowercase one.</summary>
    static bool IsCodeName(string word) => word.Length >= 4 && word.Skip(1).Any(char.IsUpper) && word.Any(char.IsLower);

    static bool Read(string? alreadyRead, string path) =>
        alreadyRead is not null && (alreadyRead.Contains($"--- {path} ---", StringComparison.OrdinalIgnoreCase) || alreadyRead.Contains($"--- {Path.GetFileName(path)} ---", StringComparison.OrdinalIgnoreCase));

    static bool InRepository(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) return true;
        return false;
    }

    [GeneratedRegex(@"(?<![\w.])[\w\-./\\]*[\w\-]\.[A-Za-z]{2,7}\b")] private static partial Regex FileName();
    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")] private static partial Regex Identifier();
}
