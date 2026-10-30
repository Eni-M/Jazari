using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>The opening screen: printed once, then it scrolls away with the conversation.</summary>
public static class Welcome
{
    public const string Tagline = "Intelligence, precisely applied.";

    public static void Render(Ui ui, string version, string model, string directory, ConnectionState state)
    {
        ui.Wordmark(version);
        ui.Blank();
        ui.Line(Tagline, Tone.Secondary);
        ui.Blank();
        ui.Rows(
        [
            ("Model", $"{model} {ui.Theme.Glyph('·')} local"),
            ("Directory", ShortPath(directory)),
            ("Connection", state switch
            {
                ConnectionState.Available => "Available",
                ConnectionState.KeyRejected => "Key rejected",
                ConnectionState.NoKey => "Key not set",
                _ => "Gateway not running",
            }),
            ("Session", "New session"),
        ]);
        ui.Blank();
        ui.Rows([("/help", "Available commands"), ("/status", "Where are we left at?"), ("/model", "Show the model"), ("/clear", "Clear conversation")]);

        if (state == ConnectionState.Available) return;
        ui.Blank();
        ui.Line("To get started", Tone.Secondary);
        foreach (var step in NextSteps(state)) ui.Line(step, Tone.Muted);
    }

    static string[] NextSteps(ConnectionState state) => state switch
    {
        ConnectionState.GatewayNotRunning =>
        [
            @"Jazari starts its servers itself once .\scripts\install-jazari.ps1 has run in your MyOwnLLM folder.",
            @"To start them by hand there: .\scripts\start-llama.ps1, and in a second window dotnet run --project src/MyOwnLLM.Gateway",
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
            @"In your MyOwnLLM folder run .\scripts\install-jazari.ps1: it sets everything up, including the key.",
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
