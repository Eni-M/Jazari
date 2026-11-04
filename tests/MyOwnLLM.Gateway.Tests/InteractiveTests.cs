using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>The prompt driven through a fake terminal: keys in, the screen out.</summary>
public class InteractiveTests : IDisposable
{
    static readonly Theme Color = new(ColorMode.TrueColor, unicode: true);
    readonly string _history = Path.Combine(Path.GetTempPath(), $"jazari-history-{Guid.NewGuid():N}");
    readonly FakeTerminal _terminal = new(80, 16);
    readonly Screen _screen;
    readonly PromptEditor _editor;

    public InteractiveTests()
    {
        _screen = new Screen(_terminal, Color, bottom: true, paced: false);
        _editor = new PromptEditor(new Ui(_screen, Color, () => 79), _screen, _terminal, () => Path.GetTempPath(), _history)
        {
            Panel = () => new PanelInfo(null, "Enter sends · / commands", "granite tiny · 10 tok/s"),
        };
    }

    public void Dispose()
    {
        _editor.StopAsync().GetAwaiter().GetResult();
        File.Delete(_history);
    }

    static async Task<T> Within<T>(Task<T> task) =>
        await Task.WhenAny(task, Task.Delay(5000)) == task ? await task : throw new TimeoutException("the editor didn't answer");

    async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Typing_and_enter_sends_the_question_and_shows_it_as_a_turn()
    {
        _editor.Start();
        _terminal.Type("hello there\n");

        var (ok, text) = await Within(_editor.ReadAsync());

        Assert.True(ok);
        Assert.Equal("hello there", text);
        await Until(() => _terminal.Vt.Lines.Any(l => l == "       ›  hello there"));
        Assert.Contains(_terminal.Vt.Lines, l => l.Contains("granite tiny"));
        Assert.Equal(0, _terminal.Vt.SoftWraps);
    }

    [Fact]
    public async Task A_line_break_inside_a_paste_is_a_new_line_not_a_send()
    {
        _terminal.Type("first line\nsecond line");
        _editor.Start();
        await Until(() => _terminal.Vt.Lines.Any(l => l.Contains("second line")));

        Assert.Contains(_terminal.Vt.Lines, l => l.Contains("first line"));
        _terminal.Type("\n");
        var (_, text) = await Within(_editor.ReadAsync());
        Assert.Equal("first line\nsecond line", text);
    }

    [Fact]
    public void The_history_file_is_cut_back_once_it_doubles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jazari-history-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(0, 1001).Select(i => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"q{i}"))));

            _ = new PromptEditor(new Ui(_screen, Color, () => 79), _screen, _terminal, () => Path.GetTempPath(), path);

            var kept = File.ReadAllLines(path);
            Assert.Equal(500, kept.Length);
            Assert.Equal("q1000", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(kept[^1])));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_question_asks_on_the_panel_and_takes_y()
    {
        _editor.Start();
        var asked = Task.Run(() => _editor.Ask("Apply this change to Program.cs?"));
        await Until(() => _terminal.Vt.Lines.Any(l => l.Contains("Apply this change to Program.cs?")));

        _terminal.Type("y");

        Assert.True(await Within(asked));
        await Until(() => !_terminal.Vt.Lines.Any(l => l.Contains("Apply this change")));
    }
}
