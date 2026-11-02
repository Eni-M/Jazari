using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Prompt;

/// <param name="Passages">The law book's passages for the question, if any.</param>
public sealed record PromptParts(string? StateNote, string? RepoContext, IReadOnlyList<ChatMessage> History, string Question, IReadOnlyList<Passage>? Passages = null);

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
        // Tried on the real model (2026-11-02). "As few words as the question needs" gave one-word answers that were often
        // wrong ("Use ScopedServiceProvider"); asking for sentences gave AddScoped with a sample, at 40-160 tokens. Every
        // answer token costs about 1/6 s on a T480, so "brief but complete" isn't worth it: it ran to the 220-token cap.
        // Avoid phrases the model can echo as headings: "add only the details" came back as "Additional Details:" with
        // invented bullets. The facts are here because the model knows nothing about itself and assumes an online service.
        "You are Jazari, a coding assistant for ASP.NET Core, C#, .NET, networking, system design, system architecture " +
        "and cybersecurity. You run entirely on this PC, so usage is unlimited and free. There is no account, subscription, " +
        "website or support team, and you can't reach the internet. Answer directly in one to three complete sentences, plus " +
        "code when it helps. Show code as the smallest change, never a whole file unless asked. No preamble, no headings, no " +
        "closing summary. If you don't know, say you don't know; never make up links, names or products.";

    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens) =>
        Build(parts, contextTokens, maxOutputTokens, out _, out _);

    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens, out int droppedMessages) =>
        Build(parts, contextTokens, maxOutputTokens, out droppedMessages, out _);

    /// <param name="droppedMessages">How many of the oldest history messages didn't fit.</param>
    /// <param name="usedPassages">Whether the law book's passages went in, so the answer can name its source.</param>
    public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens, out int droppedMessages, out bool usedPassages)
    {
        List<ChatMessage> fixedStart = [new("system", SystemPrompt)];
        if (parts.StateNote is { Length: > 0 } note) fixedStart.Add(new("system", "Notes on this project, possibly out of date. Use them only when asked about the project, and never repeat them otherwise:\n" + note));
        if (parts.RepoContext is { Length: > 0 } context) fixedStart.Add(new("system", "Relevant code:\n" + context));

        // The law book's passages and the user's matching earlier lines go inside the question, never into history, so the
        // prompt's start stays cached. The whole history is searched, so a line the cut below drops is still found.
        var pin = ConversationSearch.Pin(parts.History, parts.Question);
        var docs = parts.Passages is { Count: > 0 } passages ? string.Join("\n\n", passages.Select(p => p.Text)) : null;
        var question = new ChatMessage("user",
            (docs is null ? "" : "From the .NET docs:\n" + docs + "\n\n") +
            (pin is null ? "" : "From earlier in this conversation:\n" + pin + "\n\n") + parts.Question);
        usedPassages = docs is not null;

        var limit = contextTokens - maxOutputTokens;
        var fixedTokens = fixedStart.Sum(m => EstimateTokens(m.Content));
        var required = fixedTokens + EstimateTokens(question.Content);
        if (required > limit && (pin ?? docs) is not null) // only the additions are too much: ask without them rather than refuse
            (question, required, usedPassages) = (new("user", parts.Question), fixedTokens + EstimateTokens(parts.Question), false);
        if (required > limit) throw new InputTooLargeException(required, limit);

        // Long conversations: drop the oldest history until it fits. The cut moves in steps of DropStep messages counted from
        // the start, so it stays put for several turns: cutting one more pair every turn would change the prompt's start
        // each time, and the model would read the whole conversation again for every answer.
        var history = parts.History.ToList();
        var historyTokens = history.Sum(m => EstimateTokens(m.Content));
        var drop = 0;
        while (drop < history.Count && required + historyTokens > limit) historyTokens -= EstimateTokens(history[drop++].Content);
        droppedMessages = drop > 0 ? Math.Min(history.Count, (drop + DropStep - 1) / DropStep * DropStep) : 0;
        history.RemoveRange(0, droppedMessages);

        return [.. fixedStart, .. history, question];
    }

    const int DropStep = 8; // four question/answer pairs

    // ponytail: chars/4 heuristic; switch to llama-server /tokenize if limits get tight.
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
