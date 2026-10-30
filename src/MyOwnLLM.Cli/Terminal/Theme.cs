namespace MyOwnLLM.Cli.Terminal;

public enum Tone { Primary, Secondary, Muted, Inactive, Line }

/// <summary>The monochrome palette, rendered for whatever the terminal supports.</summary>
public sealed class Theme(ColorMode mode, bool unicode)
{
    public const string Reset = "\e[0m";

    // tone → (24-bit gray level, 256-color index, 16-color SGR code)
    static readonly Dictionary<Tone, (int Gray, int Index, int Basic)> Palette = new()
    {
        [Tone.Primary] = (0xF2, 255, 97),
        [Tone.Secondary] = (0xBB, 250, 37),
        [Tone.Muted] = (0x77, 243, 90),
        [Tone.Inactive] = (0x55, 240, 90),
        [Tone.Line] = (0x30, 236, 90),
    };

    static readonly Dictionary<char, char> Ascii = new() { ['✓'] = '+', ['›'] = '>', ['·'] = '-', ['│'] = '|', ['─'] = '-' };

    public ColorMode Mode => mode;

    public string Paint(string text, Tone tone)
    {
        var (gray, index, basic) = Palette[tone];
        return mode switch
        {
            ColorMode.TrueColor => $"\e[38;2;{gray};{gray};{gray}m{text}{Reset}",
            ColorMode.Ansi256 => $"\e[38;5;{index}m{text}{Reset}",
            ColorMode.Ansi16 => $"\e[{basic}m{text}{Reset}",
            _ => text,
        };
    }

    /// <summary>Text on the code background (#151515); no background below 256 colors.</summary>
    public string Code(string text) => mode switch
    {
        ColorMode.TrueColor => $"\e[48;2;21;21;21m{text}{Reset}",
        ColorMode.Ansi256 => $"\e[48;5;233m{text}{Reset}",
        _ => text,
    };

    public char Glyph(char c) => unicode || !Ascii.TryGetValue(c, out var ascii) ? c : ascii;
}
