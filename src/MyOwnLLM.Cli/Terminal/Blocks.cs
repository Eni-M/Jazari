namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// One piece of the conversation: a question, an answer, an error, a diff. It knows how to lay itself out at any width,
/// so the screen can draw it again after a resize. A block that can still change is live and is drawn again every frame;
/// a finished one is laid out once per width.
/// </summary>
public abstract class Block
{
    public abstract IReadOnlyList<Line> Render(int columns);

    /// <summary>Still changing: drawn again every frame.</summary>
    public virtual bool Live => false;

    /// <summary>Shows a clock, so the screen draws it again each second.</summary>
    public virtual bool Timed => false;

    /// <summary>Goes up when a finished block grows (a diff gaining a line), so its layout is made again.</summary>
    public int Version { get; protected set; }

    /// <summary>How many of its first rows won't change any more. Output that can't redraw (a one-off command) prints only these.</summary>
    public virtual int Stable(int columns, int rows) => Live ? 0 : rows;
}

/// <summary>Rows made by a function: anything that doesn't change once written.</summary>
public sealed class LinesBlock(Func<int, IReadOnlyList<Line>> render) : Block
{
    public override IReadOnlyList<Line> Render(int columns) => render(columns);
}

/// <summary>An empty row between turns. Two in a row show as one.</summary>
public sealed class BlankBlock : Block
{
    public override IReadOnlyList<Line> Render(int columns) => [Line.Empty];
}

/// <summary>Rows that change until they're done: the wordmark until "ready in 4 s", the tuning block until it finishes.</summary>
public sealed class LiveBlock(Func<int, IReadOnlyList<Line>> render, bool timed = false) : Block
{
    bool _done;

    public override IReadOnlyList<Line> Render(int columns) => render(columns);
    public override bool Live => !_done;
    public override bool Timed => timed && !_done;

    /// <summary>No more changes: laid out once more and kept.</summary>
    public void Done()
    {
        _done = true;
        Version++;
    }
}

/// <summary>
/// "live  Thinking · 12s", or "live  Reading 2,400 of 5,000 tokens · 48% · about 1 min left" once the model reports how
/// far it has read; also "live  Starting the model · 8s" while the servers come up.
/// </summary>
public sealed class ThinkingBlock(Theme theme, TimeProvider time, string label = "Thinking") : Block
{
    readonly long _started = time.GetTimestamp();
    ReadProgress? _first, _latest;
    TimeSpan _firstAt, _latestAt;

    public override bool Live => true;
    public override bool Timed => true;

    public void Report(ReadProgress progress)
    {
        var at = time.GetElapsedTime(_started);
        if (_first is null) (_first, _firstAt) = (progress, at);
        (_latest, _latestAt) = (progress, at);
    }

    public int? Percent => _latest is { Total: > 0 } p ? Math.Clamp(p.Processed * 100 / p.Total, 0, 100) : null;

    public override IReadOnlyList<Line> Render(int columns)
    {
        var dot = theme.Glyph('·');
        var seconds = (int)time.GetElapsedTime(_started).TotalSeconds;
        string text;
        if (_latest is { Total: > 0 } p)
        {
            var left = Remaining(p);
            text = $"Reading {p.Processed:N0} of {p.Total:N0} tokens {dot} {Percent}%{(left is null ? "" : $" {dot} {left}")}";
        }
        else text = seconds == 0 ? label : $"{label} {dot} {seconds}s";
        return [Layout.Row("live", Tone.Accent).Add(TextWidth.Fit(text, Layout.Body(columns)), Tone.Muted).Build()];
    }

    /// <summary>Time left at the reading speed seen so far, once there's enough to judge it.</summary>
    string? Remaining(ReadProgress p)
    {
        var read = p.Processed - _first!.Processed;
        var took = (_latestAt - _firstAt).TotalSeconds;
        if (read <= 0 || took < 2) return null;
        var left = (p.Total - p.Processed) / (read / took);
        return left < 60 ? "under a minute left" : $"about {Math.Ceiling(left / 60):0} min left";
    }
}

/// <summary>A proposed change: a box with a number or −/+ column, red removed rows and green added rows.</summary>
public sealed class DiffBlock(Theme theme) : Block
{
    readonly List<(string Marker, string Text, Tone Tone, Surface Surface)> _rows = [];

    public void Add(string marker, string text, Tone tone, Surface surface)
    {
        _rows.Add((ConsoleText.Sanitize(marker), ConsoleText.Sanitize(text).Replace("\t", "    "), tone, surface));
        Version++;
    }

    public override IReadOnlyList<Line> Render(int columns)
    {
        var body = Layout.Body(columns);
        List<Line> lines = [];
        if (theme.Edges) lines.Add(Layout.Row().Add(new string('▁', body), Tone.CodeBorder).Build());
        foreach (var (marker, text, tone, surface) in _rows)
        {
            var first = true;
            foreach (var piece in Pieces(text, body - 5))
            {
                var cell = first ? marker : "";
                var centered = new string(' ', Math.Max((4 - TextWidth.Of(cell)) / 2, 0)) + cell;
                lines.Add(Layout.Row().Add(TextWidth.PadRight(centered, 4) + " " + piece, new Style(tone, surface)).PadTo(Layout.Gutter + body, new Style(tone, surface)).Build());
                first = false;
            }
        }
        if (theme.Edges) lines.Add(Layout.Row().Add(new string('▔', body), Tone.CodeBorder).Build());
        return lines;
    }

    /// <summary>Code keeps its spaces, so a long line is cut by columns, not wrapped at words.</summary>
    public static IEnumerable<string> Pieces(string text, int room)
    {
        if (text.Length == 0) { yield return ""; yield break; }
        for (var rest = text; rest.Length > 0;)
        {
            var (head, tail) = TextWidth.Take(rest, Math.Max(room, 1));
            yield return head;
            rest = tail;
        }
    }
}
