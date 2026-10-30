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
        // Every answer token costs about 1/6 s on a T480: this wording cut answers by 68% on five test questions
        // (762 to 246 tokens) with the same findings. Avoid phrases the model can echo as headings.
        "You are Jazari, a coding assistant for ASP.NET Core, C#, .NET, networking, system design, system architecture " +
        "and cybersecurity. Answer in as few words as the question needs. Lead with the answer, then add only the details " +
        "needed to act on it. Show code as the smallest change, never a whole file unless asked. No preamble, no restating " +
        "the question, no closing summary.";

    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens)
    {
        List<ChatMessage> fixedStart = [new("system", SystemPrompt)];
        if (parts.StateNote is { Length: > 0 } note) fixedStart.Add(new("system", "Project state:\n" + note));
        if (parts.RepoContext is { Length: > 0 } context) fixedStart.Add(new("system", "Relevant code:\n" + context));
        var question = new ChatMessage("user", parts.Question);

        var limit = contextTokens - maxOutputTokens;
        var required = fixedStart.Sum(m => EstimateTokens(m.Content)) + EstimateTokens(question.Content);
        if (required > limit) throw new InputTooLargeException(required, limit);

        // Long conversations: drop the oldest history until it fits. The cut moves in steps of DropStep messages counted from
        // the start, so it stays put for several turns: cutting one more pair every turn would change the prompt's start
        // each time, and the model would read the whole conversation again for every answer.
        var history = parts.History.ToList();
        var historyTokens = history.Sum(m => EstimateTokens(m.Content));
        var drop = 0;
        while (drop < history.Count && required + historyTokens > limit) historyTokens -= EstimateTokens(history[drop++].Content);
        if (drop > 0) history.RemoveRange(0, Math.Min(history.Count, (drop + DropStep - 1) / DropStep * DropStep));

        return [.. fixedStart, .. history, question];
    }

    const int DropStep = 8; // four question/answer pairs

    // ponytail: chars/4 heuristic; switch to llama-server /tokenize if limits get tight.
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
