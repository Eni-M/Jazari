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
public sealed record PanelInfo(string? Chip, string Hint, string Status, (int Used, int Total)? Meter = null, bool Offline = false, string? Live = null);

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

        var room = Math.Max(ui.Width - Ui.Gutter, 8);
        var (lines, caretRow, caretColumn) = Layout(buffer.Text, buffer.Caret, room);
        var first = rows.Count;
        var marker = ui.Cell(theme.Glyph('›').ToString(), info.Offline ? Tone.Muted : Tone.Accent);
        for (var i = 0; i < lines.Count; i++) rows.Add(Band(theme, (i == 0 ? marker : ui.Cell()) + theme.Paint(lines[i], info.Offline ? Tone.Muted : Tone.Primary)));
        rows.Add(Footer(ui, info, open));
        return new(rows, first + caretRow, Ui.Gutter + caretColumn);
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

/// <summary>The prompt on a real console: reads keys, redraws the panel in place, and keeps history between sessions.</summary>
public sealed class PromptEditor : ILineReader
{
    readonly Ui _ui;
    readonly PromptBuffer _buffer;
    readonly string _historyFile;
    readonly Lock _gate = new();
    readonly Queue<Action> _above = new();
    bool _reading;
    bool _nothingPrinted; // the last prompt left nothing behind, so the next needs no blank row above it
    int _conversationRow; // the window row the conversation carries on from, once the panel is gone
    PanelInfo? _drawnWith;
    int _drawnWidth, _drawnHeight;

    /// <summary>What the panel shows; read again while waiting for keys, so a server coming up shows without a key press.</summary>
    public Func<PanelInfo> Panel { get; set; } = () => new(null, "", "");

    public PromptEditor(Ui ui, Func<string> directory, string? historyFile = null)
    {
        _ui = ui;
        _historyFile = historyFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "history");
        _buffer = new PromptBuffer(directory, LoadHistory(_historyFile));
    }

    static int WindowRow => Console.CursorTop - Console.WindowTop;

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

    /// <summary>Writes above the panel, whether or not a prompt is open: from a background task while the user types, say.</summary>
    public void Post(Action write)
    {
        lock (_gate)
        {
            if (_reading) { _above.Enqueue(write); return; }
        }
        write();
    }

    public async Task<(bool Ok, string? Text)> ReadAsync()
    {
        if (!_nothingPrinted) _ui.Blank();
        _nothingPrinted = false;
        _conversationRow = WindowRow;
        var treated = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        lock (_gate) _reading = true;
        try
        {
            Render();
            while (true)
            {
                DrainAbove();
                if (!Console.KeyAvailable)
                {
                    if (Panel() != _drawnWith || _ui.Width != _drawnWidth || Console.WindowHeight != _drawnHeight) Render();
                    await Task.Delay(25);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                var before = _buffer.Text;
                var result = _buffer.Apply(key, key.Key == ConsoleKey.Enter && Console.KeyAvailable);
                if (result == KeyResult.Editing)
                {
                    if (!Console.KeyAvailable) Render(); // a paste draws once, at its end
                    continue;
                }

                Erase();
                if (result == KeyResult.Cancel)
                {
                    _buffer.Reset();
                    _nothingPrinted = before.Length > 0;
                    return (false, before);
                }
                var text = _buffer.Take();
                if (text.Trim().Length > 0)
                {
                    Remember(text);
                    _ui.UserMessage(text);
                }
                else _nothingPrinted = true;
                return (true, text);
            }
        }
        finally
        {
            lock (_gate) _reading = false;
            Console.TreatControlCAsInput = treated;
            DrainAbove(draw: false);
        }
    }

    void DrainAbove(bool draw = true)
    {
        while (true)
        {
            Action? write;
            lock (_gate)
            {
                if (!_above.TryDequeue(out write)) return;
            }
            Erase();
            write();
            _conversationRow = WindowRow;
            if (draw) Render();
        }
    }

    void Render()
    {
        var info = Panel();
        var drawn = PromptPanel.Draw(_ui, _buffer, info, Console.WindowHeight);
        Show(drawn.Rows, drawn.CaretRow, drawn.CaretColumn);
        (_drawnWith, _drawnWidth, _drawnHeight) = (info, _ui.Width, Console.WindowHeight);
    }

    static string Go(int row, int column = 0) => $"\e[{row + 1};{column + 1}H";

    /// <summary>Draws the panel at the bottom of the window, whatever was drawn before.</summary>
    void Show(List<string> rows, int caretRow, int caretColumn)
    {
        var height = Console.WindowHeight;
        _conversationRow = Math.Min(_conversationRow, height - 1); // the window may have shrunk
        var (scroll, top) = PromptPanel.Place(_conversationRow, rows.Count, height);
        var screen = new StringBuilder("\e[?25l");
        screen.Append(Go(_conversationRow)).Append("\e[J");
        if (scroll > 0)
        {
            screen.Append(Go(height - 1)).Append('\n', scroll);
            _conversationRow -= scroll;
        }
        screen.Append(Go(top)).AppendJoin("\r\n", rows);
        screen.Append(Go(top + caretRow, caretColumn)).Append("\e[?25h");
        _ui.Output.Write(screen);
    }

    /// <summary>Takes the panel off the screen, leaving the cursor where the conversation carries on.</summary>
    void Erase() => _ui.Output.Write(Go(_conversationRow) + "\e[J");

    /// <summary>The panel asking a yes/no question ("? Apply this change?  y apply  n skip"). Anything but y is no.</summary>
    public bool Ask(string question)
    {
        var theme = _ui.Theme;
        var info = Panel() with { Hint = "/undo puts it back" };
        var row = _ui.Cell("?", Tone.Accent) + theme.Paint(ConsoleText.Sanitize(question) + "   ", Tone.Primary)
            + theme.On(Surface.Accent, theme.Paint(" y  apply ", Tone.Ink)) + "  " + theme.On(Surface.Selected, theme.Paint(" n  skip ", Tone.Secondary));
        var drawn = PromptPanel.Draw(_ui, new PromptBuffer(() => ""), info, Console.WindowHeight);
        _conversationRow = WindowRow;
        Show([_ui.Rule(), theme.Band(Surface.Panel, row), drawn.Rows[^1]], 1, 0);

        var treated = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key is not (ConsoleKey.Y or ConsoleKey.N or ConsoleKey.Escape) && !(key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))) continue;
                Erase();
                return key.Key == ConsoleKey.Y;
            }
        }
        finally { Console.TreatControlCAsInput = treated; }
    }
}
