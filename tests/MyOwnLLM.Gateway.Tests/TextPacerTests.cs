using System.Text;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class TextPacerTests
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task Everything_comes_out_in_order()
    {
        var shown = new StringBuilder();
        var pacer = new TextPacer(t => shown.Append(t), tick: Tick, holdChars: 3, hold: Tick);

        pacer.Add("Hel");
        pacer.Add("lo the");
        pacer.Add("re\nnext");
        await pacer.DrainAsync();

        Assert.Equal("Hello there\nnext", shown.ToString());
    }

    [Fact]
    public async Task Text_is_held_back_at_first_so_it_can_flow()
    {
        var shown = new StringBuilder();
        var pacer = new TextPacer(t => { lock (shown) shown.Append(t); }, tick: Tick, holdChars: 100, hold: TimeSpan.FromMinutes(1));

        pacer.Add("short");
        await Task.Delay(100);

        lock (shown) Assert.Equal("", shown.ToString());
        await pacer.DrainAsync(); // finishing doesn't wait out the hold
        Assert.Equal("short", shown.ToString());
    }

    [Fact]
    public async Task Long_text_comes_out_in_small_steps()
    {
        var pieces = new List<string>();
        var pacer = new TextPacer(pieces.Add, tick: Tick, holdChars: 1, hold: Tick);

        pacer.Add(new string('x', 200));
        await pacer.DrainAsync();

        Assert.True(pieces.Count > 5, $"came out in {pieces.Count} pieces");
        Assert.Equal(200, pieces.Sum(p => p.Length));
    }

    [Fact]
    public async Task An_emoji_is_never_split()
    {
        var pieces = new List<string>();
        var pacer = new TextPacer(pieces.Add, tick: Tick, holdChars: 1, hold: Tick);

        pacer.Add(string.Concat(Enumerable.Repeat("😀", 40)));
        await pacer.DrainAsync();

        Assert.All(pieces, p => Assert.False(char.IsHighSurrogate(p[^1]), "a piece ended halfway through a character"));
    }

    [Fact]
    public async Task Pieces_end_at_a_space_when_one_is_near()
    {
        var pieces = new List<string>();
        var pacer = new TextPacer(pieces.Add, tick: Tick, holdChars: 1, hold: Tick);

        pacer.Add(string.Concat(Enumerable.Repeat("word ", 60)));
        await pacer.DrainAsync();

        Assert.All(pieces, p => Assert.EndsWith(" ", p));
    }

    [Fact]
    public async Task The_first_words_wait_only_a_moment_by_default()
    {
        var shown = new StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = -1L;
        var pacer = new TextPacer(t => { lock (shown) { if (first < 0) first = clock.ElapsedMilliseconds; shown.Append(t); } }, tick: Tick);

        pacer.Add("Hi");
        // Polled rather than a fixed wait: a busy CI runner can delay the pacer's timer, but not by more than a second.
        for (var i = 0; i < 500 && Shown() != "Hi"; i++) await Task.Delay(10);

        Assert.Equal("Hi", Shown());
        Assert.True(first < 1400, $"the first words waited {first} ms; the old hold was 1.5 s");
        await pacer.DrainAsync();

        string Shown() { lock (shown) return shown.ToString(); }
    }

    [Fact]
    public async Task Discarding_drops_what_wasnt_shown_and_says_how_much()
    {
        var shown = new StringBuilder();
        var pacer = new TextPacer(t => { lock (shown) shown.Append(t); }, tick: Tick, holdChars: 100, hold: TimeSpan.FromMinutes(1));

        pacer.Add("not shown yet");

        Assert.Equal(13, await pacer.DiscardAsync());
        Assert.Equal("", shown.ToString());
    }

    [Fact]
    public async Task Draining_with_nothing_added_finishes()
    {
        var pacer = new TextPacer(_ => throw new InvalidOperationException("nothing to show"), tick: Tick);

        await pacer.DrainAsync();
    }

    [Fact]
    public async Task A_failure_showing_text_reaches_the_caller()
    {
        var pacer = new TextPacer(_ => throw new InvalidOperationException("boom"), tick: Tick, holdChars: 1, hold: Tick);

        pacer.Add("x");

        await Assert.ThrowsAsync<InvalidOperationException>(pacer.DrainAsync);
    }
}
