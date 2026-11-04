// For eval/repo_bench.py: finds a C# method with Roslyn, so a method in a repo can be blanked out and written again.
//   Blank blank <file> <method>        replaces its body with a throw and writes the original method to stdout
//   Blank put <file> <method> <answer> replaces the whole method with the text in the file <answer>
//   Blank list <folder>                one JSON line per candidate method: file, method, class, lines
// A missing or ambiguous method exits 2.
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const string Throw = "throw new System.NotImplementedException();";

static MethodDeclarationSyntax? Find(string text, string name)
{
    var found = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == name).ToList();
    return found.Count == 1 ? found[0] : null;
}

switch (args)
{
    case ["blank", var file, var name]:
    {
        var text = File.ReadAllText(file);
        if (Find(text, name) is not { } method) return 2;
        var start = method.Body?.Span.Start ?? method.ExpressionBody!.Span.Start;
        var end = method.Body?.Span.End ?? method.SemicolonToken.Span.End;
        File.WriteAllText(file, text[..start] + "{ " + Throw + " }" + text[end..]);
        Console.Write(method.ToFullString());
        return 0;
    }
    case ["put", var file, var name, var answer]:
    {
        var text = File.ReadAllText(file);
        if (Find(text, name) is not { } method) return 2;
        File.WriteAllText(file, text[..method.Span.Start] + File.ReadAllText(answer) + text[method.Span.End..]);
        return 0;
    }
    case ["list", var folder]:
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, '/').Any(p => p is "bin" or "obj" or "Migrations" or ".worktrees")) continue;
            var methods = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
            foreach (var m in methods.Where(m => m.Body is { Statements.Count: >= 2 } && m.Parent is ClassDeclarationSyntax && methods.Count(o => o.Identifier.Text == m.Identifier.Text) == 1))
            {
                var lines = m.GetLocation().GetLineSpan();
                Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetRelativePath(folder, file), method = m.Identifier.Text, @class = ((ClassDeclarationSyntax)m.Parent!).Identifier.Text, lines = lines.EndLinePosition.Line - lines.StartLinePosition.Line + 1 }));
            }
        }
        return 0;
    default:
        Console.Error.WriteLine("usage: Blank blank|put|list ...");
        return 2;
}
