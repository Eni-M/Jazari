using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

public sealed record BuildError(string File, int Line, string Code, string Message);

/// <summary>The C# compiler's errors in what dotnet build prints. Everything else in the output, warnings included, is ignored.</summary>
public static partial class BuildErrors
{
    public static IReadOnlyList<BuildError> Parse(string output) =>
        Line().Matches(output)
            .Select(m => new BuildError(m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), m.Groups["code"].Value, m.Groups["message"].Value))
            .Distinct(new SamePlace()).ToList(); // one project with two target frameworks prints each error twice

    /// <summary>Errors in <paramref name="after"/> that <paramref name="before"/> didn't have. Lines aren't compared: an edit moves every line below it.</summary>
    public static IReadOnlyList<BuildError> New(IReadOnlyList<BuildError> before, IReadOnlyList<BuildError> after)
    {
        var known = before.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. after.Where(e => !known.Contains(Key(e)))];
    }

    static string Key(BuildError e) => $"{e.File}\n{e.Code}\n{e.Message}";

    sealed class SamePlace : IEqualityComparer<BuildError>
    {
        public bool Equals(BuildError? a, BuildError? b) => a is not null && b is not null && a.Line == b.Line && string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(BuildError e) => HashCode.Combine(e.Line, e.Code, StringComparer.OrdinalIgnoreCase.GetHashCode(e.File));
    }

    [GeneratedRegex(@"^\s*(?<file>.+?)\((?<line>\d+),\d+\): error (?<code>CS\d+): (?<message>.*?)(?: \[[^\]]*\])?\s*$", RegexOptions.Multiline)]
    private static partial Regex Line();
}
