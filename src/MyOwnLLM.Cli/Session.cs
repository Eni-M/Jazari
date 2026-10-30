using System.Diagnostics;
using System.Text;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

public enum ConnectionState { Available, GatewayNotRunning, KeyRejected, NoKey }

/// <summary>One Jazari conversation: questions, slash commands, history and stopping an answer.</summary>
public sealed class Session(IGatewayClient gateway, Ui ui, Func<MarkdownStream> newRenderer, string project)
{
    static readonly (string Command, string What)[] Commands =
    [
        ("/help", "Available commands"), ("/status", "Where are we left at?"), ("/model", "Show the model"),
        ("/copy [n]", "Copy code block n of the last answer (no number: the last block)"),
        ("/clear", "Clear conversation"), ("/retry", "Ask the last question again"), ("/details", "Details of the last error"), ("/exit", "Quit"),
    ];

    readonly List<ChatTurn> _history = [];
    string? _lastQuestion;
    Exception? _lastError;
    GatewayInfo? _info;

    public IReadOnlyList<ChatTurn> History => _history;
    public bool ExitRequested { get; private set; }
    /// <summary>
    /// Print the question as a highlighted "› question" row. Off in the interactive session, where the input line
    /// itself becomes that row, so the question never appears twice.
    /// </summary>
    public bool EchoQuestions { get; init; } = true;
    /// <summary>Whether the most recent question or /status ended in an error (one-off commands exit 1).</summary>
    public bool LastFailed { get; private set; }
    /// <summary>Where /copy sends code. Replaced in tests.</summary>
    public Action<string> SetClipboard { get; init; } = TextCopy.ClipboardService.SetText;
    bool Live => ui.Theme.Mode != ColorMode.None;

    public async Task<ConnectionState> ConnectAsync(CancellationToken ct)
    {
        try
        {
            _info = await gateway.InfoAsync(ct);
            return ConnectionState.Available;
        }
        catch (GatewayException e)
        {
            _lastError = e;
            return e.Status == 401 ? ConnectionState.KeyRejected : ConnectionState.GatewayNotRunning;
        }
        catch (OperationCanceledException) { return ConnectionState.GatewayNotRunning; }
    }

    public string ModelName => _info?.Model ?? "unknown";

    public Task HandleAsync(string input, CancellationToken ct)
    {
        input = input.Trim().TrimStart('﻿'); // PowerShell pipes start with a byte-order mark
        if (input.Length == 0) return Task.CompletedTask;
        if (!input.StartsWith('/')) return AskAsync(input, ct);

        switch (input.Split(' ', 2)[0].ToLowerInvariant())
        {
            case "/help": Help(); break;
            case "/status": return StatusAsync(ct);
            case "/model": return ModelAsync(ct);
            case "/copy": Copy(input.Split(' ', 2) is [_, var number] ? number.Trim() : null); break;
            case "/clear": _history.Clear(); ui.Metadata("Conversation cleared."); break;
            case "/retry": return _lastQuestion is null ? Note("Nothing to retry.") : AskAsync(_lastQuestion, ct);
            case "/details": Details(); break;
            case "/exit": ExitRequested = true; break;
            default: ui.Metadata($"Unknown command {input.Split(' ', 2)[0]}. Try /help."); break;
        }
        return Task.CompletedTask;
    }

    public async Task AskAsync(string question, CancellationToken ct)
    {
        _lastQuestion = question;
        LastFailed = false;
        if (EchoQuestions) ui.UserMessage(question);
        ui.Blank();

        var thinking = ThinkingIndicator.Start(ui.Output, ui.Theme);
        void StopThinking() => thinking.StopAsync().GetAwaiter().GetResult();

        var renderer = newRenderer();
        var answer = new StringBuilder();
        string? id = null;
        var watch = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in gateway.ChatAsync(_history, question, project, ct).WithCancellation(ct))
            {
                if (chunk.Error is not null)
                {
                    StopThinking();
                    renderer.Complete();
                    Fail(new GatewayException(chunk.Error, null, null));
                    return;
                }
                if (chunk.Content is { } text)
                {
                    StopThinking();
                    answer.Append(text);
                    renderer.Write(text);
                }
                id = chunk.Id ?? id;
            }
            StopThinking();
            renderer.Complete();
            Remember(question, answer.ToString());
            var blocks = CodeBlocks.Extract(answer.ToString()).Count;
            var copyHint = blocks == 0 ? "" : $" {ui.Theme.Glyph('·')} {blocks} code block{(blocks == 1 ? "" : "s")}, /copy{(blocks == 1 ? "" : " 1")}";
            ui.Metadata($"{id} {ui.Theme.Glyph('·')} {watch.Elapsed.TotalSeconds:0.0} s{copyHint}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StopThinking();
            renderer.Complete();
            if (answer.Length > 0) Remember(question, answer.ToString());
            ui.Metadata("stopped");
        }
        catch (GatewayException e)
        {
            StopThinking();
            renderer.Complete();
            Fail(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StopThinking();
            renderer.Complete();
            Unexpected(e);
        }
        finally { ui.Blank(); }
    }

    async Task StatusAsync(CancellationToken ct)
    {
        LastFailed = false;
        var thinking = ThinkingIndicator.Start(ui.Output, ui.Theme);
        try
        {
            var (id, summary) = await gateway.StatusAsync(project, ct);
            await thinking.StopAsync();
            var renderer = newRenderer();
            renderer.Write(summary + "\n");
            renderer.Complete();
            ui.Metadata(id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await thinking.StopAsync(); ui.Metadata("stopped"); }
        catch (GatewayException e) { await thinking.StopAsync(); Fail(e); }
    }

    async Task ModelAsync(CancellationToken ct)
    {
        if (_info is null && await ConnectAsync(ct) != ConnectionState.Available) { Details(); return; }
        ui.Rows([("Model", $"{_info!.Model} {ui.Theme.Glyph('·')} local"), ("Gateway", $"{_info.Name} {_info.Version}")]);
    }

    void Copy(string? number)
    {
        var blocks = _history.LastOrDefault(t => t.Role == "assistant") is { } last ? CodeBlocks.Extract(last.Content) : [];
        if (blocks.Count == 0) { ui.Metadata("Nothing to copy: the last answer has no code block."); return; }
        var n = number is null ? blocks.Count : int.TryParse(number, out var parsed) ? parsed : 0;
        if (n < 1 || n > blocks.Count) { ui.Metadata($"There is no code block {number}. The last answer has {blocks.Count}."); return; }
        try
        {
            SetClipboard(blocks[n - 1]);
            ui.Metadata($"Copied code block {n}.");
        }
        catch (Exception e) when (e is not OperationCanceledException) { ui.Error("Couldn't reach the clipboard.", e.Message, null); }
    }

    void Help()
    {
        ui.Rows(Commands.Select(c => (c.Command, c.What)));
        ui.Blank();
        ui.Rows([("Enter", "Send"), ("Alt+Enter", "New line (also Shift+Enter, Ctrl+J)"), ("Ctrl+C", "Stop the answer"), ("Up / Down", "History")]);
    }

    void Details()
    {
        if (_lastError is null) { ui.Metadata("No error details."); return; }
        ui.Rows([("Error", _lastError.Message), ("Cause", (_lastError as GatewayException)?.Cause ?? "-"), ("Type", _lastError.InnerException?.GetType().Name ?? _lastError.GetType().Name)]);
    }

    void Fail(GatewayException e)
    {
        _lastError = e;
        LastFailed = true;
        var next = e.Status switch
        {
            null => "Is the gateway running? See docs/phase1-runbook.md, then /retry.",
            401 => "Set JAZARI_KEY (run: dotnet run --project src/MyOwnLLM.Gateway -- add-key).",
            _ => "/retry",
        };
        ui.Error(e.Message, e.Cause, next);
    }

    /// <summary>Anything not anticipated: show it like any other error and keep the session alive.</summary>
    public void Unexpected(Exception e)
    {
        _lastError = e;
        LastFailed = true;
        ui.Error("Something went wrong.", e.Message, "/details for more, /retry to try again.");
    }

    Task Note(string text) { ui.Metadata(text); return Task.CompletedTask; }

    void Remember(string question, string answer)
    {
        _history.Add(new ChatTurn("user", question));
        _history.Add(new ChatTurn("assistant", answer));
    }
}
