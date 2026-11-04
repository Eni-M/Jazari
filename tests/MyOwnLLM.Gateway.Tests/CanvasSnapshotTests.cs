using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>
/// The five artboards of the Jazari CLI Interface canvas, drawn by the real screen at the canvas's size (1040×680 px of
/// 14/22 px mono is 124×31 cells). Every scenario checks that nothing wraps on its own; with JAZARI_UI_SNAPSHOTS set to a
/// folder, each screen is also written there as HTML, to compare side by side with the canvas.
/// </summary>
public class CanvasSnapshotTests
{
    static readonly Theme Color = new(ColorMode.TrueColor, unicode: true);

    sealed class StaticPanel(PromptBuffer buffer, PanelInfo info, string? question = null) : IPanel
    {
        public PanelFrame Render(int columns, int height) => PromptPanel.Draw(Color, buffer, info, columns, height, question);
    }

    static PromptBuffer Typed(string text)
    {
        var buffer = new PromptBuffer(() => Path.GetTempPath());
        buffer.Type(text);
        return buffer;
    }

    static (Screen Screen, FakeTerminal Terminal, Ui Ui) Start(IPanel panel)
    {
        var terminal = new FakeTerminal(124, 31);
        var screen = new Screen(terminal, Color, bottom: true, paced: false) { Panel = panel };
        screen.Start();
        return (screen, terminal, new Ui(screen, Color, () => 123));
    }

    static void Answer(Screen screen, string markdown, string time)
    {
        var stream = new MarkdownStream(screen, Color);
        stream.Write(markdown);
        stream.Complete();
        stream.StampTime(time);
    }

    static void Save(string name, FakeTerminal terminal)
    {
        Assert.Equal(0, terminal.Vt.SoftWraps);
        Assert.False(terminal.Vt.SyncError);
        if (Environment.GetEnvironmentVariable("JAZARI_UI_SNAPSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".html"), terminal.Vt.ToHtml());
        }
    }

    const string Status = "granite 3b · iris xe · 14 tok/s";

    [Fact]
    public void Conversation()
    {
        var (screen, terminal, ui) = Start(new StaticPanel(Typed("and how do lifetimes differ in tests"),
            new("Program.cs", "Enter sends · / commands", Status, (4100, 16384))));
        ui.Header("~/MyOwnLLM", "main");
        ui.Wordmark("0.3.0");
        ui.ReplaceWordmark("ready in 4 s");
        ui.Blank();
        ui.UserMessage("what is dependency injection?");
        ui.Blank();
        Answer(screen, "A class gets the things it needs handed to it instead of creating them. In ASP.NET Core you register services once, at startup, and the framework passes them into constructors, so each class can be tested with stand-ins.\n", "3.2s");
        ui.Blank();
        ui.UserMessage("register a repository per request");
        ui.Blank();
        Answer(screen, "Use `AddScoped`: one instance per HTTP request.\n```Program.cs\nbuilder.Services.AddScoped<IOrderRepository, OrderRepository>();\n```\nTransient makes a new one every time it's asked for; singleton shares one for the whole app.\n", "4.1s");

        Assert.Equal("  jazari — ~/MyOwnLLM", terminal.Vt.Text(0)[..21]);
        Assert.EndsWith("main", terminal.Vt.Text(0));
        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("          ╚█████╔╝"));
        Assert.Contains(terminal.Vt.Lines, l => l == "          ready in 4 s");
        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("    3.2s  A class gets"));
        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("  copy 1    Program.cs"));
        Save("Main", terminal);
    }

    [Fact]
    public void Commands()
    {
        var (screen, terminal, ui) = Start(new StaticPanel(Typed("/re"), new("Program.cs", "Enter sends · / commands", Status, (4100, 16384))));
        ui.Header("~/MyOwnLLM", "main");
        Answer(screen, "…so the queue keeps one request on the model at a time and the rest wait their turn.\n", "2.4s");

        Assert.Contains(terminal.Vt.Lines, l => l.Contains("/read <path>") && l.Contains("Read a file or folder"));
        Assert.Equal(Status, terminal.Vt.Text(29).Trim());
        Save("Commands", terminal);
    }

    [Fact]
    public void Files()
    {
        var (_, terminal, ui) = Start(new StaticPanel(Typed(""),
            new("Queue/ · 2 files", "", Status, (6300, 16384), Compact: "granite 3b · 14 tok/s · 6.3k of 16k"), "Apply this change to RequestQueue.cs?"));
        ui.Header("~/MyOwnLLM", "main");
        ui.UserMessage("/read src/MyOwnLLM.Gateway/Queue");
        ui.Blank();
        ui.Metadata("2 files, 2.1k tokens. They go with every question until /clear.", "read");
        ui.Blank();
        ui.UserMessage("/edit RequestQueue.cs cap the queue at 32 requests");
        ui.Blank();
        ui.Row("5.8s", "One change in RequestQueue.cs, line 18:");
        ui.DiffLine("18", "readonly Channel<Work> _work =", Tone.Muted, Surface.Code);
        ui.DiffLine("−", "    Channel.CreateUnbounded<Work>();", Tone.Removed, Surface.Removed);
        ui.DiffLine("+", "    Channel.CreateBounded<Work>(new BoundedChannelOptions(32)", Tone.Added, Surface.Added);
        ui.DiffLine("+", "        { FullMode = BoundedChannelFullMode.Wait });", Tone.Added, Surface.Added);

        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("    read  2 files, 2.1k tokens."));
        Assert.Contains(terminal.Vt.Lines, l => l.Contains("?  Apply this change to RequestQueue.cs?"));
        Save("Files", terminal);
    }

    [Fact]
    public void Problems()
    {
        var (screen, terminal, ui) = Start(new StaticPanel(Typed("/retry"), new(null, "/details shows the full error", "", Offline: true)));
        ui.Header("~/MyOwnLLM", "main");
        ui.Wordmark("0.3.0");
        ui.Blank();
        ui.Error("Can't reach the gateway.", "Nothing answered on 127.0.0.1:5080. Jazari tried to start it and it didn't come up in 2 min.", "jazari update", nextIsCommand: true);
        ui.Metadata("Repairs the install and starts both servers. Logs: ~/AppData/Local/MyOwnLLM/logs");
        ui.Blank();
        ui.UserMessage("summarise the review report");
        ui.Blank();
        Answer(screen, "Three findings are verified: the refund controller skips the owner check, the email sender logs\n", "9.0s");
        ui.Row("cut", "The model stopped mid-answer: it ran out of memory. /retry asks again, the part above is kept.", Tone.Secondary, Tone.Danger);

        Assert.Contains(terminal.Vt.Lines, l => l.Contains("not connected"));
        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("     fix    jazari update"));
        Save("Problems", terminal);
    }

    [Fact]
    public void Startup()
    {
        var (_, terminal, ui) = Start(new StaticPanel(Typed("explain the request queue in the gateway"),
            new(null, "Type now: it sends as soon as the model is ready", "starting granite 3b")));
        ui.Header("~/MyOwnLLM", "main");
        ui.Wordmark("0.3.0");
        ui.Blank();
        ui.TuneProgress(["cpu 6 threads", "flash attention", "iris xe", "pick fastest"], 2, "once, about 2 min left", redraw: false);

        Assert.Contains(terminal.Vt.Lines, l => l.StartsWith("    live  Tuning for this PC"));
        Assert.Contains(terminal.Vt.Lines, l => l.Contains("starting granite 3b"));
        Save("Startup", terminal);
    }
}
