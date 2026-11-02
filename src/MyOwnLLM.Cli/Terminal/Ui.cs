namespace MyOwnLLM.Cli.Terminal;

/// <summary>The Jazari terminal components. Every screen is built from these, never styled ad hoc.
/// All text passed in is treated as untrusted and sanitized here, so no caller can forget.
/// In a real terminal everything hangs off a gutter of short labels (the time, "copy 1", "why"); piped output stays plain.</summary>
public sealed class Ui(TextWriter output, Theme theme, Func<int> width)
{
    public Ui(TextWriter output, Theme theme, int width) : this(output, theme, () => width) { }

    public const string Name = "J A Z A R I";
    public const int Gutter = 8;
    const int KeyColumn = 12;
    const int Measure = 86; // the design's 720 px text column, in characters

    public Theme Theme => theme;
    public TextWriter Output => output;
    public int Width => Math.Max(width(), 20);
    public bool Live => theme.Mode != ColorMode.None;
    /// <summary>The columns text gets beside the gutter.</summary>
    public int Body => Math.Max(Math.Min(Width - Gutter, Measure), 12);
    int Room => Live ? Body : Width;

    public void Blank() => output.WriteLine();

    /// <summary>
    /// Several rows as one write. Above the panel each write is a full cursor, draw and panel cycle, and a terminal without
    /// synchronized output can paint each one separately, so a 30-row block written row by row could flicker.
    /// </summary>
    void WriteLines(IEnumerable<string> lines) => output.Write(string.Concat(lines.Select(l => l + output.NewLine)));

    /// <summary>
    /// Opening the interactive session: name the tab "Jazari" and start on a clean screen (no shell banner or prompt
    /// above). Only in a real terminal; piped output is left alone.
    /// </summary>
    public void TakeOverScreen()
    {
        if (Live) output.Write("\e]0;Jazari\a\e[H\e[2J\e[3J");
    }

    /// <summary>The right-aligned label beside a row, and the space between it and the text; nothing in plain output.</summary>
    public string Cell(string? label = null, Tone tone = Tone.Muted) =>
        Live ? theme.Paint(ConsoleText.Sanitize(label ?? "").PadLeft(Gutter - 2), tone) + "  " : "";

    /// <summary>One row of the conversation: a label in the gutter, text beside it, wrapped lines under the text.</summary>
    public void Row(string? label, string text, Tone tone = Tone.Primary, Tone labelTone = Tone.Muted)
    {
        var lines = Wrap(ConsoleText.Sanitize(text), Room);
        WriteLines(lines.Select((line, i) => Cell(i == 0 ? label : null, labelTone) + theme.Paint(line, tone)));
    }

    public void Line(string text, Tone tone = Tone.Primary) => Row(null, text, tone);

    /// <summary>The top of the session: "jazari — ~/MyOwnLLM", the branch at the far right, and a thin line under them.</summary>
    public void Header(string place, string? branch)
    {
        if (!Live) return;
        var right = ConsoleText.Sanitize(branch ?? "");
        var left = $"jazari {theme.Glyph('—')} {ConsoleText.Sanitize(place)}";
        var room = Width - right.Length - 1;
        if (left.Length > room) left = left[..Math.Max(room, 0)];
        WriteLines([theme.Paint(left.PadRight(room + 1) + right, Tone.Muted), Rule(), ""]);
    }

    /// <summary>A thin line edge to edge, like the panel's background. Filling the last column doesn't wrap until the next character, and a line break comes first.</summary>
    public string Rule() => theme.Paint(new string(theme.Glyph('─'), Width + 1), Tone.Line);

    public void Wordmark(string version)
    {
        if (Live) output.WriteLine(LiveWordmark(version));
        else output.WriteLine($"{theme.Paint(Name, Tone.Primary)}  {theme.Paint(ConsoleText.Sanitize(version), Tone.Muted)}");
    }

    string LiveWordmark(string text) => Cell() + theme.Bold("jazari") + theme.Paint("  " + ConsoleText.Sanitize(text), Tone.Muted);

    /// <summary>Rewrites the wordmark's row wherever the cursor is, and leaves the cursor there: "0.3.0" becomes "ready in 4 s".</summary>
    public void ReplaceWordmark(int windowRow, string text) => output.Write($"\e7\e[{windowRow + 1};1H\e[2K{LiveWordmark(text)}\e8");

    public void Label(string text)
    {
        var clean = ConsoleText.Sanitize(text);
        output.WriteLine(Cell() + theme.Paint(Live ? clean : clean.ToUpperInvariant(), Tone.Muted));
    }

    /// <summary>Key/value rows; the key column fits the longest key (at least 12), so values never run into keys.</summary>
    public void Rows(IEnumerable<(string Key, string Value)> rows, int? keyWidth = null)
    {
        var list = rows.Select(r => (Key: ConsoleText.Sanitize(r.Key), Value: ConsoleText.Sanitize(r.Value))).ToList();
        var column = keyWidth ?? Math.Max(KeyColumn, list.Select(r => r.Key.Length + 2).DefaultIfEmpty(0).Max());
        WriteLines(list.SelectMany(r => Wrap(r.Value, Room - column).Select((line, i) =>
            Cell() + theme.Paint(i == 0 ? r.Key.PadRight(column) : new string(' ', column), Tone.Muted) + theme.Paint(line, Tone.Secondary))));
    }

    /// <summary>The key column that fits every key in all of <paramref name="groups"/>, so grouped rows line up.</summary>
    public static int KeyWidth(IEnumerable<IEnumerable<(string Key, string Value)>> groups) =>
        Math.Max(KeyColumn, groups.SelectMany(g => g).Select(r => r.Key.Length + 2).DefaultIfEmpty(0).Max());

    public void ToolLines(string label, IEnumerable<string> doneSteps)
    {
        WriteLines([theme.Paint("  " + ConsoleText.Sanitize(label).ToUpperInvariant(), Tone.Muted),
            .. doneSteps.Select(step => theme.Paint($"  {theme.Glyph('✓')} {ConsoleText.Sanitize(step)}", Tone.Muted))]);
    }

    public void ActionRequired(string what, string command)
    {
        var rule = theme.Paint(theme.Glyph('│').ToString(), Tone.Secondary) + " ";
        WriteLines([rule + "ACTION REQUIRED", rule + theme.Paint(ConsoleText.Sanitize(what) + ":", Tone.Secondary),
            rule + ConsoleText.Sanitize(command), rule + theme.Paint("[y] Allow once  [n] Deny  [a] Always allow", Tone.Secondary)]);
    }

    public void Error(string message, string? cause, string? next)
    {
        if (Live)
        {
            Row(theme.Glyph('✕').ToString(), message, Tone.Primary, Tone.Danger);
            if (cause is not null) Row("why", cause, Tone.Secondary);
            if (next is not null) Row("fix", next, Tone.Secondary);
            return;
        }
        output.WriteLine("ERROR");
        Line(message);
        if (cause is not null) Line("Cause: " + cause, Tone.Muted);
        if (next is not null) Line(next, Tone.Muted);
    }

    /// <summary>The user's turn: "› question", continuation lines indented under it.</summary>
    public void UserMessage(string text)
    {
        var lines = ConsoleText.Sanitize(text).Split('\n').SelectMany(paragraph => Wrap(paragraph, Live ? Body : Width - 2));
        WriteLines(lines.Select((line, i) =>
        {
            var marker = i == 0 ? theme.Glyph('›').ToString() : null;
            return Live ? Cell(marker, Tone.Accent) + theme.Paint(line, Tone.Secondary) : $"{marker ?? " "} {line}";
        }));
    }

    public void Metadata(string text, string? label = null)
    {
        if (Live) Row(label, text, Tone.Muted);
        else output.WriteLine(theme.Paint(ConsoleText.Sanitize(text), Tone.Muted));
    }

    /// <summary>One line of a proposed change: a line number or +/− in its own column, on the change's background.</summary>
    public void DiffLine(string marker, string text, Tone tone, Surface? surface = null)
    {
        var clean = ConsoleText.Sanitize(text);
        if (!Live) { output.WriteLine($"{marker} {clean}"); return; }
        var row = theme.Paint($" {marker,3}  {clean}", tone);
        output.WriteLine(Cell() + theme.Band(surface ?? Surface.Code, row));
    }

    /// <summary>The live "Tuning for this PC" block: what's left, a bar, and each step done, running or waiting. Redraws in place.</summary>
    public void TuneProgress(IReadOnlyList<string> steps, int done, string left, bool redraw)
    {
        if (!Live) return;
        var dot = theme.Glyph('·');
        const int bar = 44;
        var filled = steps.Count == 0 ? bar : bar * done / steps.Count;
        var title = "\e[2K" + Cell("live", Tone.Accent) + theme.Paint("Tuning for this PC ", Tone.Primary) + theme.Paint($"{dot} {left}", Tone.Muted);
        var progress = "\e[2K" + Cell() + theme.Paint(new string(theme.Glyph('━'), filled), Tone.Accent) + theme.Paint(new string(theme.Glyph('━'), bar - filled), Tone.Line);
        var row = steps.Select((step, i) =>
            i < done ? theme.Paint(theme.Glyph('✓').ToString(), Tone.Secondary) + theme.Paint(" " + ConsoleText.Sanitize(step), Tone.Muted)
            : i == done ? theme.Paint($"{theme.Glyph('●')} {ConsoleText.Sanitize(step)}", Tone.Primary)
            : theme.Paint($"{theme.Glyph('○')} {ConsoleText.Sanitize(step)}", Tone.Muted));
        WriteLines([(redraw ? "\e[3A\r" : "") + title, progress, "\e[2K" + Cell() + string.Join("   ", row)]);
    }

    /// <summary>Word-wraps to <paramref name="max"/> columns (at least 8); words longer than a line are split between characters.</summary>
    public static IReadOnlyList<string> Wrap(string text, int max) => TextWidth.Wrap(text, Math.Max(max, 8));
}
