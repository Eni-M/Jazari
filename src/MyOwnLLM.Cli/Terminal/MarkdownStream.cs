using System.Text;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// Renders a streamed answer as restrained Markdown. In a real terminal it sits beside the gutter: tokens appear as they
/// arrive, each row is word-wrapped as it fills and redrawn once with its styling. In plain mode (no color / piped) only
/// finished lines are written, with no gutter.
/// </summary>
/// <param name="visibleRows">How many conversation rows are on screen when the answer ends: the window less the panel
/// below it. Without it, <paramref name="screenRows"/>.</param>
public sealed partial class MarkdownStream(TextWriter output, Theme theme, int width, int screenRows = 24, Func<int>? visibleRows = null)
{
    enum State { Text, InFence }

    const int Measure = 86; // the design's 720 px text column, in characters

    readonly StringBuilder _line = new();
    readonly List<string[]> _table = [];
    readonly Ui _ui = new(output, theme, width);
    State _state;
    int _blocks; // code blocks so far in this answer
    int _committed; // finished rows of the current line (live)
    string _echo = ""; // what the current row shows so far (live)
    int _rows; // rows written since the answer began (live)
    bool _startsWithCode;
    bool Live => theme.Mode != ColorMode.None;
    int Body => Math.Max(Math.Min(width - Ui.Gutter, Measure), 12);

    public void Write(string token)
    {
        foreach (var c in ConsoleText.Sanitize(token))
        {
            if (c == '\n') { FinishLine(); continue; }
            if (c == '\t' && Live) { _line.Append("    "); continue; }
            _line.Append(c);
        }
        if (Live) Echo();
    }

    public void Complete()
    {
        if (_line.Length > 0) FinishLine();
        FlushTable();
        if (Live) output.Write(Theme.Reset);
    }

    /// <summary>
    /// Puts the answer's time in the gutter beside its first row, now that it's known. An answer taller than the space above
    /// the panel has scrolled that row away, and a code block on the first row has "copy 1" there, so then the time goes on
    /// a row of its own.
    /// </summary>
    public void StampTime(string time)
    {
        if (!Live) return;
        if (_rows == 0 || _startsWithCode || _rows >= (visibleRows?.Invoke() ?? screenRows) - 1) { output.WriteLine(_ui.Cell(time)); return; }
        output.Write($"\e[{_rows}A\r{_ui.Cell(time)}\e[{_rows}B\r");
    }

    void FinishLine()
    {
        var line = _line.ToString();
        _line.Clear();
        if (Live) FinishLive(line);
        else FinishPlain(line);
    }

    // -------- real terminal --------

    /// <summary>Shows the unfinished line: rows that filled up are finished for good; the last one grows as tokens arrive.</summary>
    void Echo()
    {
        var line = _line.ToString();
        if (line.Length == 0 || Held(line)) return;
        var (prefix, rows) = Layout(line);
        while (_committed < rows.Count - 1)
        {
            ClearEcho();
            WriteRow(prefix, rows[_committed], _committed);
            _committed++;
        }
        var last = rows[^1];
        if (_echo.Length > 0 && last.StartsWith(_echo, StringComparison.Ordinal) && _state != State.InFence)
            output.Write(theme.Paint(last[_echo.Length..], Tone.Primary)); // only what's new, so the row doesn't flicker
        else
        {
            ClearEcho();
            output.Write(_ui.Cell() + (_state == State.InFence ? theme.Band(Surface.Code, theme.Paint(" " + last, Tone.Primary)) : Pad(prefix, _committed) + theme.Paint(last, Tone.Primary)));
        }
        _echo = last;
    }

    /// <summary>Fences and tables wait for their whole line: they're drawn differently from what the raw text looks like.</summary>
    bool Held(string line)
    {
        var start = line.TrimStart();
        return _state == State.InFence ? start.StartsWith('`') : start.StartsWith('`') && start.Length < 3 || ConsoleText.IsFence(line) || start.StartsWith('|');
    }

    void ClearEcho()
    {
        if (_echo.Length > 0) output.Write("\r\e[2K");
        _echo = "";
    }

    void FinishLive(string line)
    {
        ClearEcho();
        if (_state == State.InFence)
        {
            if (ConsoleText.IsFence(line)) { _state = State.Text; _committed = 0; return; }
            var rows = Split(line, Body - 1);
            for (var i = _committed; i < rows.Count; i++)
            {
                output.WriteLine(_ui.Cell() + theme.Band(Surface.Code, theme.Paint(" " + rows[i], Tone.Primary)));
                _rows++;
            }
            _committed = 0;
            return;
        }

        if (line.TrimStart().StartsWith('|')) { _table.Add(Cells(line)); return; }
        FlushTable();

        if (ConsoleText.IsFence(line))
        {
            _state = State.InFence;
            _blocks++;
            _startsWithCode |= _rows == 0;
            var info = line.Trim()[3..].Trim();
            // Numbered in the gutter so /copy n can name it.
            output.WriteLine(_ui.Cell($"copy {_blocks}") + theme.Band(Surface.Code, theme.Paint(" " + (info.Length > 0 ? info : "code"), Tone.Muted)));
            _rows++;
            return;
        }

        var (prefix, wrapped) = Layout(line);
        for (var i = _committed; i < wrapped.Count; i++) WriteRow(prefix, wrapped[i], i);
        _committed = 0;
    }

    void WriteRow(string prefix, string row, int index)
    {
        output.WriteLine(_ui.Cell() + (_state == State.InFence
            ? theme.Band(Surface.Code, theme.Paint(" " + row, Tone.Primary))
            : Pad(prefix, index) + Inline(Link().Replace(row, "$1 ($2)"))));
        _rows++;
    }

    static string Pad(string prefix, int index) => index == 0 ? prefix : new string(' ', prefix.Length);

    /// <summary>
    /// How a line falls into rows. Bold markers are gone before wrapping: once a row is finished it can't change, and a
    /// marker closing later would otherwise shift it. Text with wide characters wraps early, since each takes two columns.
    /// </summary>
    (string Prefix, IReadOnlyList<string> Rows) Layout(string line)
    {
        if (_state == State.InFence) return ("", Split(line, Body - 1));
        var (prefix, body) = Parts(line);
        body = body.Replace("**", "");
        var room = Body - prefix.Length;
        return (prefix, Ui.Wrap(body, room));
    }

    /// <summary>Code keeps every space, so it's cut by columns instead of wrapped at words.</summary>
    static List<string> Split(string line, int room)
    {
        List<string> rows = [];
        for (var rest = line; rest.Length > 0;)
        {
            var (head, tail) = TextWidth.Take(rest, Math.Max(room, 1));
            rows.Add(head);
            rest = tail;
        }
        if (rows.Count == 0) rows.Add("");
        return rows;
    }

    // -------- plain output --------

    void FinishPlain(string line)
    {
        if (_state == State.InFence)
        {
            if (ConsoleText.IsFence(line)) { _state = State.Text; return; }
            // Printed untouched, no padding and no splitting: a mouse copy must give back exactly the code.
            output.WriteLine(theme.Code(line));
            return;
        }

        if (line.TrimStart().StartsWith('|')) { _table.Add(Cells(line)); return; }
        FlushTable();

        if (ConsoleText.IsFence(line))
        {
            _state = State.InFence;
            // Numbered so /copy n can name it.
            output.WriteLine(theme.Paint($"[{++_blocks}] {line.Trim()[3..].Trim()}".TrimEnd(), Tone.Muted));
            return;
        }

        var (prefix, body) = Parts(line);
        body = Link().Replace(Bold().Replace(body, "$1"), "$1 ($2)");
        var lines = Ui.Wrap(body, width - prefix.Length);
        for (var i = 0; i < lines.Count; i++)
            output.WriteLine((i == 0 ? prefix : new string(' ', prefix.Length)) + Inline(lines[i]));
    }

    static (string Prefix, string Body) Parts(string line) =>
        Heading().Match(line) is { Success: true } h ? ("", h.Groups[1].Value)
        : Bullet().Match(line) is { Success: true } b ? ("  - ", b.Groups[1].Value)
        : Numbered().Match(line) is { Success: true } n ? ($"  {n.Groups[1].Value}. ", n.Groups[2].Value)
        : ("", line);

    string Inline(string text) => InlineCode().Replace(text, m => theme.Code(m.Groups[1].Value)) is var styled && Live
        ? theme.Paint(styled.Replace(Theme.Reset, Theme.Reset + PrimaryStart()), Tone.Primary)
        : styled;

    /// <summary>What Paint puts before its text, to carry on in the primary color after inline code ends.</summary>
    string PrimaryStart() => theme.Paint("", Tone.Primary).Replace(Theme.Reset, "");

    void FlushTable()
    {
        if (_table.Count == 0) return;
        var rows = _table.Where(r => !r.All(c => SeparatorCell().IsMatch(c))).ToList();
        if (rows.Count == 0) { _table.Clear(); return; }
        var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(i => rows.Max(r => i < r.Length ? r[i].Length : 0)).ToArray();
        for (var r = 0; r < rows.Count; r++)
        {
            var text = string.Join("  ", rows[r].Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
            output.WriteLine(_ui.Cell() + (r == 0 && rows.Count > 1 ? theme.Paint(text, Tone.Muted) : Live ? theme.Paint(text, Tone.Primary) : text));
            _rows++;
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
