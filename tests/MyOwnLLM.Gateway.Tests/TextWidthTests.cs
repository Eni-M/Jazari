using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class TextWidthTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("中文", 4)]
    [InlineData("한국어", 6)]
    [InlineData("✅ done", 7)]          // a BMP emoji is two columns
    [InlineData("😀", 2)]
    [InlineData("❤️", 2)]               // narrow heart made emoji by VS16
    [InlineData("é", 1)]          // e + combining acute
    [InlineData("a​b", 2)]         // zero-width space
    [InlineData("", 1)]           // Private Use Area (Powerline glyph): narrow, not wide as before
    [InlineData("豈", 2)]           // CJK compatibility ideograph
    [InlineData("𝐀", 1)]               // narrow astral letter
    public void Columns_follow_what_the_terminal_draws(string text, int columns) => Assert.Equal(columns, TextWidth.Of(text));

    [Fact]
    public void A_joined_emoji_is_never_counted_narrower_than_its_parts()
    {
        Assert.True(TextWidth.Of("👨‍👩‍👧") >= 2);
        Assert.Single(TextWidth.Clusters("👨‍👩‍👧"));
    }

    /// <summary>The wrap before grapheme clusters, kept as the reference for plain ASCII text.</summary>
    static List<string> OldWrap(string text, int max)
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

    [Theory]
    [InlineData("")]
    [InlineData("one")]
    [InlineData("  leading spaces and  double  spaces ")]
    [InlineData("a word that goes on and on and on until it has to wrap more than once at narrow widths")]
    [InlineData("averyveryveryverylongwordwithoutanyspaces then short")]
    [InlineData("exactlyeightchars x")]
    public void Ascii_wraps_exactly_as_before(string text)
    {
        foreach (var max in new[] { 8, 9, 10, 13, 20, 40 })
            Assert.Equal(OldWrap(text, max), TextWidth.Wrap(text, max));
    }

    [Fact]
    public void Wide_text_wraps_by_columns_and_never_splits_a_character()
    {
        var rows = TextWidth.Wrap(string.Concat(Enumerable.Repeat("中😀", 10)), 9);

        Assert.All(rows, r => Assert.True(TextWidth.Of(r) <= 9, r));
        Assert.All(rows, r => Assert.False(char.IsHighSurrogate(r[^1])));
        Assert.Equal(string.Concat(Enumerable.Repeat("中😀", 10)), string.Concat(rows));
    }

    [Fact]
    public void Fit_cuts_by_columns_with_an_ellipsis()
    {
        Assert.Equal("short", TextWidth.Fit("short", 10));
        Assert.Equal("中文...", TextWidth.Fit("中文中文中文", 7));
        Assert.Equal("ab", TextWidth.Fit("abcdef", 2));
    }

    [Fact]
    public void The_caret_moves_by_whole_clusters()
    {
        var text = "a👍🏽é";
        Assert.Equal(1, TextWidth.Next(text, 0));
        Assert.Equal(1 + "👍🏽".Length, TextWidth.Next(text, 1));
        Assert.Equal(1, TextWidth.Previous(text, 1 + "👍🏽".Length));
        Assert.Equal(1 + "👍🏽".Length, TextWidth.Previous(text, text.Length));
    }
}
