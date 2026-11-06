using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MyOwnLLM.Cli;

/// <summary>
/// The shape of C# files without their bodies: each type and the signatures of its members, the way Aider's repo map
/// shows a project the model can't read in full. /read gives
/// it the files that didn't fit, so the model knows what exists and where, at a fraction of the tokens.
/// </summary>
public static partial class RepoMap
{
    /// <summary>The outline of <paramref name="files"/> in their order (riskiest first), whole files only, up to <paramref name="maxChars"/>.</summary>
    public static (string? Text, int Files) Outline(IEnumerable<ProjectFile> files, int maxChars)
    {
        var text = new StringBuilder();
        var count = 0;
        foreach (var file in files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var members = Members(file.Text);
            if (members.Count == 0) continue;
            var block = $"{file.Path}\n{string.Join('\n', members)}\n";
            if (text.Length + block.Length > maxChars) continue; // a smaller file further down may still fit
            text.Append(block);
            count++;
        }
        return (text.Length == 0 ? null : text.ToString(), count);
    }

    /// <summary>"class OrderService", "  Task RefundAsync(int orderId, CancellationToken ct)", indented by nesting.</summary>
    public static List<string> Members(string code)
    {
        var lines = new List<string>();
        foreach (var node in CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes())
        {
            var signature = node switch
            {
                TypeDeclarationSyntax type => $"{type.Keyword.Text} {type.Identifier.Text}{type.TypeParameterList}{type.ParameterList}{Bases(type.BaseList)}",
                EnumDeclarationSyntax e => $"enum {e.Identifier.Text}",
                DelegateDeclarationSyntax d => $"delegate {d.ReturnType} {d.Identifier.Text}{d.ParameterList}",
                MethodDeclarationSyntax m => $"{m.ReturnType} {m.Identifier.Text}{m.TypeParameterList}{m.ParameterList}",
                ConstructorDeclarationSyntax c => $"{c.Identifier.Text}{c.ParameterList}",
                PropertyDeclarationSyntax p => $"{p.Type} {p.Identifier.Text}",
                _ => null,
            };
            if (signature is null) continue;
            var depth = node.Ancestors().Count(a => a is BaseTypeDeclarationSyntax);
            lines.Add(new string(' ', 2 * (depth + 1)) + Spaces().Replace(signature, " ").Trim());
        }
        return lines;
    }

    static string Bases(BaseListSyntax? bases) => bases is null ? "" : " : " + string.Join(", ", bases.Types.Select(t => t.Type.ToString()));

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
