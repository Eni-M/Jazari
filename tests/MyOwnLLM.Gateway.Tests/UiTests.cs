using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class UiTests
{
    static (Ui ui, StringWriter output) Plain(int width = 80)
    {
        var output = new StringWriter();
        return (new Ui(output, new Theme(ColorMode.None, unicode: true), width), output);
    }

    static string[] Lines(StringWriter w) => w.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void Wordmark_is_spaced_capitals()
    {
        var (ui, output) = Plain();

        ui.Wordmark("0.2.0");

        Assert.Equal("J A Z A R I  0.2.0", Lines(output)[0]);
    }

    [Fact]
    public void Rows_align_keys_in_a_12_column()
    {
        var (ui, output) = Plain();

        ui.Rows([("Model", "Granite 4.1 3B · local"), ("Directory", "~/EduCore")]);

        Assert.Equal(["Model       Granite 4.1 3B · local", "Directory   ~/EduCore"], Lines(output));
    }

    [Fact]
    public void Rows_wrap_at_40_columns()
    {
        var (ui, output) = Plain(width: 40);

        ui.Rows([("Directory", string.Join(" ", Enumerable.Repeat("segment", 14)))]);

        var lines = Lines(output);
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 40, $"too long: '{l}'"));
        Assert.All(lines.Skip(1), l => Assert.StartsWith(new string(' ', 12), l));
    }

    [Fact]
    public void Error_block_has_label_message_cause_next()
    {
        var (ui, output) = Plain();

        ui.Error("Could not reach the model server.", "connection refused", "Start it, then /retry.");

        Assert.Equal(["ERROR", "Could not reach the model server.", "Cause: connection refused", "Start it, then /retry."], Lines(output));
    }

    [Fact]
    public void Error_without_cause_or_next()
    {
        var (ui, output) = Plain();

        ui.Error("Broken.", null, null);

        Assert.Equal(["ERROR", "Broken."], Lines(output));
    }

    [Fact]
    public void Components_strip_control_characters_from_untrusted_text()
    {
        var (ui, output) = Plain();

        ui.Metadata("ls\r\e[2Krm -rf /");
        ui.Line("a\e]0;title\ab");
        ui.Rows([("Model", "x\e[31my")]);
        ui.Error("e‮viltxt", "c\b", null);
        ui.UserMessage("f\e[1Ag");

        var text = output.ToString();
        Assert.DoesNotContain('\e', text);
        Assert.DoesNotContain('\b', text);
        Assert.DoesNotContain('‮', text);
        Assert.Contains("ls[2Krm -rf /", text);
    }

    [Fact]
    public void The_session_screen_sets_the_title_and_clears_the_window_but_keeps_the_scrollback()
    {
        var terminal = new FakeTerminal(80, 10);
        terminal.Vt.Write("earlier shell output\r\n");

        new Screen(terminal, new Theme(ColorMode.TrueColor, true), bottom: true, paced: false).Start();

        Assert.Contains("0;Jazari", terminal.Vt.Signals);
        Assert.DoesNotContain("\e[3J", string.Concat(terminal.Frames));
        Assert.Contains("earlier shell output", terminal.Vt.Scrollback);
    }

    [Fact]
    public void Redirected_output_has_no_escape_sequences()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorModeDetector.Detect(_ => null, outputRedirected: true), unicode: true);
        var ui = new Ui(output, theme, 80);

        ui.Wordmark("0.2.0");
        ui.Rows([("Model", "x")]);
        ui.Label("you");
        ui.Error("e", "c", "n");
        ui.Metadata("ix-1 · 2.0 s");

        Assert.DoesNotContain('\e', output.ToString());
    }

    [Fact]
    public void User_message_is_a_prompt_row()
    {
        var (ui, output) = Plain();

        ui.UserMessage("how do I add DI?\nand scopes?");

        Assert.Equal(["› how do I add DI?", "  and scopes?"], Lines(output));
    }

    [Fact]
    public void User_message_hangs_off_the_gutter_in_a_terminal()
    {
        var output = new StringWriter();
        new Ui(output, new Theme(ColorMode.TrueColor, unicode: true), 40).UserMessage("hi\nthere");

        Assert.Equal(["       ›  hi", "          there"], Shown(output, 40));
    }

    /// <summary>What a terminal one column wider than <paramref name="width"/> shows after the output.</summary>
    static string[] Shown(StringWriter output, int width)
    {
        var vt = new VtScreen(width + 1, 60);
        vt.Write(output.ToString());
        Assert.Equal(0, vt.SoftWraps);
        return vt.Everything.Reverse().SkipWhile(l => l.Length == 0).Reverse().ToArray();
    }

    static string[] Clean(StringWriter output) =>
        System.Text.RegularExpressions.Regex.Replace(output.ToString(), "\e\\[[0-9;]*[A-Za-z]", "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    static (Ui ui, StringWriter output) Live(int width = 80)
    {
        var output = new StringWriter();
        return (new Ui(output, new Theme(ColorMode.TrueColor, unicode: true), width), output);
    }

    [Fact]
    public void Rows_put_the_label_in_the_gutter_and_wrap_beside_it()
    {
        var (ui, output) = Live(width: 28);

        ui.Row("read", "two files and some tokens", Tone.Muted);

        Assert.Equal(["    read  two files and", "          some tokens"], Shown(output, 28));
    }

    [Fact]
    public void Errors_say_why_and_how_to_fix_it_in_the_gutter()
    {
        var (ui, output) = Live();

        ui.Error("Can't reach the gateway.", "Nothing answered.", "jazari update");

        Assert.Equal(["       ✕  Can't reach the gateway.", "     why  Nothing answered.", "     fix  jazari update"], Shown(output, 80));
    }

    [Fact]
    public void Header_names_the_place_and_the_branch()
    {
        var (ui, output) = Live(width: 40);

        ui.Header("~/MyOwnLLM", "main");

        var rows = Shown(output, 40);
        Assert.Equal("  jazari — ~/MyOwnLLM             main", rows[0]);
        Assert.Equal(new string('▔', 38), rows[1]); // a thin line under it, stopping at the right margin
    }

    [Fact]
    public void Header_is_left_out_of_piped_output()
    {
        var (ui, output) = Plain();

        ui.Header("~/MyOwnLLM", "main");

        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Tune_progress_redraws_in_place()
    {
        var (ui, output) = Live();

        ui.TuneProgress(["cpu", "gpu"], 0, "a few minutes", redraw: false);
        ui.TuneProgress(["cpu", "gpu"], 1, "about 1 min left", redraw: true);

        var text = output.ToString();
        Assert.Equal(1, text.Split("\e[3A").Length - 1);
        Assert.Contains("✓", Clean(output)[^1]);
        Assert.Contains("● gpu", Clean(output)[^1]);
    }

    sealed class CountingWriter : StringWriter
    {
        public int Writes;
        public override void Write(string? value) { Writes++; base.Write(value); }
        public override void Write(char value) { Writes++; base.Write(value); }
    }

    [Fact]
    public void A_wrapped_row_reaches_the_screen_in_one_write()
    {
        var output = new CountingWriter();
        var ui = new Ui(output, new Theme(ColorMode.TrueColor, unicode: true), 40);

        ui.Row("why", string.Join(' ', Enumerable.Repeat("word", 40)));

        Assert.True(Lines(output).Length > 3);
        Assert.Equal(1, output.Writes);
    }
}
