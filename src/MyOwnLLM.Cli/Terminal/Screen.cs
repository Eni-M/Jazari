using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>Where conversation blocks go: the screen, or a buffer that's replayed onto it later.</summary>
public interface IBlockSink
{
    /// <summary>Held while blocks change and while a frame is drawn.</summary>
    object Gate { get; }
    void Append(Block block);
    void Remove(Block block);
    /// <summary>Something changed: draw a new frame.</summary>
    void Changed();
    /// <summary>Whether the block's first row is still on screen, where it can be redrawn.</summary>
    bool IsOnScreen(Block block);
    /// <summary>A control sequence for the terminal itself (progress on the tab), sent with the next frame.</summary>
    void Signal(string control);
}

/// <summary>The panel at the bottom of the window: rows top to bottom, of which the last <see cref="Base"/> keep room for themselves; the rest overlay the conversation.</summary>
public readonly record struct PanelFrame(IReadOnlyList<Line> Rows, int Base, (int Row, int Column)? Caret);

public interface IPanel
{
    PanelFrame Render(int columns, int height);
}

/// <summary>Collects blocks to show later: what starting the servers had to say, while the prompt was already open.</summary>
public sealed class BlockBuffer : IBlockSink
{
    public List<Block> Blocks { get; } = [];
    public object Gate { get; } = new();
    public void Append(Block block) { lock (Gate) Blocks.Add(block); }
    public void Remove(Block block) { lock (Gate) Blocks.Remove(block); }
    public void Changed() { }
    public bool IsOnScreen(Block block) => true;
    public void Signal(string control) { }
}

/// <summary>
/// Owns the window and draws every frame of it, so nothing else writes to the terminal while it runs.
///
/// <para><b>Session mode</b> (bottom): the window shows the end of the conversation with the panel pinned at the bottom.
/// Each visible row is placed with an absolute cursor move and ends by clearing the rest of the line; a frame writes only
/// the rows that changed, inside one synchronized update, as one write. The conversation only grows: when it no longer fits,
/// the screen scrolls by writing line breaks on the last row, so what leaves the window lands in the terminal's own
/// scrollback, where mouse scrolling and selection work as usual. Nothing reads the cursor back: every row is laid out to
/// fit, so where it lands is known. A resize clears the window (not the scrollback) and lays everything out again.</para>
///
/// <para><b>Flow mode</b> (a one-off command): no panel and no absolute positions. Finished rows are printed once; the
/// rows still changing (the line being streamed, the thinking line) are redrawn in place under them.</para>
/// </summary>
public sealed class Screen : IBlockSink
{
    static readonly ConditionalWeakTable<TextWriter, Screen> Shared = new();

    readonly ITerminal _terminal;
    readonly Theme _theme;
    readonly bool _bottom;
    readonly bool _paced;
    readonly List<Block> _blocks = [];
    readonly Dictionary<Block, (int Columns, int Version, IReadOnlyList<Line> Lines)> _layouts = new(ReferenceEqualityComparer.Instance);
    readonly SemaphoreSlim _wake = new(0);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly string _blank;

    // Session mode: what each window row shows, and the first conversation row on screen (block and row in it).
    string[] _shown = [];
    int _width, _height, _area;
    int _anchorBlock, _anchorRow;
    (int Row, int Column)? _caret;
    bool _started, _stopping, _pendingResize;
    long _lastResize = -1000;
    Task? _loop;

    // Flow mode: rows already printed for good, and the rows still changing under them.
    int _doneBlock, _doneRow;
    string[] _tail = [];

    string _signals = "";

    public Screen(ITerminal terminal, Theme theme, bool bottom, bool paced)
    {
        _terminal = terminal;
        _theme = theme;
        _bottom = bottom;
        _paced = paced;
        _blank = theme.Encode(Line.Empty, 1);
    }

    /// <summary>The flow-mode screen for a writer, shared by every Ui and renderer drawing to it, so their rows stay in order.</summary>
    public static Screen For(TextWriter writer, Theme theme, Func<int> columns) =>
        Shared.GetValue(writer, w => new Screen(new WriterTerminal(w, columns), theme, bottom: false, paced: false));

    public object Gate { get; } = new();
    public IPanel? Panel { get; set; }
    public IReadOnlyList<Block> Blocks => _blocks;

    public void Append(Block block)
    {
        lock (Gate)
        {
            // Two empty rows in a row show as one.
            if (block is BlankBlock && _blocks.Count > 0 && _blocks[^1] is BlankBlock) return;
            _blocks.Add(block);
        }
        Changed();
    }

    public void Remove(Block block)
    {
        lock (Gate)
        {
            RemoveAt(_blocks.IndexOf(block));
            for (var i = 1; i < _blocks.Count; i++)
                if (_blocks[i] is BlankBlock && _blocks[i - 1] is BlankBlock) RemoveAt(i--);
        }
        Changed();
    }

    void RemoveAt(int index)
    {
        if (index < 0) return;
        if (index < _anchorBlock) _anchorBlock--;
        else if (index == _anchorBlock) _anchorRow = 0;
        if (index < _doneBlock) _doneBlock--;
        else if (index == _doneBlock) _doneRow = 0;
        _layouts.Remove(_blocks[index]);
        _blocks.RemoveAt(index);
    }

    public bool IsOnScreen(Block block)
    {
        lock (Gate)
        {
            var index = _blocks.IndexOf(block);
            var (anchor, row) = _bottom ? (_anchorBlock, _anchorRow) : (_doneBlock, _doneRow);
            return index > anchor || index == anchor && row == 0;
        }
    }

    public void Signal(string control)
    {
        lock (Gate) _signals += control;
        Changed();
    }

    public void Changed()
    {
        if (_paced) { if (_wake.CurrentCount == 0) _wake.Release(); }
        else lock (Gate) Render();
    }

    /// <summary>Session mode: takes over the window (keeping what was above in the scrollback) and starts drawing.</summary>
    public void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            _terminal.Write("\e]0;Jazari\a\e[0m\e[H\e[2J");
            (_width, _height) = _terminal.Size;
            _shown = Blank(_height);
        }
        if (_paced) _loop = Task.Run(LoopAsync);
        Changed();
    }

    /// <summary>Draws the last frame, takes the panel away and leaves the cursor under the conversation.</summary>
    public async Task StopAsync()
    {
        lock (Gate)
        {
            if (!_started) return;
            _stopping = true;
        }
        _wake.Release();
        if (_loop is { } loop) await loop;
        lock (Gate)
        {
            Panel = null;
            Render();
            var end = Math.Min(Rows(_anchorBlock, _anchorRow, _width - 1).Count, _height - 1);
            _terminal.Write($"\e[?2026l\e[0m{Go(end)}\e[J\e[?25h");
            _started = false;
        }
    }

    /// <summary>
    /// For an exit that skips <see cref="StopAsync"/> (Ctrl+Break, the process ending): ends any half-sent synchronized
    /// update, clears the panel and shows the cursor. Runs on the signal's thread, so it waits only briefly for a frame in progress.
    /// </summary>
    public void Restore()
    {
        var locked = Monitor.TryEnter(Gate, TimeSpan.FromMilliseconds(200));
        try
        {
            if (!locked) { _terminal.Write("\e[?2026l\e[0m\e[?25h"); return; }
            if (!_started) return;
            _started = false;
            _stopping = true;
            _wake.Release();
            _terminal.Write($"\e[?2026l\e[0m{Go(Math.Max(_area, 0))}\e[J\e[?25h");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { } // the console is already gone
        finally { if (locked) Monitor.Exit(Gate); }
    }

    /// <summary>Ctrl+L: clears the window and draws everything again.</summary>
    public void Redraw()
    {
        lock (Gate)
        {
            if (!_started) return;
            _terminal.Write("\e[0m\e[H\e[J");
            _shown = Blank(_height);
            _caret = null;
        }
        Changed();
    }

    /// <summary>Draws a frame now, on this thread.</summary>
    public void RenderNow()
    {
        lock (Gate) Render();
    }

    async Task LoopAsync()
    {
        var lastFrame = -1000L;
        while (true)
        {
            int wait;
            lock (Gate)
            {
                if (_stopping) return;
                wait = _pendingResize ? 100 : _blocks.Any(b => b.Timed) ? 1000 - (int)(_clock.ElapsedMilliseconds % 1000) : 100;
            }
            await _wake.WaitAsync(Math.Max(wait, 1));
            // At most about 60 frames a second: a burst of changes draws once.
            var since = _clock.ElapsedMilliseconds - lastFrame;
            if (since < 16) await Task.Delay((int)(16 - since));
            lock (Gate)
            {
                if (_stopping) return;
                try { Render(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or ArgumentOutOfRangeException) { }
            }
            lastFrame = _clock.ElapsedMilliseconds;
        }
    }

    void Render()
    {
        if (_bottom) RenderBottom();
        else RenderFlow();
    }

    // -------- layout --------

    IReadOnlyList<Line> Lines(Block block, int columns)
    {
        if (block.Live) return block.Render(columns);
        if (_layouts.TryGetValue(block, out var layout) && layout.Columns == columns && layout.Version == block.Version) return layout.Lines;
        var lines = block.Render(columns);
        _layouts[block] = (columns, block.Version, lines);
        return lines;
    }

    List<Line> Rows(int fromBlock, int fromRow, int columns)
    {
        List<Line> rows = [];
        for (var i = fromBlock; i < _blocks.Count; i++)
        {
            var lines = Lines(_blocks[i], columns);
            rows.AddRange(i == fromBlock ? lines.Skip(fromRow) : lines);
        }
        return rows;
    }

    void Advance(ref int block, ref int row, int count, int columns)
    {
        row += count;
        while (block < _blocks.Count - 1)
        {
            var rows = Lines(_blocks[block], columns).Count;
            if (row < rows) break;
            row -= rows;
            block++;
        }
    }

    void Retreat(int count, int columns)
    {
        _anchorRow -= count;
        while (_anchorRow < 0 && _anchorBlock > 0) _anchorRow += Lines(_blocks[--_anchorBlock], columns).Count;
        _anchorRow = Math.Max(_anchorRow, 0);
    }

    static string Go(int row, int column = 0) => $"\e[{row + 1};{column + 1}H";

    string[] Blank(int rows) => Enumerable.Repeat(_blank, rows).ToArray();

    // -------- session mode --------

    void RenderBottom()
    {
        if (!_started) return;
        var (width, height) = _terminal.Size;
        (width, height) = (Math.Max(width, 2), Math.Max(height, 2));
        var columns = width - 1;
        var frame = new StringBuilder();

        if (width != _width || height != _height)
        {
            // While the window is being dragged, lay out at most ten times a second.
            if (_paced && _clock.ElapsedMilliseconds - _lastResize < 100) { _pendingResize = true; return; }
            Resize(width, height, frame);
        }
        _pendingResize = false;

        var panel = Panel?.Render(columns, height) ?? new PanelFrame([], 0, null);
        var panelRows = panel.Rows.Count < height ? panel.Rows : panel.Rows.Skip(panel.Rows.Count - (height - 1)).ToList();
        var area = height - Math.Min(panel.Base, panelRows.Count);
        _area = area;

        var doc = Rows(_anchorBlock, _anchorRow, columns);
        // Rows that no longer fit leave by real scrolling, so they land in the scrollback; they're drawn first if needed.
        while (doc.Count > area)
        {
            var count = Math.Min(doc.Count - area, area);
            for (var i = 0; i < count; i++) Put(frame, i, _theme.Encode(doc[i], columns));
            frame.Append("\e[0m").Append(Go(height - 1)).Append('\n', count);
            _shown = [.. _shown.Skip(count), .. Blank(count)];
            doc.RemoveRange(0, count);
            Advance(ref _anchorBlock, ref _anchorRow, count, columns);
        }

        var top = height - panelRows.Count;
        for (var i = 0; i < height; i++)
        {
            var row = i >= top ? _theme.Encode(panelRows[i - top], columns) : i < doc.Count ? _theme.Encode(doc[i], columns) : _blank;
            if (_shown[i] != row) Put(frame, i, row);
        }

        (int Row, int Column)? caret = panel.Caret is { } c ? (top + c.Row, Math.Min(c.Column, columns - 1)) : null;
        if (frame.Length == 0 && caret == _caret && _signals.Length == 0) return; // nothing to write: a scrolled-up view stays put
        var output = new StringBuilder("\e[?2026h\e[?25l").Append(_signals).Append(frame);
        if (caret is { } at) output.Append(Go(at.Row, at.Column)).Append("\e[?25h");
        output.Append("\e[?2026l");
        _terminal.Write(output.ToString());
        (_caret, _signals) = (caret, "");
    }

    void Put(StringBuilder frame, int row, string encoded)
    {
        frame.Append(Go(row)).Append(encoded);
        _shown[row] = encoded;
    }

    /// <summary>
    /// The window changed size. A wider or narrower window lays every block out again; the first visible row keeps its
    /// place in its block by proportion, erring towards showing a row twice rather than losing one. A taller window shows
    /// rows from the scrollback again, as the terminal does; a shorter one has pushed its top rows into the scrollback.
    /// </summary>
    void Resize(int width, int height, StringBuilder frame)
    {
        var (oldColumns, columns) = (_width - 1, width - 1);
        if (width != _width && _anchorBlock < _blocks.Count && _anchorRow > 0)
        {
            var before = Lines(_blocks[_anchorBlock], oldColumns).Count;
            var after = Lines(_blocks[_anchorBlock], columns).Count;
            _anchorRow = before == 0 ? 0 : Math.Min(after, _anchorRow * after / before);
        }
        var taller = height - _height;
        if (taller > 0) Retreat(taller, columns);
        else if (taller < 0) Advance(ref _anchorBlock, ref _anchorRow, -taller, columns);
        frame.Append("\e[0m\e[H\e[J");
        (_width, _height) = (width, height);
        _shown = Blank(height);
        _caret = null;
        _lastResize = _clock.ElapsedMilliseconds;
    }

    // -------- flow mode --------

    void RenderFlow()
    {
        var columns = _terminal.Size.Width - 1;
        var rows = Rows(_doneBlock, _doneRow, columns);

        // Rows that won't change: everything up to the first live block's unsettled rows.
        var stable = 0;
        for (var i = _doneBlock; i < _blocks.Count; i++)
        {
            var lines = Lines(_blocks[i], columns);
            var start = i == _doneBlock ? Math.Min(_doneRow, lines.Count) : 0;
            var settled = Math.Clamp(_blocks[i].Stable(columns, lines.Count) - start, 0, lines.Count - start);
            stable += settled;
            if (settled < lines.Count - start) break;
        }

        var tail = rows.Skip(stable).Select(r => _theme.Encode(r, columns)).ToArray();
        if (stable == 0 && tail.SequenceEqual(_tail) && _signals.Length == 0) return;

        var output = new StringBuilder(_signals);
        if (_tail.Length > 0) output.Append($"\e[{_tail.Length}A\r\e[J");
        foreach (var row in rows.Take(stable)) output.Append(_theme.Encode(row, columns)).Append("\r\n");
        foreach (var row in tail) output.Append(row).Append("\r\n");
        _terminal.Write(output.ToString());
        Advance(ref _doneBlock, ref _doneRow, stable, columns);
        (_tail, _signals) = (tail, "");
    }
}
