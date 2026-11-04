using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MyOwnLLM.Gateway.Checks;

public enum CheckState { Compiles, DoesNotCompile, NotChecked }

/// <param name="Block">The block's number, counted over every fence as /copy counts them.</param>
/// <param name="Why">For a block that wasn't checked, what's in the way.</param>
public sealed record BlockCheck(int Block, CheckState State, IReadOnlyList<string> Errors, string? Why = null);

/// <summary>
/// Compiles the C# in an answer against the SDK's reference packs, to catch APIs the model made up. Nothing is emitted or
/// run, and no generators or analyzers load, so model-written code can't do anything here. Answers are snippets, so only
/// "this member doesn't exist on a real type" counts; a missing variable or semicolon says nothing about the API.
/// </summary>
public sealed class SnippetChecker(string? dotnetRoot = null)
{
    const int MaxChars = 20_000;

    /// <summary>Names that really exist, in NuGet packages the reference packs don't hold. Without this, correct EF Core code
    /// reads as made up. ponytail: a fixed list of the common ones; read the project's obj/project.assets.json if answers
    /// about other packages show up flagged.</summary>
    static readonly HashSet<string> PackageNames =
    [
        "AddDbContext", "AddDbContextPool", "AddDbContextFactory", "UseSqlServer", "UseNpgsql", "UseSqlite", "UseInMemoryDatabase", "DbContext", "DbSet", "DbContextOptions",
        "DbContextOptionsBuilder", "ModelBuilder", "EntityTypeBuilder", "IEntityTypeConfiguration", "FromSql", "FromSqlRaw", "FromSqlInterpolated", "Include", "ThenInclude",
        "AsNoTracking", "ToListAsync", "FirstOrDefaultAsync", "SingleOrDefaultAsync", "AnyAsync", "CountAsync", "SaveChangesAsync", "ExecuteUpdateAsync", "ExecuteDeleteAsync",
        "AddDefaultUI", "AddJwtBearer", "AddSwaggerGen", "UseSwagger", "UseSwaggerUI", "AddMediatR", "AddAutoMapper", "AddStackExchangeRedisCache", "AddHealthChecksUI",
        "Fact", "Theory", "InlineData", "Assert", "Mock", "Dapper", "SqlConnection", "SqlCommand", "NpgsqlConnection", "Key", "Required",
    ];

    /// <summary>The names answers use without declaring, with their real types, so `services.AddFoo()` can be seen to be wrong.</summary>
    static readonly Dictionary<string, string> KnownVariables = new()
    {
        ["builder"] = "WebApplicationBuilder", ["app"] = "WebApplication", ["services"] = "IServiceCollection",
        ["configuration"] = "IConfiguration", ["logger"] = "ILogger",
    };

    static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    readonly Lazy<IReadOnlyList<MetadataReference>> _references = new(() => LoadReferences(dotnetRoot ?? DotnetRoot()));
    SyntaxTree? _usings;
    readonly object _loadGate = new();

    /// <summary>A global using for every namespace in the packs. Answers leave usings out, and StringBuilder or IOptionsSnapshot
    /// without one isn't a made-up API. An extension method still has to exist on its type, wherever it's from.</summary>
    SyntaxTree Usings() => _usings ??= CSharpSyntaxTree.ParseText(string.Concat(Namespaces(CSharpCompilation.Create("names", references: _references.Value).GlobalNamespace)
        .Select(n => $"global using {n};\n")), Parse);

    static IEnumerable<string> Namespaces(INamespaceSymbol parent) => parent.GetNamespaceMembers().SelectMany(n =>
        n.GetTypeMembers().Any(t => t.DeclaredAccessibility == Accessibility.Public) ? Namespaces(n).Prepend(n.ToDisplayString()) : Namespaces(n));

    /// <summary>Loads the references in the background, so the first answer doesn't wait for them. Safe to call twice.</summary>
    public void Warm() => _ = Task.Run(() => { try { Load(); } catch (Exception) { } }); // only a head start

    /// <summary>Reads the reference packs, a second or two the first time. Time limits are for checks, not for this.</summary>
    public void Load()
    {
        lock (_loadGate) if (_references.Value.Count > 0) Compile("int warm = 0;", CancellationToken.None);
    }

    public IReadOnlyList<BlockCheck> Check(string answer, CancellationToken ct)
    {
        var checks = new List<BlockCheck>();
        var number = 0;
        foreach (var (language, code) in Blocks(answer))
        {
            number++;
            if (language.ToLowerInvariant() is not ("csharp" or "cs" or "c#")) checks.Add(new(number, CheckState.NotChecked, []));
            else if (code.Length > MaxChars) checks.Add(new(number, CheckState.NotChecked, [], "too long to check"));
            else if (_references.Value.Count == 0) checks.Add(new(number, CheckState.NotChecked, [], "the .NET reference packs weren't found"));
            else
            {
                try { checks.Add(Compile(code, ct) with { Block = number }); }
                catch (OperationCanceledException) { checks.Add(new(number, CheckState.NotChecked, [], "took too long")); }
            }
        }
        return checks;
    }

    /// <summary>The code of block <paramref name="number"/> (from 1), or null when there's no such block.</summary>
    public static string? BlockCode(string answer, int number) =>
        Blocks(answer) is var blocks && number >= 1 && number <= blocks.Count ? blocks[number - 1].Code : null;

    /// <summary>The answer with block <paramref name="number"/>'s code replaced, and everything around it as it was.</summary>
    public static string ReplaceBlock(string answer, int number, string code)
    {
        var lines = answer.Replace("\r\n", "\n").Split('\n').ToList();
        var (seen, open) = (0, -1);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            if (open < 0) { open = i; seen++; continue; }
            if (seen == number)
            {
                lines.RemoveRange(open + 1, i - open - 1);
                lines.Insert(open + 1, code);
                return string.Join('\n', lines);
            }
            open = -1;
        }
        if (open >= 0 && seen == number) // stopped mid-block
        {
            lines.RemoveRange(open + 1, lines.Count - open - 1);
            lines.Insert(open + 1, code);
            lines.Add("```");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Every fenced block with its language, as the screen finds them: an answer stopped mid-block still counts it.</summary>
    static List<(string Language, string Code)> Blocks(string answer)
    {
        var blocks = new List<(string, string)>();
        string? language = null;
        List<string> lines = [];
        foreach (var line in answer.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (language is not null) lines.Add(line);
                continue;
            }
            if (language is null) { language = line.TrimStart()[3..].Trim(); continue; }
            blocks.Add((language, string.Join('\n', lines)));
            language = null;
            lines = [];
        }
        if (language is not null) blocks.Add((language, string.Join('\n', lines)));
        return blocks;
    }

    BlockCheck Compile(string code, CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(Shaped(code), Parse, cancellationToken: ct);
        var compilation = CSharpCompilation.Create("snippet", [tree, Usings()], _references.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        var errors = compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error && d.Id is "CS0117" or "CS1061" or "CS0246" or "CS0234").ToList();

        // A using the packs don't hold means the code leans on a package: nothing below it can be judged.
        var missingUsing = errors.Where(e => e.Location.SourceTree == tree && tree.GetRoot(ct).FindNode(e.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
            .Select(e => tree.GetRoot(ct).FindNode(e.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>()!.Name?.ToString()).FirstOrDefault();
        if (missingUsing is not null) return new(0, CheckState.NotChecked, [], $"needs {missingUsing}");
        var package = errors.Select(e => NameIn(e, tree, ct)).FirstOrDefault(n => n is not null && PackageNames.Contains(n));
        if (package is not null) return new(0, CheckState.NotChecked, [], $"needs a package for {package}");

        // An unknown plain type name doesn't count: on saved answers nearly every one was an example (ISomeService, Person) or the
        // user's own type, and no check can tell those from an invented one. A wrong member on a real type can't be an example.
        var made = errors.Where(e => e.Id != "CS0246").ToList();
        return made.Count == 0
            ? new(0, CheckState.Compiles, [])
            : new(0, CheckState.DoesNotCompile, made.Select(e => e.GetMessage()).Distinct().Take(5).ToList());
    }

    static string? NameIn(Diagnostic error, SyntaxTree tree, CancellationToken ct) =>
        error.Location.SourceTree != tree ? null : tree.GetRoot(ct).FindToken(error.Location.SourceSpan.Start).ValueText;

    /// <summary>The snippet as a program: types and statements as they are (top-level statements allow both), lone members
    /// inside a class, and the usual undeclared variables declared with their real types first.</summary>
    static string Shaped(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code, Parse).GetRoot();
        var usings = string.Concat(root.Usings.Select(u => u.ToFullString()));
        var body = code[(root.Usings.LastOrDefault()?.FullSpan.End ?? 0)..];
        var hasNamespace = root.Members.Any(m => m is BaseNamespaceDeclarationSyntax);
        var declared = root.DescendantNodes().Select(n => n switch
        {
            VariableDeclaratorSyntax v => v.Identifier.Text,
            ParameterSyntax p => p.Identifier.Text,
            ForEachStatementSyntax f => f.Identifier.Text,
            SingleVariableDesignationSyntax s => s.Identifier.Text,
            _ => null,
        }).OfType<string>().ToHashSet();
        var used = root.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).ToHashSet();
        var variables = hasNamespace ? "" : string.Concat(KnownVariables.Where(k => used.Contains(k.Key) && !declared.Contains(k.Key)).Select(k => $"{k.Value} {k.Key} = default;\n"));
        var loneMembers = root.Members.Any(m => m is not (GlobalStatementSyntax or BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or DelegateDeclarationSyntax));
        return loneMembers ? $"{usings}class Snippet__ {{\n{variables}{body}\n}}" : $"{usings}{variables}{body}";
    }

    /// <summary>The dotnet folder this gateway runs from: shared/Microsoft.NETCore.App/10.0.x/ is three folders below it.</summary>
    static string DotnetRoot() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));

    static IReadOnlyList<MetadataReference> LoadReferences(string root)
    {
        var references = new List<MetadataReference>();
        foreach (var pack in new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" })
        {
            var folder = Path.Combine(root, "packs", pack);
            if (!Directory.Exists(folder)) return [];
            var newest = Directory.GetDirectories(folder).Where(d => Version.TryParse(Path.GetFileName(d), out var v) && v.Major == Environment.Version.Major)
                .OrderBy(d => Version.Parse(Path.GetFileName(d))).LastOrDefault();
            var dlls = newest is null ? null : Path.Combine(newest, "ref", $"net{Environment.Version.Major}.0");
            if (dlls is null || !Directory.Exists(dlls)) return [];
            references.AddRange(Directory.GetFiles(dlls, "*.dll").Select(f => MetadataReference.CreateFromFile(f)));
        }
        return references;
    }
}
