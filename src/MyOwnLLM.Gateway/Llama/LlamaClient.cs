using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MyOwnLLM.Gateway.Llama;

public sealed class LlamaClient(HttpClient http) : ILlamaClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        using var response = await SendAsync("/v1/chat/completions", ChatBody(messages, maxTokens, stream: false), stream: false, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync("/v1/chat/completions", ChatBody(messages, maxTokens, stream: true), stream: true, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") yield break;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } text)
                yield return text;
        }
    }

    public async Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct)
    {
        var body = new { InputPrefix = prefix, InputSuffix = suffix, NPredict = maxTokens, CachePrompt = true };
        using var response = await SendAsync("/infill", body, stream: false, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("content").GetString() ?? "";
    }

    static object ChatBody(IReadOnlyList<ChatMessage> messages, int maxTokens, bool stream) =>
        new { Messages = messages, MaxTokens = maxTokens, CachePrompt = true, Stream = stream };

    async Task<HttpResponseMessage> SendAsync(string path, object body, bool stream, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new LlamaUnavailableException(http.BaseAddress?.ToString() ?? "(unset)", e);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
            throw new LlamaErrorException((int)response.StatusCode, await ErrorMessageAsync(response, ct));
    }

    static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetProperty("error").GetProperty("message").GetString() is { Length: > 0 } message) return message;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return $"model server returned {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
