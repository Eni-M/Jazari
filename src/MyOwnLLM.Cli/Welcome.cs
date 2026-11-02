using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>The opening. Piped output gets the wordmark, four rows and a hint. A real terminal gets one row, and the rest lives on the prompt's panel.</summary>
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

    /// <summary>The first rows of an interactive session, printed before the servers are up.</summary>
    public static void Opening(Ui ui, string version, string directory)
    {
        ui.Header(ShortPath(directory), Branch(directory));
        ui.Wordmark(version);
    }

    /// <summary>The servers have answered or given up. One row says so: "ready in 4 s" replaces the version, even with a question typed meanwhile below it.</summary>
    public static void Opened(Ui ui, ConnectionState state, TimeSpan took, int wordmarkRow, bool measured)
    {
        if (state == ConnectionState.Available)
        {
            ui.ReplaceWordmark(wordmarkRow, $"ready in {Math.Max(took.TotalSeconds, 1):0} s");
            if (measured) return;
            ui.Metadata("On safe CPU settings. jazari tune measures this PC for the fastest ones.");
            ui.Blank();
            return;
        }
        var (message, cause, fix, then) = Problem(state);
        ui.Error(message, cause, fix);
        ui.Metadata(then);
        ui.Blank();
    }

    static (string Message, string Cause, string Fix, string Then) Problem(ConnectionState state) => state switch
    {
        ConnectionState.GatewayNotRunning =>
            ("Can't reach the gateway.", "Nothing answered, and Jazari couldn't start its servers.", "jazari update",
                $"Repairs the install and starts both servers. Logs: {ShortPath(ServerLauncher.DefaultLogDir)}"),
        ConnectionState.KeyRejected =>
            ("The gateway doesn't know this key.", "Its key file may have been reset.", "dotnet run --project src/MyOwnLLM.Gateway -- add-key",
                "In your MyOwnLLM folder. Then setx JAZARI_KEY <the new key> and open a new terminal."),
        _ =>
            ("There's no key for the gateway yet.", "Jazari couldn't create one.", "jazari update",
                "It repairs everything, then Jazari creates the key itself."),
    };

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

    /// <summary>
    /// The checked-out branch, read from the repository's HEAD file; null outside a repository, on a detached HEAD, or in
    /// a linked worktree. A worktree's .git is a file naming another folder, and following it isn't safe: in a cloned
    /// repository it could name a network share, and Windows would send the user's credentials there just from opening Jazari.
    /// </summary>
    public static string? Branch(string directory)
    {
        try
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (File.Exists(git)) return null;
                if (!Directory.Exists(git)) continue;
                var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head["ref: refs/heads/".Length..] : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    static string ShortPath(string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = directory.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + directory[home.Length..] : directory;
        return path.Replace('\\', '/');
    }
}
