using System.Globalization;
using System.Text;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>What the panel around the prompt shows besides the text being typed.</summary>
/// <param name="Chip">What goes with the next question ("Program.cs", "Queue/ · 2 files").</param>
/// <param name="Hint">The one thing worth knowing right now ("Enter sends · / commands").</param>
/// <param name="Status">Model, device and speed, or "starting granite 3b".</param>
/// <param name="Meter">Context used of the total, in tokens.</param>
/// <param name="Offline">The gateway didn't answer: a red dot instead of the status.</param>
/// <param name="Live">A row that replaces itself above the prompt until it's gone.</param>
/// <param name="Queued">Questions sent during an answer, waiting their turn.</param>
public sealed record PanelInfo(string? Chip, string Hint, string Status, (int Used, int Total)? Meter = null, bool Offline = false, string? Live = null, QueueRows? Queued = null);

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

    int Before(int at) => at <= 0 ? 0 : at >= 2 && char.IsLowSurrogate(Text[at - 1]) ? at - 2 : at - 1;
    int After(int at) => at >= Text.Length ? Text.Length : at + 1 < Text.Length && char.IsHighSurrogate(Text[at]) ? at + 2 : at + 1;
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

/// <summary>Draws the panel: a thin line, the live row, the command menu, the prompt and the line under it. Returns rows, no console.</summary>
public static class PromptPanel
{
    const int CommandColumn = 24;

    public readonly record struct Drawn(List<string> Rows, int CaretRow, int CaretColumn);

    public static Drawn Draw(Ui ui, PromptBuffer buffer, PanelInfo info, int screenRows)
    {
        var theme = ui.Theme;
        var rows = new List<string> { ui.Rule() };
        if (info.Live is { } live) rows.Add(Band(theme, ui.Cell("live", Tone.Accent) + theme.Paint(ConsoleText.Sanitize(live), Tone.Secondary)));
        var open = buffer.Menu.Items.Count > 0;
        if (open) rows.AddRange(MenuRows(ui, buffer, screenRows));
        if (info.Queued is { Count: > 0 } queued) rows.AddRange(QueueRowsFor(ui, queued));

        var room = Math.Max(ui.Width - Ui.Gutter, 8);
        var (lines, caretRow, caretColumn) = Layout(buffer.Text, buffer.Caret, room);
        // A paste taller than the window: show the part around the caret, and keep the rest of the panel on screen.
        var fit = Math.Max(screenRows - rows.Count - 1, 1);
        var start = Math.Clamp(caretRow - fit + 1, 0, Math.Max(lines.Count - fit, 0));
        var first = rows.Count;
        var marker = ui.Cell(theme.Glyph('›').ToString(), info.Offline ? Tone.Muted : Tone.Accent);
        foreach (var (line, i) in lines.Select((line, i) => (line, i)).Skip(start).Take(fit))
            rows.Add(Band(theme, (i == start ? marker : ui.Cell()) + theme.Paint(line, info.Offline ? Tone.Muted : Tone.Primary)));
        rows.Add(Footer(ui, info, open));
        return new(rows, first + caretRow - start, Ui.Gutter + caretColumn);
    }

    /// <summary>The rows of a frame that aren't already on screen: all of them when it's new or has moved, otherwise only those that changed.</summary>
    public static string Frame(IReadOnlyList<string>? shown, int shownTop, IReadOnlyList<string> rows, int top)
    {
        var whole = shown is null || shownTop != top || shown.Count != rows.Count;
        var frame = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
            if (whole || shown![i] != rows[i]) frame.Append(Go(top + i)).Append(rows[i]);
        return frame.ToString();
    }

    public static string Go(int row, int column = 0) => $"\e[{row + 1};{column + 1}H";

    const int QueueShown = 3;

    static IEnumerable<string> QueueRowsFor(Ui ui, QueueRows queued)
    {
        var theme = ui.Theme;
        var room = Math.Max(ui.Width - Ui.Gutter, 8);
        foreach (var question in queued.Take(QueueShown))
        {
            var text = ConsoleText.Sanitize(question).Replace('\n', ' ');
            yield return Band(theme, ui.Cell("queue") + theme.Paint(text.Length > room ? text[..(room - 3)] + "..." : text, Tone.Secondary));
        }
        if (queued.Count > QueueShown) yield return Band(theme, ui.Cell() + theme.Paint($"and {queued.Count - QueueShown} more", Tone.Muted));
    }

    static string Band(Theme theme, string row) => theme.Band(Surface.Panel, row);

    /// <summary>Where each character lands: rows cut at the terminal's edge and at line breaks, and the row and column of the caret.</summary>
    public static (List<string> Rows, int CaretRow, int CaretColumn) Layout(string text, int caret, int room)
    {
        var rows = new List<string>();
        var row = new StringBuilder();
        var (column, caretRow, caretColumn) = (0, 0, 0);
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == caret) (caretRow, caretColumn) = (rows.Count, column);
            if (i == text.Length) break;
            var c = text[i];
            if (c == '\n')
            {
                rows.Add(row.ToString());
                row.Clear();
                column = 0;
                continue;
            }
            var width = char.IsLowSurrogate(c) ? 0 : ConsoleText.IsWide(c) ? 2 : 1;
            if (column + width > room)
            {
                rows.Add(row.ToString());
                row.Clear();
                column = 0;
                if (i == caret) (caretRow, caretColumn) = (rows.Count, 0);
            }
            row.Append(c);
            column += width;
        }
        rows.Add(row.ToString());
        return (rows, caretRow, caretColumn);
    }

    static IEnumerable<string> MenuRows(Ui ui, PromptBuffer buffer, int screenRows)
    {
        var theme = ui.Theme;
        var (start, items) = buffer.Menu;
        var typed = Math.Max(buffer.Caret - start, 0);
        var room = Math.Min(Math.Max(screenRows - 6, 3), items.Count);
        var top = Math.Clamp(buffer.Selected - room / 2, 0, items.Count - room);
        var width = ui.Width - Ui.Gutter - 1;
        var column = Math.Min(CommandColumn, width);
        foreach (var (item, index) in items.Select((item, i) => (item, i)).Skip(top).Take(room))
        {
            var entry = Session.Commands.FirstOrDefault(c => c.Command.Split(' ')[0] == item);
            var extra = entry.Command is { } command && command.Contains(' ') ? command[item.Length..] : "";
            if (item.Length + extra.Length >= column) extra = "";
            // A file name can be longer than the whole row.
            var name = ConsoleText.Sanitize(item.Length >= width ? item[..(width - 1)] : item);
            var shown = Math.Min(typed, name.Length);
            var gap = Math.Max(column - name.Length - extra.Length, 1);
            var cell = theme.Paint(name[..shown], Tone.Accent) + theme.Paint(name[shown..], Tone.Primary) + theme.Paint(extra, Tone.Muted) + new string(' ', gap);
            var left = width - name.Length - extra.Length - gap;
            var description = entry.What ?? "";
            if (description.Length > left) description = left > 3 ? description[..(left - 3)] + "..." : "";
            var selected = index == buffer.Selected;
            var row = ui.Cell() + theme.On(selected ? Surface.Selected : Surface.Panel, " " + cell + theme.Paint(description, selected ? Tone.Secondary : Tone.Muted));
            yield return theme.Band(Surface.Panel, row);
        }
        var dot = theme.Glyph('·');
        var hint = $"Up and Down choose {dot} Tab completes {dot} Esc closes" + (start == 0 ? $" {dot} all {Session.Commands.Length} commands: /" : "");
        yield return Band(theme, ui.Cell() + theme.Paint(" " + (hint.Length > width ? hint[..width] : hint), Tone.Muted));
    }

    static string Footer(Ui ui, PanelInfo info, bool menuOpen)
    {
        var theme = ui.Theme;
        var width = Math.Max(ui.Width - Ui.Gutter, 8);
        var chip = info.Chip is { Length: > 0 } c ? ConsoleText.Sanitize(c.Length > 28 ? c[..25] + "..." : c) : null;
        var chipCell = chip is null ? "" : theme.On(Surface.Selected, theme.Paint($" {chip} ", Tone.Secondary)) + " ";
        var chipLength = chip is null ? 0 : chip.Length + 3;
        var hint = menuOpen ? "" : ConsoleText.Sanitize(info.Hint);

        var meter = info.Meter is { Total: > 0 } m && !info.Offline ? Meter(theme, m.Used, m.Total) : ("", 0);
        var status = info.Offline ? " not connected" : ConsoleText.Sanitize(info.Status);
        var dot = info.Offline ? theme.Paint(theme.Glyph('●').ToString(), Tone.Danger) : "";
        var statusLength = (info.Offline ? 1 : 0) + status.Length;

        // Narrow terminals lose the least useful parts first.
        if (chipLength + hint.Length + statusLength + 2 + meter.Item2 + 2 > width) meter = ("", 0);
        if (chipLength + hint.Length + statusLength + meter.Item2 + 2 > width) hint = "";
        if (chipLength + statusLength + 2 > width) (chipCell, chipLength) = ("", 0);
        if (statusLength >= width) (dot, status, statusLength) = ("", status[..(width - 1)], width - 1);

        var gap = meter.Item2 > 0 ? 2 : 0;
        var pad = Math.Max(width - chipLength - hint.Length - statusLength - gap - meter.Item2, 1);
        return Band(theme, ui.Cell() + chipCell + theme.Paint(hint, Tone.Muted) + new string(' ', pad)
            + dot + theme.Paint(status, Tone.Muted) + new string(' ', gap) + meter.Item1);
    }

    /// <summary>
    /// Where the panel goes: at the bottom of the window, like the design. A conversation that has reached it scrolls up
    /// first, by real scrolling, so what leaves the window lands in the terminal's scrollback.
    /// </summary>
    public static (int Scroll, int Top) Place(int conversationRow, int panelRows, int screenRows)
    {
        var top = Math.Max(screenRows - panelRows, 0);
        return (Math.Max(conversationRow - top, 0), top);
    }

    /// <summary>Ten cells and "4.1k of 16k".</summary>
    static (string Text, int Length) Meter(Theme theme, int used, int total)
    {
        var filled = Math.Clamp(used * 10 / total, used > 0 ? 1 : 0, 10);
        var numbers = $"  {Tokens(used)} of {Tokens(total)}";
        var cell = theme.Glyph('■');
        return (theme.Paint(new string(cell, filled), Tone.Secondary) + theme.Paint(new string(cell, 10 - filled), Tone.Line) + theme.Paint(numbers, Tone.Muted), 10 + numbers.Length);
    }

    /// <summary>"950", "4.1k", "16k".</summary>
    public static string Tokens(int n) =>
        n < 1000 ? n.ToString(CultureInfo.InvariantCulture)
        : n < 10000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";
}

/// <summary>
/// What the conversation writes to. Everything goes through one place, because the panel stays on screen while an
/// answer streams and the two can't be drawn over each other.
/// </summary>
public sealed class ConversationWriter(TextWriter inner) : TextWriter
{
    public TextWriter Inner => inner;
    /// <summary>Set while the panel is up: it draws the text above the panel. Otherwise text goes straight through.</summary>
    public Action<string>? Sink { get; set; }
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) => Write(value.ToString());
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (Sink is { } sink) sink(value);
        else inner.Write(value);
    }
    public override void WriteLine() => Write(NewLine);
    public override void WriteLine(string? value) => Write(value + NewLine);
}

/// <summary>
/// The prompt on a real console. The panel stays at the bottom of the window from start to exit: keys are read the whole
/// time, so the user can type while an answer streams, and whatever they send meanwhile waits in a visible queue.
/// Only rows that changed are redrawn, inside a synchronized update, so typing doesn't flicker.
/// </summary>
public sealed class PromptEditor : ILineReader
{
    readonly Ui _ui;
    readonly ConversationWriter _conversation;
    readonly TextWriter _out;
    readonly PromptBuffer _buffer;
    readonly string _historyFile;
    readonly object _gate = new();
    readonly List<(bool Ok, string? Text)> _queue = [];
    readonly SemaphoreSlim _ready = new(0);
    Task? _pump;
    bool _running;
    bool _treatedControlC;
    Exception? _failure;
    int _conversationRow, _conversationColumn; // where the conversation carries on, in window rows
    List<string>? _drawn;
    int _drawnTop, _drawnHeight, _drawnWidth;
    PanelInfo? _drawnWith;
    string? _question; // the panel is asking yes or no
    bool _answer;
    ManualResetEventSlim? _answered;

    /// <summary>What the panel shows; read again while waiting for keys, so a server coming up shows without a key press.</summary>
    public Func<PanelInfo> Panel { get; set; } = () => new(null, "", "");
    /// <summary>Ctrl+C: stops what's running, returning true. False means nothing was, and it counts toward quitting.</summary>
    public Func<bool>? Interrupt { get; set; }
    /// <summary>An answer or command is running, so Enter queues.</summary>
    public bool Busy { get; set; }

    /// <summary>How many rows the conversation has on screen: the window less the panel's rows below it.</summary>
    public int ConversationRows
    {
        get { lock (_gate) return Math.Max(Console.WindowHeight - (_drawn?.Count ?? 0), 1); }
    }

    public PromptEditor(Ui ui, ConversationWriter conversation, Func<string> directory, string? historyFile = null)
    {
        _ui = ui;
        _conversation = conversation;
        _out = conversation.Inner;
        _historyFile = historyFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "history");
        _buffer = new PromptBuffer(directory, LoadHistory(_historyFile));
    }

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

    /// <summary>Puts the panel on screen and starts reading keys. From here on, the conversation draws above the panel.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _treatedControlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            ReadCursor();
            _conversation.Sink = WriteAbove;
            Render();
        }
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Takes the panel away and leaves the cursor under the conversation.</summary>
    public async Task StopAsync()
    {
        lock (_gate) _running = false;
        if (_pump is { } pump) await pump;
        lock (_gate)
        {
            _conversation.Sink = null;
            _out.Write(Sync(Go(BelowConversation()) + "\e[J" + Go(_conversationRow, _conversationColumn) + "\e[?25h"));
            _drawn = null;
            Console.TreatControlCAsInput = _treatedControlC;
        }
    }

    /// <summary>The next thing the user sent: in the order sent, and right away if they sent it during an answer.</summary>
    public async Task<(bool Ok, string? Text)> ReadAsync()
    {
        Start();
        while (true)
        {
            await _ready.WaitAsync();
            (bool Ok, string? Text) next;
            lock (_gate)
            {
                if (_failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                if (_queue.Count == 0) continue; // Ctrl+C cleared it
                next = _queue[0];
                _queue.RemoveAt(0);
                Render();
            }
            if (next is (true, { Length: > 0 } text))
            {
                _ui.Blank();
                _ui.UserMessage(text);
            }
            return next;
        }
    }

    async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                lock (_gate) { if (!_running) return; }
                if (!Console.KeyAvailable)
                {
                    lock (_gate) { if (Changed()) Render(); }
                    await Task.Delay(25);
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                var more = Console.KeyAvailable; // a paste draws once, at its end
                lock (_gate)
                {
                    if (_question is not null) AnswerQuestion(key);
                    else Apply(key, more);
                    if (!more) Render();
                }
            }
        }
        catch (Exception e)
        {
            lock (_gate) _failure = e;
            _ready.Release();
        }
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

    bool Changed() => CurrentInfo() != _drawnWith || _ui.Width != _drawnWidth || Console.WindowHeight != _drawnHeight;

    PanelInfo CurrentInfo()
    {
        var info = Panel();
        var dot = _ui.Theme.Glyph('·');
        var queued = _queue.Where(q => q.Ok).Select(q => q.Text ?? "").ToList();
        // The record's equality would see a new list as a change, so an empty queue stays null.
        return info with { Queued = queued.Count > 0 ? new QueueRows(queued) : null, Hint = Busy ? $"Enter queues {dot} Ctrl+C stops" : info.Hint };
    }

    /// <param name="into">Inside a write that's already under way (<see cref="WriteAbove"/>): add the drawing to it instead of writing.</param>
    void Render(StringBuilder? into = null)
    {
        var info = CurrentInfo();
        var height = Console.WindowHeight;
        if (_question is { } question)
        {
            var footer = PromptPanel.Draw(_ui, new PromptBuffer(() => ""), info with { Hint = "/undo puts it back" }, height).Rows[^1];
            Show([_ui.Rule(), QuestionRow(question), footer], 1, 0, into);
        }
        else
        {
            var drawn = PromptPanel.Draw(_ui, _buffer, info, height);
            Show(drawn.Rows, drawn.CaretRow, drawn.CaretColumn, into);
        }
        (_drawnWith, _drawnWidth, _drawnHeight) = (info, _ui.Width, height);
    }

    static string Go(int row, int column = 0) => PromptPanel.Go(row, column);
    /// <summary>All of it reaches the screen at once, where the terminal supports that, so a redraw never shows half done.</summary>
    static string Sync(string text) => "\e[?2026h" + text + "\e[?2026l";
    /// <summary>The first free row: a half-written row still belongs to the conversation.</summary>
    int BelowConversation() => _conversationColumn > 0 ? _conversationRow + 1 : _conversationRow;

    void ReadCursor()
    {
        var (column, top) = Console.GetCursorPosition();
        (_conversationRow, _conversationColumn) = (Math.Max(top - Console.WindowTop, 0), column);
    }

    /// <summary>Draws the panel at the bottom of the window, writing only the rows that changed.</summary>
    void Show(List<string> rows, int caretRow, int caretColumn, StringBuilder? into = null)
    {
        var height = Console.WindowHeight;
        _conversationRow = Math.Min(_conversationRow, height - 1); // the window may have shrunk
        var (scroll, top) = PromptPanel.Place(BelowConversation(), rows.Count, height);
        var screen = new StringBuilder("\e[?25l");
        if (_drawn is not null && (scroll > 0 || top != _drawnTop || rows.Count != _drawn.Count))
        {
            screen.Append(Go(BelowConversation())).Append("\e[J");
            _drawn = null;
        }
        if (scroll > 0)
        {
            // Real scrolling, so what leaves the window lands in the terminal's scrollback.
            screen.Append(Go(height - 1)).Append('\n', scroll);
            _conversationRow -= scroll;
        }
        screen.Append(PromptPanel.Frame(_drawn, _drawnTop, rows, top));
        screen.Append(Go(top + caretRow, caretColumn)).Append("\e[?25h");
        if (into is not null) into.Append(screen); // the caller's synchronized block is still open
        else _out.Write(Sync(screen.ToString()));
        (_drawn, _drawnTop) = (rows, top);
    }

    /// <summary>
    /// Conversation text. It goes where the conversation left off, and the panel is only taken down when a new line would
    /// reach it; then the conversation scrolls up and the panel comes back below it.
    /// </summary>
    void WriteAbove(string text)
    {
        lock (_gate)
        {
            var reaches = _drawn is not null && _conversationRow + text.Count(c => c == '\n') + 1 >= _drawnTop;
            var screen = new StringBuilder("\e[?25l");
            if (reaches)
            {
                screen.Append(Go(BelowConversation())).Append("\e[J");
                _drawn = null;
            }
            screen.Append(Go(_conversationRow, _conversationColumn)).Append(text);
            // Two writes inside one synchronized block: the text has to reach the screen before the cursor can be read
            // back, and the panel and caret then follow in a single write that also closes the block. A nested block
            // used to close it early, halfway through.
            _out.Write("\e[?2026h" + screen);
            ReadCursor();
            var rest = new StringBuilder();
            if (_drawn is null) Render(rest);
            else if (_question is null)
            {
                var drawn = PromptPanel.Draw(_ui, _buffer, CurrentInfo(), Console.WindowHeight);
                rest.Append(Go(_drawnTop + drawn.CaretRow, drawn.CaretColumn)).Append("\e[?25h");
            }
            else rest.Append(Go(_drawnTop + 1)).Append("\e[?25h");
            _out.Write(rest.Append("\e[?2026l").ToString());
        }
    }

    string QuestionRow(string question)
    {
        var theme = _ui.Theme;
        var row = _ui.Cell("?", Tone.Accent) + theme.Paint(ConsoleText.Sanitize(question) + "   ", Tone.Primary)
            + theme.On(Surface.Accent, theme.Paint(" y  apply ", Tone.Ink)) + "  " + theme.On(Surface.Selected, theme.Paint(" n  skip ", Tone.Secondary));
        return theme.Band(Surface.Panel, row);
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
        lock (_gate)
        {
            (_question, _answered) = (question, answered);
            Render();
        }
        answered.Wait();
        lock (_gate)
        {
            _answered = null;
            return _answer;
        }
    }
}
