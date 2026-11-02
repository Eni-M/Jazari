using System.Text.RegularExpressions;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class PromptEditorTests
{
    static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool shift = false, bool alt = false, bool control = false) => new(c, key, shift, alt, control);
    static ConsoleKeyInfo Enter => Key(ConsoleKey.Enter, '\r');
    static string Plain(string text) => Regex.Replace(text, "\e\\[[0-9;?]*[A-Za-z]", "");

    static PromptBuffer Typed(string text, params string[] history)
    {
        var buffer = new PromptBuffer(() => Path.GetTempPath(), history);
        foreach (var c in text) buffer.Apply(c == '\n' ? Key(ConsoleKey.J, '\n', control: true) : Key(ConsoleKey.A, c));
        return buffer;
    }

    [Fact]
    public void Enter_sends_what_was_typed_and_remembers_it()
    {
        var buffer = Typed("hello");

        Assert.Equal(KeyResult.Submit, buffer.Apply(Enter));
        Assert.Equal("hello", buffer.Take());
        Assert.Equal("", buffer.Text);
        Assert.Equal(["hello"], buffer.History);
    }

    [Fact]
    public void A_line_break_in_a_paste_does_not_send()
    {
        var buffer = Typed("first");

        Assert.Equal(KeyResult.Editing, buffer.Apply(Enter, morePending: true));
        Assert.Equal("first\n", buffer.Text);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Modified_enter_adds_a_line(bool shift, bool alt, bool control)
    {
        var buffer = Typed("a");

        Assert.Equal(KeyResult.Editing, buffer.Apply(Key(ConsoleKey.Enter, '\r', shift, alt, control)));
        Assert.Equal("a\n", buffer.Text);
    }

    [Fact]
    public void Ctrl_C_cancels() => Assert.Equal(KeyResult.Cancel, Typed("x").Apply(Key(ConsoleKey.C, '\x03', control: true)));

    [Fact]
    public void Altgr_characters_are_typed()
    {
        var buffer = Typed("");

        buffer.Apply(Key(ConsoleKey.Q, '@', alt: true, control: true));

        Assert.Equal("@", buffer.Text);
    }

    [Fact]
    public void Editing_keys_move_and_remove()
    {
        var buffer = Typed("one two");

        buffer.Apply(Key(ConsoleKey.LeftArrow));
        buffer.Apply(Key(ConsoleKey.Backspace, '\b'));
        Assert.Equal("one to", buffer.Text);

        buffer.Apply(Key(ConsoleKey.Home));
        buffer.Apply(Key(ConsoleKey.Delete));
        Assert.Equal("ne to", buffer.Text);

        buffer.Apply(Key(ConsoleKey.End));
        buffer.Apply(Key(ConsoleKey.W, '\x17', control: true));
        Assert.Equal("ne ", buffer.Text);
    }

    [Fact]
    public void Backspace_removes_a_whole_emoji()
    {
        var buffer = Typed("a😀");

        buffer.Apply(Key(ConsoleKey.Backspace, '\b'));

        Assert.Equal("a", buffer.Text);
    }

    [Fact]
    public void Up_and_down_walk_the_history_and_come_back_to_the_draft()
    {
        var buffer = Typed("draft", "older", "newer");

        buffer.Apply(Key(ConsoleKey.UpArrow));
        Assert.Equal("newer", buffer.Text);
        buffer.Apply(Key(ConsoleKey.UpArrow));
        Assert.Equal("older", buffer.Text);
        buffer.Apply(Key(ConsoleKey.DownArrow));
        buffer.Apply(Key(ConsoleKey.DownArrow));
        Assert.Equal("draft", buffer.Text);
    }

    [Fact]
    public void Up_moves_inside_typed_lines_before_the_history()
    {
        var buffer = Typed("ab\ncd", "older");

        buffer.Apply(Key(ConsoleKey.UpArrow));

        Assert.Equal("ab\ncd", buffer.Text);
        Assert.Equal(2, buffer.Caret);
    }

    [Fact]
    public void Slash_opens_every_command()
    {
        var buffer = Typed("/");

        Assert.Equal(Session.Commands.Length, buffer.Menu.Items.Count);
    }

    [Fact]
    public void The_menu_narrows_and_arrows_choose()
    {
        var buffer = Typed("/re");

        Assert.Equal(["/read", "/review", "/retry"], buffer.Menu.Items);
        buffer.Apply(Key(ConsoleKey.DownArrow));
        Assert.Equal(1, buffer.Selected);
        buffer.Apply(Key(ConsoleKey.UpArrow));
        buffer.Apply(Key(ConsoleKey.UpArrow));
        Assert.Equal(2, buffer.Selected); // wraps
    }

    [Fact]
    public void Tab_completes_the_choice_and_leaves_room_for_its_argument()
    {
        var buffer = Typed("/rea");

        buffer.Apply(Key(ConsoleKey.Tab, '\t'));

        Assert.Equal("/read ", buffer.Text);
    }

    [Fact]
    public void Enter_on_a_command_needing_an_argument_waits_for_it()
    {
        var buffer = Typed("/rea");

        Assert.Equal(KeyResult.Editing, buffer.Apply(Enter));
        Assert.Equal("/read ", buffer.Text);
    }

    [Fact]
    public void Enter_on_a_command_needing_nothing_sends_it()
    {
        var buffer = Typed("/ret");

        Assert.Equal(KeyResult.Submit, buffer.Apply(Enter));
        Assert.Equal("/retry", buffer.Take());
    }

    [Fact]
    public void A_command_typed_out_in_full_sends_at_once()
    {
        var buffer = Typed("/retry");

        Assert.Empty(buffer.Menu.Items);
        Assert.Equal(KeyResult.Submit, buffer.Apply(Enter));
    }

    [Fact]
    public void Esc_closes_the_menu_until_the_text_changes()
    {
        var buffer = Typed("/re");

        buffer.Apply(Key(ConsoleKey.Escape, '\x1b'));
        Assert.Empty(buffer.Menu.Items);
        Assert.Equal("/re", buffer.Text);

        buffer.Apply(Key(ConsoleKey.A, 'a'));
        Assert.NotEmpty(buffer.Menu.Items);
    }

    [Fact]
    public void Layout_cuts_at_the_edge_and_at_line_breaks()
    {
        var (rows, row, column) = PromptPanel.Layout("abcdef\ngh", 9, 4);

        Assert.Equal(["abcd", "ef", "gh"], rows);
        Assert.Equal((2, 2), (row, column));
    }

    [Fact]
    public void Layout_counts_wide_characters_as_two_columns()
    {
        var (rows, _, column) = PromptPanel.Layout("你好吗", 1, 4);

        Assert.Equal(["你好", "吗"], rows);
        Assert.Equal(2, column);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(4100, "4.1k")]
    [InlineData(16384, "16k")]
    public void Tokens_are_short(int tokens, string expected) => Assert.Equal(expected, PromptPanel.Tokens(tokens));

    static PromptPanel.Drawn Draw(PromptBuffer buffer, PanelInfo info, int width = 120)
    {
        var ui = new Ui(new StringWriter(), new Theme(ColorMode.TrueColor, unicode: true), width);
        return PromptPanel.Draw(ui, buffer, info, 40);
    }

    [Fact]
    public void Panel_has_a_rule_the_prompt_and_the_line_under_it()
    {
        var drawn = Draw(Typed("hi"), new("Program.cs", "Enter sends", "granite 3b · iris xe · 14 tok/s", (4100, 16384)));
        var rows = drawn.Rows.Select(Plain).ToList();

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("─", rows[0]);
        Assert.Equal("     ›  hi", rows[1]);
        Assert.Contains(" Program.cs ", rows[2]);
        Assert.Contains("Enter sends", rows[2]);
        Assert.Contains("granite 3b · iris xe · 14 tok/s", rows[2]);
        Assert.EndsWith("■■■■■■■■■■  4.1k of 16k", rows[2]);
        Assert.Equal((1, Ui.Gutter + 2), (drawn.CaretRow, drawn.CaretColumn));
    }

    [Fact]
    public void Panel_rows_fit_the_width_so_the_terminal_never_wraps_them()
    {
        var buffer = Typed("/");
        var info = new PanelInfo("a-rather-long-file-name-for-a-chip.cs", "Enter sends · / commands", "granite 3b · iris xe · 14 tok/s", (4100, 16384));

        foreach (var width in new[] { 120, 60, 40, 20 })
            Assert.All(Draw(buffer, info, width).Rows.Skip(1).Select(Plain), r => Assert.True(r.Length <= width, $"{r.Length} > {width}: '{r}'"));
    }

    [Theory]
    [InlineData(5, 3, 40, 0, 37)] // a short conversation: the panel still sits at the bottom
    [InlineData(37, 3, 40, 0, 37)] // the conversation just reaches it
    [InlineData(39, 3, 40, 2, 37)] // past it: the conversation scrolls up
    [InlineData(39, 8, 40, 7, 32)] // a menu makes the panel taller
    [InlineData(0, 50, 40, 0, 0)] // a panel taller than the window starts at the top
    public void The_panel_is_placed_at_the_bottom_of_the_window(int conversationRow, int rows, int height, int scroll, int top) =>
        Assert.Equal((scroll, top), PromptPanel.Place(conversationRow, rows, height));

    [Fact]
    public void Menu_sits_above_the_prompt_and_describes_each_command()
    {
        var drawn = Draw(Typed("/re"), new(null, "hint", "status"));
        var rows = drawn.Rows.Select(Plain).ToList();

        Assert.Contains("/read <path>", rows[1]);
        Assert.Contains("Read a file or folder", rows[1]);
        Assert.Contains("/review [path]", rows[2]);
        Assert.Contains("/retry", rows[3]);
        Assert.Contains($"all {Session.Commands.Length} commands: /", rows[4]);
        Assert.Equal("     ›  /re", rows[5]);
        Assert.DoesNotContain("hint", rows[6]); // the menu's own hint is enough
        Assert.Equal(5, drawn.CaretRow);
    }

    [Fact]
    public void A_menu_of_one_draws()
    {
        var rows = Draw(Typed("/mo"), new(null, "", "")).Rows.Select(Plain).ToList();

        Assert.Contains("/model", rows[1]);
    }

    [Fact]
    public void A_short_screen_scrolls_the_menu_to_the_choice()
    {
        var buffer = Typed("/");
        for (var i = 0; i < 10; i++) buffer.Apply(Key(ConsoleKey.DownArrow));
        var ui = new Ui(new StringWriter(), new Theme(ColorMode.TrueColor, unicode: true), 100);

        var rows = PromptPanel.Draw(ui, buffer, new(null, "", ""), screenRows: 8).Rows.Select(Plain).ToList();

        Assert.Contains(rows, r => r.Contains(buffer.Menu.Items[10]));
        Assert.Equal(1 + 3 + 1 + 1 + 1, rows.Count); // rule, three commands, the menu's hint, prompt, footer
    }

    [Fact]
    public void Offline_shows_a_red_dot_instead_of_the_model()
    {
        var row = Draw(Typed(""), new(null, "", "granite 3b", (10, 100), Offline: true)).Rows[^1];

        Assert.Contains("\e[38;2;240;144;123m●", row, StringComparison.Ordinal);
        Assert.Contains("not connected", row);
        Assert.DoesNotContain("granite", row);
    }

    [Fact]
    public void The_live_row_goes_above_the_prompt()
    {
        var rows = Draw(Typed(""), new(null, "Type now", "starting granite 3b", Live: "Starting the model")).Rows.Select(Plain).ToList();

        Assert.Equal("  live  Starting the model", rows[1]);
        Assert.Contains("Type now", rows[^1]);
    }

    [Fact]
    public async Task The_panel_shortens_the_model_name()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.TrueColor, unicode: true);
        var gateway = new FakeGateway { Info = _ => Task.FromResult(new GatewayInfo("Jazari", "Granite 4.0-H Tiny", "0.3.0")) };
        var session = new Session(gateway, new Ui(output, theme, 100), () => new MarkdownStream(output, theme, 100), @"C:\Repo");

        await session.ConnectAsync(CancellationToken.None);

        Assert.StartsWith("granite tiny", session.Panel().Status);
    }

    [Fact]
    public void The_short_device_name_drops_the_vendor()
    {
        Assert.Equal("iris xe", (Tuning.Default("f", 8, 16L << 30) with { Device = "Vulkan0", DeviceName = "Intel(R) Iris(R) Xe Graphics" }).ShortName());
        Assert.Equal("mx150", (Tuning.Default("f", 8, 16L << 30) with { Device = "Vulkan1", DeviceName = "NVIDIA GeForce MX150" }).ShortName());
        Assert.Equal("cpu 4 threads", Tuning.Default("f", 8, 16L << 30).ShortName());
    }

    [Theory]
    [InlineData("C:/x/granite-4.1-3b-Q4_K_M.gguf", "granite 3b")]
    [InlineData("C:/x/granite-4.0-h-tiny-Q4_K_M.gguf", "granite tiny")]
    [InlineData("custom.gguf", "the model")]
    [InlineData(null, "the model")]
    public void The_starting_model_comes_from_the_file_name(string? file, string expected) => Assert.Equal(expected, Session.ModelFromFile(file));

    [Fact]
    public void A_git_file_is_never_followed()
    {
        var dir = Directory.CreateTempSubdirectory("jazari worktree").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".git"), @"gitdir: \\attacker.example\share");

            Assert.Null(Welcome.Branch(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Branch_is_read_from_the_repository_head()
    {
        var dir = Directory.CreateTempSubdirectory("jazari branch").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/feat/x\n");

            Assert.Equal("feat/x", Welcome.Branch(Path.Combine(dir, "src")));

            File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "15af06e60c81\n");
            Assert.Null(Welcome.Branch(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
