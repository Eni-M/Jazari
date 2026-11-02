namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// The canvas's geometry in terminal cells (JetBrains Mono 14/22 px is about 8.4 px a column): an 88 px gutter with labels
/// right-aligned 20 px before the text becomes 8 label columns and 2 spaces; the 720 px text column becomes 86 columns;
/// the 32 px right padding becomes a margin of 4 columns (2 on narrow terminals). Every component takes its numbers from
/// here. <c>columns</c> is always what a row may use: the terminal's width less one, since writing the last column makes
/// some terminals wrap.
/// </summary>
public static class Layout
{
    public const int Gutter = 10;
    public const int LabelColumns = 8;
    public const int Measure = 86;

    public static int Margin(int columns) => columns >= 99 ? 4 : 2;

    /// <summary>The text column beside the gutter.</summary>
    public static int Body(int columns) => Math.Max(Math.Min(columns - Gutter - Margin(columns), Measure), 12);

    /// <summary>A gutter cell: the label right-aligned in 8 columns, then 2 spaces.</summary>
    public static Span Label(string? label, Tone tone = Tone.Muted, bool bold = false) =>
        new(TextWidth.PadLeft(TextWidth.Fit(ConsoleText.Sanitize(label ?? ""), LabelColumns, ""), LabelColumns) + "  ", new Style(tone, Bold: bold));

    /// <summary>A row with a gutter label and text beside it.</summary>
    public static LineBuilder Row(string? label = null, Tone tone = Tone.Muted, bool bold = false) =>
        new LineBuilder().Add(Label(label, tone, bold));
}
