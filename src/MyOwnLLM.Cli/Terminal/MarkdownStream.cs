using System.Text;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// Renders a streamed answer as restrained Markdown. On a real terminal it becomes an <see cref="AnswerBlock"/> on the
/// screen, laid out again whenever the window changes size. In plain mode (no color / piped) only finished lines are
/// written, with no gutter, exactly as before.
/// </summary>
public sealed partial class MarkdownStream
{
    enum State { Text, InFence }

    readonly TextWriter _output;
    readonly Theme _theme;
    readonly int _width;
    readonly IBlockSink? _sink;
    readonly AnswerBlock? _answer;
    readonly StringBuilder _line = new();
    readonly List<string[]> _table = [];
    State _state;
    int _blocks; // code blocks so far in this answer

    /// <summary>To a writer: plain without color, otherwise drawn by the writer's shared flow-mode screen.</summary>
    public MarkdownStream(TextWriter output, Theme theme, int width, int screenRows = 24, Func<int>? visibleRows = null)
    {
        _output = output;
        _theme = theme;
        _width = width;
        if (theme.Mode == ColorMode.None) return;
        _sink = Screen.For(output, theme, () => width);
        _answer = new AnswerBlock(theme);
        _sink.Append(_answer);
    }

    /// <summary>To the session's screen.</summary>
    public MarkdownStream(IBlockSink sink, Theme theme)
    {
        _output = TextWriter.Null;
        _theme = theme;
        _width = 100;
        _sink = sink;
        _answer = new AnswerBlock(theme);
        sink.Append(_answer);
    }

    bool Live => _sink is not null;

    public void Write(string token)
    {
        if (_sink is { } sink)
        {
            lock (sink.Gate) _answer!.Feed(token);
            sink.Changed();
            return;
        }
        foreach (var c in ConsoleText.Sanitize(token))
        {
            if (c == '\n') { FinishPlain(_line.ToString()); _line.Clear(); continue; }
            _line.Append(c);
        }
    }

    public void Complete()
    {
        if (_sink is { } sink)
        {
            lock (sink.Gate) _answer!.Complete();
            sink.Changed();
            return;
        }
        if (_line.Length > 0) { FinishPlain(_line.ToString()); _line.Clear(); }
        FlushTable();
    }

    /// <summary>
    /// Puts the answer's time in the gutter beside its first row, now that it's known. If that row has already left the
    /// screen, or a code block's "copy 1" is there, the time goes on a row of its own.
    /// </summary>
    public void StampTime(string time)
    {
        if (_sink is not { } sink) return;
        var gone = !sink.IsOnScreen(_answer!);
        lock (sink.Gate) _answer!.Stamp(time, gone);
        sink.Changed();
    }

    // -------- plain output --------

    void FinishPlain(string line)
    {
        if (_state == State.InFence)
        {
            if (ConsoleText.IsFence(line)) { _state = State.Text; return; }
            // Printed untouched, no padding and no splitting: a mouse copy must give back exactly the code.
            _output.WriteLine(_theme.Code(line));
            return;
        }

        if (line.TrimStart().StartsWith('|')) { _table.Add(Cells(line)); return; }
        FlushTable();

        if (ConsoleText.IsFence(line))
        {
            _state = State.InFence;
            // Numbered so /copy n can name it.
            _output.WriteLine(_theme.Paint($"[{++_blocks}] {line.Trim()[3..].Trim()}".TrimEnd(), Tone.Muted));
            return;
        }

        var (prefix, body) = Parts(line);
        body = Link().Replace(Bold().Replace(body, "$1"), "$1 ($2)");
        var lines = Ui.Wrap(body, _width - prefix.Length);
        for (var i = 0; i < lines.Count; i++)
            _output.WriteLine((i == 0 ? prefix : new string(' ', prefix.Length)) + Inline(lines[i]));
    }

    static (string Prefix, string Body) Parts(string line) =>
        Heading().Match(line) is { Success: true } h ? ("", h.Groups[1].Value)
        : Bullet().Match(line) is { Success: true } b ? ("  - ", b.Groups[1].Value)
        : Numbered().Match(line) is { Success: true } n ? ($"  {n.Groups[1].Value}. ", n.Groups[2].Value)
        : ("", line);

    string Inline(string text) => InlineCode().Replace(text, m => _theme.Code(m.Groups[1].Value));

    void FlushTable()
    {
        if (_table.Count == 0) return;
        var rows = _table.Where(r => !r.All(c => SeparatorCell().IsMatch(c))).ToList();
        if (rows.Count == 0) { _table.Clear(); return; }
        var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(i => rows.Max(r => i < r.Length ? r[i].Length : 0)).ToArray();
        for (var r = 0; r < rows.Count; r++)
        {
            var text = string.Join("  ", rows[r].Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
            _output.WriteLine(r == 0 && rows.Count > 1 ? _theme.Paint(text, Tone.Muted) : text);
        }
        _table.Clear();
    }

    static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    [GeneratedRegex(@"^#{1,6}\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^\s*(\d+)\.\s+(.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"^:?-+:?$")] private static partial Regex SeparatorCell();
}
