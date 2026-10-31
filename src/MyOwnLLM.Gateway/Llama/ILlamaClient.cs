namespace MyOwnLLM.Gateway.Llama;

public sealed record ChatMessage(string Role, string Content);

/// <summary>How far llama-server has read the prompt. <see cref="Cached"/> tokens came from the prompt cache.</summary>
public sealed record PromptProgress(int Total, int Cached, int Processed);

/// <summary>llama-server's numbers for a finished answer.</summary>
public sealed record AnswerStats(int PromptTokens, int CachedTokens, double PromptPerSecond, int AnswerTokens, double AnswerPerSecond);

/// <summary>One piece of a streamed answer: text, reading progress, or the closing numbers.</summary>
public sealed record StreamPart(string? Text, PromptProgress? Progress = null, AnswerStats? Stats = null, bool CutOff = false);

public interface ILlamaClient
{
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null);
    /// <summary>Like <see cref="ChatAsync"/>, also saying whether the answer stopped at <paramref name="maxTokens"/>.</summary>
    async Task<(string Text, bool CutOff)> ChatWithEndAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null) =>
        (await ChatAsync(messages, maxTokens, ct, jsonSchema), false);
    IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct);
    Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct);
    /// <summary>The context size llama-server was started with, or null when it can't say.</summary>
    Task<int?> ContextTokensAsync(CancellationToken ct);
}

public sealed class LlamaUnavailableException(string baseUrl, Exception inner)
    : Exception($"model server not reachable at {baseUrl}", inner);

/// <summary>llama-server answered with an error status (e.g. 400 when the prompt exceeds the context).</summary>
public sealed class LlamaErrorException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
