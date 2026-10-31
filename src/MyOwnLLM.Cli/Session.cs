using System.Diagnostics;
using System.Text;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

public enum ConnectionState { Available, GatewayNotRunning, KeyRejected, NoKey }

/// <summary>One Jazari conversation: questions, slash commands, history and stopping an answer.</summary>
public sealed partial class Session(IGatewayClient gateway, Ui ui, Func<MarkdownStream> newRenderer, string project)
{
    public static readonly (string Command, string What)[] Commands =
    [
        ("/help", "Available commands"), ("/status", "Where are we left at?"), ("/model", "Show the model"),
        ("/copy [n]", "Copy code block n of the last answer (no number: the last block)"),
        ("/read <path>", "Read a file or folder for the next questions"), ("/diff [ref]", "Read your uncommitted changes (or the changes since ref)"),
        ("/context", "What the next question will send"), ("/review [path]", "Review a whole project and write a report"),
        ("/edit <file> <what>", "Change a file, after showing you the change"), ("/undo", "Undo the last /edit"),
        ("/commit", "Write a commit message for the staged changes"),
        ("/clear", "Clear conversation and files read"), ("/retry", "Ask the last question again"), ("/details", "Details of the last error"), ("/exit", "Quit"),
    ];

    /// <summary>The command names alone, for Tab completion.</summary>
    public static IEnumerable<string> CommandNames => Commands.Select(c => c.Command.Split(' ')[0]);

    /// <summary>Commands whose argument is a path, so Tab completes file and folder names after them.</summary>
    public static readonly string[] PathCommands = ["/read", "/review", "/edit"];

    readonly List<ChatTurn> _history = [];
    string? _lastQuestion;
    Exception? _lastError;
    GatewayInfo? _info;

    public IReadOnlyList<ChatTurn> History => _history;
    /// <summary>Whether files, a diff or piped text will go with the next question.</summary>
    public bool HasContext => _context is not null;
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
    /// <summary>Asks the user a yes/no question. Without one, /edit never writes.</summary>
    public Func<string, bool> Confirm { get; init; } = _ => false;
    /// <summary>Where reviews keep their progress and reports.</summary>
    public string ReviewDir { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "reviews");

    const int MaxReadTokens = 5000; // answers fall from 5.5 to 2.8 tok/s at this size, and keep falling beyond it
    const int EditAnswerTokens = 1000;
    string? _context;
    string? _contextLabel;
    (string Path, string Text)? _undo;
    /// <summary>Runs git in the project and returns its exit code and output. Replaced in tests.</summary>
    public Func<string[], CancellationToken, Task<(int ExitCode, string Output)>> Git { get; init; } = RunGitAsync;
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
            case "/read": Read(Argument(input)); break;
            case "/diff": return DiffAsync(Argument(input), ct);
            case "/context": ShowContext(); break;
            case "/commit": return CommitAsync(ct);
            case "/review": return new ProjectReview(gateway, ui, ReviewDir).RunAsync(Resolve(Argument(input) ?? project), ct);
            case "/edit": return EditAsync(Argument(input), ct);
            case "/undo": Undo(); break;
            case "/clear": _history.Clear(); _context = _contextLabel = null; ui.Metadata("Conversation cleared."); break;
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
        AnswerTimings? timings = null;
        var cutOff = false;
        var watch = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in gateway.ChatAsync(_history, question, project, ct, _context).WithCancellation(ct))
            {
                if (chunk.Progress is { } progress) { thinking.Report(progress); continue; }
                timings = chunk.Timings ?? timings;
                cutOff |= chunk.CutOff;
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
            ui.Metadata($"{id} {ui.Theme.Glyph('·')} {watch.Elapsed.TotalSeconds:0.0} s{Describe(timings)}{copyHint}");
            if (cutOff) ui.Metadata("The answer reached its length limit. Say \"go on\" for the rest.");
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

    static string? Argument(string input) => input.Split(' ', 2) is [_, var rest] && rest.Trim().Trim('"') is { Length: > 0 } text ? text : null;
    string Resolve(string path) => Path.GetFullPath(path, project);

    void Read(string? path)
    {
        if (path is null) { ui.Metadata("Usage: /read <file or folder>"); return; }
        var target = Resolve(path);
        if (!File.Exists(target) && !Directory.Exists(target)) { ui.Metadata($"Can't find {target}."); return; }

        var (files, skipped) = ProjectFiles.Load(target);
        var context = new StringBuilder();
        var read = 0;
        foreach (var file in files)
        {
            var piece = $"--- {file.Path} ---\n{file.Text}\n";
            if ((context.Length + piece.Length) / 4 > MaxReadTokens) break;
            context.Append(piece);
            read++;
        }
        if (read == 0) { ui.Metadata(files.Count == 0 ? "Nothing readable there." : $"{files[0].Path} is too big to read at once. Use /review for large files."); return; }
        _context = context.ToString();
        _contextLabel = $"{read} file{(read == 1 ? "" : "s")} from {path}";
        // Reading takes minutes on a CPU: start now, while the question is typed, and the answer comes from the prompt cache.
        _ = gateway.WarmAsync(_history.ToList(), project, _context, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // only a head start: failures don't matter
        var left = files.Count - read;
        ui.Metadata($"Read {read} file{(read == 1 ? "" : "s")}, about {_context.Length / 4} tokens{(left > 0 ? $". {left} didn't fit, riskiest went first; /review reads everything" : "")}{(skipped > 0 ? $". {skipped} skipped (generated, data or secrets)" : "")}.");
    }

    /// <summary>Text piped into <c>jazari ask</c> (a diff, a log, an error): goes with the question the way /read does.</summary>
    public void UsePipedInput(string text)
    {
        var limit = MaxReadTokens * 4;
        _context = $"--- piped input ---\n{(text.Length > limit ? text[..limit] : text)}\n";
        _contextLabel = "piped input";
        if (text.Length > limit) ui.Metadata($"Only the first {MaxReadTokens} tokens or so of the piped input were used.");
    }

    /// <summary>git in the project with nothing from the repo's own config that could run a program: no external diff
    /// tools, no text conversion filters, no file-system monitor. The repo may be a clone of someone else's code.</summary>
    string[] SafeGit(params string[] args) => ["-C", project, "-c", "core.fsmonitor=false", .. args];

    static async Task<(int ExitCode, string Output)> RunGitAsync(string[] args, CancellationToken ct) =>
        UpdateCheck.FindGit(Environment.GetEnvironmentVariable("PATH")) is { } git
            ? await new ProcessRunner().RunAsync(git, args, ct)
            : (-1, "git isn't installed");

    async Task<string?> DiffTextAsync(string[] range, CancellationToken ct)
    {
        var (exit, output) = await Git(SafeGit(["diff", "--no-ext-diff", "--no-textconv", "--no-color", .. range]), ct);
        if (exit == 0) return output;
        ui.Metadata(exit == -1 ? "git isn't installed, or isn't on PATH." : "This folder isn't a git repository, or git couldn't read it.");
        return null;
    }

    /// <summary>/diff: the uncommitted changes (or the changes since a ref) go with the next questions, like /read.</summary>
    async Task DiffAsync(string? since, CancellationToken ct)
    {
        if (await DiffTextAsync([since ?? "HEAD"], ct) is not { } diff) return;
        if (diff.Trim().Length == 0) { ui.Metadata(since is null ? "No uncommitted changes." : $"No changes since {since}."); return; }
        var limit = MaxReadTokens * 4;
        _context = $"--- git diff {since ?? "HEAD"} ---\n{(diff.Length > limit ? diff[..limit] : diff)}\n";
        var files = diff.Split('\n').Count(l => l.StartsWith("diff --git "));
        _contextLabel = $"git diff {since ?? "HEAD"}, {files} file{(files == 1 ? "" : "s")}";
        _ = gateway.WarmAsync(_history.ToList(), project, _context, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        ui.Metadata($"Read the changes in {files} file{(files == 1 ? "" : "s")}, about {_context.Length / 4} tokens" +
                    $"{(diff.Length > limit ? $"; only the first {MaxReadTokens} or so fit" : "")}. Ask about them, for example: review these changes.");
    }

    void ShowContext()
    {
        var turns = _history.Count / 2;
        ui.Metadata(_context is null ? "No files read. /read or /diff adds some." : $"Read: {_contextLabel}, about {_context.Length / 4} tokens.");
        ui.Metadata($"Conversation: {turns} question{(turns == 1 ? "" : "s")} and answer{(turns == 1 ? "" : "s")}, about {_history.Sum(t => t.Content.Length) / 4} tokens. /clear empties both.");
    }

    const string CommitSchema = """
        {"type":"object","required":["subject"],"additionalProperties":false,"properties":{
        "subject":{"type":"string","maxLength":72},"body":{"type":"string","maxLength":600}}}
        """;

    /// <summary>/commit: a message for the staged changes, in the style of the repo's recent commits, copied for git commit.</summary>
    async Task CommitAsync(CancellationToken ct)
    {
        if (await DiffTextAsync(["--cached"], ct) is not { } staged) return;
        if (staged.Trim().Length == 0) { ui.Metadata("Nothing is staged. git add the changes first."); return; }
        // Only the style is taken from the history, never its subjects: shown earlier subjects, a 3B model describes those.
        var (_, log) = await Git(SafeGit("log", "-10", "--format=%s"), ct);
        var subjects = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prefixed = subjects.Length > 0 && subjects.Count(s => ConventionalPrefix().IsMatch(s)) * 2 > subjects.Length;
        // Measured on Granite 4.1 3B with a 4,400-token diff: given the whole diff as context, it invented unrelated changes;
        // given the file summary and the diff with one line around each change, inside the question, it described them right.
        var (_, stat) = await Git(SafeGit("diff", "--cached", "--stat", "--no-color"), ct);
        var compact = await DiffTextAsync(["--cached", "-U1"], ct) ?? staged;
        var changes = $"{stat.TrimEnd()}\n\n{compact}";
        const int limit = 8000; // characters, about 2,000 tokens
        var question = $"--- staged changes ---\n{(changes.Length > limit ? changes[..limit] : changes)}\n\n" +
            "Write the git commit message for the staged changes above. subject: what these changes do, imperative mood, " +
            $"at most 72 characters{(prefixed ? ", starting with a type like feat:, fix:, perf:, docs: or chore:" : "")}. " +
            "body: why, only when the subject can't say it; empty otherwise.";

        var thinking = ThinkingIndicator.Start(ui.Output, ui.Theme);
        string reply;
        try { reply = await gateway.CompleteAsync(question, "", CommitSchema, 250, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await thinking.StopAsync(); ui.Metadata("stopped"); return; }
        catch (GatewayException e) { await thinking.StopAsync(); Fail(e); return; }
        await thinking.StopAsync();

        if (CommitMessage(reply) is not { } message) { ui.Metadata("The model's answer couldn't be read. Try /commit again."); return; }
        ui.Line(message, Tone.Primary);
        try { SetClipboard(message); ui.Metadata("Copied. Paste it into git commit, or edit it first."); }
        catch (Exception e) when (e is not OperationCanceledException) { ui.Metadata("Couldn't copy it; select the text above instead."); }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\w+(\([^)]*\))?!?: ")] private static partial System.Text.RegularExpressions.Regex ConventionalPrefix();

    /// <summary>"subject\n\nbody", or null when the reply isn't the expected JSON.</summary>
    public static string? CommitMessage(string reply)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(reply);
            var subject = doc.RootElement.GetProperty("subject").GetString()?.Trim();
            var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString()?.Trim() : null;
            return string.IsNullOrEmpty(subject) ? null : string.IsNullOrEmpty(body) ? subject : $"{subject}\n\n{body}";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    async Task EditAsync(string? argument, CancellationToken ct)
    {
        if (argument?.Split(' ', 2) is not [var file, var request]) { ui.Metadata("Usage: /edit <file> <what to change>"); return; }
        var path = Resolve(file);
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            ui.Metadata("Jazari only edits files inside this project.");
            return;
        }
        if (!File.Exists(path)) { ui.Metadata($"Can't find {path}."); return; }

        var original = File.ReadAllText(path);
        if (original.Length / 4 > MaxReadTokens) { ui.Metadata("That file is too big to edit in one go."); return; }

        var thinking = ThinkingIndicator.Start(ui.Output, ui.Theme);
        string reply;
        try { reply = await gateway.CompleteAsync(FileEdit.Instructions + request, $"--- {file} ---\n{original}", FileEdit.Schema, EditAnswerTokens, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await thinking.StopAsync(); ui.Metadata("stopped"); return; }
        catch (GatewayException e) { await thinking.StopAsync(); Fail(e); return; }
        await thinking.StopAsync();

        if (FileEdit.Parse(reply) is not { } edits) { ui.Metadata("The model's answer couldn't be read. Nothing changed."); return; }
        var (text, problem) = FileEdit.Apply(original, edits);
        if (text is null) { ui.Metadata($"Nothing changed: {problem}."); return; }

        foreach (var edit in edits)
        {
            foreach (var line in edit.Search.Split('\n')) ui.Line("- " + ConsoleText.Sanitize(line.TrimEnd('\r')), Tone.Muted);
            foreach (var line in edit.Replace.Split('\n')) ui.Line("+ " + ConsoleText.Sanitize(line.TrimEnd('\r')), Tone.Primary);
            ui.Blank();
        }
        if (!Confirm($"Apply this change to {file}?")) { ui.Metadata("Nothing changed."); return; }
        FileEdit.Write(path, text);
        _undo = (path, original);
        ui.Metadata("Changed. /undo puts it back.");
    }

    void Undo()
    {
        if (_undo is not var (path, text)) { ui.Metadata("Nothing to undo."); return; }
        FileEdit.Write(path, text);
        _undo = null;
        ui.Metadata($"Put back {path}.");
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

    /// <summary>" · 5,000 tokens read, 1,200 from cache · 6.2 tok/s": shows when the cache missed, which is what makes waits long.</summary>
    string Describe(AnswerTimings? t) => t is null or { PromptTokens: 0 } ? "" :
        $" {ui.Theme.Glyph('·')} {t.PromptTokens:N0} tokens read, {t.CachedTokens:N0} from cache" +
        (t.AnswerPerSecond > 0 ? $" {ui.Theme.Glyph('·')} {t.AnswerPerSecond:0.0} tok/s" : "");

    void Remember(string question, string answer)
    {
        _history.Add(new ChatTurn("user", question));
        _history.Add(new ChatTurn("assistant", answer));
    }
}
