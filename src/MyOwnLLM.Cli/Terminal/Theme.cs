namespace MyOwnLLM.Cli.Terminal;

public enum Tone { Primary, Secondary, Muted, Inactive, Line, Accent, Danger, Added, Removed, Ink, Border, CodeBorder, ChipBorder, MeterEmpty }

/// <summary>The backgrounds the interface draws text on.</summary>
public enum Surface { Panel, Code, Inline, Selected, Added, Removed, Accent }

/// <summary>The palette of the Jazari CLI Interface design, rendered for whatever the terminal supports.</summary>
/// <param name="edges">Thin box edges drawn with one-eighth blocks (▁▔▕▏). Windows Terminal and VS Code draw these
/// glyphs themselves; classic console fonts may not have them, so there boxes are drawn with backgrounds only.</param>
public sealed class Theme(ColorMode mode, bool unicode, bool? edges = null)
{
    public const string Reset = "\e[0m";

    // tone → (24-bit color, 256-color index, 16-color SGR code)
    static readonly Dictionary<Tone, (int Rgb, int Index, int Basic)> Palette = new()
    {
        [Tone.Primary] = (0xECECE8, 255, 97),
        [Tone.Secondary] = (0xB4B4AE, 250, 37),
        [Tone.Muted] = (0x8A8A84, 245, 90),
        [Tone.Inactive] = (0x5F5F5A, 240, 90),
        [Tone.Line] = (0x26282C, 236, 90),
        [Tone.Accent] = (0x8FB8E8, 111, 94),
        [Tone.Danger] = (0xF0907B, 209, 91),
        [Tone.Added] = (0xA6D9AE, 151, 92),
        [Tone.Removed] = (0xEBA596, 217, 91),
        [Tone.Ink] = (0x0E0F11, 233, 30),
        [Tone.Border] = (0x1E2024, 234, 90),
        [Tone.CodeBorder] = (0x222428, 235, 90),
        [Tone.ChipBorder] = (0x2C2E33, 236, 90),
        [Tone.MeterEmpty] = (0x33353A, 237, 90),
    };

    // surface → (24-bit color, 256-color index); below 256 colors there are no backgrounds
    static readonly Dictionary<Surface, (int Rgb, int Index)> Surfaces = new()
    {
        [Surface.Panel] = (0x121316, 233),
        [Surface.Code] = (0x16171A, 234),
        [Surface.Inline] = (0x1B1C20, 234),
        [Surface.Selected] = (0x1E2024, 235),
        [Surface.Added] = (0x18261B, 22),
        [Surface.Removed] = (0x2A1C1A, 52),
        [Surface.Accent] = (0x8FB8E8, 111),
    };

    static readonly Dictionary<char, char> Ascii = new()
    {
        ['✓'] = '+', ['›'] = '>', ['·'] = '-', ['│'] = '|', ['─'] = '-', ['—'] = '-', ['✕'] = 'x', ['−'] = '-',
        ['●'] = '*', ['○'] = 'o', ['■'] = '#', ['━'] = '=',
    };

    public ColorMode Mode => mode;

    /// <summary>Whether thin box edges are drawn (see the constructor).</summary>
    public bool Edges { get; } = (edges ?? true) && unicode;

    /// <summary>
    /// One row as escape codes, cut to <paramref name="columns"/> so the terminal never wraps it. Every row ends by
    /// clearing the rest of the line (on the fill color, if any), so a shorter row leaves nothing of the longer one behind.
    /// </summary>
    public string Encode(Line line, int columns)
    {
        var text = new System.Text.StringBuilder();
        Style? current = null;
        var used = 0;
        foreach (var span in line.Spans)
        {
            if (span.Text.Length == 0) continue;
            var room = columns - used;
            if (room <= 0) break;
            var piece = span.Text;
            if (TextWidth.Of(piece) > room)
            {
                piece = TextWidth.Take(piece, room).Head;
                if (TextWidth.Of(piece) > room) break;
            }
            if (current != span.Style)
            {
                text.Append(Sgr(span.Style));
                current = span.Style;
            }
            text.Append(piece);
            used += TextWidth.Of(piece);
        }
        if (line.Fill is { } fill && Sgr(new Style(Bg: fill)) is var background && background != "\e[0m")
            text.Append(background).Append("\e[K").Append(Reset);
        else text.Append(Reset).Append("\e[K");
        return text.ToString();
    }

    /// <summary>
    /// The escape code for a style, starting from a reset. Below 256 colors there are no backgrounds, so a selected row
    /// and the accent button are drawn in reverse video instead; with no color at all, reverse video and bold remain.
    /// </summary>
    public string Sgr(Style style)
    {
        var codes = new List<string> { "0" };
        if (style.Bold) codes.Add("1");
        var reverse = style.Bg is Surface.Selected or Surface.Accent;
        switch (mode)
        {
            case ColorMode.TrueColor:
                if (style.Fg is { } fg) codes.Add("38;2;" + Rgb(Palette[fg].Rgb));
                if (style.Bg is { } bg) codes.Add("48;2;" + Rgb(Surfaces[bg].Rgb));
                break;
            case ColorMode.Ansi256:
                if (style.Fg is { } fg256) codes.Add("38;5;" + Palette[fg256].Index);
                if (style.Bg is { } bg256) codes.Add("48;5;" + Surfaces[bg256].Index);
                break;
            case ColorMode.Ansi16:
                var tone = style.Bg is Surface.Accent ? Tone.Accent : style.Fg;
                if (tone is { } fg16) codes.Add(Palette[fg16].Basic.ToString());
                if (reverse) codes.Add("7");
                break;
            default:
                if (reverse) codes.Add("7");
                break;
        }
        return "\e[" + string.Join(';', codes) + "m";
    }

    static string Rgb(int color) => $"{color >> 16 & 0xFF};{color >> 8 & 0xFF};{color & 0xFF}";

    public string Paint(string text, Tone tone)
    {
        var (rgb, index, basic) = Palette[tone];
        return mode switch
        {
            ColorMode.TrueColor => $"\e[38;2;{Rgb(rgb)}m{text}{Reset}",
            ColorMode.Ansi256 => $"\e[38;5;{index}m{text}{Reset}",
            ColorMode.Ansi16 => $"\e[{basic}m{text}{Reset}",
            _ => text,
        };
    }

    public string Bold(string text) => mode == ColorMode.None ? text : $"\e[1m{text}{Reset}";

    string Background(Surface surface)
    {
        var (rgb, index) = Surfaces[surface];
        return mode switch
        {
            ColorMode.TrueColor => $"\e[48;2;{Rgb(rgb)}m",
            ColorMode.Ansi256 => $"\e[48;5;{index}m",
            _ => "",
        };
    }

    /// <summary>Text on a background. Painted text inside it resets colors, so the background is restored after each reset.</summary>
    public string On(Surface surface, string text)
    {
        var background = Background(surface);
        return background.Length == 0 ? text : background + text.Replace(Reset, Reset + background) + Reset;
    }

    /// <summary>
    /// A row on a background out to the terminal's edge. Filled by "erase to end of line", so it adds no characters
    /// that a mouse selection would pick up.
    /// </summary>
    public string Band(Surface surface, string text)
    {
        var background = Background(surface);
        return background.Length == 0 ? text : background + text.Replace(Reset, Reset + background) + "\e[K" + Reset;
    }

    /// <summary>Inline code.</summary>
    public string Code(string text) => On(Surface.Inline, text);

    public char Glyph(char c) => unicode || !Ascii.TryGetValue(c, out var ascii) ? c : ascii;
}
