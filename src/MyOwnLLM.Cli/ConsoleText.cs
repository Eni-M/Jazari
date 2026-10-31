using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>Model and server text is untrusted: strip control characters so it can't drive the terminal.</summary>
public static partial class ConsoleText
{
    public static string Sanitize(string? text) => text is null ? "" : ControlChars().Replace(text, "");

    /// <summary>
    /// A ``` line, for the screen and /copy alike. Ordinal: a culture comparison ignores zero-width characters, so a
    /// hidden "\u200B```" would count as a fence.
    /// </summary>
    public static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    // C0 (except \t and \n) and C1 controls, DEL, and bidi overrides that can disguise text. \r goes too, so "\r\n" becomes "\n".
    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F\x7F-\x9F‪-‮⁦-⁩]")]
    private static partial Regex ControlChars();
}
