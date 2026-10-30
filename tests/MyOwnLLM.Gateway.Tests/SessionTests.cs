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

    public IAsyncEnumerable<SseChunk> ChatAsync(IReadOnlyList<ChatTurn> history, string question, string project, CancellationToken ct)
    {
        Asked.Add((history.ToList(), question));
        return Chat(history, question, ct);
    }

    public Task<(string Id, string Summary)> StatusAsync(string project, CancellationToken ct) => Task.FromResult(("ix-9", "We are building the CLI."));
    public Task<bool> FeedbackAsync(string id, bool accepted, CancellationToken ct) => Task.FromResult(true);
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
        Assert.Contains("THINKING", text);
        Assert.True(text.IndexOf("THINKING", StringComparison.Ordinal) < text.LastIndexOf("\r\e[2K", StringComparison.Ordinal), "indicator must be cleared after it shows");
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
