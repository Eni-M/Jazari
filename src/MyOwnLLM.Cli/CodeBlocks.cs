namespace MyOwnLLM.Cli;

/// <summary>The fenced code blocks of an answer, exactly as the model wrote them, for /copy.</summary>
public static class CodeBlocks
{
    public static IReadOnlyList<string> Extract(string answer)
    {
        List<string> blocks = [];
        List<string>? current = null;
        foreach (var line in answer.Replace("\r\n", "\n").Split('\n'))
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
