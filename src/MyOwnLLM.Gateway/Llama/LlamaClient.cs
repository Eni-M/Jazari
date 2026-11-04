using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MyOwnLLM.Gateway.Llama;

/// <param name="keyFile">llama-server's API key, which jazari writes and starts it with, so only the gateway can call it.
/// Read on every request: a key made again, or a server started before the gateway, never leaves the two out of step. No
/// file (eval/ runs its own server without one): no key is sent.</param>
public sealed class LlamaClient(HttpClient http, string? keyFile = null) : ILlamaClient
{
    // Nulls stay out, so with a plain llama-server the request is what it always was.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    const double CodeTemperature = 0.2; // llama-server's default of 0.8 is meant for prose; code wants the likeliest answer
    // llama-server's own defaults at b11982, written out so an upgrade that changes them can't change Jazari's answers
    // (reports/Jazari CLI and model enhancements.md, section 2). Changing them needs an eval/ run.
    const int TopK = 40;
    const double TopP = 0.95, MinP = 0.05;

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null) =>
        (await ChatWithEndAsync(messages, maxTokens, ct, jsonSchema, model)).Text;

    public async Task<(string Text, bool CutOff)> ChatWithEndAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null)
    {
        var body = jsonSchema is null
            ? ChatBody(messages, maxTokens, stream: false, model)
            : new { Model = model, Messages = messages, MaxTokens = maxTokens, CachePrompt = true, Stream = false, Temperature = CodeTemperature, TopK, TopP, MinP, JsonSchema = JsonDocument.Parse(jsonSchema).RootElement };
        using var response = await SendAsync("/v1/chat/completions", body, stream: false, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var cutOff = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length";
        return (choice.GetProperty("message").GetProperty("content").GetString() ?? "", cutOff);
    }

    public async IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct, string? model = null)
    {
        // return_progress: llama-server reports how far it has read the prompt, which takes minutes on a CPU.
        var body = new { Model = model, Messages = messages, MaxTokens = maxTokens, CachePrompt = true, Stream = true, Temperature = CodeTemperature, TopK, TopP, MinP, ReturnProgress = true };
        using var response = await SendAsync("/v1/chat/completions", body, stream: true, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // An error after streaming began (out of memory, a decode failure) must not pass for a finished answer.
            if (line.StartsWith("error:")) throw new LlamaErrorException(500, StreamError(line["error:".Length..].Trim()));
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") yield break;
            foreach (var part in ReadChunk(data)) yield return part;
        }
    }

    /// <summary>One streamed chunk. Progress and timings are llama-server extras, so anything missing is just skipped.</summary>
    public static List<StreamPart> ReadChunk(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) throw new LlamaErrorException(500, StreamError(data));
        List<StreamPart> parts = [];
        if (root.TryGetProperty("prompt_progress", out var p) && p.ValueKind == JsonValueKind.Object)
            parts.Add(new(null, Progress: new(Int(p, "total"), Int(p, "cache"), Int(p, "processed"))));
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } text)
            parts.Add(new(text));
        if (root.TryGetProperty("choices", out var ends) && ends.ValueKind == JsonValueKind.Array && ends.GetArrayLength() > 0
            && ends[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
            parts.Add(new(null, CutOff: true));
        if (root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
            parts.Add(new(null, Stats: new(Int(t, "prompt_n") + Int(t, "cache_n"), Int(t, "cache_n"), Number(t, "prompt_per_second"),
                Int(t, "predicted_n"), Number(t, "predicted_per_second"))));
        return parts;
    }

    /// <summary>The message from an in-stream error, {"error":{"message":"..."}} or {"error":"..."}.</summary>
    static string StreamError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e : doc.RootElement;
            var message = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (message is { Length: > 0 }) return $"the model server failed while answering: {message}";
        }
        catch (JsonException) { }
        return "the model server failed while answering";
    }

    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    static double Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    public async Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct, string? model = null)
    {
        var body = new { Model = model, InputPrefix = prefix, InputSuffix = suffix, NPredict = maxTokens, CachePrompt = true };
        using var response = await SendAsync("/infill", body, stream: false, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("content").GetString() ?? "";
    }

    public async Task<int?> ContextTokensAsync(CancellationToken ct)
    {
        try
        {
            var size = await ContextOfAsync("/props", ct);
            if (size is not null) return size;
            // A router has no size of its own: ask the model it has loaded.
            var loaded = (await ModelStatesAsync(ct))?.FirstOrDefault(s => s.Value is ModelState.Loaded).Key;
            return loaded is null ? null : await ContextOfAsync($"/props?model={Uri.EscapeDataString(loaded)}", ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException) { return null; }
    }

    async Task<int?> ContextOfAsync(string path, CancellationToken ct)
    {
        using var request = WithKey(new HttpRequestMessage(HttpMethod.Get, path));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var settings = root.TryGetProperty("default_generation_settings", out var s) ? s : root;
        return settings.TryGetProperty("n_ctx", out var n) && n.TryGetInt32(out var tokens) && tokens > 0 ? tokens : null;
    }

    public async Task<IReadOnlyDictionary<string, ModelState>?> ModelStatesAsync(CancellationToken ct)
    {
        try
        {
            using var request = WithKey(new HttpRequestMessage(HttpMethod.Get, "/models"));
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? ReadModelStates(await response.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    /// <summary>Null when the models have no status, as on a plain llama-server. Sleeping counts as loaded: the next request wakes it.</summary>
    public static IReadOnlyDictionary<string, ModelState>? ReadModelStates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var states = new Dictionary<string, ModelState>();
        foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object) return null;
            var failed = status.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.True;
            states[model.GetProperty("id").GetString() ?? ""] = status.GetProperty("value").GetString() switch
            {
                "loaded" or "sleeping" => ModelState.Loaded,
                "loading" => ModelState.Loading,
                _ => failed ? ModelState.Failed : ModelState.Unloaded,
            };
        }
        return states;
    }

    public async Task LoadModelAsync(string name, CancellationToken ct)
    {
        using var _ = await SendAsync("/models/load", new { Model = name }, stream: false, ct);
    }

    static object ChatBody(IReadOnlyList<ChatMessage> messages, int maxTokens, bool stream, string? model) =>
        new { Model = model, Messages = messages, MaxTokens = maxTokens, CachePrompt = true, Stream = stream, Temperature = CodeTemperature, TopK, TopP, MinP };

    async Task<HttpResponseMessage> SendAsync(string path, object body, bool stream, CancellationToken ct)
    {
        using var request = WithKey(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) });
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

    HttpRequestMessage WithKey(HttpRequestMessage request)
    {
        string? key = null;
        try { if (keyFile is not null && File.Exists(keyFile)) key = File.ReadAllText(keyFile).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // jazari writing it right now: the server answers 401, which says so
        if (key is { Length: > 0 }) request.Headers.Authorization = new("Bearer", key);
        return request;
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
