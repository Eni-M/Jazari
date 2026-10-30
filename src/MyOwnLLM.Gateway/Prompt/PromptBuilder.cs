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
        "You are MyOwnLLM, a concise coding assistant for ASP.NET Core, C#, .NET, networking, system design, " +
        "system architecture and cybersecurity. Prefer short answers and diffs over whole files.";

    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens)
    {
        List<ChatMessage> messages = [new("system", SystemPrompt)];
        if (parts.StateNote is { Length: > 0 } note) messages.Add(new("system", "Project state:\n" + note));
        if (parts.RepoContext is { Length: > 0 } context) messages.Add(new("system", "Relevant code:\n" + context));
        messages.AddRange(parts.History);
        messages.Add(new("user", parts.Question));

        var estimated = messages.Sum(m => EstimateTokens(m.Content));
        var limit = contextTokens - maxOutputTokens;
        if (estimated > limit) throw new InputTooLargeException(estimated, limit);
        return messages;
    }

    // ponytail: chars/4 heuristic; switch to llama-server /tokenize if limits get tight.
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
