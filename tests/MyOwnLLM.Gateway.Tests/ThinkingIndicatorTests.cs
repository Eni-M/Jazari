using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class ThinkingIndicatorTests
{
    [Fact]
    public async Task Shows_thinking_with_elapsed_seconds_then_clears()
    {
        var output = new StringWriter();
        var ticks = 0;
        var indicator = ThinkingIndicator.Start(output, new Theme(ColorMode.TrueColor, unicode: true),
            async (_, ct) => { if (++ticks > 2) await Task.Delay(Timeout.Infinite, ct); });

        for (var i = 0; i < 50 && ticks <= 2; i++) await Task.Delay(10);
        await indicator.StopAsync();

        var text = output.ToString();
        Assert.Contains("THINKING", text);
        Assert.Contains("1s", text);
        Assert.Contains("2s", text);
        Assert.EndsWith("\r\e[2K", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_progress_replaces_thinking_and_the_taskbar_is_cleared_after()
    {
        var output = new StringWriter();
        var indicator = ThinkingIndicator.Start(output, new Theme(ColorMode.TrueColor, unicode: true), (_, ct) => Task.Delay(Timeout.Infinite, ct));

        indicator.Report(new MyOwnLLM.Cli.ReadProgress(5000, 1000, 2500));
        await indicator.StopAsync();

        var text = output.ToString();
        Assert.Contains("READING 2,500 of 5,000 tokens · 50%", text);
        Assert.Contains("\e]9;4;1;50\a", text);
        Assert.EndsWith("\e]9;4;0;0\a", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_mode_writes_nothing()
    {
        var output = new StringWriter();

        var indicator = ThinkingIndicator.Start(output, new Theme(ColorMode.None, unicode: true), (_, ct) => Task.Delay(1, ct));
        await Task.Delay(50);
        await indicator.StopAsync();

        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Stop_is_safe_to_call_twice()
    {
        var indicator = ThinkingIndicator.Start(new StringWriter(), new Theme(ColorMode.TrueColor, true), (_, ct) => Task.Delay(Timeout.Infinite, ct));

        await indicator.StopAsync();
        await indicator.StopAsync();
    }
}
