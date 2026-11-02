namespace MyOwnLLM.Cli.Terminal;

public enum Tone { Primary, Secondary, Muted, Inactive, Line, Accent, Danger, Added, Removed, Ink }

/// <summary>The backgrounds the interface draws text on.</summary>
public enum Surface { Panel, Code, Inline, Selected, Added, Removed, Accent }

/// <summary>The palette of the Jazari CLI Interface design, rendered for whatever the terminal supports.</summary>
public sealed class Theme(ColorMode mode, bool unicode)
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
