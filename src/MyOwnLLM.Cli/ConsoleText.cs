using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>Model and server text is untrusted: strip control characters so it can't drive the terminal.</summary>
public static partial class ConsoleText
{
    public static string Sanitize(string? text) => text is null ? "" : ControlChars().Replace(text, "");

    // C0 (except \t and \n) and C1 controls, plus DEL. \r goes too, so "\r\n" becomes "\n".
    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F\x7F-\x9F]")]
    private static partial Regex ControlChars();
}
