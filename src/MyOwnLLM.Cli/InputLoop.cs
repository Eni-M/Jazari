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
    static readonly string[] Commands = ["/help", "/status", "/model", "/clear", "/retry", "/details", "/exit"];
    readonly Prompt _prompt;

    public PromptLineReader(Theme theme)
    {
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
        var result = await _prompt.ReadLineAsync();
        return (result.IsSuccess, result.Text);
    }

    sealed class Callbacks : PromptCallbacks
    {
        protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken ct) =>
            Task.FromResult(text.StartsWith('/') && !text.Contains(' '));

        protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken ct) =>
            Task.FromResult(text.StartsWith('/') ? new TextSpan(0, caret) : new TextSpan(caret, 0));

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompletionItem>>(text.StartsWith('/')
                ? Commands.Where(c => c.StartsWith(text[..caret], StringComparison.OrdinalIgnoreCase)).Select(c => new CompletionItem(c)).ToList()
                : []);
    }
}

/// <summary>The interactive session: welcome once, then read → answer until /exit, end of input, or Ctrl+C twice.</summary>
public static class InputLoop
{
    public static async Task RunAsync(Session session, Ui ui, string version, string directory, ILineReader reader)
    {
        var state = await session.ConnectAsync(CancellationToken.None);
        Welcome.Render(ui, version, session.ModelName, directory, state);
        ui.Blank();

        // While an answer streams nobody is reading keys, so Ctrl+C arrives as a console signal: stop the answer.
        CancellationTokenSource? generating = null;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (generating is not { } current) return;
            e.Cancel = true;
            current.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var cancelledOnce = false;
            while (!session.ExitRequested)
            {
                var (ok, text) = await reader.ReadAsync();
                if (!ok)
                {
                    if (cancelledOnce) break;
                    cancelledOnce = true;
                    ui.Metadata("Press Ctrl+C again to exit.");
                    continue;
                }
                if (text is null) break;
                cancelledOnce = false;

                generating = new CancellationTokenSource();
                try { await session.HandleAsync(text, generating.Token); }
                finally
                {
                    generating.Dispose();
                    generating = null;
                }
            }
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
