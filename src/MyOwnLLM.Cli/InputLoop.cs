using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>Where input lines come from. <c>Ok == false</c> means the user cancelled (Ctrl+C); <c>null</c> text means end of input.</summary>
public interface ILineReader
{
    Task<(bool Ok, string? Text)> ReadAsync();
}

/// <summary>Plain line reading for pipes and terminals without a real console (no editing, history or completion).</summary>
public sealed class TextLineReader(TextReader input) : ILineReader
{
    public async Task<(bool Ok, string? Text)> ReadAsync() => (true, await input.ReadLineAsync());
}

/// <summary>The interactive session: welcome once, then read → answer until /exit, end of input, or Ctrl+C twice.</summary>
public static class InputLoop
{
    /// <summary>
    /// Plain input: opens once setup has been looked at. A real console (<see cref="PromptEditor"/>) goes through
    /// <see cref="RunInteractiveAsync"/> instead, which opens at once and lets the user type while the servers start.
    /// </summary>
    public static async Task RunAsync(Session session, Ui ui, string version, string directory, ILineReader reader, bool hasKey = true,
        Task<bool>? updateAvailable = null, string? runningOn = null)
    {
        // Like any good CLI, open even when setup is incomplete, and say what's missing instead of exiting.
        var state = hasKey ? await session.ConnectAsync(CancellationToken.None) : ConnectionState.NoKey;
        Welcome.Render(ui, version, session.ModelName, directory, state, runningOn);
        ui.Blank();
        await LoopAsync(session, ui, reader, updateAvailable, Task.CompletedTask);
    }

    /// <param name="start">Starts the servers; whatever it reported is printed once it's done. Returns whether there's a key.</param>
    /// <param name="measured">Whether this PC's best settings were measured.</param>
    public static async Task RunInteractiveAsync(Session session, Ui ui, string version, string directory, PromptEditor editor,
        Func<Task<(bool HasKey, string Report)>> start, Task<bool>? updateAvailable, bool measured)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        editor.Panel = session.Panel;
        Welcome.Opening(ui, version, directory);
        var wordmarkRow = Console.CursorTop - Console.WindowTop - 1;

        async Task OpenAsync()
        {
            ConnectionState state;
            var report = "";
            try
            {
                (var hasKey, report) = await start();
                state = hasKey ? await session.ConnectAsync(CancellationToken.None) : ConnectionState.NoKey;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                session.Unexpected(e);
                state = ConnectionState.GatewayNotRunning;
            }
            session.StartingModel = null;
            editor.Post(() =>
            {
                ui.Output.Write(report);
                Welcome.Opened(ui, state, clock.Elapsed, wordmarkRow, measured);
            });
        }

        await LoopAsync(session, ui, editor, updateAvailable, OpenAsync());
    }

    /// <param name="opening">Finishes when the servers are up; a question typed sooner waits for it.</param>
    static async Task LoopAsync(Session session, Ui ui, ILineReader reader, Task<bool>? updateAvailable, Task opening)
    {

        // While an answer streams nobody is reading keys, so Ctrl+C arrives as a console signal: stop the answer.
        CancellationTokenSource? generating = null;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (generating is not { } current) return;
            e.Cancel = true;
            try { current.Cancel(); }
            catch (ObjectDisposedException) { } // the answer finished at that very moment
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            // Ctrl+C that clears typed text is just "clear". Exit needs two Ctrl+C in a row on an empty prompt, close together.
            var lastEmptyCancel = (DateTime?)null;
            var noticeShown = false;
            while (!session.ExitRequested)
            {
                // Shown once, when the background check has finished; an unfinished check never delays the prompt.
                if (!noticeShown && updateAvailable is { IsCompletedSuccessfully: true, Result: true })
                {
                    ui.Metadata("update available, run jazari update");
                    noticeShown = true;
                }
                var (ok, text) = await reader.ReadAsync();
                if (!ok)
                {
                    if (!string.IsNullOrEmpty(text)) { lastEmptyCancel = null; continue; }
                    if (lastEmptyCancel is { } first && DateTime.UtcNow - first < TimeSpan.FromSeconds(3)) break;
                    lastEmptyCancel = DateTime.UtcNow;
                    ui.Metadata("Press Ctrl+C again to exit.");
                    continue;
                }
                if (text is null) break;
                lastEmptyCancel = null;
                await opening;

                generating = new CancellationTokenSource();
                try { await session.HandleAsync(text, generating.Token); }
                catch (Exception e) when (e is not OperationCanceledException) { session.Unexpected(e); }
                finally
                {
                    var finished = generating;
                    generating = null; // first, so a Ctrl+C from now on finds nothing to cancel
                    finished.Dispose();
                }
            }
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
