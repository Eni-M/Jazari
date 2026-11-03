namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// The Jazari terminal components. Every screen is built from these, never styled ad hoc. All text passed in is treated
/// as untrusted and sanitized here, so no caller can forget. In a real terminal each component becomes a block on the
/// <see cref="Screen"/>, laid out in the canvas's geometry (a gutter of short labels: the time, "copy 1", "why"); piped
/// output stays plain text, written as it always was.
/// </summary>
public sealed class Ui
{
    readonly TextWriter _output;
    readonly IBlockSink? _sink;
    readonly Theme _theme;
    readonly Func<int> _width;
    LiveBlock? _wordmark, _tune;
    string _wordmarkText = "";
    (IReadOnlyList<string> Steps, int Done, string Left) _tuneState;

    /// <summary>Text to a writer: plain when there's no color, otherwise drawn by the writer's shared flow-mode screen.</summary>
    public Ui(TextWriter output, Theme theme, Func<int> width)
    {
        _output = output;
        _theme = theme;
        _width = width;
        if (theme.Mode != ColorMode.None) _sink = Screen.For(output, theme, width);
    }

    public Ui(TextWriter output, Theme theme, int width) : this(output, theme, () => width) { }

    /// <summary>Blocks to a screen (the interactive session) or a buffer.</summary>
    public Ui(IBlockSink sink, Theme theme, Func<int> width)
    {
        _output = TextWriter.Null;
        _sink = sink;
        _theme = theme;
        _width = width;
    }

    public const string Name = "J A Z A R I";
    public const int Gutter = Layout.Gutter;
    const int KeyColumn = 12;

    public Theme Theme => _theme;
    public IBlockSink? Sink => _sink;
    public int Width => Math.Max(_width(), 20);
    /// <summary>Drawn on a real terminal (a screen), not written as plain text.</summary>
    public bool Live => _sink is not null;

    void Add(Block block) => _sink!.Append(block);
    void Add(Func<int, IReadOnlyList<Line>> rows) => Add(new LinesBlock(rows));

    public void Blank()
    {
        if (Live) Add(new BlankBlock());
        else _output.WriteLine();
    }

    /// <summary>Adds blocks collected elsewhere (what starting the servers had to say).</summary>
    public void Replay(BlockBuffer buffer)
    {
        if (!Live) return;
        foreach (var block in buffer.Blocks) Add(block);
    }

    /// <summary>"live  Thinking · 12s" until <see cref="ThinkingIndicator.StopAsync"/>; nothing in plain output.</summary>
    public ThinkingIndicator Thinking(string label = "Thinking") => Live ? ThinkingIndicator.Start(_sink!, _theme, label: label) : ThinkingIndicator.None;

    /// <summary>One row of the conversation: a label in the gutter, text beside it, wrapped lines under the text.</summary>
    public void Row(string? label, string text, Tone tone = Tone.Primary, Tone labelTone = Tone.Muted)
    {
        var clean = ConsoleText.Sanitize(text);
        if (!Live) { WriteLines(Wrap(clean, Width).Select(line => _theme.Paint(line, tone))); return; }
        Add(columns => RowLines(label, labelTone, clean, tone, columns));
    }

    static List<Line> RowLines(string? label, Tone labelTone, string text, Style style, int columns, bool boldLabel = false) =>
        Wrap(text, Layout.Body(columns)).Select((line, i) => Layout.Row(i == 0 ? label : null, labelTone, boldLabel).Add(line, style).Build()).ToList();

    public void Line(string text, Tone tone = Tone.Primary) => Row(null, text, tone);

    /// <summary>The top of the session: "jazari — ~/MyOwnLLM", the branch at the far right, and a thin line under them.</summary>
    public void Header(string place, string? branch)
    {
        if (!Live) return;
        var right = ConsoleText.Sanitize(branch ?? "");
        var path = ConsoleText.Sanitize(place);
        var dash = _theme.Glyph('—').ToString();
        Add(columns =>
        {
            // Like every row, it keeps clear of the right edge: text there would wrap when the window narrows.
            var edge = columns - Layout.Margin(columns);
            var room = edge - 2 - TextWidth.Of(right) - 2;
            var left = new LineBuilder().Add("  jazari ", Tone.Muted).Add(dash, Tone.Inactive).Add(" " + TextWidth.Fit(path, Math.Max(room - 9, 4)), Tone.Muted);
            var row = left.PadTo(edge - TextWidth.Of(right)).Add(right, Tone.Muted).Build();
            var rule = new string(_theme.Edges ? '▔' : _theme.Glyph('─'), edge);
            return [row, new Line([new(rule, Tone.Border)])];
        });
    }

    /// <summary>"jazari  0.3.0" under the header; <see cref="ReplaceWordmark"/> later makes it "jazari  ready in 4 s".</summary>
    public void Wordmark(string version)
    {
        if (!Live)
        {
            _output.WriteLine($"{_theme.Paint(Name, Tone.Primary)}  {_theme.Paint(ConsoleText.Sanitize(version), Tone.Muted)}");
            return;
        }
        _wordmarkText = ConsoleText.Sanitize(version);
        _wordmark = new LiveBlock(_ => [Layout.Row().Add("jazari", new Style(Tone.Primary, Bold: true)).Add("  " + _wordmarkText, Tone.Muted).Build()]);
        Add(_wordmark);
    }

    /// <summary>"0.3.0" becomes "ready in 4 s", wherever the wordmark is now.</summary>
    public void ReplaceWordmark(string text)
    {
        if (_wordmark is not { } wordmark) return;
        lock (_sink!.Gate)
        {
            _wordmarkText = ConsoleText.Sanitize(text);
            wordmark.Done();
        }
        _sink.Changed();
    }

    public void Label(string text)
    {
        var clean = ConsoleText.Sanitize(text);
        if (Live) Add(_ => [Layout.Row().Add(clean, Tone.Muted).Build()]);
        else _output.WriteLine(_theme.Paint(clean.ToUpperInvariant(), Tone.Muted));
    }

    /// <summary>Key/value rows; the key column fits the longest key (at least 12), so values never run into keys.</summary>
    public void Rows(IEnumerable<(string Key, string Value)> rows, int? keyWidth = null)
    {
        var list = rows.Select(r => (Key: ConsoleText.Sanitize(r.Key), Value: ConsoleText.Sanitize(r.Value))).ToList();
        var column = keyWidth ?? Math.Max(KeyColumn, list.Select(r => TextWidth.Of(r.Key) + 2).DefaultIfEmpty(0).Max());
        if (!Live)
        {
            WriteLines(list.SelectMany(r => Wrap(r.Value, Width - column).Select((line, i) =>
                _theme.Paint(i == 0 ? r.Key.PadRight(column) : new string(' ', column), Tone.Muted) + _theme.Paint(line, Tone.Secondary))));
            return;
        }
        Add(columns => list.SelectMany(r => Wrap(r.Value, Layout.Body(columns) - column).Select((line, i) =>
            Layout.Row().Add(TextWidth.PadRight(i == 0 ? r.Key : "", column), Tone.Muted).Add(line, Tone.Secondary).Build())).ToList());
    }

    /// <summary>The key column that fits every key in all of <paramref name="groups"/>, so grouped rows line up.</summary>
    public static int KeyWidth(IEnumerable<IEnumerable<(string Key, string Value)>> groups) =>
        Math.Max(KeyColumn, groups.SelectMany(g => g).Select(r => TextWidth.Of(r.Key) + 2).DefaultIfEmpty(0).Max());

    /// <summary>
    /// "✕ message", "why cause", "fix next". When <paramref name="nextIsCommand"/>, the fix is a command to type and is
    /// shown in a box, like code.
    /// </summary>
    public void Error(string message, string? cause, string? next, bool nextIsCommand = false)
    {
        if (!Live)
        {
            _output.WriteLine("ERROR");
            Line(message);
            if (cause is not null) Line("Cause: " + cause, Tone.Muted);
            if (next is not null) Line(next, Tone.Muted);
            return;
        }
        var (cleanMessage, cleanCause, cleanNext) = (ConsoleText.Sanitize(message), cause is null ? null : ConsoleText.Sanitize(cause), next is null ? null : ConsoleText.Sanitize(next));
        var cross = _theme.Glyph('✕').ToString();
        Add(columns =>
        {
            var lines = RowLines(cross, Tone.Danger, cleanMessage, Tone.Primary, columns, boldLabel: true);
            if (cleanCause is not null) lines.AddRange(RowLines("why", Tone.Muted, cleanCause, Tone.Secondary, columns));
            if (cleanNext is null) return lines;
            if (!nextIsCommand) { lines.AddRange(RowLines("fix", Tone.Muted, cleanNext, Tone.Secondary, columns)); return lines; }
            var box = Math.Min(TextWidth.Of(cleanNext) + 4, Layout.Body(columns));
            if (_theme.Edges) lines.Add(Layout.Row().Add(new string('▁', box), Tone.CodeBorder).Build());
            lines.Add(Layout.Row("fix").Add(TextWidth.PadRight("  " + TextWidth.Fit(cleanNext, box - 4), box), new Style(Tone.Primary, Surface.Code)).Build());
            if (_theme.Edges) lines.Add(Layout.Row().Add(new string('▔', box), Tone.CodeBorder).Build());
            return lines;
        });
    }

    /// <summary>The user's turn: "› question", continuation lines indented under it.</summary>
    public void UserMessage(string text)
    {
        var paragraphs = ConsoleText.Sanitize(text).Split('\n');
        if (!Live)
        {
            WriteLines(paragraphs.SelectMany(p => Wrap(p, Width - 2)).Select((line, i) => $"{(i == 0 ? _theme.Glyph('›').ToString() : " ")} {line}"));
            return;
        }
        var marker = _theme.Glyph('›').ToString();
        Add(columns => paragraphs.SelectMany(p => Wrap(p, Layout.Body(columns)))
            .Select((line, i) => Layout.Row(i == 0 ? marker : null, Tone.Accent, bold: true).Add(line, Tone.Secondary).Build()).ToList());
    }

    public void Metadata(string text, string? label = null)
    {
        if (Live) Row(label, text, Tone.Muted);
        else _output.WriteLine(_theme.Paint(ConsoleText.Sanitize(text), Tone.Muted));
    }

    /// <summary>
    /// One line of a proposed change: a line number or −/+ in its own column, on the change's background. Lines in a row
    /// share one box.
    /// </summary>
    public void DiffLine(string marker, string text, Tone tone, Surface? surface = null)
    {
        if (!Live) { _output.WriteLine($"{marker} {ConsoleText.Sanitize(text)}"); return; }
        lock (_sink!.Gate)
        {
            var open = _sink is Screen screen && screen.Blocks.Count > 0 && screen.Blocks[^1] is DiffBlock last ? last
                : _sink is BlockBuffer buffer && buffer.Blocks.Count > 0 && buffer.Blocks[^1] is DiffBlock buffered ? buffered : null;
            if (open is null)
            {
                open = new DiffBlock(_theme);
                open.Add(marker, text, tone, surface ?? Surface.Code);
                Add(open);
                return;
            }
            open.Add(marker, text, tone, surface ?? Surface.Code);
        }
        _sink.Changed();
    }

    /// <summary>The live "Tuning for this PC" block: what's left, a bar, and each step done, running or waiting. Redraws in place.</summary>
    public void TuneProgress(IReadOnlyList<string> steps, int done, string left, bool redraw)
    {
        if (!Live) return;
        lock (_sink!.Gate) _tuneState = (steps.Select(ConsoleText.Sanitize).ToList(), done, ConsoleText.Sanitize(left));
        if (redraw && _tune is not null) { _sink.Changed(); return; }
        _tune = new LiveBlock(TuneRows);
        Add(_tune);
    }

    IReadOnlyList<Line> TuneRows(int columns)
    {
        var (steps, done, left) = _tuneState;
        var dot = _theme.Glyph('·');
        var bar = Math.Min(Layout.Body(columns), 66);
        var filled = steps.Count == 0 ? bar : bar * Math.Min(done, steps.Count) / steps.Count;
        var row = Layout.Row();
        foreach (var (step, i) in steps.Select((s, i) => (s, i)))
        {
            if (i > 0) row.Add("   ");
            if (i < done) row.Add(_theme.Glyph('✓').ToString(), Tone.Secondary).Add(" " + step, Tone.Muted);
            else if (i == done) row.Add($"{_theme.Glyph('●')} {step}", Tone.Primary);
            else row.Add($"{_theme.Glyph('○')} {step}", Tone.Muted);
        }
        return
        [
            Layout.Row("live", Tone.Accent).Add("Tuning for this PC ", Tone.Primary).Add($"{dot} {left}", Tone.Muted).Build(),
            Layout.Row().Add(new string(_theme.Glyph('━'), filled), Tone.Accent).Add(new string(_theme.Glyph('━'), bar - filled), Tone.Line).Build(),
            row.Build(),
        ];
    }

    /// <summary>Several rows as one write.</summary>
    void WriteLines(IEnumerable<string> lines) => _output.Write(string.Concat(lines.Select(l => l + _output.NewLine)));

    /// <summary>Word-wraps to <paramref name="max"/> columns (at least 8); words longer than a line are split between characters.</summary>
    public static IReadOnlyList<string> Wrap(string text, int max) => TextWidth.Wrap(text, Math.Max(max, 8));
}
