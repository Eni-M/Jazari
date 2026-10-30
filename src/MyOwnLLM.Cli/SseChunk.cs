using System.Text.Json;

namespace MyOwnLLM.Cli;

/// <summary>One server-sent event from a streamed chat: a piece of text, the final id, or an error.</summary>
public sealed record SseChunk(string? Content, string? Id, string? Error)
{
    public static SseChunk? Parse(string line)
    {
        if (!line.StartsWith("data: ") || line == "data: [DONE]") return null;
        using var doc = JsonDocument.Parse(line["data: ".Length..]);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) return new(null, null, error.GetString());
        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var content = root.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out var c) ? c.GetString() : null;
        return new(content, id, null);
    }
}
