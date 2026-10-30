using System.Text.RegularExpressions;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class MarkdownStreamTests
{
    static string Render(string markdown, ColorMode mode = ColorMode.None, int width = 80)
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(mode, unicode: true), width);
        for (var i = 0; i < markdown.Length; i += 3) stream.Write(markdown.Substring(i, Math.Min(3, markdown.Length - i)));
        stream.Complete();
        return output.ToString();
    }

    static string[] PlainLines(string markdown, int width = 80) =>
        Render(markdown, ColorMode.None, width).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void Plain_text_passes_through() => Assert.Equal(["hello world"], PlainLines("hello world\n"));

    [Fact]
    public void Heading_drops_hashes() => Assert.Equal(["Setup"], PlainLines("## Setup\n"));

    [Fact]
    public void List_items_indent_two() => Assert.Equal(["  - a", "  - b", "  1. c"], PlainLines("- a\n* b\n1. c\n"));

    [Fact]
    public void Inline_code_loses_backticks_in_plain() => Assert.Equal(["use Foo() here"], PlainLines("use `Foo()` here\n"));

    [Fact]
    public void Links_show_text_then_url() => Assert.Equal(["see docs (https://x.dev)"], PlainLines("see [docs](https://x.dev)\n"));

    [Fact]
    public void Code_fence_keeps_code_and_shows_language()
    {
        Assert.Equal(["[1] csharp", "var x = 1;", "after"], PlainLines("```csharp\nvar x = 1;\n```\nafter\n"));
    }

    [Fact]
    public void Code_fence_lines_get_code_background()
    {
        var output = Render("```\nvar x = 1;\n```\n", ColorMode.TrueColor);

        Assert.Contains("\e[48;2;21;21;21mvar x = 1;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_is_backgrounded() => Assert.Contains("\e[48;2;21;21;21mFoo()", Render("use `Foo()` here\n", ColorMode.TrueColor), StringComparison.Ordinal);

    [Fact]
    public void Table_is_aligned_after_it_ends()
    {
        Assert.Equal(["a    bb", "ccc  d", "after"], PlainLines("| a | bb |\n|---|---|\n| ccc | d |\nafter\n"));
    }

    [Fact]
    public void Table_at_the_end_is_flushed_on_complete() => Assert.Equal(["x  y"], PlainLines("| x | y |"));

    [Fact]
    public void Unclosed_fence_resets_on_complete() => Assert.EndsWith("\e[0m", Render("```\ncode", ColorMode.TrueColor), StringComparison.Ordinal);

    [Fact]
    public void Escape_codes_are_stripped() =>
        Assert.False(Render("\e[31mred\n", ColorMode.TrueColor).Contains("\e[31m", StringComparison.Ordinal));

    [Fact]
    public void Plain_mode_never_writes_escape_sequences() =>
        Assert.DoesNotContain('\e', Render("# H\n- a `b`\n```\nc\n```\n| d | e |\n"));

    [Fact]
    public void Wraps_long_lines_at_width_40()
    {
        var lines = PlainLines(string.Join(" ", Enumerable.Repeat("word", 30)) + "\n", width: 40);

        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 40));
    }

    [Fact]
    public void Color_mode_shows_tokens_before_the_line_ends()
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(ColorMode.TrueColor, true), 80);

        stream.Write("Hel");

        Assert.Contains("Hel", Regex.Replace(output.ToString(), "\e\\[[0-9;]*[A-Za-z]", ""));
    }

    [Fact]
    public void Code_fence_keeps_indentation() =>
        Assert.Equal(["[1]", "if (x)", "    return x;", "        nested();"], PlainLines("```\nif (x)\n    return x;\n        nested();\n```\n"));

    [Fact]
    public void Long_code_lines_are_not_word_wrapped_at_spaces()
    {
        var line = "var result = items.Where(i => i.IsActive).Select(i => i.Name).ToList();";
        var lines = PlainLines("```\n" + line + "\n```\n", width: 40);

        Assert.Equal(line, string.Concat(lines.Skip(1)));
    }

    [Fact]
    public void Separator_only_table_does_not_crash() => Assert.Empty(PlainLines("|---|---|\n").Where(l => l.Length > 0));

    [Fact]
    public void Long_lines_are_never_redrawn_across_rows()
    {
        var output = Render(string.Join(" ", Enumerable.Repeat("word", 60)) + "\n\nnext\n", ColorMode.TrueColor, width: 40);

        Assert.DoesNotMatch(@"\e\[\d+F", output);
    }

    [Fact]
    public void Lines_with_tabs_or_wide_characters_are_not_redrawn()
    {
        Assert.DoesNotContain("\r\e[2K", Render("a\tb\n", ColorMode.TrueColor), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\e[2K", Render("你好 `x`\n", ColorMode.TrueColor), StringComparison.Ordinal);
    }

    [Fact]
    public void Short_styled_line_is_redrawn_in_place() => Assert.Contains("\r\e[2K", Render("- item\n", ColorMode.TrueColor), StringComparison.Ordinal);
}
