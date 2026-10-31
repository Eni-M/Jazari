using System.Globalization;

namespace MyOwnLLM.Cli;

/// <summary>The fenced code blocks of an answer, as shown on screen, for /copy.</summary>
public static class CodeBlocks
{
    public static IReadOnlyList<string> Extract(string answer)
    {
        List<string> blocks = [];
        List<string>? current = null;
        // Fences are found exactly as the screen finds them (a "\u200B```" line is text there, so it's text here too); then
        // the invisible format characters come out of what's copied: what gets pasted must be what was shown.
        foreach (var line in ConsoleText.Sanitize(answer).Split('\n'))
        {
            if (!ConsoleText.IsFence(line))
            {
                current?.Add(new string(line.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()));
                continue;
            }
            if (current is null) { current = []; continue; }
            blocks.Add(string.Join('\n', current));
            current = null;
        }
        if (current is not null) blocks.Add(string.Join('\n', current)); // the answer was stopped mid-block
        return blocks;
    }
}
