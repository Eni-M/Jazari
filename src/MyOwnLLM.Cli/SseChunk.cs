using System.Text.Json;

namespace MyOwnLLM.Cli;

/// <summary>How far the model has read the prompt. <see cref="Cached"/> tokens didn't need reading again.</summary>
public sealed record ReadProgress(int Total, int Cached, int Processed);

/// <summary>The model's numbers for one answer.</summary>
public sealed record AnswerTimings(int PromptTokens, int CachedTokens, double PromptPerSecond, int AnswerTokens, double AnswerPerSecond);

/// <summary>A page of Microsoft's .NET docs an answer drew on.</summary>
public sealed record DocSource(string Title, string Url);

/// <summary>One server-sent event from a streamed chat: a piece of text, reading progress, the final id and numbers, or an error.</summary>
/// <param name="DroppedTurns">Questions with their answers that no longer fit the prompt. An older gateway doesn't send it: 0.</param>
/// <param name="Sources">The docs pages the answer drew on, from the law book. None, or an older gateway: null.</param>
public sealed record SseChunk(string? Content, string? Id, string? Error, ReadProgress? Progress = null, AnswerTimings? Timings = null, bool CutOff = false, int DroppedTurns = 0,
    IReadOnlyList<DocSource>? Sources = null)
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
        var choice = root.GetProperty("choices")[0];
        var content = choice.GetProperty("delta").TryGetProperty("content", out var c) ? c.GetString() : null;
        var cutOff = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() == "length";
        var timings = root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object
            ? new AnswerTimings(t.GetProperty("prompt_tokens").GetInt32(), t.GetProperty("cached_tokens").GetInt32(), t.GetProperty("prompt_per_second").GetDouble(),
                t.GetProperty("answer_tokens").GetInt32(), t.GetProperty("answer_per_second").GetDouble())
            : null;
        var dropped = root.TryGetProperty("dropped_turns", out var d) && d.TryGetInt32(out var n) ? n : 0;
        var sources = root.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => new DocSource(x.GetProperty("title").GetString() ?? "", x.GetProperty("url").GetString() ?? "")).ToList()
            : null;
        return new(content, id, null, Timings: timings, CutOff: cutOff, DroppedTurns: dropped, Sources: sources);
    }
}
