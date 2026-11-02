using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>A clock tests move by hand.</summary>
public sealed class TestClock : TimeProvider
{
    long _ticks;
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

public class ThinkingIndicatorTests
{
    static readonly Theme Color = new(ColorMode.TrueColor, unicode: true);

    static (Screen Screen, FakeTerminal Terminal) NewScreen()
    {
        var terminal = new FakeTerminal(100, 20);
        var screen = new Screen(terminal, Color, bottom: true, paced: false);
        screen.Start();
        return (screen, terminal);
    }

    [Fact]
    public async Task Counts_the_seconds_in_the_gutter_row()
    {
        var (screen, terminal) = NewScreen();
        var clock = new TestClock();
        var thinking = ThinkingIndicator.Start(screen, Color, clock);

        Assert.Equal("    live  Thinking", terminal.Vt.Text(0));
        clock.Advance(TimeSpan.FromSeconds(12));
        screen.RenderNow();
        Assert.Equal("    live  Thinking · 12s", terminal.Vt.Text(0));

        await thinking.StopAsync();
        Assert.Equal("", terminal.Vt.Text(0));
    }

    [Fact]
    public async Task Reading_progress_replaces_the_seconds_and_shows_on_the_tab()
    {
        var (screen, terminal) = NewScreen();
        var clock = new TestClock();
        var thinking = ThinkingIndicator.Start(screen, Color, clock);

        thinking.Report(new ReadProgress(5000, 0, 1000));
        clock.Advance(TimeSpan.FromSeconds(4));
        thinking.Report(new ReadProgress(5000, 0, 3000));

        Assert.Equal("    live  Reading 3,000 of 5,000 tokens · 60% · under a minute left", terminal.Vt.Text(0));
        Assert.Contains("9;4;1;60", terminal.Vt.Signals);
        await thinking.StopAsync();
        Assert.Contains("9;4;0;0", terminal.Vt.Signals);
    }

    [Fact]
    public async Task Stopping_twice_is_harmless()
    {
        var (screen, _) = NewScreen();
        var thinking = ThinkingIndicator.Start(screen, Color, new TestClock());

        await thinking.StopAsync();
        await thinking.StopAsync();

        Assert.Empty(screen.Blocks);
    }

    [Fact]
    public async Task Plain_output_shows_nothing()
    {
        var output = new StringWriter();
        var ui = new Ui(output, new Theme(ColorMode.None, unicode: true), 80);

        var thinking = ui.Thinking();
        thinking.Report(new ReadProgress(100, 0, 50));
        await thinking.StopAsync();

        Assert.Equal("", output.ToString());
    }
}
