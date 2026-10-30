using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class InputLoopTests
{
    [Fact]
    public async Task Plain_reader_runs_commands_until_end_of_input()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.None, unicode: true);
        var ui = new Ui(output, theme, 80);
        var session = new Session(new FakeGateway(), ui, () => new MarkdownStream(output, theme, 80), @"C:\Repo");

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", new TextLineReader(new StringReader("/model\nhi\n")));

        var text = output.ToString();
        Assert.Contains("J A Z A R I  0.2.0", text);
        Assert.Contains("Granite 4.1 3B", text);
        Assert.Contains("Hello", text);
    }

    [Fact]
    public async Task Plain_reader_stops_at_exit()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.None, unicode: true);
        var ui = new Ui(output, theme, 80);
        var gateway = new FakeGateway();
        var session = new Session(gateway, ui, () => new MarkdownStream(output, theme, 80), @"C:\Repo");

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", new TextLineReader(new StringReader("/exit\nnever asked\n")));

        Assert.Empty(gateway.Asked);
    }

    sealed class ScriptedReader(params (bool Ok, string? Text)[] script) : ILineReader
    {
        int _next;
        public Task<(bool Ok, string? Text)> ReadAsync() => Task.FromResult(_next < script.Length ? script[_next++] : (true, (string?)null));
        public int Reads => _next;
    }

    static (Session, Ui, StringWriter, FakeGateway) NewSession()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.None, unicode: true);
        var ui = new Ui(output, theme, 80);
        var gateway = new FakeGateway();
        return (new Session(gateway, ui, () => new MarkdownStream(output, theme, 80), @"C:\Repo"), ui, output, gateway);
    }

    [Fact]
    public async Task Ctrl_C_that_clears_typed_text_does_not_count_toward_exit()
    {
        var (session, ui, _, gateway) = NewSession();
        var reader = new ScriptedReader((false, "half typed"), (false, "more text"), (true, "still here"));

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", reader);

        Assert.Equal("still here", Assert.Single(gateway.Asked).Question);
    }

    [Fact]
    public async Task Ctrl_C_twice_on_an_empty_prompt_exits()
    {
        var (session, ui, _, gateway) = NewSession();
        var reader = new ScriptedReader((false, ""), (false, ""), (true, "never asked"));

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", reader);

        Assert.Empty(gateway.Asked);
    }

    [Fact]
    public async Task Unexpected_error_prints_error_block_and_keeps_the_session()
    {
        var (session, ui, output, gateway) = NewSession();
        gateway.Chat = (_, _, _) => throw new InvalidOperationException("boom");
        var reader = new ScriptedReader((true, "first"), (true, "/model"));

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", reader);

        Assert.Contains("ERROR", output.ToString());
        Assert.Contains("Granite 4.1 3B", output.ToString());
    }

    [Fact]
    public async Task Connect_cancelled_reports_gateway_not_running()
    {
        var (session, _, _, gateway) = NewSession();
        gateway.Info = ct => throw new OperationCanceledException(ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(ConnectionState.GatewayNotRunning, await session.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task Without_a_key_the_session_still_opens()
    {
        var (session, ui, output, gateway) = NewSession();
        var infoCalls = 0;
        gateway.Info = _ => { infoCalls++; return Task.FromResult(new GatewayInfo("Jazari", "Granite 4.1 3B", "0.2.0")); };

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", new ScriptedReader((true, "/help")), hasKey: false);

        Assert.Contains("Key not set", output.ToString());
        Assert.Contains("/details", output.ToString());
        Assert.Equal(0, infoCalls);
    }

    [Fact]
    public async Task Interactive_session_does_not_repeat_the_question()
    {
        var output = new StringWriter();
        var theme = new Theme(ColorMode.None, unicode: true);
        var ui = new Ui(output, theme, 80);
        var session = new Session(new FakeGateway(), ui, () => new MarkdownStream(output, theme, 80), @"C:\Repo") { EchoQuestions = false };

        await InputLoop.RunAsync(session, ui, "0.2.0", @"C:\Repo", new ScriptedReader((true, "unique question text")));

        Assert.DoesNotContain("unique question text", output.ToString());
        Assert.Contains("Hello", output.ToString());
    }
}
