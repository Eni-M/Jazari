using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class DeepCommandTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari-deep").FullName;
    readonly FakeGateway _gateway = new() { Info = _ => Task.FromResult(new GatewayInfo("Jazari", "m", "0", DeepAvailable: true)) };
    readonly StringWriter _output = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Session NewSession(string? conversations = null)
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        return new Session(_gateway, new Ui(_output, theme, 200), () => new MarkdownStream(_output, theme, 200), _dir) { ConversationDir = conversations };
    }

    static async IAsyncEnumerable<SseChunk> Say(params SseChunk[] chunks)
    {
        foreach (var chunk in chunks) { await Task.Yield(); yield return chunk; }
    }

    [Fact]
    public async Task Deep_with_a_question_sends_deep_and_the_next_question_does_not()
    {
        var session = NewSession();
        await session.HandleAsync("/deep why does this deadlock", CancellationToken.None);
        Assert.True(_gateway.LastDeep);
        Assert.Equal("why does this deadlock", _gateway.Asked[^1].Question);

        await session.HandleAsync("and now", CancellationToken.None);
        Assert.False(_gateway.LastDeep);
    }

    [Fact]
    public async Task Deep_alone_asks_the_last_question_again_without_the_answer_it_replaces()
    {
        var session = NewSession();
        await session.HandleAsync("first", CancellationToken.None);
        await session.HandleAsync("the question", CancellationToken.None);

        await session.HandleAsync("/deep", CancellationToken.None);

        Assert.True(_gateway.LastDeep);
        Assert.Equal("the question", _gateway.Asked[^1].Question);
        Assert.Equal(["first", _gateway.Asked[^1].History[1].Content], _gateway.Asked[^1].History.Select(t => t.Content));
        Assert.Equal(6, session.History.Count); // both answers stay, for /save and the screen
    }

    [Fact]
    public async Task Deep_alone_with_nothing_to_retry_says_so()
    {
        await NewSession().HandleAsync("/deep", CancellationToken.None);
        Assert.Empty(_gateway.Asked);
        Assert.Contains("Nothing to ask again", _output.ToString());
    }

    [Fact]
    public async Task Deep_alone_after_a_restored_conversation_uses_its_last_question()
    {
        var saved = Path.Combine(_dir, "saved");
        var first = NewSession(saved);
        await first.HandleAsync("remembered question", CancellationToken.None);

        var next = NewSession(saved);
        next.Resume();
        await next.HandleAsync("/deep", CancellationToken.None);

        Assert.Equal("remembered question", _gateway.Asked[^1].Question);
        Assert.Empty(_gateway.Asked[^1].History);
    }

    [Fact]
    public async Task Deep_alone_after_a_failed_question_asks_that_question_with_the_history_whole()
    {
        var session = NewSession();
        await session.HandleAsync("worked", CancellationToken.None);
        _gateway.Chat = (_, _, _) => Say(new SseChunk(null, null, "the model server failed"));
        await session.HandleAsync("failed one", CancellationToken.None);
        _gateway.Chat = (_, _, _) => FakeGateway.Chunks("fine");

        await session.HandleAsync("/deep", CancellationToken.None);

        Assert.Equal("failed one", _gateway.Asked[^1].Question);
        Assert.Equal(2, _gateway.Asked[^1].History.Count);
    }

    [Fact]
    public async Task The_check_row_ends_the_answer_and_stages_never_reach_the_text()
    {
        _gateway.Chat = (_, _, _) => Say(new SseChunk(null, null, null, Stage: "thinking", StageTokens: 40), new SseChunk("the answer", null, null), new SseChunk(null, "ix-1", null, Check: "checked: code compiles"));
        var session = NewSession();

        await session.HandleAsync("/deep q", CancellationToken.None);

        var text = _output.ToString();
        Assert.True(text.IndexOf("checked: code compiles", StringComparison.Ordinal) > text.IndexOf("the answer", StringComparison.Ordinal));
        Assert.DoesNotContain("thinking", text);
    }

    [Fact]
    public async Task A_fast_answer_with_bad_code_names_the_block_and_offers_deep()
    {
        _gateway.Chat = (_, _, _) => Say(new SseChunk("code", null, null), new SseChunk(null, "ix-1", null, Checks: [new BlockNote(2, "doesnotcompile", ["no AddFoo"])]));
        var session = NewSession();
        await session.ConnectAsync(CancellationToken.None);

        await session.HandleAsync("q", CancellationToken.None);

        Assert.Contains("block 2 doesn't compile: no AddFoo", _output.ToString());
        Assert.Contains("Ctrl+D for /deep", _output.ToString());
        Assert.True(session.OffersDeep);
    }

    [Fact]
    public async Task Without_a_smart_model_the_note_does_not_offer_deep()
    {
        _gateway.Info = _ => Task.FromResult(new GatewayInfo("Jazari", "m", "0"));
        _gateway.Chat = (_, _, _) => Say(new SseChunk(null, "ix-1", null, Checks: [new BlockNote(1, "doesnotcompile", ["no AddFoo"])]));
        var session = NewSession();
        await session.ConnectAsync(CancellationToken.None);

        await session.HandleAsync("q", CancellationToken.None);

        Assert.DoesNotContain("Ctrl+D", _output.ToString());
        Assert.False(session.OffersDeep);
    }

    [Fact]
    public async Task A_fast_answer_with_good_code_shows_no_row_and_the_offer_ends_with_the_next_question()
    {
        var bad = true;
        _gateway.Chat = (_, _, _) => Say(new SseChunk(null, "ix-1", null, Checks: bad ? [new BlockNote(1, "doesnotcompile", ["no"])] : [new BlockNote(1, "compiles", [])]));
        var session = NewSession();
        await session.ConnectAsync(CancellationToken.None);
        await session.HandleAsync("q", CancellationToken.None);
        Assert.True(session.OffersDeep);

        bad = false;
        _output.GetStringBuilder().Clear();
        await session.HandleAsync("q2", CancellationToken.None);

        Assert.False(session.OffersDeep);
        Assert.DoesNotContain("compile", _output.ToString());
    }

    [Fact]
    public async Task An_older_gateway_with_no_deep_says_to_update()
    {
        _gateway.Info = _ => Task.FromResult(new GatewayInfo("Jazari", "m", "0"));
        await NewSession().HandleAsync("/deep q", CancellationToken.None);
        Assert.Empty(_gateway.Asked);
        Assert.Contains("jazari update", _output.ToString());
    }

    static ConsoleKeyInfo CtrlD => new('\u0004', ConsoleKey.D, false, false, true);
    static ConsoleKeyInfo PlainD => new('D', ConsoleKey.D, true, false, false);

    [Fact]
    public void Ctrl_D_on_an_empty_prompt_after_the_hint_sends_deep()
    {
        var buffer = new PromptBuffer(() => ".") { OffersDeep = () => true };
        Assert.Equal(KeyResult.Submit, buffer.Apply(CtrlD));
        Assert.Equal("/deep", buffer.Take());
    }

    [Fact]
    public void Ctrl_D_does_nothing_without_the_hint_or_with_text_typed()
    {
        var quiet = new PromptBuffer(() => ".") { OffersDeep = () => false };
        Assert.Equal(KeyResult.Editing, quiet.Apply(CtrlD));
        Assert.Equal("", quiet.Text);

        var typed = new PromptBuffer(() => ".") { OffersDeep = () => true };
        typed.Type("Does");
        Assert.Equal(KeyResult.Editing, typed.Apply(CtrlD));
        Assert.Equal("Does", typed.Text);
    }

    [Fact]
    public void The_letter_D_always_types_a_D()
    {
        var buffer = new PromptBuffer(() => ".") { OffersDeep = () => true };
        Assert.Equal(KeyResult.Editing, buffer.Apply(PlainD));
        Assert.Equal("D", buffer.Text);
    }

    [Fact]
    public async Task Ask_deep_parses_and_json_carries_the_check_row()
    {
        var args = CliArgs.Parse(["ask", "why", "--deep", "--json"]);
        Assert.True(args.Deep);
        Assert.True(args.Json);
        Assert.False(CliArgs.Parse(["ask", "why"]).Deep);

        _gateway.Chat = (_, _, _) => Say(new SseChunk("a", "ix-1", null, Check: "checked: code compiles"));
        var json = await NewSession().AnswerAsJsonAsync("why", CancellationToken.None, deep: true);
        Assert.True(_gateway.LastDeep);
        Assert.Contains("\"check\":\"checked: code compiles\"", json);
    }

    [Fact]
    public void Stages_parse_and_the_indicator_shows_them()
    {
        var stage = SseChunk.Parse("""data: {"stage":"thinking","tokens":120}""")!;
        Assert.Equal("thinking", stage.Stage);
        Assert.Equal(120, stage.StageTokens);
        var counted = SseChunk.Parse("""data: {"stage":"reading","tokens":null}""")!; // what the gateway sends for a stage without a count
        Assert.Equal("reading", counted.Stage);
        Assert.Null(counted.StageTokens);
        var final =SseChunk.Parse("""data: {"id":"ix-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"check":"checked: code compiles","checks":[{"block":1,"state":"compiles","errors":[]}]}""")!;
        Assert.Equal("checked: code compiles", final.Check);
        Assert.Equal("compiles", final.Checks![0].State);

        var block = new ThinkingBlock(new Theme(ColorMode.None, unicode: true), TimeProvider.System);
        block.Stage("thinking", 120);
        var text = string.Concat(block.Render(80).SelectMany(l => l.Spans).Select(s => s.Text));
        Assert.Contains("Thinking", text);
        Assert.Contains("120 tokens", text);
    }
}
