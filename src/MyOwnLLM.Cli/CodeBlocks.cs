using System.Globalization;

namespace MyOwnLLM.Cli;

/// <summary>The fenced code blocks of an answer, as shown on screen, for /copy.</summary>
public static class CodeBlocks
{
    public static IReadOnlyList<string> Extract(string answer)
    {
        List<string> blocks = [];
        List<string>? current = null;
        // Same sanitizing as the screen, and invisible format characters too: what gets pasted must be what was shown.
        var visible = new string(ConsoleText.Sanitize(answer).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        foreach (var line in visible.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("```"))
            {
                current?.Add(line);
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
