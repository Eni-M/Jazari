using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class ScreenTests
{
    static readonly Theme Color = new(ColorMode.TrueColor, unicode: true);

    sealed class TestPanel : IPanel
    {
        public int Extra { get; set; }
        public PanelFrame Render(int columns, int height)
        {
            var rows = Enumerable.Range(0, Extra).Select(i => new Line([new($"menu {i}")], Surface.Panel))
                .Concat([new Line([new("edge")], Surface.Panel), new Line([new("> typed")], Surface.Panel), new Line([new("footer")], Surface.Panel), new Line([], Surface.Panel)]).ToList();
            return new(rows, 4, (Extra + 1, 7));
        }
    }

    static (Screen Screen, FakeTerminal Terminal, TestPanel Panel) Start(int width = 60, int height = 12)
    {
        var terminal = new FakeTerminal(width, height);
        var panel = new TestPanel();
        var screen = new Screen(terminal, Color, bottom: true, paced: false) { Panel = panel };
        screen.Start();
        return (screen, terminal, panel);
    }

    static void Say(Screen screen, string text) => screen.Append(new LinesBlock(_ => [Layout.Row().Add(text).Build()]));

    /// <summary>Scrollback and conversation rows on screen, without the panel and the blank gap above it.</summary>
    static List<string> Conversation(FakeTerminal terminal, int panelRows = 4) =>
        terminal.Vt.Scrollback.Concat(terminal.Vt.Lines.Take(terminal.Vt.Height - panelRows)).Select(r => r.Trim()).Where(r => r.Length > 0).ToList();

    [Fact]
    public void The_panel_sits_at_the_bottom_and_the_conversation_starts_at_the_top()
    {
        var (screen, terminal, _) = Start();

        Say(screen, "hello");

        Assert.Equal("          hello", terminal.Vt.Text(0));
        Assert.Equal("edge", terminal.Vt.Text(8));
        Assert.Equal("> typed", terminal.Vt.Text(9));
        Assert.Equal((9, 7), (terminal.Vt.Row, terminal.Vt.Column));
        Assert.True(terminal.Vt.CursorVisible);
    }

    [Fact]
    public void A_long_conversation_scrolls_into_the_scrollback_exactly_once_and_in_order()
    {
        var (screen, terminal, _) = Start();

        for (var i = 1; i <= 30; i++) Say(screen, $"row {i}");

        Assert.Equal(Enumerable.Range(1, 30).Select(i => $"row {i}"), Conversation(terminal));
        Assert.Equal("row 30", terminal.Vt.Text(7).Trim()); // the last row sits right above the panel
        Assert.Equal(0, terminal.Vt.SoftWraps);
        Assert.False(terminal.Vt.SyncError);
        Assert.Equal(0, terminal.Vt.SyncDepth);
    }

    [Fact]
    public void Every_frame_is_one_write_and_an_idle_frame_writes_nothing()
    {
        var (screen, terminal, _) = Start();
        Say(screen, "hello");
        var writes = terminal.Writes;

        screen.RenderNow();
        screen.RenderNow();

        Assert.Equal(writes, terminal.Writes);
        Assert.All(terminal.Frames.Skip(1), f => Assert.StartsWith("\e[?2026h", f));
    }

    [Fact]
    public void Opening_the_menu_covers_the_conversation_and_closing_it_brings_it_back_without_scrolling()
    {
        var (screen, terminal, panel) = Start();
        for (var i = 1; i <= 8; i++) Say(screen, $"row {i}");
        var scrollback = terminal.Vt.Scrollback.ToList();

        panel.Extra = 5;
        screen.RenderNow();
        Assert.Equal("menu 0", terminal.Vt.Text(3));
        panel.Extra = 0;
        screen.RenderNow();

        Assert.Equal(scrollback, terminal.Vt.Scrollback);
        Assert.Equal("row 8", terminal.Vt.Text(7).Trim());
    }

    [Fact]
    public void A_streaming_answer_scrolls_without_losing_or_repeating_words()
    {
        var (screen, terminal, _) = Start(width: 40, height: 10);
        var answer = new AnswerBlock(Color);
        screen.Append(answer);
        var words = Enumerable.Range(1, 120).Select(i => $"w{i}").ToList();

        foreach (var word in words)
        {
            answer.Feed(word + (word.EndsWith('0') ? "\n" : " "));
            screen.Changed();
        }
        answer.Complete();
        screen.Changed();

        var shown = string.Join(" ", Conversation(terminal)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(words, shown);
        Assert.Equal(0, terminal.Vt.SoftWraps);
    }

    [Fact]
    public void Resizing_lays_everything_out_again_and_nothing_wraps()
    {
        var (screen, terminal, _) = Start(width: 80, height: 14);
        var answer = new AnswerBlock(Color);
        screen.Append(answer);
        answer.Feed(string.Join(" ", Enumerable.Range(1, 60).Select(i => $"word{i}")) + "\ntheend");
        screen.Changed();

        foreach (var (width, height) in new[] { (50, 14), (30, 10), (80, 20), (24, 6) })
        {
            terminal.Resize(width, height);
            screen.RenderNow();
            Assert.Equal(0, terminal.Vt.SoftWraps);
            Assert.Equal("edge", terminal.Vt.Text(height - 4));
            Assert.Equal(1, terminal.Vt.Lines.Count(l => l.Contains("theend"))); // the end of the answer, exactly once
        }
    }

    [Fact]
    public void The_time_goes_in_the_first_rows_gutter_while_it_is_on_screen()
    {
        var (screen, terminal, _) = Start();
        var stream = new MarkdownStream(screen, Color);

        stream.Write("A class gets what it needs handed to it.\n");
        stream.Complete();
        stream.StampTime("3.2s");

        Assert.Equal("    3.2s  A class gets what it needs handed to it.", terminal.Vt.Text(0));
    }

    [Fact]
    public void The_time_goes_on_a_row_of_its_own_when_the_first_row_has_scrolled_away()
    {
        var (screen, terminal, _) = Start(height: 8);
        var stream = new MarkdownStream(screen, Color);

        stream.Write(string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n")));
        stream.Complete();
        stream.StampTime("3.2s");

        Assert.Equal("    3.2s", terminal.Vt.Text(3));
        Assert.DoesNotContain(terminal.Vt.Scrollback, r => r.Contains("3.2s"));
    }

    [Fact]
    public async Task Removing_a_block_takes_its_rows_away_and_two_blank_rows_show_as_one()
    {
        var (screen, terminal, _) = Start();
        Say(screen, "question");
        screen.Append(new BlankBlock());
        var thinking = ThinkingIndicator.Start(screen, Color, new TestClock());
        screen.Append(new BlankBlock());
        Assert.Contains("Thinking", terminal.Vt.Text(2));

        await thinking.StopAsync();
        Say(screen, "answer");

        Assert.Equal(["          question", "", "          answer"], terminal.Vt.Lines.Take(3));
    }

    [Fact]
    public async Task Stopping_takes_the_panel_away_and_leaves_the_cursor_under_the_conversation()
    {
        var (screen, terminal, _) = Start();
        Say(screen, "hello");

        await screen.StopAsync();

        Assert.Equal("", terminal.Vt.Text(8));
        Assert.Equal(1, terminal.Vt.Row);
        Assert.True(terminal.Vt.CursorVisible);
    }

    [Fact]
    public void Ctrl_l_redraws_a_damaged_window()
    {
        var (screen, terminal, _) = Start();
        Say(screen, "hello");
        terminal.Vt.Write("\e[1;1Hgarbage written by something else");

        screen.Redraw();

        Assert.Equal("          hello", terminal.Vt.Text(0));
    }

    [Fact]
    public async Task Dragging_the_window_narrower_never_makes_the_terminal_wrap_a_row()
    {
        var terminal = new FakeTerminal(110, 20);
        var screen = new Screen(terminal, Color, bottom: true, paced: true) { Panel = new TestPanel() };
        screen.Start();
        var ui = new Ui(screen, Color, () => 109);
        ui.Header("~/MyOwnLLM", "main");
        for (var i = 0; i < 6; i++) ui.Row("why", string.Join(" ", Enumerable.Repeat($"words{i}", 30)));
        await Task.Delay(150);

        // One column at a time, the way a mouse drag arrives; the terminal reflows each step before Jazari can redraw.
        for (var width = 110; width >= 50; width--)
        {
            terminal.Resize(width, 20);
            await Task.Delay(15);
        }
        await Task.Delay(700); // settled: laid out at the full width once more

        Assert.Equal(0, terminal.Vt.Reflowed);
        Assert.Equal(0, terminal.Vt.SoftWraps);
        Assert.Equal("edge", terminal.Vt.Text(16));
        Assert.True(terminal.Vt.Lines.Max(l => l.Length) > 50 - 8, "after the drag the rows use the full width again");
        await screen.StopAsync();
    }
}
