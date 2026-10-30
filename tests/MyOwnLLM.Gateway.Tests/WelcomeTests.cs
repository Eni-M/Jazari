using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class WelcomeTests
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string[] Render(ConnectionState state, int width = 80, string? directory = null)
    {
        var output = new StringWriter();
        Welcome.Render(new Ui(output, new Theme(ColorMode.None, unicode: true), width), "0.2.0", "Granite 4.1 3B",
            directory ?? Path.Combine(Home, "EduCore"), state);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void Welcome_matches_spec_layout()
    {
        Assert.Equal(
        [
            "J A Z A R I  0.2.0",
            "",
            "Intelligence, precisely applied.",
            "",
            "Model       Granite 4.1 3B · local",
            "Directory   ~/EduCore",
            "Connection  Available",
            "Session     New session",
            "",
            "/help       Available commands",
            "/status     Where are we left at?",
            "/model      Show the model",
            "/clear      Clear conversation",
        ], Render(ConnectionState.Available));
    }

    [Theory]
    [InlineData(ConnectionState.GatewayNotRunning, "Connection  Gateway not running")]
    [InlineData(ConnectionState.KeyRejected, "Connection  Key rejected")]
    [InlineData(ConnectionState.NoKey, "Connection  Key not set")]
    public void Welcome_shows_connection_problems(ConnectionState state, string line) => Assert.Contains(line, Render(state));

    [Fact]
    public void Directory_outside_home_is_shown_as_is() => Assert.Contains(@"Directory   D:/work/api", Render(ConnectionState.Available, directory: @"D:\work\api"));

    [Fact]
    public void Welcome_fits_40_columns() =>
        Assert.All(Render(ConnectionState.GatewayNotRunning, width: 40, directory: Path.Combine(Home, "a-very-long-folder-name", "another-long-folder-name")),
            l => Assert.True(l.Length <= 40, $"too long: '{l}'"));

    [Fact]
    public void Available_has_no_setup_hint() => Assert.DoesNotContain(Render(ConnectionState.Available), l => l.StartsWith("To get started"));

    [Theory]
    [InlineData(ConnectionState.NoKey, "add-key", "JAZARI_KEY")]
    [InlineData(ConnectionState.GatewayNotRunning, "start-llama.ps1", "MyOwnLLM.Gateway")]
    [InlineData(ConnectionState.KeyRejected, "add-key", "JAZARI_KEY")]
    public void Problems_come_with_next_steps(ConnectionState state, string first, string second)
    {
        var text = string.Join("\n", Render(state));

        Assert.Contains(first, text);
        Assert.Contains(second, text);
    }
}
