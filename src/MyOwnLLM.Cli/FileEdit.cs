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
    /// <summary>
    /// The length caps bound a model that loops inside a string: without them it writes until it runs out of tokens and
    /// the reply never closes, the slowest possible failure.
    /// </summary>
    public const string Schema = """
        {"type":"object","required":["edits"],"properties":{"edits":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object",
        "required":["search","replace"],"properties":{"search":{"type":"string","maxLength":1500},"replace":{"type":"string","maxLength":3000}}}}}}
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
    /// <param name="applied">Gets what actually changed, which the fallback can make differ from the model's text: that's what the user approves.</param>
    public static (string? Text, string? Problem) Apply(string original, IReadOnlyList<Replacement> edits, List<Replacement>? applied = null)
    {
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
                applied?.Add(new(search, replace));
                continue;
            }
            var (lineText, problem, change) = ApplyByLines(text, search, replace);
            if (lineText is null) return (null, $"{problem}: {Short(search)}");
            text = lineText;
            applied?.Add(change!);
        }
        return (WithEndings(original, text), null);
    }

    /// <summary>
    /// The edited text with the original's line ends back: lines before and after the change keep their own, so a file
    /// mixing \r\n and \n changes only where the edit did; the changed lines take the file's usual one.
    /// </summary>
    static string WithEndings(string original, string edited)
    {
        var old = original.Split('\n'); // a line ending in \r\n keeps its \r here
        var now = edited.Split('\n');
        var (head, tail) = (0, 0);
        while (head < old.Length && head < now.Length && old[head].TrimEnd('\r') == now[head]) head++;
        while (tail < old.Length - head && tail < now.Length - head && old[^(tail + 1)].TrimEnd('\r') == now[^(tail + 1)]) tail++;
        var usual = old.SkipLast(1).Count(l => l.EndsWith('\r')) * 2 > old.Length - 1 ? "\r" : "";
        // The last piece has no line end after it, so it gets no \r of its own.
        var middle = now[head..^tail].Select((l, i) => head + i == now.Length - 1 ? l : l + usual);
        return string.Join('\n', old[..head].Concat(middle).Concat(old[^tail..]));
    }

    /// <summary>
    /// Fallback for small models that get indentation or trailing spaces wrong: matches whole lines with their ends trimmed,
    /// then shifts the whole replacement by the difference between the file's indentation and the model's, so lines
    /// indented less than the first keep their place relative to it (which matters in Python and YAML).
    /// </summary>
    static (string? Text, string? Problem, Replacement? Change) ApplyByLines(string text, string search, string replace)
    {
        var wanted = search.Trim('\n').Split('\n');
        if (wanted.All(string.IsNullOrWhiteSpace)) return (null, "this text isn't in the file", null);
        var lines = text.Split('\n');
        // Looser each time, as Aider's applier is (it measured 9x more failed edits without it): the lines as written but
        // for spacing, then also without the blank lines the model added or left out. Still only a match found once applies.
        var spans = Matches(lines, wanted, skipBlank: false);
        if (spans.Count == 0) spans = Matches(lines, [.. wanted.Where(w => !string.IsNullOrWhiteSpace(w))], skipBlank: true);
        if (spans.Count == 0) return (null, "this text isn't in the file", null);
        if (spans.Count > 1) return (null, "this text appears more than once", null);

        var (at, end) = spans[0];
        var fileIndent = Indent(lines[at]);
        var modelIndent = Indent(wanted.First(w => !string.IsNullOrWhiteSpace(w)));
        var unit = (fileIndent + modelIndent).Distinct().ToList();
        if (unit.Count > 1) return (null, "the indentation mixes tabs and spaces, so it can't be matched safely", null);
        var pad = unit.Count == 1 ? unit[0] : ' ';
        var shift = fileIndent.Length - modelIndent.Length;
        string Shifted(string l)
        {
            if (l.Trim().Length == 0) return "";
            var own = Indent(l);
            if (own.Any(c => c != pad)) return l; // a line with other indentation is left as the model wrote it
            return new string(pad, Math.Max(0, own.Length + shift)) + l[own.Length..];
        }
        var replaced = replace.Trim('\n').Length == 0 ? [] : replace.Trim('\n').Split('\n').Select(Shifted).ToArray();
        var change = new Replacement(string.Join('\n', lines[at..end]), string.Join('\n', replaced));
        return (string.Join('\n', lines[..at].Concat(replaced).Concat(lines[end..])), null, change);
    }

    /// <summary>Where <paramref name="wanted"/> appears as whole lines, compared without spacing differences (at most two
    /// places: two already means it can't apply). Each is a start line and the line after the last one matched.</summary>
    static List<(int Start, int End)> Matches(string[] lines, string[] wanted, bool skipBlank)
    {
        var found = new List<(int, int)>();
        for (var i = 0; i < lines.Length && found.Count < 2; i++)
        {
            if (skipBlank && string.IsNullOrWhiteSpace(lines[i])) continue;
            var (j, k) = (0, i);
            while (j < wanted.Length && k < lines.Length)
            {
                if (skipBlank && string.IsNullOrWhiteSpace(lines[k])) { k++; continue; }
                if (Spaced(lines[k]) != Spaced(wanted[j])) break;
                (j, k) = (j + 1, k + 1);
            }
            if (j == wanted.Length) found.Add((i, k));
        }
        return found;
    }

    /// <summary>The line's words with single spaces between them: "x  =\t1 " and "x = 1" compare equal.</summary>
    static string Spaced(string line) => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Syntax errors the edit adds to a .cs file, as "line 12: ; expected"; none for other files, and none the file
    /// already had. Only parsed, never compiled: it catches a lost brace or semicolon, not a wrong method name.
    /// </summary>
    public static List<string> NewSyntaxErrors(string path, string before, string after)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return [];
        static List<(int Line, string Message)> Errors(string code) =>
            [.. Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code).GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1, d.GetMessage()))];
        var had = Errors(before);
        var added = new List<string>();
        foreach (var (line, message) in Errors(after))
        {
            var same = had.FindIndex(e => e.Message == message);
            if (same >= 0) had.RemoveAt(same);
            else added.Add($"line {line}: {message}");
        }
        return added;
    }

    static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    /// <summary>A file's text and the encoding it's stored in, so an edit writes it back the same way.</summary>
    public sealed record Stored(string Text, Encoding Encoding, byte[] Bytes);

    /// <summary>
    /// Reads a text file in UTF-8 (with or without a byte-order mark) or UTF-16/32 with one. Null for anything else
    /// (Windows-1252, say): decoding it as UTF-8 and writing it back would damage every accented letter in the file.
    /// </summary>
    public static Stored? Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding encoding = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true, true),
            [0xFF, 0xFE, 0, 0, ..] => new UTF32Encoding(false, true, true),
            [0xFF, 0xFE, ..] => new UnicodeEncoding(false, true, true),
            [0xFE, 0xFF, ..] => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(false, true),
        };
        try
        {
            var bom = encoding.GetPreamble().Length;
            return new(encoding.GetString(bytes, bom, bytes.Length - bom), encoding, bytes);
        }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>The bytes to save: the text in the file's own encoding, with its byte-order mark if it had one.</summary>
    public static byte[] Encode(string text, Encoding encoding) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    static string Short(string text) => (text.Length > 60 ? text[..60] + "..." : text).Replace("\n", " ");
}
