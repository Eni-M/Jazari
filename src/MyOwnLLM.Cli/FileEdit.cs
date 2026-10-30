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
            var replace = edit.Replace.Replace("\r\n", "\n");
            var first = text.IndexOf(search, StringComparison.Ordinal);
            if (first >= 0)
            {
                if (text.IndexOf(search, first + 1, StringComparison.Ordinal) >= 0) return (null, $"this text appears more than once: {Short(search)}");
                text = text[..first] + replace + text[(first + search.Length)..];
                continue;
            }
            var (lineText, problem) = ApplyByLines(text, search, replace);
            if (lineText is null) return (null, $"{problem}: {Short(search)}");
            text = lineText;
        }
        return (crlf ? text.Replace("\n", "\r\n") : text, null);
    }

    /// <summary>
    /// Fallback for small models that get indentation or trailing spaces wrong: matches whole lines with their ends trimmed,
    /// then moves the replacement to the file's own indentation.
    /// </summary>
    static (string? Text, string? Problem) ApplyByLines(string text, string search, string replace)
    {
        var wanted = search.Trim('\n').Split('\n');
        if (wanted.All(string.IsNullOrWhiteSpace)) return (null, "this text isn't in the file");
        var lines = text.Split('\n');
        var starts = Enumerable.Range(0, lines.Length - wanted.Length + 1)
            .Where(i => wanted.Select((w, j) => lines[i + j].Trim() == w.Trim()).All(same => same)).Take(2).ToList();
        if (starts.Count == 0) return (null, "this text isn't in the file");
        if (starts.Count > 1) return (null, "this text appears more than once");

        var at = starts[0];
        var fileIndent = Indent(lines[at]);
        var modelIndent = Indent(wanted[0]);
        var replaced = replace.Trim('\n').Split('\n')
            .Select(l => l.Length == 0 ? l : l.StartsWith(modelIndent, StringComparison.Ordinal) ? fileIndent + l[modelIndent.Length..] : l);
        return (string.Join('\n', lines[..at].Concat(replace.Trim('\n').Length == 0 ? [] : replaced).Concat(lines[(at + wanted.Length)..])), null);
    }

    static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    /// <summary>Saves over the file, keeping whether it had a byte-order mark.</summary>
    public static void Write(string path, string text)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        File.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    static string Short(string text) => (text.Length > 60 ? text[..60] + "..." : text).Replace("\n", " ");
}
