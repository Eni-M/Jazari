using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Prompt;

public sealed record PromptParts(string? StateNote, string? RepoContext, IReadOnlyList<ChatMessage> History, string Question);

public sealed class InputTooLargeException(int estimated, int limit)
    : Exception($"input too large (~{estimated} tokens, limit {limit})")
{
    public int Estimated { get; } = estimated;
    public int Limit { get; } = limit;
}

/// <summary>
/// Builds prompts in a fixed order (system, state note, repo context, history, question)
/// so consecutive requests share a prefix and hit llama.cpp's prompt cache.
/// </summary>
public static class PromptBuilder
{
    public const string SystemPrompt =
        "You are Jazari, a concise coding assistant for ASP.NET Core, C#, .NET, networking, system design, " +
        "system architecture and cybersecurity. Prefer short answers and diffs over whole files.";

    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens)
    {
        List<ChatMessage> fixedStart = [new("system", SystemPrompt)];
        if (parts.StateNote is { Length: > 0 } note) fixedStart.Add(new("system", "Project state:\n" + note));
        if (parts.RepoContext is { Length: > 0 } context) fixedStart.Add(new("system", "Relevant code:\n" + context));
        var question = new ChatMessage("user", parts.Question);

        var limit = contextTokens - maxOutputTokens;
        var required = fixedStart.Sum(m => EstimateTokens(m.Content)) + EstimateTokens(question.Content);
        if (required > limit) throw new InputTooLargeException(required, limit);

        // Long conversations: drop the oldest history (a user/assistant pair at a time) until it fits.
        var history = parts.History.ToList();
        var historyTokens = history.Sum(m => EstimateTokens(m.Content));
        while (history.Count > 0 && required + historyTokens > limit)
        {
            var drop = Math.Min(2, history.Count);
            historyTokens -= history.Take(drop).Sum(m => EstimateTokens(m.Content));
            history.RemoveRange(0, drop);
        }

        return [.. fixedStart, .. history, question];
    }

    // ponytail: chars/4 heuristic; switch to llama-server /tokenize if limits get tight.
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
