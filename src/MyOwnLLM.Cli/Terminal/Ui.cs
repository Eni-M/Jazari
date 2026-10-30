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

    public void Line(string text, Tone tone = Tone.Primary)
    {
        foreach (var line in Wrap(ConsoleText.Sanitize(text), Width)) output.WriteLine(theme.Paint(line, tone));
    }

    public void Wordmark(string version) => output.WriteLine($"{theme.Paint(Name, Tone.Primary)}  {theme.Paint(ConsoleText.Sanitize(version), Tone.Muted)}");

    public void Label(string text) => output.WriteLine(theme.Paint(ConsoleText.Sanitize(text).ToUpperInvariant(), Tone.Muted));

    public void Rows(IEnumerable<(string Key, string Value)> rows)
    {
        foreach (var (key, value) in rows)
        {
            var lines = Wrap(ConsoleText.Sanitize(value), Width - KeyColumn);
            output.WriteLine(theme.Paint(ConsoleText.Sanitize(key).PadRight(KeyColumn), Tone.Muted) + theme.Paint(lines[0], Tone.Secondary));
            foreach (var more in lines.Skip(1)) output.WriteLine(new string(' ', KeyColumn) + theme.Paint(more, Tone.Secondary));
        }
    }

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

    public void Metadata(string text) => output.WriteLine(theme.Paint(ConsoleText.Sanitize(text), Tone.Muted));

    public void Divider() => output.WriteLine(theme.Paint(new string(theme.Glyph('─'), Math.Min(Width, 60)), Tone.Line));

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
