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
        ("/commit", "Write a commit message for the staged changes"), ("/docs <question>", "Look it up in the .NET docs, without the model"),
        ("/save [file]", "Save the conversation as Markdown"),
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
    /// <summary>The settings the model runs on, for the line under the prompt.</summary>
    public Tuning? Tuning { get; init; }
    /// <summary>While the servers start: the model's short name. Null once they've answered or given up.</summary>
    public string? StartingModel { get; set; }

    string? _hint; // the panel's hint, when something more useful than the default just happened
    string? _chip; // what goes with the next question, short enough for the panel
    bool _reachable = true;
    AnswerTimings? _timings;
    int _droppedTurns; // questions the prompt no longer holds, as last said; /clear starts again
    string? _lastAnswer; // the id and numbers an answer used to print, for /details

    const int MaxReadTokens = 5000; // answers fall from 5.5 to 2.8 tok/s at this size, and keep falling beyond it
    const int EditAnswerTokens = 1000;
    string? _context;
    string? _contextLabel;
    (string Path, byte[] Before, byte[] Written)? _undo;
    /// <summary>Runs git in the project and returns its exit code and output. Replaced in tests.</summary>
    public Func<string[], CancellationToken, Task<(int ExitCode, string Output)>> Git { get; init; } = RunGitAsync;
    bool Live => ui.Theme.Mode != ColorMode.None;

    public async Task<ConnectionState> ConnectAsync(CancellationToken ct)
    {
        try
        {
            _info = await gateway.InfoAsync(ct);
            _reachable = true;
            return ConnectionState.Available;
        }
        catch (GatewayException e)
        {
            _lastError = e;
            _reachable = false;
            return e.Status == 401 ? ConnectionState.KeyRejected : ConnectionState.GatewayNotRunning;
        }
        catch (OperationCanceledException) { _reachable = false; return ConnectionState.GatewayNotRunning; }
    }

    public string ModelName => _info?.Model ?? "unknown";

    /// <summary>"granite tiny" from "granite-4.0-h-tiny-Q4_K_M.gguf"; "the model" when the name doesn't look like that.</summary>
    public static string ModelFromFile(string? file) =>
        ModelFile().Match(Path.GetFileName(file ?? "")) is { Success: true } m ? $"{m.Groups[1].Value} {m.Groups[2].Value}".ToLowerInvariant() : "the model";

    [System.Text.RegularExpressions.GeneratedRegex(@"^([A-Za-z]+)-[\d.]+-(?:h-)?([^-]+)-")] private static partial System.Text.RegularExpressions.Regex ModelFile();
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\b\d+\.\d+(-H)?\b")] private static partial System.Text.RegularExpressions.Regex ModelVersion();

    /// <summary>What the panel around the prompt shows right now.</summary>
    public PanelInfo Panel()
    {
        var dot = ui.Theme.Glyph('·');
        if (StartingModel is { } starting)
            return new(_chip, "Type now: it sends as soon as the model is ready", $"starting {starting}");

        var speed = _timings is { AnswerPerSecond: > 0 } t ? t.AnswerPerSecond : Tuning?.AnswerPerSecond ?? 0;
        string?[] parts = [ModelVersion().Replace(ModelName, "").ToLowerInvariant(), Tuning?.ShortName(), speed > 0 ? $"{speed:0} tok/s" : null];
        var used = _timings is { } last ? last.PromptTokens + last.AnswerTokens : (_history.Sum(h => h.Content.Length) + (_context?.Length ?? 0)) / 4;
        var total = Tuning?.ContextTokens ?? 16384;
        var status = string.Join($" {dot} ", parts.OfType<string>().Where(p => p.Length > 0));
        // Under a question the footer is narrower: model and speed, then the context in words.
        string?[] compact = [parts[0], parts[2], used > 0 ? $"{PromptPanel.Tokens(used)} of {PromptPanel.Tokens(total)}" : null];
        return new(_chip, _hint ?? $"Enter sends {dot} / commands", status, used > 0 ? (used, total) : null, Offline: !_reachable,
            Compact: string.Join($" {dot} ", compact.OfType<string>().Where(p => p.Length > 0)));
    }

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
            case "/diff": return Stoppable(() => DiffAsync(Argument(input), ct), ct);
            case "/context": ShowContext(); break;
            case "/commit": return Stoppable(() => CommitAsync(ct), ct);
            case "/review": return new ProjectReview(gateway, ui, ReviewDir).RunAsync(Resolve(Argument(input) ?? project), ct);
            case "/edit": return Stoppable(() => EditAsync(input.Split(' ', 2) is [_, var rest] ? rest : null, ct), ct);
            case "/undo": Undo(); break;
            case "/docs": return DocsAsync(Argument(input), ct);
            case "/save": Save(Argument(input)); break;
            case "/clear": _history.Clear(); _context = _contextLabel = _chip = null; _timings = null; _droppedTurns = 0; ui.Metadata("Conversation cleared."); break;
            case "/retry": return _lastQuestion is null ? Note("Nothing to retry.") : AskAsync(_lastQuestion, ct);
            case "/details": Details(); break;
            case "/exit": ExitRequested = true; break;
            default: UnknownCommand(input.Split(' ', 2)[0]); break;
        }
        return Task.CompletedTask;
    }

    public async Task AskAsync(string question, CancellationToken ct)
    {
        _lastQuestion = question;
        LastFailed = false;
        if (EchoQuestions) ui.UserMessage(question);
        ui.Blank();

        var thinking = ui.Thinking();
        void StopThinking() => thinking.StopAsync().GetAwaiter().GetResult();

        var renderer = newRenderer();
        // In a real terminal the text flows at a steady pace instead of in the model's bursts; "Thinking" stays up until it starts.
        var pacer = ui.Live ? new TextPacer(t => { StopThinking(); renderer.Write(t); }) : null;
        async Task FinishAsync()
        {
            if (pacer is not null) await pacer.DrainAsync();
            StopThinking();
            renderer.Complete();
        }
        var answer = new StringBuilder();
        string? id = null;
        AnswerTimings? timings = null;
        var cutOff = false;
        var dropped = 0;
        IReadOnlyList<DocSource>? sources = null;
        var watch = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in gateway.ChatAsync(_history, question, project, ct, _context).WithCancellation(ct))
            {
                if (chunk.Progress is { } progress) { thinking.Report(progress); continue; }
                timings = chunk.Timings ?? timings;
                cutOff |= chunk.CutOff;
                dropped = Math.Max(dropped, chunk.DroppedTurns);
                sources = chunk.Sources ?? sources;
                if (chunk.Error is not null)
                {
                    await FinishAsync();
                    Stamp(renderer, watch);
                    FailOrCut(new GatewayException(chunk.Error, null, null), answer.Length > 0);
                    return;
                }
                if (chunk.Content is { } text)
                {
                    answer.Append(text);
                    if (pacer is null) { StopThinking(); renderer.Write(text); }
                    else pacer.Add(text);
                }
                id = chunk.Id ?? id;
            }
            await FinishAsync();
            Remember(question, answer.ToString());
            _timings = timings ?? _timings;
            _reachable = true;
            _hint = null;
            var dot = ui.Theme.Glyph('·');
            var blocks = CodeBlocks.Extract(answer.ToString()).Count;
            var copyHint = blocks == 0 ? "" : $" {dot} {blocks} code block{(blocks == 1 ? "" : "s")}, /copy{(blocks == 1 ? "" : " 1")}";
            _lastAnswer = $"{id} {dot} {watch.Elapsed.TotalSeconds:0.0} s{Describe(timings)}";
            // On a real terminal the time goes in the gutter, /copy n beside each block and the speed under the prompt.
            if (ui.Live) Stamp(renderer, watch);
            else ui.Metadata($"{_lastAnswer}{copyHint}");
            // The docs page the answer drew on, once per page. It stays out of the answer, so /copy and the history are unchanged.
            foreach (var source in (sources ?? []).DistinctBy(s => s.Url.Split('#')[0]))
            {
                var text = $"{source.Title} {dot} {source.Url}";
                if (ui.Live) ui.Row("source", text, Tone.Secondary);
                else ui.Metadata(text);
            }
            if (cutOff && ui.Live) ui.Row("cut", "The answer reached its length limit. Say \"go on\" for the rest.", Tone.Secondary, Tone.Danger);
            else if (cutOff) ui.Metadata("The answer reached its length limit. Say \"go on\" for the rest.");
            // Once per cut: the gateway still searches the dropped turns (ConversationSearch), so say so too.
            if (dropped > _droppedTurns)
            {
                _droppedTurns = dropped;
                var text = $"The oldest {dropped} question{(dropped == 1 ? "" : "s")} no longer fit in the model's memory. Jazari can still look up what you said in them.";
                if (ui.Live) ui.Row("cut", text, Tone.Secondary);
                else ui.Metadata(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped: what the model wrote but the screen hadn't shown yet is dropped, not poured out after the stop.
            if (pacer is not null) answer.Length -= await pacer.DiscardAsync();
            StopThinking();
            renderer.Complete();
            Stamp(renderer, watch);
            if (answer.Length > 0) Remember(question, answer.ToString());
            ui.Metadata("stopped");
        }
        catch (GatewayException e)
        {
            await FinishAsync();
            Stamp(renderer, watch);
            FailOrCut(e, answer.Length > 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FinishAsync();
            Unexpected(e);
        }
        finally { if (!ui.Live) ui.Blank(); }
    }

    static void Stamp(MarkdownStream renderer, Stopwatch watch)
    {
        var seconds = watch.Elapsed.TotalSeconds;
        renderer.StampTime(seconds < 100 ? $"{seconds:0.0}s" : $"{seconds:0}s");
    }

    /// <summary>An error before any text is a problem to fix; after some text, the answer was cut short and what came stays on screen.</summary>
    void FailOrCut(GatewayException e, bool partial)
    {
        if (!(partial && ui.Live)) { Fail(e); return; }
        _lastError = e;
        LastFailed = true;
        _hint = "/details shows the full error";
        ui.Row("cut", $"{e.Message} /retry asks again, the part above is kept.", Tone.Secondary, Tone.Danger);
    }

    async Task StatusAsync(CancellationToken ct)
    {
        LastFailed = false;
        if (await WithThinking(() => gateway.StatusAsync(project, ct), ct) is not (true, var (id, summary))) return;
        var renderer = newRenderer();
        renderer.Write(summary + "\n");
        renderer.Complete();
        ui.Metadata(id);
    }

    /// <summary>
    /// Waits on the gateway behind the thinking indicator, which is cleared whatever happens: an indicator left running
    /// would keep redrawing over the prompt. Stopping and gateway errors are reported here; Ok is false after them.
    /// </summary>
    async Task<(bool Ok, T Value)> WithThinking<T>(Func<Task<T>> call, CancellationToken ct)
    {
        var thinking = ui.Thinking();
        Exception? error = null;
        T value = default!;
        try { value = await call(); }
        catch (Exception e) { error = e; }
        finally { await thinking.StopAsync(); }
        switch (error)
        {
            case null: return (true, value);
            case OperationCanceledException when ct.IsCancellationRequested: ui.Metadata("stopped"); return (false, value);
            case GatewayException e: Fail(e); return (false, value);
            default: System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error); return (false, value);
        }
    }

    /// <summary>Ctrl+C while git runs is a stop like any other, not the end of the session.</summary>
    async Task Stoppable(Func<Task> command, CancellationToken ct)
    {
        try { await command(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ui.Metadata("stopped"); }
    }

    async Task ModelAsync(CancellationToken ct)
    {
        if (_info is null && await ConnectAsync(ct) != ConnectionState.Available) { Details(); return; }
        ui.Rows([("Model", $"{_info!.Model} {ui.Theme.Glyph('·')} local"), ("Gateway", $"{_info.Name} {_info.Version}")]);
    }

    static string? Argument(string input) => input.Split(' ', 2) is [_, var rest] && rest.Trim().Trim('"') is { Length: > 0 } text ? text : null;

    /// <summary>"\"My File.cs\" make it async" or "File.cs make it async": the path, in quotes when it has spaces, and the rest.</summary>
    public static (string Path, string Request)? PathAndRest(string? text)
    {
        if (text?.Trim() is not { Length: > 0 } trimmed) return null;
        if (trimmed.StartsWith('"'))
            return trimmed.IndexOf('"', 1) is var end and > 1 ? (trimmed[1..end], trimmed[(end + 1)..].Trim()) : null;
        return trimmed.Split(' ', 2) is [var path, var rest] ? (path, rest.Trim()) : (trimmed, "");
    }

    void UnknownCommand(string typed)
    {
        var near = CommandNames.MinBy(c => Distance(typed.ToLowerInvariant(), c))!;
        ui.Metadata(Distance(typed.ToLowerInvariant(), near) <= 2 ? $"Unknown command {typed}. Did you mean {near}?" : $"Unknown command {typed}. Try /help.");
    }

    /// <summary>Edits (insert, delete, change) from one text to the other: /reveiw is 2 from /review.</summary>
    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
                (diagonal, row[j]) = (row[j], Math.Min(Math.Min(row[j], row[j - 1]) + 1, diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)));
        }
        return row[b.Length];
    }
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
        _chip = File.Exists(target) ? Path.GetFileName(target) : $"{Path.GetFileName(target.TrimEnd('/', '\\'))}/ {ui.Theme.Glyph('·')} {read} file{(read == 1 ? "" : "s")}";
        // Reading takes minutes on a CPU: start now, while the question is typed, and the answer comes from the prompt cache.
        _ = gateway.WarmAsync(_history.ToList(), project, _context, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // only a head start: failures don't matter
        var left = files.Count - read;
        var extras = $"{(left > 0 ? $". {left} didn't fit, riskiest went first; /review reads everything" : "")}{(skipped > 0 ? $". {skipped} skipped (generated, data or secrets)" : "")}";
        if (ui.Live) ui.Metadata($"{read} file{(read == 1 ? "" : "s")}, {PromptPanel.Tokens(_context.Length / 4)} tokens{extras}. They go with every question until /clear.", "read");
        else ui.Metadata($"Read {read} file{(read == 1 ? "" : "s")}, about {_context.Length / 4} tokens{extras}.");
    }

    /// <summary>Text piped into <c>jazari ask</c> (a diff, a log, an error): goes with the question the way /read does.</summary>
    public void UsePipedInput(string text)
    {
        var limit = MaxReadTokens * 4;
        _context = $"--- piped input ---\n{(text.Length > limit ? text[..limit] : text)}\n";
        _contextLabel = _chip = "piped input";
        if (text.Length > limit) ui.Metadata($"Only the first {MaxReadTokens} tokens or so of the piped input were used.");
    }

    /// <summary>
    /// git in the project with nothing from the repo's own config that could run a program: no file-system monitor, no
    /// signature checker, and every clean/smudge/process filter the repo names switched off (an empty command is no
    /// filter); external diff tools and text conversion are off in the diff calls themselves. The repo may be a clone of
    /// someone else's code. Reading the config to find the filters runs nothing.
    /// </summary>
    async Task<(int ExitCode, string Output)> SafeGitAsync(string[] args, CancellationToken ct)
    {
        var (found, filters) = await Git(["-C", project, "config", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process)$"], ct);
        var off = found == 0 ? filters.Split('\n').Select(k => k.Trim()).Where(k => k.Length > 0).SelectMany(k => new[] { "-c", k + "=" }) : [];
        return await Git(["-C", project, "-c", "core.fsmonitor=false", "-c", "log.showSignature=false", .. off, .. args], ct);
    }

    static async Task<(int ExitCode, string Output)> RunGitAsync(string[] args, CancellationToken ct) =>
        UpdateCheck.FindGit(Environment.GetEnvironmentVariable("PATH")) is { } git
            ? await new ProcessRunner().RunAsync(git, args, ct)
            : (-1, "git isn't installed");

    async Task<string?> DiffTextAsync(string[] range, CancellationToken ct, bool quiet = false)
    {
        var (exit, output) = await SafeGitAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-color", .. range], ct);
        if (exit != 0)
        {
            ui.Metadata(exit == -1 ? "git isn't installed, or isn't on PATH." : "This folder isn't a git repository, or git couldn't read it.");
            return null;
        }
        var (diff, hidden) = WithoutSecrets(output);
        if (hidden.Count > 0 && !quiet) ui.Metadata($"Left out the changes to {string.Join(", ", hidden)}: files like these hold keys and passwords.");
        return diff;
    }

    /// <summary>The diff without the files /read would never send either (.env, *.pem, appsettings.Production.json).</summary>
    public static (string Diff, List<string> Hidden) WithoutSecrets(string diff)
    {
        var kept = new List<string>();
        var hidden = new List<string>();
        var skip = false;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git "))
            {
                // Both sides: a renamed .env is still a .env.
                var secret = line.Split(' ').Skip(2).Select(t => t.Trim('"')).FirstOrDefault(ProjectFiles.IsSecret);
                skip = secret is not null;
                if (skip) hidden.Add(Path.GetFileName(secret!));
            }
            if (!skip) kept.Add(line);
        }
        return (string.Join('\n', kept), hidden);
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
        _chip = $"git diff {ui.Theme.Glyph('·')} {files} file{(files == 1 ? "" : "s")}";
        _ = gateway.WarmAsync(_history.ToList(), project, _context, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        ui.Metadata($"{(ui.Live ? "The" : "Read the")} changes in {files} file{(files == 1 ? "" : "s")}, about {_context.Length / 4} tokens" +
                    $"{(diff.Length > limit ? $"; only the first {MaxReadTokens} or so fit" : "")}. Ask about them, for example: review these changes.", ui.Live ? "read" : null);
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
        var (_, log) = await SafeGitAsync(["log", "-10", "--format=%s"], ct);
        var subjects = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prefixed = subjects.Length > 0 && subjects.Count(s => ConventionalPrefix().IsMatch(s)) * 2 > subjects.Length;
        // Measured on Granite 4.1 3B with a 4,400-token diff: given the whole diff as context, it invented unrelated changes;
        // given the file summary and the diff with one line around each change, inside the question, it described them right.
        var (_, stat) = await SafeGitAsync(["diff", "--cached", "--stat", "--no-color"], ct);
        var compact = await DiffTextAsync(["--cached", "-U1"], ct, quiet: true) ?? staged;
        var changes = $"{stat.TrimEnd()}\n\n{compact}";
        const int limit = 8000; // characters, about 2,000 tokens
        var question = $"--- staged changes ---\n{(changes.Length > limit ? changes[..limit] : changes)}\n\n" +
            "Write the git commit message for the staged changes above. subject: what these changes do, imperative mood, " +
            $"at most 72 characters{(prefixed ? ", starting with a type like feat:, fix:, perf:, docs: or chore:" : "")}. " +
            "body: why, only when the subject can't say it; empty otherwise.";

        if (await WithThinking(() => gateway.CompleteAsync(question, "", CommitSchema, 250, ct), ct) is not (true, var reply)) return;
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
        if (PathAndRest(argument) is not (var file, { Length: > 0 } request)) { ui.Metadata("Usage: /edit <file> <what to change>, the file in \"quotes\" if its name has spaces"); return; }
        var path = Resolve(file);
        if (!InsideProject(path)) { ui.Metadata("Jazari only edits files inside this project."); return; }
        if (!File.Exists(path)) { ui.Metadata($"Can't find {path}."); return; }

        if (FileEdit.Read(path) is not { } stored) { ui.Metadata("That file isn't UTF-8 or UTF-16 text, so Jazari can't edit it without damaging it."); return; }
        var original = stored.Text;
        if (original.Length / 4 > MaxReadTokens) { ui.Metadata("That file is too big to edit in one go."); return; }

        var asked = FileEdit.Instructions + request;
        var watch = Stopwatch.StartNew();
        if (await WithThinking(() => gateway.CompleteAsync(asked, $"--- {file} ---\n{original}", FileEdit.Schema, EditAnswerTokens, ct), ct) is not (true, var reply)) return;

        if (FileEdit.Parse(reply) is not { } edits) { ui.Metadata("The model's answer couldn't be read. Nothing changed."); return; }
        var applied = new List<Replacement>();
        var (text, problem) = FileEdit.Apply(original, edits, applied);
        if (text is null) { ui.Metadata($"Nothing changed: {problem}."); return; }

        if (ui.Live) ui.Row($"{watch.Elapsed.TotalSeconds:0.0}s", applied.Count == 1
            ? $"One change in {file}{(LineOf(original, applied[0].Search) is var at and > 0 ? $", line {at}" : "")}:"
            : $"{applied.Count} changes in {file}:");
        foreach (var edit in applied) // what will be written, not what the model sent
        {
            if (ui.Live && LineOf(original, edit.Search) is var at and > 1) ui.DiffLine((at - 1).ToString(), original.Split('\n')[at - 2].TrimEnd('\r'), Tone.Muted);
            var minus = ui.Live ? ui.Theme.Glyph('−').ToString() : "-";
            foreach (var line in edit.Search.Split('\n')) ui.DiffLine(minus, line.TrimEnd('\r'), ui.Live ? Tone.Removed : Tone.Muted, Surface.Removed);
            foreach (var line in edit.Replace.Split('\n')) ui.DiffLine("+", line.TrimEnd('\r'), ui.Live ? Tone.Added : Tone.Primary, Surface.Added);
            ui.Blank();
        }
        if (FileEdit.NewSyntaxErrors(path, original, text) is { Count: > 0 } broken)
            ui.Error("This change breaks the C# syntax.", string.Join("; ", broken.Take(3)), "Say no, then /edit again with a clearer request.");
        if (!Confirm($"Apply this change to {file}?")) { ui.Metadata("Nothing changed."); return; }
        var written = FileEdit.Encode(text, stored.Encoding);
        File.WriteAllBytes(path, written);
        _undo = (path, stored.Bytes, written);
        _hint = "/undo puts it back";
        ui.Metadata("Changed. /undo puts it back.");
    }

    /// <summary>The line a piece of text starts on, from 1; 0 when it isn't there.</summary>
    static int LineOf(string text, string piece) =>
        text.IndexOf(piece, StringComparison.Ordinal) is var at and >= 0 ? text.AsSpan(0, at).Count('\n') + 1 : 0;

    void Undo()
    {
        if (_undo is not var (path, before, written)) { ui.Metadata("Nothing to undo."); return; }
        // Work done on the file since /edit would be lost: only undo while it still holds exactly what /edit wrote.
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(written))
        {
            ui.Metadata($"{path} has changed since /edit, so it wasn't put back. Your editor's undo can take it from here.");
            _undo = null;
            return;
        }
        File.WriteAllBytes(path, before);
        _undo = null;
        _hint = null;
        ui.Metadata($"Put back {path}.");
    }

    /// <summary>
    /// Whether a path is inside the project without leaving it through a symbolic link or junction on the way, which
    /// a cloned repository could use to point an edit at any file on the PC.
    /// </summary>
    bool InsideProject(string path)
    {
        var root = Path.GetFullPath(project);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return false;
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) return false;
        }
        return true;
    }

    /// <summary>/docs: the passages of Microsoft's .NET docs the law book finds, shown as they are, with their pages.</summary>
    async Task DocsAsync(string? question, CancellationToken ct)
    {
        if (question is null) { ui.Metadata("Usage: /docs <question>"); return; }
        if (await WithThinking(() => gateway.DocsAsync(question, ct), ct) is not (true, var (available, passages))) return;
        if (!available) { ui.Metadata(@"No law book is installed. lawbook/build.py makes one; copy it to %LOCALAPPDATA%\MyOwnLLM\law-book.db."); return; }
        if (passages.Count == 0) { ui.Metadata("The .NET docs have no passage that clearly covers that. Ask the question without /docs."); return; }
        foreach (var passage in passages)
        {
            var renderer = newRenderer();
            renderer.Write(passage.Text + "\n");
            renderer.Complete();
            ui.Metadata($"{passage.Title} {ui.Theme.Glyph('·')} {passage.Url}");
        }
    }

    /// <summary>/save: the conversation as Markdown, in the project unless a path says otherwise. Never overwrites a file.</summary>
    void Save(string? name)
    {
        if (_history.Count == 0) { ui.Metadata("Nothing to save yet."); return; }
        var path = Resolve(name ?? $"jazari-{DateTime.Now:yyyyMMdd-HHmm}.md");
        if (File.Exists(path)) { ui.Metadata($"{path} already exists. /save <another name>"); return; }
        var text = new StringBuilder($"# Jazari conversation, {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        for (var i = 0; i + 1 < _history.Count; i += 2)
            text.Append($"\n### You\n\n{_history[i].Content}\n\n### Jazari\n\n{_history[i + 1].Content}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
        var turns = _history.Count / 2;
        ui.Metadata($"Saved {turns} question{(turns == 1 ? "" : "s")} and answer{(turns == 1 ? "" : "s")} to {path}.");
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

    static readonly (string Label, string[] Commands)[] HelpGroups =
    [
        ("Ask", ["/retry", "/status", "/docs"]),
        ("Files and code", ["/read", "/diff", "/context", "/edit", "/undo", "/copy", "/review", "/commit"]),
        ("Session", ["/model", "/save", "/clear", "/details", "/help", "/exit"]),
    ];

    static readonly (string Key, string Value)[] Keys =
    [
        ("Enter", "Send"), ("Alt+Enter", "New line (also Shift+Enter, Ctrl+J)"), ("Tab", "Complete commands and paths"),
        ("Up / Down", "History; in a menu, choose"), ("Esc", "Close the menu"), ("Ctrl+C", "Stop the answer; twice on an empty line quits"),
    ];

    void Help()
    {
        var groups = HelpGroups.Select(g => (g.Label, Rows: g.Commands.Select(name => Commands.First(c => c.Command.Split(' ')[0] == name))
            .Select(c => (c.Command, c.What)).ToList())).ToList();
        groups[0].Rows.Insert(0, ("(just type)", "Ask a question; answers keep the conversation"));
        var width = Ui.KeyWidth([.. groups.Select(g => g.Rows), Keys]);
        foreach (var (label, rows) in groups)
        {
            ui.Label(label);
            ui.Rows(rows, width);
            ui.Blank();
        }
        ui.Label("Keys");
        ui.Rows(Keys, width);
    }

    /// <summary>What the next question carries besides itself, for the line above the prompt; null when nothing.</summary>
    public string? StatusLine
    {
        get
        {
            var turns = _history.Count / 2;
            var parts = new[] { _contextLabel, turns > 0 ? $"{turns} question{(turns == 1 ? "" : "s")}" : null }.OfType<string>().ToList();
            return parts.Count == 0 ? null : string.Join($" {ui.Theme.Glyph('·')} ", parts);
        }
    }

    void Details()
    {
        if (_lastError is null)
        {
            // A real terminal doesn't print these under each answer, and `jazari feedback` needs the id.
            ui.Metadata(ui.Live && _lastAnswer is { } last ? $"Last answer: {last}" : "No error details.");
            return;
        }
        ui.Rows([("Error", _lastError.Message), ("Cause", (_lastError as GatewayException)?.Cause ?? "-"), ("Type", _lastError.InnerException?.GetType().Name ?? _lastError.GetType().Name)]);
    }

    void Fail(GatewayException e)
    {
        _lastError = e;
        LastFailed = true;
        _hint = "/details shows the full error";
        if (e.Status is null) _reachable = false;
        var next = e.Status switch
        {
            null => "Is the gateway running? See docs/phase1-runbook.md, then /retry.",
            401 => "Open jazari again: it replaces a key the gateway rejects. If that doesn't help, run jazari update.",
            _ => "/retry",
        };
        ui.Error(e.Message, e.Cause, next);
    }

    /// <summary>Anything not anticipated: show it like any other error and keep the session alive.</summary>
    public void Unexpected(Exception e)
    {
        _lastError = e;
        LastFailed = true;
        _hint = "/details shows the full error";
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
