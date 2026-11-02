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
        for (var i = 0; i < lines.Count; i++) output.WriteLine(Cell(i == 0 ? label : null, labelTone) + theme.Paint(lines[i], tone));
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
        output.WriteLine(theme.Paint(left.PadRight(room + 1) + right, Tone.Muted));
        output.WriteLine(Rule());
        output.WriteLine();
    }

    public string Rule() => theme.Paint(new string(theme.Glyph('─'), Width), Tone.Line);

    public void Wordmark(string version)
    {
        if (Live) output.WriteLine(Cell() + theme.Bold("jazari") + theme.Paint("  " + ConsoleText.Sanitize(version), Tone.Muted));
        else output.WriteLine($"{theme.Paint(Name, Tone.Primary)}  {theme.Paint(ConsoleText.Sanitize(version), Tone.Muted)}");
    }

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
        foreach (var (key, value) in list)
        {
            var lines = Wrap(value, Room - column);
            output.WriteLine(Cell() + theme.Paint(key.PadRight(column), Tone.Muted) + theme.Paint(lines[0], Tone.Secondary));
            foreach (var more in lines.Skip(1)) output.WriteLine(Cell() + new string(' ', column) + theme.Paint(more, Tone.Secondary));
        }
    }

    /// <summary>The key column that fits every key in all of <paramref name="groups"/>, so grouped rows line up.</summary>
    public static int KeyWidth(IEnumerable<IEnumerable<(string Key, string Value)>> groups) =>
        Math.Max(KeyColumn, groups.SelectMany(g => g).Select(r => r.Key.Length + 2).DefaultIfEmpty(0).Max());

    public void ToolLines(string label, IEnumerable<string> doneSteps)
    {
        output.WriteLine(theme.Paint("  " + ConsoleText.Sanitize(label).ToUpperInvariant(), Tone.Muted));
        foreach (var step in doneSteps) output.WriteLine(theme.Paint($"  {theme.Glyph('✓')} {ConsoleText.Sanitize(step)}", Tone.Muted));
    }

    public void ActionRequired(string what, string command)
    {
        var rule = theme.Paint(theme.Glyph('│').ToString(), Tone.Secondary) + " ";
        output.WriteLine(rule + "ACTION REQUIRED");
        output.WriteLine(rule + theme.Paint(ConsoleText.Sanitize(what) + ":", Tone.Secondary));
        output.WriteLine(rule + ConsoleText.Sanitize(command));
        output.WriteLine(rule + theme.Paint("[y] Allow once  [n] Deny  [a] Always allow", Tone.Secondary));
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
        var first = true;
        foreach (var paragraph in ConsoleText.Sanitize(text).Split('\n'))
            foreach (var line in Wrap(paragraph, Live ? Body : Width - 2))
            {
                var marker = first ? theme.Glyph('›').ToString() : null;
                output.WriteLine(Live ? Cell(marker, Tone.Accent) + theme.Paint(line, Tone.Secondary) : $"{marker ?? " "} {line}");
                first = false;
            }
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
        if (redraw) output.Write("\e[3A\r");
        var dot = theme.Glyph('·');
        const int bar = 44;
        var filled = steps.Count == 0 ? bar : bar * done / steps.Count;
        output.WriteLine("\e[2K" + Cell("live", Tone.Accent) + theme.Paint("Tuning for this PC ", Tone.Primary) + theme.Paint($"{dot} {left}", Tone.Muted));
        output.WriteLine("\e[2K" + Cell() + theme.Paint(new string(theme.Glyph('━'), filled), Tone.Accent) + theme.Paint(new string(theme.Glyph('━'), bar - filled), Tone.Line));
        var row = steps.Select((step, i) =>
            i < done ? theme.Paint(theme.Glyph('✓').ToString(), Tone.Secondary) + theme.Paint(" " + ConsoleText.Sanitize(step), Tone.Muted)
            : i == done ? theme.Paint($"{theme.Glyph('●')} {ConsoleText.Sanitize(step)}", Tone.Primary)
            : theme.Paint($"{theme.Glyph('○')} {ConsoleText.Sanitize(step)}", Tone.Muted));
        output.WriteLine("\e[2K" + Cell() + string.Join("   ", row));
    }

    /// <summary>Word-wraps to <paramref name="max"/> columns; words longer than a line are split.</summary>
    public static IReadOnlyList<string> Wrap(string text, int max)
    {
        max = Math.Max(max, 8);
        List<string> lines = [];
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var w = word;
            while (w.Length > max)
            {
                if (current.Length > 0) { lines.Add(current); current = ""; }
                lines.Add(w[..max]);
                w = w[max..];
            }
            if (current.Length == 0) current = w;
            else if (current.Length + 1 + w.Length <= max) current += " " + w;
            else { lines.Add(current); current = w; }
        }
        lines.Add(current);
        return lines;
    }
}
