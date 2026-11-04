using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class WelcomeTests
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    const string IrisXe = "Intel(R) Iris(R) Xe Graphics · flash attention · 14.9 tok/s";

    static string[] Render(ConnectionState state, int width = 80, string? directory = null, string? runningOn = null)
    {
        var output = new StringWriter();
        Welcome.Render(new Ui(output, new Theme(ColorMode.None, unicode: true), width), "0.2.0", "Granite 4.1 3B",
            directory ?? Path.Combine(Home, "EduCore"), state, runningOn);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void Welcome_is_a_compact_header()
    {
        Assert.Equal(
        [
            "J A Z A R I  0.2.0",
            "",
            "Model       Granite 4.1 3B · local",
            "Running on  " + IrisXe,
            "Directory   ~/EduCore",
            "Connection  Available",
            "",
            "/help for commands · Tab completes · Ctrl+C stops an answer",
        ], Render(ConnectionState.Available, runningOn: IrisXe));
    }

    [Fact]
    public void Running_on_is_left_out_before_the_pc_was_measured() =>
        Assert.DoesNotContain(Render(ConnectionState.Available), l => l.StartsWith("Running on"));

    [Theory]
    [InlineData(ConnectionState.GatewayNotRunning, "Connection  Gateway not running")]
    [InlineData(ConnectionState.KeyRejected, "Connection  Key rejected")]
    [InlineData(ConnectionState.NoKey, "Connection  Key not set")]
    public void Welcome_shows_connection_problems(ConnectionState state, string line) => Assert.Contains(line, Render(state));

    [Fact]
    public void Directory_outside_home_is_shown_as_is() =>
        Assert.Contains("Directory   D:/work/api", Render(ConnectionState.Available, directory: @"D:\work\api"));

    [Fact]
    public void A_folder_whose_name_starts_like_home_is_not_home() =>
        Assert.Contains("Directory   " + (Home + "2\\api").Replace('\\', '/'), Render(ConnectionState.Available, directory: Home + "2\\api"));

    [Fact]
    public void Welcome_fits_40_columns() =>
        Assert.All(Render(ConnectionState.GatewayNotRunning, width: 40, directory: Path.Combine(Home, "a-very-long-folder-name", "another-long-folder-name"), runningOn: IrisXe),
            l => Assert.True(l.Length <= 40, $"too long: '{l}'"));

    [Fact]
    public void Available_has_no_setup_hint() => Assert.DoesNotContain(Render(ConnectionState.Available), l => l.StartsWith("TO GET STARTED"));

    [Theory]
    [InlineData(ConnectionState.NoKey, "jazari update", "creates the key")]
    [InlineData(ConnectionState.GatewayNotRunning, "jazari update", "start-llama.ps1")]
    [InlineData(ConnectionState.KeyRejected, "jazari update", "new key")]
    public void Problems_come_with_next_steps(ConnectionState state, string first, string second)
    {
        var text = string.Join("\n", Render(state));
        if (state != ConnectionState.GatewayNotRunning) Assert.DoesNotContain("dotnet run", text); // installed users may have no clone

        Assert.Contains("TO GET STARTED", text);
        Assert.Contains(first, text);
        Assert.Contains(second, text);
    }
}
