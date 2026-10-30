using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MyOwnLLM.Cli;

public sealed record GatewayInfo(string Name, string Model, string Version);
public sealed record ChatTurn(string Role, string Content);

/// <summary>Anything that went wrong talking to the gateway, already phrased for the user. <see cref="Status"/> is null when it couldn't be reached.</summary>
public sealed class GatewayException(string message, string? cause, int? status, Exception? inner = null) : Exception(message, inner)
{
    public string? Cause { get; } = cause;
    public int? Status { get; } = status;
}

public interface IGatewayClient
{
    Task<GatewayInfo> InfoAsync(CancellationToken ct);
    IAsyncEnumerable<SseChunk> ChatAsync(IReadOnlyList<ChatTurn> history, string question, string project, CancellationToken ct, string? context = null);
    /// <summary>One answer, not streamed and outside the conversation, forced into the shape of <paramref name="jsonSchema"/>.</summary>
    Task<string> CompleteAsync(string question, string context, string jsonSchema, int maxTokens, CancellationToken ct);
    Task<(string Id, string Summary)> StatusAsync(string project, CancellationToken ct);
    Task<bool> FeedbackAsync(string id, bool accepted, CancellationToken ct);
}

public sealed class GatewayClient(HttpClient http) : IGatewayClient
{
    public async Task<GatewayInfo> InfoAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/v1/info"), ct);
        var json = await ReadJson(response, ct);
        return Unexpected(() => new GatewayInfo(json.GetProperty("name").GetString() ?? "Jazari", json.GetProperty("model").GetString() ?? "?", json.GetProperty("version").GetString() ?? "?"));
    }

    public async Task<string> CompleteAsync(string question, string context, string jsonSchema, int maxTokens, CancellationToken ct)
    {
        var body = new { messages = new[] { new { role = "user", content = question } }, max_tokens = maxTokens, context, json_schema = jsonSchema };
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(body) }, ct);
        var json = await ReadJson(response, ct);
        return Unexpected(() => json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
    }

    public async IAsyncEnumerable<SseChunk> ChatAsync(IReadOnlyList<ChatTurn> history, string question, string project, [EnumeratorCancellation] CancellationToken ct, string? context = null)
    {
        var messages = history.Select(t => new { role = t.Role, content = t.Content }).Append(new { role = "user", content = question });
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(new { messages, stream = true, context }) };
        request.Headers.Add("X-Project-Path", project);
        using var response = await SendAsync(request, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (IOException e) { throw new GatewayException("lost connection to the gateway", e.Message, null, e); }
            if (line is null) yield break;
            if (Unexpected(() => SseChunk.Parse(line)) is { } chunk) yield return chunk;
        }
    }

    public async Task<(string Id, string Summary)> StatusAsync(string project, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/status") { Content = JsonContent.Create(new { project_path = project }) }, ct);
        var json = await ReadJson(response, ct);
        return Unexpected(() => (json.GetProperty("id").GetString() ?? "", json.GetProperty("summary").GetString() ?? ""));
    }

    public async Task<bool> FeedbackAsync(string id, bool accepted, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/feedback") { Content = JsonContent.Create(new { id, accepted }) }, ct);
            return true;
        }
        catch (GatewayException e) when (e.Status == 404) { return false; }
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using (request) response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new GatewayException($"gateway not reachable at {http.BaseAddress}", e.Message, null, e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) // HttpClient.Timeout, not the user
        {
            throw new GatewayException("the gateway took too long to answer", e.Message, null, e);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            throw new GatewayException(await ErrorText(response, ct) ?? (status == 401 ? "the API key was rejected" : $"gateway returned {status} {response.ReasonPhrase}"),
                $"HTTP {status}", status);
        }
    }

    static async Task<string?> ErrorText(HttpResponseMessage response, CancellationToken ct)
    {
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("error").GetString(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static async Task<JsonElement> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return Unexpected(() => JsonDocument.Parse(body).RootElement);
    }

    /// <summary>A reply that isn't what Jazari's gateway sends (e.g. another service on the port) becomes a readable error.</summary>
    static T Unexpected<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GatewayException("unexpected response from the gateway", e.Message, null, e);
        }
    }
}
