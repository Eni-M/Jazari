namespace MyOwnLLM.Cli.Terminal;

/// <summary>The Jazari terminal components. Every screen is built from these, never styled ad hoc.
/// All text passed in is treated as untrusted and sanitized here, so no caller can forget.</summary>
public sealed class Ui(TextWriter output, Theme theme, int width)
{
    public const string Name = "J A Z A R I";
    const int KeyColumn = 12;

    public Theme Theme => theme;
    public TextWriter Output => output;
    public int Width => Math.Max(width, 20);

    public void Blank() => output.WriteLine();

    /// <summary>
    /// Opening the interactive session: name the tab "Jazari" and start on a clean screen (no shell banner or prompt
    /// above). Only in a real terminal; piped output is left alone.
    /// </summary>
    public void TakeOverScreen()
    {
        if (theme.Mode != ColorMode.None) output.Write("\e]0;Jazari\a\e[H\e[2J\e[3J");
    }

    public void Line(string text, Tone tone = Tone.Primary)
    {
        foreach (var line in Wrap(ConsoleText.Sanitize(text), Width)) output.WriteLine(theme.Paint(line, tone));
    }

    public void Wordmark(string version) => output.WriteLine($"{theme.Paint(Name, Tone.Primary)}  {theme.Paint(ConsoleText.Sanitize(version), Tone.Muted)}");

    public void Label(string text) => output.WriteLine(theme.Paint(ConsoleText.Sanitize(text).ToUpperInvariant(), Tone.Muted));

    /// <summary>Key/value rows; the key column fits the longest key (at least 12), so values never run into keys.</summary>
    public void Rows(IEnumerable<(string Key, string Value)> rows, int? keyWidth = null)
    {
        var list = rows.Select(r => (Key: ConsoleText.Sanitize(r.Key), Value: ConsoleText.Sanitize(r.Value))).ToList();
        var column = keyWidth ?? Math.Max(KeyColumn, list.Select(r => r.Key.Length + 2).DefaultIfEmpty(0).Max());
        foreach (var (key, value) in list)
        {
            var lines = Wrap(value, Width - column);
            output.WriteLine(theme.Paint(key.PadRight(column), Tone.Muted) + theme.Paint(lines[0], Tone.Secondary));
            foreach (var more in lines.Skip(1)) output.WriteLine(new string(' ', column) + theme.Paint(more, Tone.Secondary));
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
        output.WriteLine("ERROR");
        Line(message);
        if (cause is not null) Line("Cause: " + cause, Tone.Muted);
        if (next is not null) Line(next, Tone.Muted);
    }

    /// <summary>The user's turn: "› question" on the raised surface, full width, continuation lines indented under it.</summary>
    public void UserMessage(string text)
    {
        var first = true;
        foreach (var paragraph in ConsoleText.Sanitize(text).Split('\n'))
            foreach (var line in Wrap(paragraph, Width - 2))
            {
                output.WriteLine(theme.Raised((first ? $"{theme.Glyph('›')} " : "  ") + line, Width));
                first = false;
            }
    }

    public void Metadata(string text) => output.WriteLine(theme.Paint(ConsoleText.Sanitize(text), Tone.Muted));

    /// <summary>The thin line above the prompt; with a label, it starts with what the next question will carry.</summary>
    public void Divider(string? label = null)
    {
        var length = Math.Min(Width, 60);
        if (label is not { Length: > 0 }) { output.WriteLine(theme.Paint(new string(theme.Glyph('─'), length), Tone.Line)); return; }
        var text = ConsoleText.Sanitize(label);
        if (text.Length > length - 4) text = text[..(length - 7)] + "...";
        output.WriteLine(theme.Paint(text + " ", Tone.Muted) + theme.Paint(new string(theme.Glyph('─'), Math.Max(3, length - text.Length - 1)), Tone.Line));
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
