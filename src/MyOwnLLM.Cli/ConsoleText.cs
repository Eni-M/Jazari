using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>Model and server text is untrusted: strip control characters so it can't drive the terminal.</summary>
public static partial class ConsoleText
{
    public static string Sanitize(string? text) => text is null ? "" : ControlChars().Replace(text, "");

    /// <summary>Windows consoles only act on escape sequences when asked to; the whole interface draws with them.</summary>
    public static void EnableEscapes()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return;
        const uint VirtualTerminalProcessing = 4;
        var handle = GetStdHandle(-11); // standard output
        if (GetConsoleMode(handle, out var mode)) SetConsoleMode(handle, mode | VirtualTerminalProcessing);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GetStdHandle(int which);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle, uint mode);

    /// <summary>
    /// A ``` line, for the screen and /copy alike. Ordinal: a culture comparison ignores zero-width characters, so a
    /// hidden "\u200B```" would count as a fence.
    /// </summary>
    public static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    // C0 (except \t and \n) and C1 controls, DEL, and bidi overrides that can disguise text. \r goes too, so "\r\n" becomes "\n".
    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F\x7F-\x9F‪-‮⁦-⁩]")]
    private static partial Regex ControlChars();
}
