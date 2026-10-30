using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class TerminalThemeTests
{
    static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData("NO_COLOR", "1", false, ColorMode.None)]
    [InlineData("COLORTERM", "truecolor", false, ColorMode.TrueColor)]
    [InlineData("COLORTERM", "24bit", false, ColorMode.TrueColor)]
    [InlineData("WT_SESSION", "abc", false, ColorMode.TrueColor)]
    [InlineData("TERM", "xterm-256color", false, ColorMode.Ansi256)]
    [InlineData("TERM", "xterm", false, ColorMode.Ansi16)]
    [InlineData("COLORTERM", "truecolor", true, ColorMode.None)]
    public void Detect(string name, string value, bool redirected, ColorMode expected)
    {
        Assert.Equal(expected, ColorModeDetector.Detect(Env((name, value)), redirected));
    }

    [Fact]
    public void Detect_with_nothing_is_16_colors() => Assert.Equal(ColorMode.Ansi16, ColorModeDetector.Detect(Env(), false));

    [Fact]
    public void No_color_beats_truecolor() =>
        Assert.Equal(ColorMode.None, ColorModeDetector.Detect(Env(("NO_COLOR", "1"), ("COLORTERM", "truecolor")), false));

    [Fact]
    public void Paint_truecolor_uses_exact_rgb()
    {
        var s = new Theme(ColorMode.TrueColor, unicode: true).Paint("x", Tone.Muted);

        Assert.Contains("\e[38;2;119;119;119m", s);
        Assert.EndsWith("\e[0m", s);
    }

    [Fact]
    public void Paint_256_uses_index() => Assert.Contains("\e[38;5;243m", new Theme(ColorMode.Ansi256, true).Paint("x", Tone.Muted));

    [Fact]
    public void Paint_16_uses_basic_codes() => Assert.Contains("\e[97m", new Theme(ColorMode.Ansi16, true).Paint("x", Tone.Primary));

    [Fact]
    public void Code_truecolor_has_background_and_resets()
    {
        var s = new Theme(ColorMode.TrueColor, true).Code("y");

        Assert.Contains("\e[48;2;21;21;21m", s);
        Assert.EndsWith("\e[0m", s);
    }

    [Fact]
    public void Code_without_256_colors_has_no_background() => Assert.DoesNotContain("48;", new Theme(ColorMode.Ansi16, true).Code("y"));

    [Fact]
    public void None_is_plain()
    {
        var theme = new Theme(ColorMode.None, true);

        Assert.Equal("x", theme.Paint("x", Tone.Muted));
        Assert.Equal("y", theme.Code("y"));
    }

    [Theory]
    [InlineData('✓', '+')]
    [InlineData('›', '>')]
    [InlineData('·', '-')]
    [InlineData('│', '|')]
    [InlineData('─', '-')]
    public void Glyph_falls_back_without_unicode(char unicode, char ascii)
    {
        Assert.Equal(ascii, new Theme(ColorMode.None, unicode: false).Glyph(unicode));
        Assert.Equal(unicode, new Theme(ColorMode.None, unicode: true).Glyph(unicode));
    }
}
