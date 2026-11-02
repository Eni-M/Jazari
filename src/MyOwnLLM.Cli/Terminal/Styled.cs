namespace MyOwnLLM.Cli.Terminal;

/// <summary>How a piece of text looks. No color set means the terminal's own.</summary>
public readonly record struct Style(Tone? Fg = null, Surface? Bg = null, bool Bold = false)
{
    public static implicit operator Style(Tone tone) => new(tone);
}

public readonly record struct Span(string Text, Style Style = default);

/// <summary>
/// One row on screen as styled pieces, never wider than the terminal. <see cref="Fill"/> paints the rest of the row out
/// to the terminal's edge (the panel's background); otherwise the rest of the row is cleared.
/// </summary>
public sealed class Line(IReadOnlyList<Span> spans, Surface? fill = null)
{
    public static readonly Line Empty = new([]);

    public IReadOnlyList<Span> Spans { get; } = spans;
    public Surface? Fill { get; } = fill;
    public string Text => string.Concat(Spans.Select(s => s.Text));
    public int Width => TextWidth.Of(Text);

    /// <summary>The same row with its first span (the gutter) replaced.</summary>
    public Line WithGutter(Span gutter) => new([gutter, .. Spans.Skip(1)], Fill);
}

/// <summary>Builds a row piece by piece, keeping count of its columns.</summary>
public sealed class LineBuilder
{
    readonly List<Span> _spans = [];

    public int Width { get; private set; }

    public LineBuilder Add(string text, Style style = default)
    {
        if (text.Length == 0) return this;
        _spans.Add(new(text, style));
        Width += TextWidth.Of(text);
        return this;
    }

    public LineBuilder Add(Span span) => Add(span.Text, span.Style);

    public LineBuilder AddRange(IEnumerable<Span> spans)
    {
        foreach (var span in spans) Add(span);
        return this;
    }

    /// <summary>Spaces up to <paramref name="column"/>.</summary>
    public LineBuilder PadTo(int column, Style style = default) => column > Width ? Add(new string(' ', column - Width), style) : this;

    public Line Build(Surface? fill = null) => new(_spans.ToList(), fill);
}

public static class Styled
{
    /// <summary>Word-wraps text that has a style per character, keeping each piece's style across row breaks.</summary>
    public static List<List<Span>> Wrap(string text, Style[] styles, int max) =>
        TextWidth.WrapRanges(text, max).Select(r => Spans(text, styles, r.Start, r.End)).ToList();

    /// <summary>Runs of equal style in text[start..end].</summary>
    public static List<Span> Spans(string text, Style[] styles, int start, int end)
    {
        List<Span> spans = [];
        var from = start;
        for (var i = start + 1; i <= end; i++)
        {
            if (i < end && styles[i] == styles[from]) continue;
            if (i > from) spans.Add(new(text[from..i], styles[from]));
            from = i;
        }
        return spans;
    }
}
