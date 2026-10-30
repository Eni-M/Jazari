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
