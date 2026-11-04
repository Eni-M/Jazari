namespace MyOwnLLM.Gateway.Llama;

public sealed record ChatMessage(string Role, string Content);

/// <summary>How far llama-server has read the prompt. <see cref="Cached"/> tokens came from the prompt cache.</summary>
public sealed record PromptProgress(int Total, int Cached, int Processed);

/// <summary>llama-server's numbers for a finished answer.</summary>
public sealed record AnswerStats(int PromptTokens, int CachedTokens, double PromptPerSecond, int AnswerTokens, double AnswerPerSecond);

/// <summary>One piece of a streamed answer: text, reading progress, or the closing numbers.</summary>
public sealed record StreamPart(string? Text, PromptProgress? Progress = null, AnswerStats? Stats = null, bool CutOff = false);

/// <summary>How far a model is from answering, as llama-server's router reports it. <see cref="Failed"/>: it tried and couldn't.</summary>
public enum ModelState { Unloaded, Loading, Loaded, Failed }

public interface ILlamaClient
{
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null);
    /// <summary>Like <see cref="ChatAsync"/>, also saying whether the answer stopped at <paramref name="maxTokens"/>.</summary>
    async Task<(string Text, bool CutOff)> ChatWithEndAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null) =>
        (await ChatAsync(messages, maxTokens, ct, jsonSchema, model), false);
    IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? model = null);
    Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct, string? model = null);
    /// <summary>The context size llama-server was started with, or null when it can't say.</summary>
    Task<int?> ContextTokensAsync(CancellationToken ct);
    /// <summary>The router's models and their states; null from a plain llama-server, or when it can't say.</summary>
    Task<IReadOnlyDictionary<string, ModelState>?> ModelStatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, ModelState>?>(null);
    /// <summary>Asks the router to load a model, which unloads the other. Returns at once: <see cref="ModelStatesAsync"/> says when it's done.</summary>
    Task LoadModelAsync(string name, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class LlamaUnavailableException(string baseUrl, Exception inner)
    : Exception($"model server not reachable at {baseUrl}", inner);

/// <summary>llama-server answered with an error status (e.g. 400 when the prompt exceeds the context).</summary>
public sealed class LlamaErrorException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
