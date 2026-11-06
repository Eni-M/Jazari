using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MyOwnLLM.Cli;

/// <summary>
/// /review's pre-pass: problems a rule finds for certain, without the model. Secrets in any file, like gitleaks, and a few C# patterns read from the syntax tree. Nothing is
/// built or run: a cloned project's build could run its own code, so analyzers that need a build are out.
/// </summary>
public static partial class StaticChecks
{
    sealed record SecretRule(string Id, Regex Pattern, string What);

    static readonly SecretRule[] Secrets =
    [
        new("aws-key", AwsKey(), "An AWS access key"),
        new("private-key", PrivateKey(), "A private key"),
        new("github-token", GitHubToken(), "A GitHub token"),
        new("slack-token", SlackToken(), "A Slack token"),
        new("stripe-key", StripeKey(), "A live Stripe secret key"),
        new("google-key", GoogleKey(), "A Google API key"),
    ];

    public static List<Finding> Scan(IEnumerable<ProjectFile> files)
    {
        List<Finding> found = [];
        foreach (var file in files)
        {
            var lines = file.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var rule in Secrets.Where(r => r.Pattern.IsMatch(lines[i])))
                    found.Add(Rule(file, i, lines[i], rule.Id, "high", $"{rule.What} is written in the file: anyone who can read the repository can use it.",
                        "Revoke it now, then load it from configuration or a secret store."));
                // Examples in documentation are expected; a password in code or config isn't.
                if (!file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && ConnectionPassword().IsMatch(lines[i]))
                    found.Add(Rule(file, i, lines[i], "connection-password", "high",
                        "A password is written in a connection string.", "Keep it out of the code: user secrets in development, a secret store in production."));
            }
            if (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) found.AddRange(CSharp(file));
        }
        return found;
    }

    static IEnumerable<Finding> CSharp(ProjectFile file)
    {
        var tree = CSharpSyntaxTree.ParseText(file.Text);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var (id, severity, problem, fix) = node switch
            {
                MethodDeclarationSyntax m when m.Modifiers.Any(SyntaxKind.AsyncKeyword) && m.ReturnType.ToString() == "void" && !IsEventHandler(m) =>
                    ("async-void", "medium", "An async void method: an exception in it can't be caught by the caller and ends the process.", "Return Task, and await it."),
                // Only a catch of everything: an empty catch of one named exception, or with a filter, is a choice someone made.
                CatchClauseSyntax c when c.Block.Statements.Count == 0 && c.Filter is null && c.Declaration?.Type.ToString() is null or "Exception" or "System.Exception" && !HasComment(c) =>
                    ("empty-catch", "medium", "An empty catch of every exception hides all errors, bugs included, so failures go unnoticed.", "Catch only the exception you expect, or log it; a comment can say why ignoring it is safe."),
                InterpolatedStringExpressionSyntax s when s.Contents.Any(c => c is InterpolationSyntax) && Sql().IsMatch(Literal(s)) =>
                    ("sql-text", "high", "SQL is built with interpolated values: if one comes from a user, it can change the query (SQL injection).", "Pass the values as parameters."),
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && b.Parent is not BinaryExpressionSyntax && Joins(b) is { } text && Sql().IsMatch(text) =>
                    ("sql-text", "high", "SQL is built by joining strings: if a value comes from a user, it can change the query (SQL injection).", "Pass the values as parameters."),
                AssignmentExpressionSyntax a when CertificateCallback().IsMatch(a.Left.ToString()) && AlwaysTrue(a.Right) =>
                    ("tls-any-cert", "high", "Every TLS certificate is accepted, so anyone between the app and the server can read and change the traffic.", "Remove the callback, or check the certificate."),
                MemberAccessExpressionSyntax ma when WeakHash().IsMatch(ma.Expression.ToString()) && ma.Name.Identifier.Text == "Create" =>
                    ("weak-hash", "low", $"{ma.Expression} is broken for security: fine for a checksum, not for passwords, signatures or tokens.", "Use SHA256, or a password hasher for passwords."),
                _ => default,
            };
            if (id is null) continue;
            // Roslyn's own lines: it also breaks at a lone \r or U+2028, where Split('\n') wouldn't.
            var line = tree.GetLineSpan(node.Span).StartLinePosition.Line;
            yield return Rule(file, line, tree.GetText().Lines[line].ToString(), id, severity, problem, fix);
        }
    }

    static Finding Rule(ProjectFile file, int index, string quote, string id, string severity, string problem, string fix) =>
        new(file.Path, file.FirstLine + index, Masked(quote).Trim(), problem, severity, fix, Verified: true) { Rule = id };

    /// <summary>
    /// The text with every secret the rules know cut to its first four characters, and a line with a private key header
    /// cut after the header (a one-line PEM carries the key on it): what a report quotes must never copy a secret.
    /// </summary>
    public static string Masked(string text) => string.Join('\n', text.Split('\n').Select(line =>
    {
        if (PrivateKey().Match(line) is { Success: true } key) return line[..(key.Index + key.Length)];
        foreach (var rule in Secrets) line = rule.Pattern.Replace(line, m => Cut(m.Value));
        return ConnectionPassword().Replace(line, m => m.Value[..(m.Groups[1].Index - m.Index)] + Cut(m.Groups[1].Value));
    }));

    static string Cut(string secret) => secret[..Math.Min(4, secret.Length)] + "********";

    static bool IsEventHandler(MethodDeclarationSyntax m) =>
        m.ParameterList.Parameters is [_, var args] && args.Type?.ToString().EndsWith("EventArgs") == true;

    /// <summary>A comment in it or after it on the line: "catch { } // the console is already gone".</summary>
    static bool HasComment(CatchClauseSyntax c) =>
        c.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));

    static string Literal(InterpolatedStringExpressionSyntax s) =>
        string.Concat(s.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));

    /// <summary>The text of a + chain that mixes a string literal with something that isn't one; null when it's all literals.</summary>
    static string? Joins(BinaryExpressionSyntax b)
    {
        var parts = Flatten(b);
        if (parts.All(p => p is LiteralExpressionSyntax) || !parts.Any(p => p.IsKind(SyntaxKind.StringLiteralExpression))) return null;
        return string.Concat(parts.OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText));
    }

    /// <summary>The terms of a + chain, left to right. A stack, not recursion: a generated file can join thousands of strings.</summary>
    static List<ExpressionSyntax> Flatten(BinaryExpressionSyntax chain)
    {
        List<ExpressionSyntax> parts = [];
        var stack = new Stack<ExpressionSyntax>([chain]);
        while (stack.TryPop(out var e))
            if (e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression)) { stack.Push(b.Right); stack.Push(b.Left); }
            else parts.Add(e);
        return parts;
    }

    static bool AlwaysTrue(ExpressionSyntax e) => e switch
    {
        LambdaExpressionSyntax { ExpressionBody: LiteralExpressionSyntax l } => l.IsKind(SyntaxKind.TrueLiteralExpression),
        LambdaExpressionSyntax { Block.Statements: [ReturnStatementSyntax { Expression: LiteralExpressionSyntax l }] } => l.IsKind(SyntaxKind.TrueLiteralExpression),
        _ => e.ToString().Contains("DangerousAcceptAnyServerCertificateValidator"),
    };

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")] private static partial Regex AwsKey();
    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY")] private static partial Regex PrivateKey();
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,})")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}")] private static partial Regex SlackToken();
    [GeneratedRegex(@"\b[sr]k_live_[A-Za-z0-9]{24,}")] private static partial Regex StripeKey();
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b")] private static partial Regex GoogleKey();
    /// <summary>
    /// Password=... in a connection string, written without spaces as they are: "password = x" is C# and "Password = @p"
    /// SQL. Not a placeholder either: {0}, &lt;password&gt;, $(VAR), %VAR%, ***, @param.
    /// </summary>
    [GeneratedRegex(@"(?i)\b(?:password|pwd)=([^;""'\s{<$%*\[@][^;""'\s]{3,})")] private static partial Regex ConnectionPassword();
    [GeneratedRegex(@"(?i)^\s*(?:SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s+\S+\s+SET|DELETE\s+FROM)\b")] private static partial Regex Sql();
    /// <summary>ServerCertificateCustomValidationCallback (HttpClientHandler), ServerCertificateValidationCallback (ServicePointManager).</summary>
    [GeneratedRegex(@"Certificate\w*ValidationCallback$")] private static partial Regex CertificateCallback();
    [GeneratedRegex(@"^(?:System\.Security\.Cryptography\.)?(?:MD5|SHA1|DES|TripleDES|RC2)$")] private static partial Regex WeakHash();
}
