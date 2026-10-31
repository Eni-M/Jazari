using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
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

/// <summary>The full editor: multi-line input, persistent history, Tab completion of / commands.</summary>
public sealed class PromptLineReader : ILineReader
{
    readonly Prompt _prompt;
    readonly Ui _ui;
    /// <summary>What the next question carries (files read, conversation), shown on the line above the prompt.</summary>
    public Func<string?>? Status { get; set; }

    public PromptLineReader(Ui ui)
    {
        _ui = ui;
        var theme = ui.Theme;
        var historyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "history");
        Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
        var keys = new KeyBindings(newLine: new KeyPressPattern[]
        {
            new(ConsoleModifiers.Shift, ConsoleKey.Enter), new(ConsoleModifiers.Alt, ConsoleKey.Enter), new(ConsoleModifiers.Control, ConsoleKey.J),
        });
        _prompt = new Prompt(historyFile, new Callbacks(), configuration: new PromptConfiguration(keys, prompt: $"{theme.Glyph('›')} "));
    }

    public async Task<(bool Ok, string? Text)> ReadAsync()
    {
        _ui.Blank();
        _ui.Divider(Status?.Invoke());
        var result = await _prompt.ReadLineAsync();
        if (result.IsSuccess && FitsOneRow(result.Text))
        {
            // Turn the line just typed into the highlighted "› question" row. Only for a single line that fits the
            // terminal, so we know it took exactly one row; anything else stays as typed (never shown twice either way).
            _ui.Output.Write("\e[1A\r\e[2K");
            _ui.UserMessage(result.Text);
        }
        return (result.IsSuccess, result.Text);
    }

    bool FitsOneRow(string text) =>
        text.Length > 0 && !text.Contains('\n') && text.Length + 2 < _ui.Width && text.All(c => c < 'ᄀ' && c != '\t');

    sealed class Callbacks : PromptCallbacks
    {
        static string Here => Directory.GetCurrentDirectory();

        protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken ct) =>
            Task.FromResult(Completions.For(text, caret, Here).Items.Count > 0);

        protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken ct)
        {
            var start = Completions.For(text, caret, Here).Start;
            return Task.FromResult(new TextSpan(start, caret - start));
        }

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompletionItem>>(Completions.For(text, caret, Here).Items.Select(i => new CompletionItem(i)).ToList());
    }
}

/// <summary>The interactive session: welcome once, then read → answer until /exit, end of input, or Ctrl+C twice.</summary>
public static class InputLoop
{
    public static async Task RunAsync(Session session, Ui ui, string version, string directory, ILineReader reader, bool hasKey = true,
        Task<bool>? updateAvailable = null, string? runningOn = null)
    {
        // Like any good CLI, open even when setup is incomplete, and say what's missing instead of exiting.
        var state = hasKey ? await session.ConnectAsync(CancellationToken.None) : ConnectionState.NoKey;
        Welcome.Render(ui, version, session.ModelName, directory, state, runningOn);
        if (reader is PromptLineReader prompt) prompt.Status = () => session.StatusLine;
        ui.Blank();

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
