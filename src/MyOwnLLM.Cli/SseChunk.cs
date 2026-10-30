using System.Text.Json;

namespace MyOwnLLM.Cli;

/// <summary>How far the model has read the prompt. <see cref="Cached"/> tokens didn't need reading again.</summary>
public sealed record ReadProgress(int Total, int Cached, int Processed);

/// <summary>The model's numbers for one answer.</summary>
public sealed record AnswerTimings(int PromptTokens, int CachedTokens, double PromptPerSecond, int AnswerTokens, double AnswerPerSecond);

/// <summary>One server-sent event from a streamed chat: a piece of text, reading progress, the final id and numbers, or an error.</summary>
public sealed record SseChunk(string? Content, string? Id, string? Error, ReadProgress? Progress = null, AnswerTimings? Timings = null)
{
    public static SseChunk? Parse(string line)
    {
        if (!line.StartsWith("data: ") || line == "data: [DONE]") return null;
        using var doc = JsonDocument.Parse(line["data: ".Length..]);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) return new(null, null, error.GetString());
        if (root.TryGetProperty("progress", out var p))
            return new(null, null, null, new(p.GetProperty("total").GetInt32(), p.GetProperty("cached").GetInt32(), p.GetProperty("processed").GetInt32()));
        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var content = root.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var c) ? c.GetString() : null;
        var timings = root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object
            ? new AnswerTimings(t.GetProperty("prompt_tokens").GetInt32(), t.GetProperty("cached_tokens").GetInt32(), t.GetProperty("prompt_per_second").GetDouble(),
                t.GetProperty("answer_tokens").GetInt32(), t.GetProperty("answer_per_second").GetDouble())
            : null;
        return new(content, id, null, Timings: timings);
    }
}
