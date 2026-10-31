using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>The opening header: the wordmark, four rows and a hint, then it scrolls away with the conversation.</summary>
public static class Welcome
{
    /// <param name="runningOn">Where this PC's tuning runs the model ("Intel(R) Iris(R) Xe Graphics · flash attention · 14.9 tok/s"), when known.</param>
    public static void Render(Ui ui, string version, string model, string directory, ConnectionState state, string? runningOn = null)
    {
        var dot = ui.Theme.Glyph('·');
        ui.Wordmark(version);
        ui.Blank();
        List<(string, string)> rows = [("Model", $"{model} {dot} local")];
        if (runningOn is { Length: > 0 }) rows.Add(("Running on", runningOn));
        rows.Add(("Directory", ShortPath(directory)));
        rows.Add(("Connection", state switch
        {
            ConnectionState.Available => "Available",
            ConnectionState.KeyRejected => "Key rejected",
            ConnectionState.NoKey => "Key not set",
            _ => "Gateway not running",
        }));
        ui.Rows(rows);
        ui.Blank();
        ui.Line($"/help for commands {dot} Tab completes {dot} Ctrl+C stops an answer", Tone.Muted);

        if (state == ConnectionState.Available) return;
        ui.Blank();
        ui.Label("To get started");
        foreach (var step in NextSteps(state)) ui.Line(step, Tone.Muted);
    }

    static string[] NextSteps(ConnectionState state) => state switch
    {
        ConnectionState.GatewayNotRunning =>
        [
            "Jazari starts its servers itself; if it can't, run: jazari update",
            @"To start them by hand in your MyOwnLLM folder: .\scripts\start-llama.ps1, and in a second window dotnet run --project src/MyOwnLLM.Gateway",
            "Then type /model here to check the connection.",
        ],
        ConnectionState.KeyRejected =>
        [
            "The gateway doesn't know this key.",
            "In your MyOwnLLM folder: dotnet run --project src/MyOwnLLM.Gateway -- add-key",
            "then setx JAZARI_KEY <the new key> and open a new terminal.",
        ],
        _ =>
        [
            "Run jazari update: it repairs everything, then Jazari creates the key itself.",
            "By hand: dotnet run --project src/MyOwnLLM.Gateway -- add-key, then setx JAZARI_KEY <the key>.",
        ],
    };

    static string ShortPath(string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = directory.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + directory[home.Length..] : directory;
        return path.Replace('\\', '/');
    }
}
