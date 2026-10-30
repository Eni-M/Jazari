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
                _ => "Gateway not running",
            }),
            ("Session", "New session"),
        ]);
        ui.Blank();
        ui.Rows([("/help", "Available commands"), ("/status", "Where are we left at?"), ("/model", "Show the model"), ("/clear", "Clear conversation")]);
    }

    static string ShortPath(string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = directory.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + directory[home.Length..] : directory;
        return path.Replace('\\', '/');
    }
}
