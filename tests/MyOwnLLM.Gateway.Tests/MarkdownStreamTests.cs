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

        Assert.Contains(";48;2;22;23;26m", output, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_is_backgrounded() => Assert.Contains(";48;2;27;28;32mFoo()", Render("use `Foo()` here\n", ColorMode.TrueColor), StringComparison.Ordinal);

    [Fact]
    public void Table_is_aligned_after_it_ends()
    {
        Assert.Equal(["a    bb", "ccc  d", "after"], PlainLines("| a | bb |\n|---|---|\n| ccc | d |\nafter\n"));
    }

    [Fact]
    public void Table_at_the_end_is_flushed_on_complete() => Assert.Equal(["x  y"], PlainLines("| x | y |"));

    [Fact]
    public void Unclosed_fence_is_closed_on_complete() =>
        Assert.StartsWith(new string(' ', Ui.Gutter) + "▔", Screen(Render("```\ncode", ColorMode.TrueColor))[^1]);

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
    public void Separator_only_table_does_not_crash() => Assert.DoesNotContain(PlainLines("|---|---|\n"), l => l.Length > 0);

    [Fact]
    public void Long_lines_are_never_redrawn_across_rows()
    {
        var output = Render(string.Join(" ", Enumerable.Repeat("word", 60)) + "\n\nnext\n", ColorMode.TrueColor, width: 40);

        Assert.DoesNotMatch(@"\e\[\d+F", output);
    }

    [Fact]
    public void Tabs_become_spaces_so_columns_can_be_counted() => Assert.DoesNotContain('\t', Render("a\tb\n", ColorMode.TrueColor));

    [Fact]
    public void A_line_shows_while_it_streams_and_settles_once_complete()
    {
        var raw = Render("- item\n", ColorMode.TrueColor);

        Assert.Contains("\e[1A\r\e[J", raw, StringComparison.Ordinal); // the unfinished line is redrawn in place
        Assert.Equal(["          - item"], Screen(raw));
    }

    /// <summary>What a real terminal ends up showing, scrollback included; nothing may wrap on its own.</summary>
    static string[] Screen(string raw)
    {
        var vt = new VtScreen(201, 200);
        vt.Write(raw);
        Assert.Equal(0, vt.SoftWraps);
        return vt.Everything.Reverse().SkipWhile(l => l.Length == 0).Reverse().ToArray();
    }

    static string Stream(int width, params string[] tokens)
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(ColorMode.TrueColor, true), width);
        foreach (var token in tokens) stream.Write(token);
        stream.Complete();
        return output.ToString();
    }

    [Fact]
    public void Answers_sit_beside_the_gutter() => Assert.Equal(["          Hello there"], Screen(Stream(80, "Hel", "lo the", "re\n")));

    [Fact]
    public void Long_lines_wrap_beside_the_gutter_as_they_stream()
    {
        var words = Enumerable.Repeat("word ", 12).ToArray();

        var rows = Screen(Stream(40, words));

        Assert.True(rows.Length > 1);
        Assert.All(rows, r => Assert.StartsWith(new string(' ', Ui.Gutter) + "word", r));
        Assert.All(rows, r => Assert.True(r.Length <= 40, $"too long: '{r}'"));
        Assert.Equal(12, string.Concat(rows).Split("word").Length - 1);
    }

    [Fact]
    public void Text_stays_in_the_designs_column_on_a_wide_terminal() =>
        Assert.All(Screen(Stream(200, string.Concat(Enumerable.Repeat("word ", 60)))), r => Assert.True(r.Length <= Ui.Gutter + 86));

    [Fact]
    public void Bold_markers_never_show_even_while_streaming()
    {
        var rows = Screen(Stream(80, "a **bo", "ld** b\n"));

        Assert.Equal(["          a bold b"], rows);
    }

    [Fact]
    public void Code_blocks_are_numbered_in_the_gutter()
    {
        var rows = Screen(Stream(80, "```Program.cs\nvar x = 1;\n```\ndone\n"));

        Assert.Equal(new string(' ', Ui.Gutter) + new string('▁', Layout.Body(80)), rows[0]);
        Assert.Equal("  copy 1    Program.cs", rows[1]);
        Assert.Equal("            var x = 1;", rows[2]);
        Assert.Equal(new string(' ', Ui.Gutter) + new string('▔', Layout.Body(80)), rows[3]);
        Assert.Equal("          done", rows[4]);
    }

    [Fact]
    public void Code_on_its_own_background_adds_no_trailing_spaces()
    {
        var raw = Stream(80, "```\nx\n```\n");

        Assert.Contains(";48;2;22;23;26m", raw, StringComparison.Ordinal);
        Assert.Contains("\e[K", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_code_is_cut_by_columns_keeping_every_character()
    {
        var line = "var result = items.Where(i => i.IsActive).Select(i => i.Name).ToList();";

        var rows = Screen(Stream(40, "```\n" + line + "\n```\n"));

        var code = rows.Where(r => r.Length > Ui.Gutter + 2 && r[..(Ui.Gutter + 2)].Trim().Length == 0 && r[Ui.Gutter + 2] is not ('▁' or '▔'));
        Assert.Equal(line.Replace(" ", ""), string.Concat(code).Replace(" ", ""));
        Assert.True(code.Count() > 1);
    }

    [Fact]
    public void Plain_output_has_no_gutter() => Assert.Equal(["Hello"], PlainLines("Hello\n"));

    [Fact]
    public void In_a_one_off_command_the_time_follows_the_answer()
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(ColorMode.TrueColor, true), 80);
        stream.Write("one\ntwo\n");
        stream.Complete();

        stream.StampTime("3.2s");

        Assert.Equal(["          one", "          two", "    3.2s"], Screen(output.ToString()));
    }

    [Fact]
    public void The_time_never_replaces_a_code_blocks_number()
    {
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(ColorMode.TrueColor, true), 80);
        stream.Write("```\nx\n```\nsaid\n");
        stream.Complete();

        stream.StampTime("3.2s");

        Assert.Contains(Screen(output.ToString()), r => r.TrimStart().StartsWith("copy 1"));
        Assert.Contains("3.2s", Screen(output.ToString())[^1]);
    }

    [Fact]
    public void A_finished_answer_is_never_redrawn_to_add_its_time()
    {
        // Printed rows are final in a one-off command: the time goes on a new row instead of moving back up.
        var output = new StringWriter();
        var stream = new MarkdownStream(output, new Theme(ColorMode.TrueColor, true), 80);
        stream.Write("1\n2\n3\n4\n");
        stream.Complete();

        stream.StampTime("3.2s");

        Assert.DoesNotMatch(@"\e\[\d+A", output.ToString()[output.ToString().LastIndexOf('4')..]);
        Assert.Contains("3.2s", Screen(output.ToString())[^1]);
    }
}
