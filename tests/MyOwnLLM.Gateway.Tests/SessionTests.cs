using System.Runtime.CompilerServices;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

sealed class FakeGateway : IGatewayClient
{
    public Func<CancellationToken, Task<GatewayInfo>> Info { get; set; } = _ => Task.FromResult(new GatewayInfo("Jazari", "Granite 4.1 3B", "0.2.0"));
    public Func<IReadOnlyList<ChatTurn>, string, CancellationToken, IAsyncEnumerable<SseChunk>> Chat { get; set; } = (_, _, _) => Chunks("Hel", "lo");
    public List<(IReadOnlyList<ChatTurn> History, string Question)> Asked { get; } = [];

    public static async IAsyncEnumerable<SseChunk> Chunks(params string[] parts)
    {
        foreach (var p in parts) { await Task.Yield(); yield return new SseChunk(p, null, null); }
        yield return new SseChunk(null, "ix-3", null);
    }

    public Task<GatewayInfo> InfoAsync(CancellationToken ct) => Info(ct);

    public IAsyncEnumerable<SseChunk> ChatAsync(IReadOnlyList<ChatTurn> history, string question, string project, CancellationToken ct, string? context = null, string? conventions = null)
    {
        Asked.Add((history.ToList(), question));
        LastContext = context;
        LastConventions = conventions;
        return Chat(history, question, ct);
    }

    public string? LastContext { get; private set; }
    public string? LastConventions { get; private set; }
    public Func<string, string, Task<string>> Complete { get; set; } = (_, _) => Task.FromResult("""{"findings":[]}""");
    public int Completions { get; private set; }
    public Task<string> CompleteAsync(string question, string context, string jsonSchema, int maxTokens, CancellationToken ct, string? conventions = null)
    {
        Completions++;
        LastConventions = conventions;
        return Complete(question, context);
    }

    public Task<(string Id, string Summary)> StatusAsync(string project, CancellationToken ct) => Task.FromResult(("ix-9", "We are building the CLI."));
    public Task<bool> FeedbackAsync(string id, bool accepted, CancellationToken ct) => Task.FromResult(true);

    public List<string?> Warmed { get; } = [];
    public Task WarmAsync(IReadOnlyList<ChatTurn> history, string project, string? context, CancellationToken ct, string? conventions = null)
    {
        Warmed.Add(context);
        return Task.CompletedTask;
    }

    public (bool Available, IReadOnlyList<DocPassage> Passages) Docs { get; set; } = (true, []);
    public Task<(bool Available, IReadOnlyList<DocPassage> Passages)> DocsAsync(string question, CancellationToken ct) => Task.FromResult(Docs);
}

public class SessionTests
{
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();
    readonly Session _session;

    public SessionTests()
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        _session = new Session(_gateway, new Ui(_output, theme, 80), () => new MarkdownStream(_output, theme, 80), @"C:\Repo");
    }

    string Text => _output.ToString();

    [Fact]
    public async Task Question_streams_and_records_history()
    {
        await _session.HandleAsync("hi there", CancellationToken.None);

        Assert.Contains("› hi there", Text);
        Assert.DoesNotContain(Ui.Name, Text);
        Assert.Contains("Hello", Text);
        Assert.Contains("ix-3", Text);
        Assert.Equal([new ChatTurn("user", "hi there"), new ChatTurn("assistant", "Hello")], _session.History);
    }

    static async IAsyncEnumerable<SseChunk> Dropping(int turns)
    {
        await Task.Yield();
        yield return new SseChunk("ok", null, null);
        yield return new SseChunk(null, "ix-3", null, DroppedTurns: turns);
    }

    const string NoLongerFit = "no longer fit in the model's memory";

    [Fact]
    public async Task A_cut_is_mentioned_once_and_again_when_more_goes()
    {
        var counts = new Queue<int>([4, 4, 8]);
        _gateway.Chat = (_, _, _) => Dropping(counts.Dequeue());

        await _session.HandleAsync("one", CancellationToken.None);
        var first = Text;
        await _session.HandleAsync("two", CancellationToken.None);
        var second = Text[first.Length..];
        await _session.HandleAsync("three", CancellationToken.None);
        var third = Text[(first.Length + second.Length)..];

        Assert.Contains("The oldest 4 questions no longer fit in the model's memory. Jazari can still look up what you said in them.", first);
        Assert.DoesNotContain(NoLongerFit, second);
        Assert.Contains("The oldest 8 questions " + NoLongerFit, third);
    }

    static async IAsyncEnumerable<SseChunk> Citing(params DocSource[] sources)
    {
        await Task.Yield();
        yield return new SseChunk("Use Results.NotFound().", null, null);
        yield return new SseChunk(null, "ix-3", null, Sources: sources.Length == 0 ? null : sources);
    }

    static readonly DocSource Responses = new("Minimal API responses: IResult", "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses#iresult");

    /// <summary>A session in a real terminal, and its output without colour codes.</summary>
    (Session Session, Func<string> Text) Live()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.TrueColor, unicode: true);
        var session = new Session(_gateway, new Ui(output, theme, 120), () => new MarkdownStream(output, theme, 120), @"C:\Repo");
        return (session, () => System.Text.RegularExpressions.Regex.Replace(output.ToString(), @"\e\[[0-9;?]*[A-Za-z]|\e\][^\a]*\a", ""));
    }

    [Fact]
    public async Task A_source_row_follows_the_answer()
    {
        var (session, text) = Live();
        _gateway.Chat = (_, _, _) => Citing(Responses);

        await session.HandleAsync("not found from a minimal API?", CancellationToken.None);

        // A long address wraps under the text, as every row does. The time goes back up into the answer's gutter: \r parts lines.
        var lines = text().Split('\n', '\r');
        var row = Array.FindIndex(lines, l => l.TrimStart().StartsWith("source  "));
        Assert.Matches(@"^\s*source  Minimal API responses: IResult ·", lines[row]);
        Assert.Contains(Responses.Url, string.Concat(lines[row..].Take(3).Select(l => l.Trim())));
        Assert.True(text().IndexOf("Results.NotFound()") < text().IndexOf("source  "));
    }

    [Fact]
    public async Task Piped_output_names_the_source_too()
    {
        _gateway.Chat = (_, _, _) => Citing(Responses);
        await _session.HandleAsync("not found from a minimal API?", CancellationToken.None);
        Assert.Contains($"{Responses.Title} · {Responses.Url}", Text);
    }

    [Fact]
    public async Task No_source_row_without_passages()
    {
        var (session, text) = Live();
        _gateway.Chat = (_, _, _) => Citing();
        await session.HandleAsync("hello", CancellationToken.None);
        Assert.DoesNotContain("source", text());
        Assert.DoesNotContain("learn.microsoft.com", text());
    }

    [Fact]
    public async Task Two_passages_from_one_page_show_once()
    {
        var (session, text) = Live();
        _gateway.Chat = (_, _, _) => Citing(Responses, Responses with { Title = "Minimal API responses: TypedResults", Url = "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses#typedresults" });
        await session.HandleAsync("not found from a minimal API?", CancellationToken.None);
        Assert.Single(text().Split('\n'), l => l.Contains("minimal-apis/responses"));
    }

    [Fact]
    public async Task Passages_from_two_pages_show_both()
    {
        _gateway.Chat = (_, _, _) => Citing(Responses, new DocSource("Records", "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/records"));
        await _session.HandleAsync("q", CancellationToken.None);
        Assert.Contains(Responses.Url, Text);
        Assert.Contains("types/records", Text);
    }

    [Fact]
    public async Task The_source_is_not_part_of_the_answer()
    {
        _gateway.Chat = (_, _, _) => Citing(Responses);
        await _session.HandleAsync("not found from a minimal API?", CancellationToken.None);
        Assert.Equal("Use Results.NotFound().", _session.History[^1].Content);
    }

    [Fact]
    public async Task Clear_starts_counting_again()
    {
        _gateway.Chat = (_, _, _) => Dropping(4);

        await _session.HandleAsync("one", CancellationToken.None);
        await _session.HandleAsync("/clear", CancellationToken.None);
        await _session.HandleAsync("two", CancellationToken.None);

        Assert.Equal(2, Text.Split(NoLongerFit).Length - 1);
    }

    [Fact]
    public async Task Copy_puts_the_exact_code_on_the_clipboard()
    {
        string? copied = null;
        var theme = new Theme(ColorMode.None, unicode: true);
        var session = new Session(_gateway, new Ui(_output, theme, 80), () => new MarkdownStream(_output, theme, 80), @"C:\Repo") { SetClipboard = t => copied = t };
        _gateway.Chat = (_, _, _) => FakeGateway.Chunks("One:\n```csharp\nint a;\n  int b;\n```\nTwo:\n```\nx\n```\n");

        await session.HandleAsync("code please", CancellationToken.None);
        Assert.Contains("2 code blocks", Text);

        await session.HandleAsync("/copy 1", CancellationToken.None);
        Assert.Equal("int a;\n  int b;", copied);

        await session.HandleAsync("/copy", CancellationToken.None);
        Assert.Equal("x", copied); // no number: the last block

        await session.HandleAsync("/copy 9", CancellationToken.None);
        Assert.Contains("no code block 9", Text);
    }

    [Fact]
    public void Copied_code_has_no_hidden_characters() =>
        Assert.Equal(["int a;\nint b;"], CodeBlocks.Extract("```\r\nint​ a;\u001b\r\n‮int b;\r\n```\r\n"));

    [Fact]
    public async Task Copy_with_nothing_to_copy_says_so()
    {
        await _session.HandleAsync("/copy", CancellationToken.None);

        Assert.Contains("Nothing to copy", Text);
    }

    [Fact]
    public async Task History_is_sent_with_next_question()
    {
        await _session.HandleAsync("first", CancellationToken.None);
        await _session.HandleAsync("second", CancellationToken.None);

        Assert.Equal([new ChatTurn("user", "first"), new ChatTurn("assistant", "Hello")], _gateway.Asked[1].History);
        Assert.Equal("second", _gateway.Asked[1].Question);
    }

    [Fact]
    public async Task Clear_forgets_history()
    {
        await _session.HandleAsync("first", CancellationToken.None);

        await _session.HandleAsync("/clear", CancellationToken.None);

        Assert.Empty(_session.History);
    }

    [Fact]
    public async Task Retry_resends_last_question()
    {
        await _session.HandleAsync("again?", CancellationToken.None);

        await _session.HandleAsync("/retry", CancellationToken.None);

        Assert.Equal(["again?", "again?"], _gateway.Asked.Select(a => a.Question));
    }

    [Fact]
    public async Task Unknown_command_hints_help()
    {
        await _session.HandleAsync("/dance", CancellationToken.None);

        Assert.Contains("Unknown command /dance. Try /help.", Text);
    }

    [Theory]
    [InlineData("/reveiw", "/review")]
    [InlineData("/STAUTS", "/status")]
    [InlineData("/cler", "/clear")]
    public async Task A_misspelled_command_suggests_the_right_one(string typed, string meant)
    {
        await _session.HandleAsync(typed, CancellationToken.None);

        Assert.Contains($"Unknown command {typed}. Did you mean {meant}?", Text);
    }

    [Fact]
    public async Task Help_lists_commands_and_newline_keys()
    {
        await _session.HandleAsync("/help", CancellationToken.None);

        foreach (var c in new[] { "/status", "/model", "/clear", "/retry", "/details", "/exit", "Alt+Enter" }) Assert.Contains(c, Text);
    }

    [Fact]
    public async Task Gateway_down_prints_error_block_not_exception()
    {
        _gateway.Chat = (_, _, _) => Throwing(new GatewayException("gateway not reachable at http://127.0.0.1:5080/", "connection refused", null));

        await _session.HandleAsync("hello?", CancellationToken.None);
        await _session.HandleAsync("/details", CancellationToken.None);

        Assert.Contains("ERROR", Text);
        Assert.Contains("gateway not reachable at http://127.0.0.1:5080/", Text);
        Assert.Contains("connection refused", Text);
    }

    [Fact]
    public async Task Error_chunk_prints_error_block()
    {
        _gateway.Chat = (_, _, _) => Single(new SseChunk(null, null, "lost connection to the model server"));

        await _session.HandleAsync("hello?", CancellationToken.None);

        Assert.Contains("ERROR", Text);
        Assert.Contains("lost connection to the model server", Text);
    }

    [Theory]
    [InlineData(null, false, ConnectionState.Available)]
    [InlineData(401, true, ConnectionState.KeyRejected)]
    [InlineData(null, true, ConnectionState.GatewayNotRunning)]
    public async Task Connect_reports_states(int? status, bool fail, ConnectionState expected)
    {
        if (fail) _gateway.Info = _ => throw new GatewayException("x", null, status);

        Assert.Equal(expected, await _session.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_mid_stream_keeps_partial_answer()
    {
        using var cts = new CancellationTokenSource();
        _gateway.Chat = (_, _, ct) => PartThenWait(cts, ct);

        await _session.HandleAsync("long answer please", cts.Token);

        Assert.Contains("part", Text);
        Assert.Contains("stopped", Text);
        Assert.Equal(new ChatTurn("assistant", "part"), _session.History[^1]);
    }

    [Fact]
    public async Task Status_prints_summary()
    {
        await _session.HandleAsync("/status", CancellationToken.None);

        Assert.Contains("We are building the CLI.", Text);
    }

    [Fact]
    public async Task Model_prints_name_and_model()
    {
        await _session.HandleAsync("/model", CancellationToken.None);

        Assert.Contains("Granite 4.1 3B", Text);
    }

    [Fact]
    public async Task LastFailed_tracks_the_latest_request()
    {
        _gateway.Chat = (_, _, _) => Throwing(new GatewayException("down", null, null));
        await _session.HandleAsync("one", CancellationToken.None);
        Assert.True(_session.LastFailed);

        _gateway.Chat = (_, _, _) => FakeGateway.Chunks("ok");
        await _session.HandleAsync("two", CancellationToken.None);
        Assert.False(_session.LastFailed);
    }

    [Fact]
    public async Task Byte_order_mark_from_piped_input_is_ignored()
    {
        await _session.HandleAsync("﻿hello", CancellationToken.None);
        await _session.HandleAsync("﻿/exit", CancellationToken.None);

        Assert.Equal("hello", _gateway.Asked.Single().Question);
        Assert.True(_session.ExitRequested);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("a question")]
    public async Task Thinking_shows_while_waiting_in_color_mode(string input)
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.TrueColor, unicode: true);
        var session = new Session(_gateway, new Ui(output, theme, 80), () => new MarkdownStream(output, theme, 80), @"C:\Repo");

        await session.HandleAsync(input, CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("Thinking", text);
        var vt = new VtScreen(81, 100);
        vt.Write(text);
        Assert.DoesNotContain(vt.Everything, row => row.Contains("Thinking")); // shown while waiting, gone once the answer is in
        Assert.Equal(0, vt.SoftWraps);
    }

    [Fact]
    public async Task Exit_sets_flag()
    {
        await _session.HandleAsync("/exit", CancellationToken.None);

        Assert.True(_session.ExitRequested);
    }

    static async IAsyncEnumerable<SseChunk> Throwing(Exception e)
    {
        await Task.Yield();
        throw e;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    static async IAsyncEnumerable<SseChunk> Single(SseChunk chunk)
    {
        await Task.Yield();
        yield return chunk;
    }

    static async IAsyncEnumerable<SseChunk> PartThenWait(CancellationTokenSource cts, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new SseChunk("part", null, null);
        cts.Cancel();
        await Task.Delay(Timeout.Infinite, ct);
        yield return new SseChunk("never", null, null);
    }
}
