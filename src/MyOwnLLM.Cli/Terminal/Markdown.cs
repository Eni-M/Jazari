using System.Text;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// A streamed answer as restrained Markdown, laid out at any width. Finished lines become fixed pieces; the line still
/// arriving is laid out again on every token. Inline markers (`code`, **bold**) are hidden the moment they arrive, so
/// text never shifts when a span closes: only the last row of the line being written can still change.
/// </summary>
public sealed partial class AnswerBlock(Theme theme) : Block
{
    abstract record Piece;
    sealed record TextPiece(string Line) : Piece;
    sealed record CodeTop(string Info, int Number) : Piece;
    sealed record CodeLine(string Text) : Piece;
    sealed record CodeBottom : Piece;
    sealed record TablePiece(List<string[]> Rows) : Piece;

    readonly List<Piece> _pieces = [];
    readonly Dictionary<Piece, (int Columns, IReadOnlyList<Line> Lines)> _layouts = new(ReferenceEqualityComparer.Instance);
    readonly StringBuilder _open = new();
    readonly List<string[]> _table = [];
    bool _inFence, _complete;
    int _blocks, _openRows;
    string? _time;
    bool _timeOnOwnRow;

    public override bool Live => !_complete;

    /// <summary>Code blocks so far, numbered for /copy.</summary>
    public int CodeBlocks => _blocks;

    public void Feed(string token)
    {
        foreach (var c in ConsoleText.Sanitize(token))
        {
            if (c == '\n') { Finish(_open.ToString()); _open.Clear(); }
            else if (c == '\t') _open.Append("    ");
            else _open.Append(c);
        }
    }

    public void Complete()
    {
        if (_complete) return;
        if (_open.Length > 0) { Finish(_open.ToString()); _open.Clear(); }
        FlushTable();
        if (_inFence) { _pieces.Add(new CodeBottom()); _inFence = false; }
        _complete = true;
        Version++;
    }

    /// <summary>The answer's time in the gutter of its first row, or on a row of its own after it when that row has gone.</summary>
    public void Stamp(string time, bool ownRow)
    {
        _time = ConsoleText.Sanitize(time);
        _timeOnOwnRow = ownRow;
        Version++;
    }

    void Finish(string line)
    {
        if (_inFence)
        {
            if (ConsoleText.IsFence(line)) { _pieces.Add(new CodeBottom()); _inFence = false; }
            else _pieces.Add(new CodeLine(line));
            return;
        }
        if (line.TrimStart().StartsWith('|')) { _table.Add(Cells(line)); return; }
        FlushTable();
        if (ConsoleText.IsFence(line))
        {
            _inFence = true;
            _pieces.Add(new CodeTop(line.Trim()[3..].Trim(), ++_blocks));
            return;
        }
        _pieces.Add(new TextPiece(line));
    }

    void FlushTable()
    {
        if (_table.Count == 0) return;
        _pieces.Add(new TablePiece([.. _table]));
        _table.Clear();
    }

    public override int Stable(int columns, int rows)
    {
        if (_complete) return rows;
        // A link can change how the line reads once it's complete, so that line waits.
        var open = _open.ToString();
        return rows - _openRows + (open.Contains('[') ? 0 : Math.Max(_openRows - 1, 0));
    }

    public override IReadOnlyList<Line> Render(int columns)
    {
        List<Line> lines = [];
        foreach (var piece in _pieces)
        {
            if (!_layouts.TryGetValue(piece, out var layout) || layout.Columns != columns)
                _layouts[piece] = layout = (columns, Lay(piece, columns));
            lines.AddRange(layout.Lines);
        }
        var openLines = OpenLines(columns);
        _openRows = openLines.Count;
        lines.AddRange(openLines);

        if (_time is { } time)
        {
            if (_timeOnOwnRow || lines.Count == 0 || lines[0].Spans.Count == 0 || lines[0].Spans[0].Text.Trim().Length > 0)
                lines.Add(Layout.Row(time).Build());
            else lines[0] = lines[0].WithGutter(Layout.Label(time));
        }
        return lines;
    }

    List<Line> OpenLines(int columns)
    {
        var open = _open.ToString();
        if (open.Length == 0) return [];
        if (_inFence) return open.TrimStart().StartsWith('`') ? [] : CodeRows(open, columns);
        var start = open.TrimStart();
        // A line that may turn out to be a fence or a table row waits until it's complete.
        if (start.StartsWith('`') && start.Length < 3 || ConsoleText.IsFence(open) || start.StartsWith('|')) return [];
        return TextRows(open, columns);
    }

    IReadOnlyList<Line> Lay(Piece piece, int columns) => piece switch
    {
        TextPiece text => TextRows(text.Line, columns),
        CodeTop top => CodeTopRows(top, columns),
        CodeLine code => CodeRows(code.Text, columns),
        CodeBottom => theme.Edges ? [Layout.Row().Add(new string('▔', Layout.Body(columns)), Tone.CodeBorder).Build()] : [],
        TablePiece table => TableRows(table.Rows, columns),
        _ => [],
    };

    List<Line> TextRows(string line, int columns)
    {
        if (line.Trim().Length == 0) return [Line.Empty];
        var (prefix, body, heading) = Parts(line);
        var (text, styles) = Inline(body, heading);
        var room = Layout.Body(columns) - TextWidth.Of(prefix);
        return Styled.Wrap(text, styles, room).Select((spans, i) =>
            Layout.Row().Add(i == 0 ? prefix : new string(' ', TextWidth.Of(prefix)), Tone.Primary).AddRange(spans).Build()).ToList();
    }

    List<Line> CodeTopRows(CodeTop top, int columns)
    {
        var body = Layout.Body(columns);
        var code = new Style(Tone.Muted, Surface.Code);
        List<Line> rows = [];
        if (theme.Edges) rows.Add(Layout.Row().Add(new string('▁', body), Tone.CodeBorder).Build());
        rows.Add(Layout.Row($"copy {top.Number}").Add(TextWidth.PadRight("  " + TextWidth.Fit(top.Info, body - 4), body), code).Build());
        return rows;
    }

    /// <summary>Code keeps every space, so it's cut by columns instead of wrapped at words; on the code background to the box's edge.</summary>
    static List<Line> CodeRows(string text, int columns)
    {
        var body = Layout.Body(columns);
        var style = new Style(Tone.Primary, Surface.Code);
        return DiffBlock.Pieces(text, body - 4).Select(piece => Layout.Row().Add(TextWidth.PadRight("  " + piece, body), style).Build()).ToList();
    }

    static List<Line> TableRows(List<string[]> table, int columns)
    {
        var rows = table.Where(r => !r.All(c => SeparatorCell().IsMatch(c))).ToList();
        if (rows.Count == 0) return [];
        var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(i => rows.Max(r => i < r.Length ? TextWidth.Of(r[i]) : 0)).ToArray();
        var body = Layout.Body(columns);
        return rows.Select((row, r) =>
        {
            var text = string.Join("  ", row.Select((c, i) => TextWidth.PadRight(c, widths[i]))).TrimEnd();
            return Layout.Row().Add(TextWidth.Fit(text, body), r == 0 && rows.Count > 1 ? Tone.Muted : Tone.Primary).Build();
        }).ToList();
    }

    static (string Prefix, string Body, bool Heading) Parts(string line) =>
        Heading().Match(line) is { Success: true } h ? ("", h.Groups[1].Value, true)
        : Bullet().Match(line) is { Success: true } b ? ("- ", b.Groups[1].Value, false)
        : Numbered().Match(line) is { Success: true } n ? ($"{n.Groups[1].Value}. ", n.Groups[2].Value, false)
        : ("", line, false);

    /// <summary>
    /// The text as shown and a style per character: `code` on the inline background, **bold** in bold, a complete
    /// [link](url) as its text with the address after it in muted. Markers are dropped as they arrive.
    /// </summary>
    static (string Text, Style[] Styles) Inline(string body, bool heading)
    {
        var text = new StringBuilder();
        var styles = new List<Style>();
        void Emit(string s, Style style) { text.Append(s); styles.AddRange(Enumerable.Repeat(style, s.Length)); }
        var (bold, code) = (heading, false);
        for (var i = 0; i < body.Length;)
        {
            if (code)
            {
                if (body[i] == '`') code = false;
                else Emit(body[i].ToString(), new Style(Tone.Primary, Surface.Inline));
                i++;
            }
            else if (string.CompareOrdinal(body, i, "**", 0, 2) == 0) { bold = !bold || heading; i += 2; }
            else if (body[i] == '`') { code = true; i++; }
            else if (body[i] == '[' && Link().Match(body, i) is { Success: true } link && link.Index == i)
            {
                Emit(link.Groups[1].Value, new Style(Tone.Primary, Bold: bold));
                Emit(" (" + link.Groups[2].Value + ")", Tone.Muted);
                i += link.Length;
            }
            else
            {
                Emit(body[i].ToString(), new Style(Tone.Primary, Bold: bold));
                i++;
            }
        }
        return (text.ToString(), styles.ToArray());
    }

    static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    [GeneratedRegex(@"^#{1,6}\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^\s*(\d+)\.\s+(.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"\G\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"^:?-+:?$")] private static partial Regex SeparatorCell();
}
