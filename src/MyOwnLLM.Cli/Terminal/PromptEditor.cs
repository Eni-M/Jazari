using System.Diagnostics;
using System.Globalization;
using System.Text;
using Grid = MyOwnLLM.Cli.Terminal.Layout;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>What the panel around the prompt shows besides the text being typed.</summary>
/// <param name="Chip">What goes with the next question ("Program.cs", "Queue/ · 2 files").</param>
/// <param name="Hint">The one thing worth knowing right now ("Enter sends · / commands").</param>
/// <param name="Status">Model, device and speed, or "starting granite 3b".</param>
/// <param name="Meter">Context used of the total, in tokens.</param>
/// <param name="Offline">The gateway didn't answer: a red dot instead of the status.</param>
/// <param name="Queued">Questions sent during an answer, waiting their turn.</param>
/// <param name="Compact">The status for the narrow footer under a question: "granite 3b · 14 tok/s · 6.3k of 16k".</param>
public sealed record PanelInfo(string? Chip, string Hint, string Status, (int Used, int Total)? Meter = null, bool Offline = false, QueueRows? Queued = null, string? Compact = null);

/// <summary>The waiting questions, compared by what they say, so the panel only redraws when they change.</summary>
public sealed class QueueRows : List<string>
{
    public QueueRows() { }
    public QueueRows(IEnumerable<string> rows) : base(rows) { }
    public override bool Equals(object? other) => other is QueueRows rows && this.SequenceEqual(rows);
    public override int GetHashCode() => Count;
}

public enum KeyResult { Editing, Submit, Cancel }

/// <summary>The text being typed and everything keys do to it. No console in here, so all of it can be tested.</summary>
public sealed class PromptBuffer(Func<string> directory, IEnumerable<string>? history = null)
{
    readonly List<string> _history = history?.ToList() ?? [];
    int _historyAt = -1; // -1: not walking the history
    string _draft = "";
    string? _closedFor; // the text Esc closed the menu on

    public string Text { get; private set; } = "";
    public int Caret { get; private set; }
    public int Selected { get; private set; }
    /// <summary>Where a chosen item replaces the text from, and what can be chosen. No items: no menu.</summary>
    public (int Start, IReadOnlyList<string> Items) Menu { get; private set; } = (0, []);
    public IReadOnlyList<string> History => _history;

    public KeyResult Apply(ConsoleKeyInfo key, bool morePending = false)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var pressed = key.Key;
        if (control && pressed == ConsoleKey.C) return KeyResult.Cancel;

        // A key waiting right behind Enter means a paste, and a pasted line break doesn't send.
        if (pressed == ConsoleKey.Enter && (morePending || key.Modifiers.HasFlag(ConsoleModifiers.Shift) || key.Modifiers.HasFlag(ConsoleModifiers.Alt) || control)
            || control && pressed == ConsoleKey.J) { Insert("\n"); return KeyResult.Editing; }

        switch (pressed)
        {
            case ConsoleKey.Enter:
                if (Menu.Items.Count == 0) return KeyResult.Submit;
                if (Accept(enter: true)) return KeyResult.Submit;
                break;
            case ConsoleKey.Tab when Menu.Items.Count > 0: Accept(enter: false); break;
            case ConsoleKey.Tab: break;
            case ConsoleKey.Escape when Menu.Items.Count > 0: _closedFor = Text; Refresh(); break;
            case ConsoleKey.Escape: break;
            case ConsoleKey.UpArrow: Vertical(-1); break;
            case ConsoleKey.DownArrow: Vertical(1); break;
            case ConsoleKey.LeftArrow: Move(control ? WordStart() : Before(Caret)); break;
            case ConsoleKey.RightArrow: Move(control ? WordEnd() : After(Caret)); break;
            case ConsoleKey.Home: Move(LineStart()); break;
            case ConsoleKey.End: Move(LineEnd()); break;
            case ConsoleKey.Backspace when control: Remove(WordStart(), Caret); break;
            case ConsoleKey.W when control: Remove(WordStart(), Caret); break;
            case ConsoleKey.Backspace: Remove(Before(Caret), Caret); break;
            case ConsoleKey.Delete: Remove(Caret, After(Caret)); break;
            case ConsoleKey.U when control: Remove(LineStart(), Caret); break;
            case ConsoleKey.K when control: Remove(Caret, LineEnd()); break;
            default:
                // AltGr arrives as Ctrl+Alt with a real character, so the character decides, not the modifiers.
                if (!char.IsControl(key.KeyChar)) Insert(key.KeyChar.ToString());
                break;
        }
        return KeyResult.Editing;
    }

    /// <summary>Hands over the text for sending, remembers it, and starts empty again.</summary>
    public string Take()
    {
        var text = Text;
        if (text.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != text)) _history.Add(text);
        Reset();
        return text;
    }

    public void Reset()
    {
        Text = "";
        Caret = 0;
        _historyAt = -1;
        _closedFor = null;
        Refresh();
    }

    /// <summary>Typed or pasted text, inserted at the caret in one step: a paste costs one completion lookup, not one per character.</summary>
    public void Type(string text) => Insert(text);

    void Insert(string text)
    {
        Text = Text.Insert(Caret, text);
        Caret += text.Length;
        Changed();
    }

    void Remove(int from, int to)
    {
        if (to <= from) return;
        Text = Text.Remove(from, to - from);
        Caret = from;
        Changed();
    }

    void Move(int to)
    {
        Caret = to;
        Refresh();
    }

    void Changed()
    {
        Selected = 0;
        _historyAt = -1;
        _closedFor = null;
        Refresh();
    }

    void Refresh()
    {
        var (start, items) = Completions.For(Text, Caret, directory());
        var typed = Text[start..Caret];
        // A single item that's already typed out has nothing left to offer, and Enter should send it.
        var nothingLeft = items.Count == 1 && items[0].Equals(typed, StringComparison.OrdinalIgnoreCase);
        Menu = _closedFor == Text || nothingLeft ? (start, []) : (start, items);
        Selected = Menu.Items.Count == 0 ? 0 : Math.Min(Selected, Menu.Items.Count - 1);
    }

    /// <summary>Puts the chosen item in the text. True when Enter should then send it: a command that doesn't need anything more.</summary>
    bool Accept(bool enter)
    {
        var (start, items) = Menu;
        var item = items[Selected];
        var command = Session.Commands.FirstOrDefault(c => c.Command.Split(' ')[0] == item).Command;
        Text = Text[..start] + item + Text[Caret..];
        Caret = start + item.Length;
        var send = false;
        if (command is not null)
        {
            var needsMore = command.Contains('<');
            send = enter && !needsMore;
            if (!send && command.Contains(' ') && !Text[Caret..].StartsWith(' ')) { Text = Text.Insert(Caret, " "); Caret++; }
        }
        Selected = 0;
        Refresh();
        return send;
    }

    void Vertical(int direction)
    {
        if (Menu.Items.Count > 0) { Selected = (Selected + direction + Menu.Items.Count) % Menu.Items.Count; return; }

        // Inside typed lines the arrows move the caret; off the top or bottom they walk the history.
        var (start, end) = (LineStart(), LineEnd());
        if (direction < 0 && start > 0)
        {
            var above = Text.LastIndexOf('\n', start - 2 < 0 ? 0 : start - 2) + 1;
            Move(Math.Min(above + Caret - start, start - 1));
            return;
        }
        if (direction > 0 && end < Text.Length)
        {
            var next = end + 1;
            var nextEnd = Text.IndexOf('\n', next);
            Move(Math.Min(next + Caret - start, nextEnd < 0 ? Text.Length : nextEnd));
            return;
        }

        if (_history.Count == 0) return;
        if (_historyAt < 0)
        {
            if (direction > 0) return;
            _draft = Text;
            _historyAt = _history.Count;
        }
        _historyAt = Math.Clamp(_historyAt + direction, 0, _history.Count);
        Text = _historyAt == _history.Count ? _draft : _history[_historyAt];
        if (_historyAt == _history.Count) _historyAt = -1;
        Caret = Text.Length;
        _closedFor = Text; // recalling "/read x" shouldn't open a menu over it
        Refresh();
    }

    // By grapheme cluster: an emoji with its skin tone or a letter with its accent moves and deletes as one.
    int Before(int at) => at <= 0 ? 0 : TextWidth.Previous(Text, at);
    int After(int at) => TextWidth.Next(Text, at);
    int LineStart() => Caret == 0 ? 0 : Text.LastIndexOf('\n', Caret - 1) + 1;
    int LineEnd() => Text.IndexOf('\n', Caret) is var i and >= 0 ? i : Text.Length;

    int WordStart()
    {
        var i = Caret;
        while (i > 0 && char.IsWhiteSpace(Text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(Text[i - 1])) i--;
        return i;
    }

    int WordEnd()
    {
        var i = Caret;
        while (i < Text.Length && char.IsWhiteSpace(Text[i])) i++;
        while (i < Text.Length && !char.IsWhiteSpace(Text[i])) i++;
        return i;
    }
}

/// <summary>
/// Draws the panel at the bottom of the window as rows: a thin top edge, the command menu or queue when there is one,
/// the prompt, the footer and a padding row, all on the panel's background. Four rows keep room for themselves; a menu,
/// a queue or a multi-line prompt grows upward over the conversation instead of pushing it into the scrollback.
/// </summary>
public static class PromptPanel
{
    const int CommandColumn = 24;
    const int QueueShown = 3;
    public const int BaseRows = 4;

    public static PanelFrame Draw(Theme theme, PromptBuffer buffer, PanelInfo info, int columns, int height, string? question = null)
    {
        var rows = new List<Line> { Edge(theme, columns) };
        if (question is not null)
        {
            rows.Add(QuestionRow(theme, question, columns));
            rows.Add(Footer(theme, info with { Hint = "/undo puts it back" }, columns, menuOpen: false, compact: true));
            rows.Add(Padding());
            return new(rows, BaseRows, null);
        }

        var open = buffer.Menu.Items.Count > 0;
        if (open) rows.AddRange(MenuRows(theme, buffer, columns, height));
        if (info.Queued is { Count: > 0 } queued) rows.AddRange(QueueRowsFor(queued, columns));

        var room = Math.Max(columns - Grid.Gutter - Grid.Margin(columns), 8);
        var (lines, caretRow, caretColumn) = Layout(buffer.Text, buffer.Caret, room);
        // A paste taller than the window: show the part around the caret, and keep the footer on screen.
        var fit = Math.Max(height - rows.Count - 3, 1);
        var start = Math.Clamp(caretRow - fit + 1, 0, Math.Max(lines.Count - fit, 0));
        var first = rows.Count;
        var (marker, text) = info.Offline ? (Tone.Muted, Tone.Muted) : (Tone.Accent, Tone.Primary);
        foreach (var (line, i) in lines.Select((line, i) => (line, i)).Skip(start).Take(fit))
            rows.Add(OnPanel(Grid.Row(i == start ? theme.Glyph('›').ToString() : null, marker, bold: true).Add(line, text)));
        rows.Add(Footer(theme, info, columns, open, compact: false));
        rows.Add(Padding());
        return new(rows, BaseRows, (first + caretRow - start, Grid.Gutter + caretColumn));
    }

    /// <summary>Every piece on the panel's background, and the rest of the row too.</summary>
    static Line OnPanel(LineBuilder row) =>
        new(row.Build().Spans.Select(s => s with { Style = s.Style with { Bg = s.Style.Bg ?? Surface.Panel } }).ToList(), Surface.Panel);

    static Line Padding() => new([], Surface.Panel);

    /// <summary>The panel's 1 px top border: a one-eighth block along the top of a padding row.</summary>
    static Line Edge(Theme theme, int columns) =>
        theme.Edges ? OnPanel(new LineBuilder().Add(new string('▔', columns), Tone.Border)) : Padding();

    static Line QuestionRow(Theme theme, string question, int columns)
    {
        var apply = " y  apply ";
        var skip = theme.Edges ? " n  skip " : " n  skip ";
        var buttons = 2 + TextWidth.Of(apply) + 2 + TextWidth.Of(skip) + (theme.Edges ? 2 : 0);
        var room = Math.Max(columns - Grid.Gutter - Grid.Margin(columns) - buttons, 8);
        var row = Grid.Row("?", Tone.Accent, bold: true).Add(TextWidth.Fit(ConsoleText.Sanitize(question), room), Tone.Primary).Add("  ")
            .Add(apply, new Style(Tone.Ink, Surface.Accent)).Add("  ");
        if (theme.Edges) row.Add("▕", Tone.ChipBorder).Add(skip, Tone.Secondary).Add("▏", Tone.ChipBorder);
        else row.Add(skip, new Style(Tone.Secondary, Surface.Selected));
        return OnPanel(row);
    }

    static IEnumerable<Line> QueueRowsFor(QueueRows queued, int columns)
    {
        var room = Math.Max(columns - Grid.Gutter - Grid.Margin(columns), 8);
        foreach (var question in queued.Take(QueueShown))
            yield return OnPanel(Grid.Row("queue").Add(TextWidth.Fit(ConsoleText.Sanitize(question).Replace('\n', ' '), room), Tone.Secondary));
        if (queued.Count > QueueShown) yield return OnPanel(Grid.Row().Add($"and {queued.Count - QueueShown} more", Tone.Muted));
    }

    /// <summary>Where each character lands: rows cut at the terminal's edge and at line breaks, and the row and column of the caret.</summary>
    public static (List<string> Rows, int CaretRow, int CaretColumn) Layout(string text, int caret, int room)
    {
        var rows = new List<string>();
        var row = new StringBuilder();
        var (column, caretRow, caretColumn) = (0, 0, 0);
        var clusters = TextWidth.Clusters(text).ToList();
        for (var n = 0; n <= clusters.Count; n++)
        {
            var i = n < clusters.Count ? clusters[n].Start : text.Length;
            if (i == caret || n > 0 && caret > clusters[n - 1].Start && caret < i) (caretRow, caretColumn) = (rows.Count, column);
            if (n == clusters.Count) break;
            var (start, length) = clusters[n];
            if (text[start] == '\n')
            {
                rows.Add(row.ToString());
                row.Clear();
                column = 0;
                continue;
            }
            var width = TextWidth.Cluster(text, start, length);
            if (column + width > room)
            {
                rows.Add(row.ToString());
                row.Clear();
                column = 0;
                if (i == caret) (caretRow, caretColumn) = (rows.Count, 0);
            }
            row.Append(text, start, length);
            column += width;
        }
        rows.Add(row.ToString());
        return (rows, caretRow, caretColumn);
    }

    static IEnumerable<Line> MenuRows(Theme theme, PromptBuffer buffer, int columns, int height)
    {
        var (start, items) = buffer.Menu;
        var typed = Math.Max(buffer.Caret - start, 0);
        var shown = Math.Min(Math.Max(height - 8, 3), items.Count);
        var top = Math.Clamp(buffer.Selected - shown / 2, 0, items.Count - shown);
        var width = Math.Max(Math.Min(Grid.Body(columns), columns - Grid.Gutter), 4);
        var column = Math.Max(Math.Min(CommandColumn, width - 2), 1);
        foreach (var (item, index) in items.Select((item, i) => (item, i)).Skip(top).Take(shown))
        {
            var entry = Session.Commands.FirstOrDefault(c => c.Command.Split(' ')[0] == item);
            var extra = entry.Command is { } command && command.Contains(' ') ? command[item.Length..] : "";
            var name = TextWidth.Fit(ConsoleText.Sanitize(item), width - 2, "");
            if (TextWidth.Of(name) + TextWidth.Of(extra) >= column) extra = "";
            var typedPart = name[..Math.Min(typed, name.Length)];
            var selected = index == buffer.Selected;
            var surface = selected ? Surface.Selected : Surface.Panel;
            var row = Grid.Row().Add(" ", new Style(Bg: surface))
                .Add(typedPart, new Style(Tone.Accent, surface)).Add(name[typedPart.Length..], new Style(Tone.Primary, surface)).Add(extra, new Style(Tone.Muted, surface))
                .PadTo(Grid.Gutter + 1 + column, new Style(Bg: surface));
            var description = TextWidth.Fit(entry.What ?? "", Grid.Gutter + width - row.Width - 1);
            row.Add(description, new Style(selected ? Tone.Secondary : Tone.Muted, surface)).PadTo(Grid.Gutter + width, new Style(Bg: surface));
            yield return OnPanel(row);
        }
        if (theme.Edges) yield return OnPanel(Grid.Row().Add(new string('▔', width), Tone.Border));
        var dot = theme.Glyph('·');
        var hint = $"Up and Down choose {dot} Tab completes {dot} Esc closes" + (start == 0 ? $" {dot} all {Session.Commands.Length} commands: /" : "");
        yield return OnPanel(Grid.Row().Add(" " + TextWidth.Fit(hint, width - 1), Tone.Muted));
    }

    /// <summary>
    /// The row under the prompt: what goes with the next question and the hint on the left; model, device, speed and the
    /// context meter on the right. While the menu is open only the status shows; under a question the status is compact.
    /// Narrow terminals lose the least useful parts first.
    /// </summary>
    static Line Footer(Theme theme, PanelInfo info, int columns, bool menuOpen, bool compact)
    {
        var width = Math.Max(columns - Grid.Gutter - Grid.Margin(columns), 8);
        var chip = !menuOpen && info.Chip is { Length: > 0 } c ? TextWidth.Fit(ConsoleText.Sanitize(c), 28) : null;
        var chipWidth = chip is null ? 0 : TextWidth.Of(chip) + (theme.Edges ? 4 : 2) + 1;
        var hint = menuOpen ? "" : ConsoleText.Sanitize(info.Hint);
        var status = info.Offline ? "not connected" : ConsoleText.Sanitize(compact ? info.Compact ?? info.Status : info.Status);
        var statusWidth = TextWidth.Of(status) + (info.Offline ? 2 : 0);
        var meter = !menuOpen && !compact && !info.Offline && info.Meter is { Total: > 0 } m ? m : ((int Used, int Total)?)null;
        var numbers = meter is { } mm ? $" {Tokens(mm.Used)} of {Tokens(mm.Total)}" : "";
        var meterWidth = meter is null ? 0 : 2 + 10 + numbers.Length;

        if (chipWidth + TextWidth.Of(hint) + statusWidth + meterWidth + 2 > width) (meter, meterWidth) = (null, 0);
        if (chipWidth + TextWidth.Of(hint) + statusWidth + 2 > width) hint = "";
        if (chipWidth + statusWidth + 2 > width) (chip, chipWidth) = (null, 0);
        if (statusWidth > width) (status, statusWidth) = (TextWidth.Fit(status, width - (info.Offline ? 2 : 0)), width);

        var row = Grid.Row();
        if (chip is not null)
        {
            if (theme.Edges) row.Add("▕", Tone.ChipBorder).Add($" {chip} ", Tone.Secondary).Add("▏", Tone.ChipBorder);
            else row.Add($" {chip} ", new Style(Tone.Secondary, Surface.Selected));
            row.Add(" ");
        }
        row.Add(hint, Tone.Muted).PadTo(Grid.Gutter + width - statusWidth - meterWidth);
        if (info.Offline) row.Add(theme.Glyph('●').ToString(), Tone.Danger).Add(" ");
        row.Add(status, Tone.Muted);
        if (meter is { } used)
        {
            var filled = Math.Clamp(used.Used * 10 / used.Total, used.Used > 0 ? 1 : 0, 10);
            var cell = theme.Glyph('■');
            row.Add("  ").Add(new string(cell, filled), Tone.Secondary).Add(new string(cell, 10 - filled), Tone.MeterEmpty).Add(numbers, Tone.Muted);
        }
        return OnPanel(row);
    }

    /// <summary>"950", "4.1k", "16k".</summary>
    public static string Tokens(int n) =>
        n < 1000 ? n.ToString(CultureInfo.InvariantCulture)
        : n < 10000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";
}

/// <summary>
/// The prompt on a real console. The panel stays at the bottom of the window from start to exit: keys are read the whole
/// time, so the user can type while an answer streams, and whatever they send meanwhile waits in a visible queue. The
/// <see cref="Screen"/> draws it; this class reads keys, keeps the text and the queue, and asks yes/no questions.
/// </summary>
public sealed class PromptEditor : ILineReader, IPanel
{
    readonly Ui _ui;
    readonly Screen _screen;
    readonly ITerminal _terminal;
    readonly PromptBuffer _buffer;
    readonly string _historyFile;
    readonly List<(bool Ok, string? Text)> _queue = [];
    readonly SemaphoreSlim _ready = new(0);
    Task? _pump;
    bool _running;
    bool _treatedControlC;
    Exception? _failure;
    string? _question; // the panel is asking yes or no
    bool _answer;
    ManualResetEventSlim? _answered;
    PanelInfo? _drawnWith;

    /// <summary>What the panel shows; read again while waiting for keys, so a server coming up shows without a key press.</summary>
    public Func<PanelInfo> Panel { get; set; } = () => new(null, "", "");
    /// <summary>Ctrl+C: stops what's running, returning true. False means nothing was, and it counts toward quitting.</summary>
    public Func<bool>? Interrupt { get; set; }
    /// <summary>An answer or command is running, so Enter queues.</summary>
    public bool Busy { get; set; }

    public PromptEditor(Ui ui, Screen screen, ITerminal terminal, Func<string> directory, string? historyFile = null)
    {
        _ui = ui;
        _screen = screen;
        _terminal = terminal;
        _historyFile = historyFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "history");
        _buffer = new PromptBuffer(directory, LoadHistory(_historyFile));
    }

    object Gate => _screen.Gate;

    /// <summary>Base64, one entry a line: a typed line break can't split an entry.</summary>
    static IEnumerable<string> LoadHistory(string path)
    {
        string[] lines;
        try { lines = File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var entries = new List<string>();
        foreach (var line in lines.TakeLast(500))
        {
            try { entries.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line))); }
            catch (FormatException) { } // not an entry; the rest are still good
        }
        return entries;
    }

    void Remember(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
            File.AppendAllText(_historyFile, Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // history is a convenience
    }

    /// <summary>Puts the panel on screen and starts reading keys. From here on the screen draws the conversation above it.</summary>
    public void Start()
    {
        lock (Gate)
        {
            if (_running) return;
            _running = true;
            _treatedControlC = _terminal.TreatControlCAsInput;
            _terminal.TreatControlCAsInput = true;
            _screen.Panel = this;
        }
        _screen.Start();
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Takes the panel away and leaves the cursor under the conversation.</summary>
    public async Task StopAsync()
    {
        lock (Gate) _running = false;
        if (_pump is { } pump) await pump;
        await _screen.StopAsync();
        try { _terminal.TreatControlCAsInput = _treatedControlC; }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    /// <summary>For an exit that skips <see cref="StopAsync"/> (Ctrl+Break while idle, the process ending): see <see cref="Screen.Restore"/>.</summary>
    public void Restore()
    {
        _screen.Restore();
        try { _terminal.TreatControlCAsInput = _treatedControlC; }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    public PanelFrame Render(int columns, int height)
    {
        var info = CurrentInfo();
        _drawnWith = info;
        return PromptPanel.Draw(_ui.Theme, _buffer, info, columns, height, _question);
    }

    PanelInfo CurrentInfo()
    {
        var info = Panel();
        var dot = _ui.Theme.Glyph('·');
        var queued = _queue.Where(q => q.Ok).Select(q => q.Text ?? "").ToList();
        // The record's equality would see a new list as a change, so an empty queue stays null.
        return info with { Queued = queued.Count > 0 ? new QueueRows(queued) : null, Hint = Busy ? $"Enter queues {dot} Ctrl+C stops" : info.Hint };
    }

    /// <summary>The next thing the user sent: in the order sent, and right away if they sent it during an answer.</summary>
    public async Task<(bool Ok, string? Text)> ReadAsync()
    {
        Start();
        while (true)
        {
            await _ready.WaitAsync();
            (bool Ok, string? Text) next;
            lock (Gate)
            {
                if (_failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                if (_queue.Count == 0) continue; // Ctrl+C cleared it
                next = _queue[0];
                _queue.RemoveAt(0);
            }
            _screen.Changed();
            if (next is (true, { Length: > 0 } text))
            {
                _ui.Blank();
                _ui.UserMessage(text);
                _ui.Blank();
            }
            return next;
        }
    }

    async Task PumpAsync()
    {
        try
        {
            var sinceKey = Stopwatch.StartNew();
            var sinceInfo = Stopwatch.StartNew();
            while (true)
            {
                lock (Gate) { if (!_running) return; }
                if (!_terminal.KeyAvailable)
                {
                    // The panel's own text (status, meter, the server coming up) can change without a key: look a few times a second.
                    if (sinceInfo.ElapsedMilliseconds >= 250)
                    {
                        sinceInfo.Restart();
                        bool changed;
                        lock (Gate) changed = CurrentInfo() != _drawnWith;
                        if (changed) _screen.Changed();
                    }
                    await Task.Delay(sinceKey.ElapsedMilliseconds < 300 ? 8 : 33);
                    continue;
                }
                var keys = Drain();
                // A paste can arrive in pieces; one that ends on a line break waits a moment for the rest, so the break
                // stays a new line instead of sending half the paste.
                if (keys.Count > 0 && keys[^1].Key == ConsoleKey.Enter)
                {
                    await Task.Delay(15);
                    keys.AddRange(Drain());
                }
                sinceKey.Restart();
                lock (Gate) Handle(keys);
                _screen.Changed();
            }
        }
        catch (Exception e)
        {
            lock (Gate) _failure = e;
            _ready.Release();
        }
    }

    List<ConsoleKeyInfo> Drain()
    {
        var keys = new List<ConsoleKeyInfo>();
        while (keys.Count < 100_000 && _terminal.KeyAvailable) keys.Add(_terminal.ReadKey());
        return keys;
    }

    /// <summary>Keys in one batch: runs of plain characters go in as one piece; a line break followed by more keys is part of a paste.</summary>
    void Handle(List<ConsoleKeyInfo> keys)
    {
        var run = new StringBuilder();
        void Flush()
        {
            if (run.Length == 0) return;
            _buffer.Type(run.ToString());
            run.Clear();
        }
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (_question is not null) { Flush(); AnswerQuestion(key); continue; }
            var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            if (control && key.Key == ConsoleKey.L) { Flush(); _screen.Redraw(); continue; }
            if (IsPlain(key)) { run.Append(key.KeyChar); continue; }
            Flush();
            Apply(key, more: i < keys.Count - 1);
        }
        Flush();
    }

    static bool IsPlain(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var altGr = control && key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        return !char.IsControl(key.KeyChar) && (!control || altGr) && key.Key is not (ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.Escape
            or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End
            or ConsoleKey.Backspace or ConsoleKey.Delete);
    }

    void Apply(ConsoleKeyInfo key, bool more)
    {
        var typed = _buffer.Text;
        switch (_buffer.Apply(key, key.Key == ConsoleKey.Enter && more))
        {
            case KeyResult.Cancel:
                if (Interrupt?.Invoke() == true) { _queue.Clear(); break; } // stopping includes what was lined up behind it
                _buffer.Reset();
                Send(false, typed);
                break;
            case KeyResult.Submit:
                var text = _buffer.Take();
                if (text.Trim().Length == 0) break;
                Remember(text);
                Send(true, text);
                break;
        }
    }

    void Send(bool ok, string text)
    {
        _queue.Add((ok, text));
        _ready.Release();
    }

    void AnswerQuestion(ConsoleKeyInfo key)
    {
        if (key.Key is not (ConsoleKey.Y or ConsoleKey.N or ConsoleKey.Escape) && !(key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))) return;
        _answer = key.Key == ConsoleKey.Y;
        _question = null;
        _answered?.Set();
    }

    /// <summary>The panel asking a yes/no question ("? Apply this change?  y apply  n skip"). Anything but y is no.</summary>
    public bool Ask(string question)
    {
        Start();
        using var answered = new ManualResetEventSlim();
        lock (Gate) (_question, _answered) = (question, answered);
        _screen.Changed();
        answered.Wait();
        lock (Gate)
        {
            _answered = null;
            return _answer;
        }
    }
}
