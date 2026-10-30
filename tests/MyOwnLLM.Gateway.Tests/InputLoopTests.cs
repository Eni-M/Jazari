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
}
