using System.Globalization;
using System.Net;
using System.Text;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>
/// A small terminal emulator for tests: it plays escape codes onto a grid of cells, the way Windows Terminal would, so
/// tests can check what's on screen instead of which bytes were written. It records soft wraps (a row too wide for the
/// window, which the interface must never cause), the scrollback, and whether synchronized updates are balanced.
/// </summary>
public sealed class VtScreen
{
    public sealed record Cell(string Text, int? Fg, int? Bg, bool Bold, bool Reverse)
    {
        public static readonly Cell Blank = new(" ", null, null, false, false);
    }

    Cell[][] _rows;
    bool[] _wrapped;
    int _fg = -1, _bg = -1;
    bool _bold, _reverse, _pending;
    int _savedRow, _savedColumn;

    public VtScreen(int width, int height)
    {
        (Width, Height) = (width, height);
        _rows = Enumerable.Range(0, height).Select(_ => NewRow()).ToArray();
        _wrapped = new bool[height];
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Row { get; private set; }
    public int Column { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public int SyncDepth { get; private set; }
    public bool SyncError { get; private set; }
    public int SoftWraps { get; private set; }
    public List<string> Scrollback { get; } = [];
    public List<string> Signals { get; } = [];
    /// <summary>Whether ED 2 moves the screen into the scrollback first, as Windows Terminal and conhost do.</summary>
    public bool EraseToScrollback { get; init; } = true;

    Cell[] NewRow() => Enumerable.Repeat(Cell.Blank, Width).ToArray();

    public string Text(int row) => string.Concat(_rows[row].Select(c => c.Text)).TrimEnd();
    public Cell At(int row, int column) => _rows[row][column];
    public IEnumerable<string> Lines => Enumerable.Range(0, Height).Select(Text);
    public string Screen => string.Join("\n", Lines).TrimEnd('\n');
    /// <summary>Scrollback and screen, top to bottom.</summary>
    public IEnumerable<string> Everything => Scrollback.Concat(Lines);

    /// <summary>Rows Windows Terminal had to wrap when the window narrowed: their text reached past the new edge.</summary>
    public int Reflowed { get; private set; }

    /// <summary>
    /// Resizes like Windows Terminal: a row whose text (not its background) reaches past the new width wraps onto extra
    /// rows, and rows that no longer fit push the top of the screen into the scrollback, keeping the cursor's row.
    /// </summary>
    public void Resize(int width, int height, bool revealScrollback = false)
    {
        if (revealScrollback && height > Height)
        {
            // Like conhost with the top edge dragged up: the window shows the rows above it, which move the screen down.
            var count = Math.Min(height - Height, Scrollback.Count);
            var shown = Scrollback.TakeLast(count).Select(t => Enumerable.Range(0, Width).Select(i => i < t.Length ? new Cell(t[i].ToString(), null, null, false, false) : Cell.Blank).ToArray());
            Scrollback.RemoveRange(Scrollback.Count - count, count);
            _rows = [.. shown, .. _rows];
            _wrapped = new bool[_rows.Length];
            Row += count;
            Height += count;
        }
        var rows = new List<Cell[]>();
        var cursor = 0;
        Cell[] Fit(IEnumerable<Cell> cells) => cells.Take(width).Concat(Enumerable.Repeat(Cell.Blank, width)).Take(width).ToArray();
        for (var r = 0; r < _rows.Length; r++)
        {
            var cells = _rows[r];
            var end = Array.FindLastIndex(cells, c => c.Text.Length > 0 && c.Text != " ") + 1;
            if (end > 0 && end < cells.Length && cells[end].Text.Length == 0) end++; // the second half of a wide character
            if (r == Row) cursor = rows.Count;
            if (end > width)
            {
                Reflowed++;
                for (var i = 0; i < end; i += width) rows.Add(Fit(cells.Skip(i)));
            }
            else rows.Add(Fit(cells));
        }
        while (rows.Count > height && cursor > 0)
        {
            Scrollback.Add(string.Concat(rows[0].Select(c => c.Text)).TrimEnd());
            rows.RemoveAt(0);
            cursor--;
        }
        rows = rows.Take(height).ToList();
        while (rows.Count < height) rows.Add(Enumerable.Repeat(Cell.Blank, width).ToArray());
        (Width, Height, _rows, _wrapped) = (width, height, rows.ToArray(), new bool[height]);
        Row = Math.Min(cursor, height - 1);
        Column = Math.Min(Column, width - 1);
        _pending = false;
    }

    public void Write(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '\e') { i = Escape(text, i + 1); continue; }
            if (c == '\r') { Column = 0; _pending = false; i++; continue; }
            if (c == '\n') { LineFeed(); _pending = false; i++; continue; }
            if (c == '\a' || c == '\b') { i++; continue; }
            var length = Math.Max(StringInfo.GetNextTextElementLength(text, i), 1);
            Print(text.Substring(i, length));
            i += length;
        }
    }

    void LineFeed()
    {
        if (Row < Height - 1) { Row++; return; }
        Scrollback.Add(Text(0));
        _rows = [.. _rows.Skip(1), NewRow()];
        _wrapped = [.. _wrapped.Skip(1), false];
    }

    void Print(string cluster)
    {
        var width = Math.Max(TextWidth.Of(cluster), 1);
        if (_pending || Column + width > Width)
        {
            SoftWraps++;
            _wrapped[Row] = true;
            Column = 0;
            LineFeed();
            _pending = false;
        }
        var cell = new Cell(cluster, _fg < 0 ? null : _fg, _bg < 0 ? null : _bg, _bold, _reverse);
        _rows[Row][Column] = cell;
        if (width == 2 && Column + 1 < Width) _rows[Row][Column + 1] = cell with { Text = "" };
        Column += width;
        if (Column >= Width) { Column = Width - 1; _pending = true; }
    }

    int Escape(string text, int i)
    {
        if (i >= text.Length) return i;
        switch (text[i])
        {
            case '[': return Csi(text, i + 1);
            case ']':
                var end = text.IndexOf('\a', i);
                var st = text.IndexOf("\e\\", i, StringComparison.Ordinal);
                if (end < 0 || st >= 0 && st < end) end = st;
                Signals.Add(text[(i + 1)..end]);
                return end + (text[end] == '\a' ? 1 : 2);
            case '7': (_savedRow, _savedColumn) = (Row, Column); return i + 1;
            case '8': (Row, Column) = (_savedRow, _savedColumn); return i + 1;
            default: return i + 1;
        }
    }

    int Csi(string text, int i)
    {
        var start = i;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is ';' or '?')) i++;
        var raw = text[start..i];
        var final = text[i];
        var isPrivate = raw.StartsWith('?');
        var args = raw.TrimStart('?').Split(';').Select(a => int.TryParse(a, out var n) ? n : -1).ToArray();
        int Arg(int index, int fallback) => index < args.Length && args[index] >= 0 ? args[index] : fallback;
        _pending = final is not 'm';
        switch (final)
        {
            case 'H' or 'f': Row = Math.Clamp(Arg(0, 1) - 1, 0, Height - 1); Column = Math.Clamp(Arg(1, 1) - 1, 0, Width - 1); break;
            case 'A': Row = Math.Max(Row - Arg(0, 1), 0); break;
            case 'B': Row = Math.Min(Row + Arg(0, 1), Height - 1); break;
            case 'C': Column = Math.Min(Column + Arg(0, 1), Width - 1); break;
            case 'D': Column = Math.Max(Column - Arg(0, 1), 0); break;
            case 'G': Column = Math.Clamp(Arg(0, 1) - 1, 0, Width - 1); break;
            case 'd': Row = Math.Clamp(Arg(0, 1) - 1, 0, Height - 1); break;
            case 'K':
                var mode = Arg(0, 0);
                for (var c = 0; c < Width; c++)
                    if (mode == 2 || mode == 0 && c >= Column || mode == 1 && c <= Column) _rows[Row][c] = Cell.Blank with { Bg = _bg < 0 ? null : _bg };
                break;
            case 'J':
                var j = Arg(0, 0);
                if (j == 3) { Scrollback.Clear(); break; }
                if (j == 2 && EraseToScrollback) Scrollback.AddRange(Lines.Reverse().SkipWhile(l => l.Length == 0).Reverse());
                for (var r = 0; r < Height; r++)
                    if (j == 2 || j == 0 && r > Row || j == 1 && r < Row) { _rows[r] = NewRow(); _wrapped[r] = false; }
                if (j == 0) for (var c = Column; c < Width; c++) _rows[Row][c] = Cell.Blank;
                if (j == 1) for (var c = 0; c <= Column; c++) _rows[Row][c] = Cell.Blank;
                break;
            case 'h' or 'l' when isPrivate:
                var on = final == 'h';
                if (Arg(0, 0) == 25) CursorVisible = on;
                if (Arg(0, 0) == 2026)
                {
                    if (on) { if (SyncDepth > 0) SyncError = true; SyncDepth++; }
                    else { if (SyncDepth == 0) SyncError = true; SyncDepth = Math.Max(SyncDepth - 1, 0); }
                }
                break;
            case 'm': Sgr(args); break;
        }
        return i + 1;
    }

    void Sgr(int[] args)
    {
        for (var k = 0; k < args.Length; k++)
        {
            var a = Math.Max(args[k], 0);
            switch (a)
            {
                case 0: (_fg, _bg, _bold, _reverse) = (-1, -1, false, false); break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case 7: _reverse = true; break;
                case 27: _reverse = false; break;
                case 39: _fg = -1; break;
                case 49: _bg = -1; break;
                case >= 30 and <= 37 or >= 90 and <= 97: _fg = Basic(a); break;
                case >= 40 and <= 47 or >= 100 and <= 107: _bg = Basic(a - 10); break;
                case 38 or 48:
                    var target = a;
                    int color;
                    if (k + 1 < args.Length && args[k + 1] == 2 && k + 4 < args.Length) { color = args[k + 2] << 16 | args[k + 3] << 8 | args[k + 4]; k += 4; }
                    else if (k + 1 < args.Length && args[k + 1] == 5 && k + 2 < args.Length) { color = Xterm(args[k + 2]); k += 2; }
                    else break;
                    if (target == 38) _fg = color; else _bg = color;
                    break;
            }
        }
    }

    static readonly int[] Basics = [0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2];
    static int Basic(int code) => code >= 90 ? Basics[code - 90 + 8] : Basics[code - 30];
    static int Xterm(int index)
    {
        if (index < 16) return Basics[index];
        if (index >= 232) { var v = 8 + (index - 232) * 10; return v << 16 | v << 8 | v; }
        index -= 16;
        int Level(int n) => n == 0 ? 0 : 55 + n * 40;
        return Level(index / 36) << 16 | Level(index / 6 % 6) << 8 | Level(index % 6);
    }

    /// <summary>The screen as an HTML page in the canvas's colors and font, for side-by-side screenshots.</summary>
    public string ToHtml(int fg = 0xECECE8, int bg = 0x0E0F11)
    {
        var html = new StringBuilder($$"""
            <!doctype html><meta charset="utf-8"><style>
            body{margin:0;background:#{{bg:X6}}}
            pre{margin:0;font:14px/22px 'JetBrains Mono',monospace;color:#{{fg:X6}};letter-spacing:0}
            span{display:inline-block;height:22px;vertical-align:top}
            </style><pre>
            """);
        foreach (var row in _rows)
        {
            foreach (var cell in row)
            {
                if (cell.Text.Length == 0) continue;
                var (f, b) = (cell.Fg ?? fg, cell.Bg ?? bg);
                if (cell.Reverse) (f, b) = (b, f);
                var width = Math.Max(TextWidth.Of(cell.Text), 1) * 8.4;
                html.Append($"<span style=\"width:{width.ToString(CultureInfo.InvariantCulture)}px;color:#{f:X6};background:#{b:X6}{(cell.Bold ? ";font-weight:700" : "")}\">{WebUtility.HtmlEncode(cell.Text)}</span>");
            }
            html.Append('\n');
        }
        return html.Append("</pre>").ToString();
    }
}

/// <summary>A terminal backed by a <see cref="VtScreen"/>, with keys queued by the test.</summary>
public sealed class FakeTerminal(int width, int height) : ITerminal
{
    readonly Queue<ConsoleKeyInfo> _keys = new();
    readonly object _gate = new();

    public VtScreen Vt { get; } = new(width, height);
    public int Writes { get; private set; }
    public List<string> Frames { get; } = [];

    public (int Width, int Height) Size { get { lock (_gate) return (Vt.Width, Vt.Height); } }

    public void Write(string text)
    {
        lock (_gate)
        {
            Writes++;
            Frames.Add(text);
            Vt.Write(text);
        }
    }

    public void Resize(int width, int height, bool revealScrollback = false) { lock (_gate) Vt.Resize(width, height, revealScrollback); }

    public int Top { get { lock (_gate) return Vt.Scrollback.Count; } }

    public bool KeyAvailable { get { lock (_gate) return _keys.Count > 0; } }
    public ConsoleKeyInfo ReadKey() { lock (_gate) return _keys.Dequeue(); }
    public bool TreatControlCAsInput { get; set; }

    public void Type(string text)
    {
        lock (_gate)
            foreach (var c in text)
                _keys.Enqueue(c switch
                {
                    '\n' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                    '\b' => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                    _ => new ConsoleKeyInfo(c, char.IsAsciiLetter(c) ? (ConsoleKey)char.ToUpperInvariant(c) : 0, false, false, false),
                });
    }

    public void Press(ConsoleKey key, char c = '\0', bool control = false)
    {
        lock (_gate) _keys.Enqueue(new ConsoleKeyInfo(c, key, false, false, control));
    }
}
