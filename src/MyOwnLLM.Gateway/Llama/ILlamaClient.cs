namespace MyOwnLLM.Gateway.Llama;

public sealed record ChatMessage(string Role, string Content);

public interface ILlamaClient
{
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct);
    IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct);
    Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct);
}

public sealed class LlamaUnavailableException(string baseUrl, Exception inner)
    : Exception($"model server not reachable at {baseUrl}", inner);

/// <summary>llama-server answered with an error status (e.g. 400 when the prompt exceeds the context).</summary>
public sealed class LlamaErrorException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
