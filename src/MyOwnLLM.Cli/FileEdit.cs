using System.Text;
using System.Text.Json;

namespace MyOwnLLM.Cli;

public sealed record Replacement(string Search, string Replace);

/// <summary>
/// Turns the model's search/replace answer into a new version of a file. Nothing touches the disk here: the caller shows
/// the change and writes it only after the user says yes. Every search text must match exactly once, or nothing applies.
/// </summary>
public static class FileEdit
{
    public const string Schema = """
        {"type":"object","required":["edits"],"properties":{"edits":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object",
        "required":["search","replace"],"properties":{"search":{"type":"string"},"replace":{"type":"string"}}}}}}
        """;

    public const string Instructions =
        "Change the file above as asked below. Answer only with edits: search is text copied exactly from the file, long enough " +
        "to appear only once, and replace is what it becomes. Keep the file's style and change nothing else.\n\nRequest: ";

    public static IReadOnlyList<Replacement>? Parse(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.GetProperty("edits").EnumerateArray()
                .Select(e => new Replacement(e.GetProperty("search").GetString() ?? "", e.GetProperty("replace").GetString() ?? "")).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>The new text, or the reason it can't apply. Compares with \n line ends and restores the file's own.</summary>
    public static (string? Text, string? Problem) Apply(string original, IReadOnlyList<Replacement> edits)
    {
        var crlf = original.Contains("\r\n");
        var text = original.Replace("\r\n", "\n");
        foreach (var edit in edits)
        {
            var search = edit.Search.Replace("\r\n", "\n");
            if (search.Length == 0) return (null, "an edit has nothing to search for");
            var first = text.IndexOf(search, StringComparison.Ordinal);
            if (first < 0) return (null, $"this text isn't in the file: {Short(search)}");
            if (text.IndexOf(search, first + 1, StringComparison.Ordinal) >= 0) return (null, $"this text appears more than once: {Short(search)}");
            text = text[..first] + edit.Replace.Replace("\r\n", "\n") + text[(first + search.Length)..];
        }
        return (crlf ? text.Replace("\n", "\r\n") : text, null);
    }

    /// <summary>Saves over the file, keeping whether it had a byte-order mark.</summary>
    public static void Write(string path, string text)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        File.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    static string Short(string text) => (text.Length > 60 ? text[..60] + "..." : text).Replace("\n", " ");
}
