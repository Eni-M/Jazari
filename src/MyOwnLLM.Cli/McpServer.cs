using System.Collections.Concurrent;
using System.Text.Json;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>
/// <c>jazari mcp</c>: Jazari's law book, answers and reviews for an MCP client such as VS Code, as JSON-RPC, one message a line,
/// over stdin and stdout, so no port is open. Whatever a tool returns goes to the model the client chats with, and that model
/// decides what to call: it's treated as less trusted than the person at the keyboard, hence review_file's rules.
/// </summary>
/// <param name="startServers">Starts what isn't running and says how it went. Run on a tool's first need, not on connecting:
/// an editor can start this server just by opening a folder, and that mustn't load the model.</param>
/// <param name="heartbeat">How often a slow tool reports, to a client that asked with a progress token.</param>
public sealed class McpServer(TextReader input, TextWriter output, IGatewayClient gateway, string project, string reviewDir,
    Func<CancellationToken, Task<ConnectionState>> startServers, string version, TimeSpan? heartbeat = null)
{
    const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602;
    static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]; // newest first; nothing used here differs between them

    static readonly (string Name, string Argument, string Description, string ArgumentDescription)[] Tools =
    [
        ("law_book_search", "query", "Searches Jazari's offline excerpts of Microsoft's .NET docs. Returns the passages with their pages, or says none clearly covers it. Fast: no model runs.", "What to look up."),
        ("jazari_ask", "question", "Asks Jazari, a small local model for ASP.NET Core, C#, .NET, networking, system design and security. One answer with no memory of earlier calls. Files, types and methods the question names are found in the project and sent along. Can take minutes.", "The whole question."),
        ("review_file", "path", "Reviews one file of the project for security problems and real bugs, and returns the report. Findings come from a small local model, so check each one. Takes about a minute for every 2,000 tokens of the file; if it's stopped, calling it again carries on.", "The file, relative to the project or a full path inside it."),
    ];

    readonly SemaphoreSlim _writing = new(1), _modelLane = new(1), _starting = new(1);
    readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    readonly TimeSpan _heartbeat = heartbeat ?? TimeSpan.FromSeconds(10);

    /// <summary>Runs until the client closes stdin, which also stops whatever is still running.</summary>
    public async Task RunAsync()
    {
        using var stop = new CancellationTokenSource();
        List<Task> calls = [];
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            calls.RemoveAll(c => c.IsCompleted);
            if (await HandleAsync(line, stop.Token) is { } call) calls.Add(call);
        }
        await stop.CancelAsync();
        await Task.WhenAll(calls);
    }

    /// <summary>Reads one message. A tool call comes back as a running task, so ping and cancel are read while it works.</summary>
    async Task<Task?> HandleAsync(string line, CancellationToken stop)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(line).RootElement; }
        catch (JsonException) { await ErrorAsync(null, ParseError, "That isn't JSON."); return null; }
        if (message.ValueKind != JsonValueKind.Object) { await ErrorAsync(null, InvalidRequest, "Expected one JSON object a line."); return null; }

        var method = Property(message, "method") is { ValueKind: JsonValueKind.String } m ? m.GetString() : null;
        var parameters = Property(message, "params");
        if (Property(message, "id") is not { ValueKind: JsonValueKind.String or JsonValueKind.Number } id)
        {
            if (method == "notifications/cancelled" && Property(parameters, "requestId") is { } cancelled) Cancel(cancelled);
            return null; // a notification needs no answer, and a reply to a request: this server sends none
        }

        switch (method)
        {
            case "initialize":
                var asked = Property(parameters, "protocolVersion") is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                await RespondAsync(id, new
                {
                    protocolVersion = Versions.Contains(asked) ? asked : Versions[0],
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "jazari", version },
                });
                return null;
            case "ping":
                await RespondAsync(id, new { });
                return null;
            case "tools/list":
                await RespondAsync(id, new { tools = Tools.Select(t => new
                {
                    name = t.Name, description = t.Description,
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object> { [t.Argument] = new { type = "string", description = t.ArgumentDescription } },
                        required = new[] { t.Argument },
                    },
                }) });
                return null;
            case "tools/call":
                var name = Property(parameters, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
                if (!Tools.Any(t => t.Name == name)) { await ErrorAsync(id, InvalidParams, $"No tool called {name}."); return null; }
                // Registered before anything else is read, so a cancel that follows at once finds it.
                var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _running[id.GetRawText()] = cancel;
                return Task.Run(() => CallToolAsync(id, name!, parameters, cancel));
            default:
                await ErrorAsync(id, MethodNotFound, $"No method called {method}.");
                return null;
        }
    }

    void Cancel(JsonElement requestId)
    {
        if (!_running.TryGetValue(requestId.GetRawText(), out var running)) return;
        try { _ = running.CancelAsync(); }
        catch (ObjectDisposedException) { } // it finished just now
    }

    async Task CallToolAsync(JsonElement id, string name, JsonElement? parameters, CancellationTokenSource cancel)
    {
        var ct = cancel.Token;
        using var stopBeating = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var beating = Property(Property(parameters, "_meta"), "progressToken") is { } token ? BeatAsync(token, stopBeating.Token) : Task.CompletedTask;
        (string Text, bool IsError)? result = null;
        try { result = await RunToolAsync(name, Property(parameters, "arguments"), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { result = (e.Message, true); } // one bad call mustn't end the editor's connection
        finally
        {
            // Fully stopped before the response goes out: a progress note for a finished request is an error to some clients.
            await stopBeating.CancelAsync();
            await beating;
            _running.TryRemove(id.GetRawText(), out _);
        }

        // The protocol: no response to a cancelled request.
        if (result is { } done && !ct.IsCancellationRequested)
            await RespondAsync(id, new { content = new[] { new { type = "text", text = done.Text } }, isError = done.IsError });
        cancel.Dispose();
    }

    async Task BeatAsync(JsonElement token, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeat);
            for (var beats = 1; await timer.WaitForNextTickAsync(ct); beats++)
                await SendAsync(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = beats, message = "Jazari is still working." } });
        }
        catch (OperationCanceledException) { }
    }

    Task<(string Text, bool IsError)> RunToolAsync(string name, JsonElement? arguments, CancellationToken ct)
    {
        var argument = Tools.First(t => t.Name == name).Argument;
        if (Property(arguments, argument) is not { ValueKind: JsonValueKind.String } given || string.IsNullOrWhiteSpace(given.GetString()))
            return Task.FromResult(($"{name} needs {argument}, as text.", true));
        var text = given.GetString()!;
        return name switch
        {
            "law_book_search" => SearchAsync(text, ct),
            "jazari_ask" => AskAsync(text, ct),
            _ => ReviewAsync(text, ct),
        };
    }

    /// <summary>Needs no model, so the servers start only if the gateway doesn't answer, and it doesn't wait for a running answer.</summary>
    async Task<(string, bool)> SearchAsync(string query, CancellationToken ct)
    {
        (bool Available, IReadOnlyList<DocPassage> Passages) found;
        try { found = await gateway.DocsAsync(query, ct); }
        catch (GatewayException e) when (e.Status is null)
        {
            if (await StartServersAsync(ct) is { } problem) return (problem, true);
            found = await gateway.DocsAsync(query, ct);
        }
        if (!found.Available) return ("No law book is installed. jazari update installs it once the manifest names it.", true);
        if (found.Passages.Count == 0) return ("The .NET docs have no passage that clearly covers that.", false);
        return (string.Join("\n\n---\n\n", found.Passages.Select(p => $"{p.Text}\n\nFrom \"{p.Title}\": {p.Url}")), false); // the pages' licence asks for the source
    }

    async Task<(string, bool)> AskAsync(string question, CancellationToken ct)
    {
        await _modelLane.WaitAsync(ct);
        try
        {
            if (await StartServersAsync(ct) is { } problem) return (problem, true);
            var theme = new Theme(ColorMode.None, unicode: true);
            var session = new Session(gateway, new Ui(TextWriter.Null, theme, 100), () => new MarkdownStream(TextWriter.Null, theme, 100), project);
            var json = JsonDocument.Parse(await session.AnswerAsJsonAsync(question, ct)).RootElement;
            ct.ThrowIfCancellationRequested(); // the session turns a stop into an error field
            var answer = json.GetProperty("answer").GetString() ?? "";
            if (json.GetProperty("error").GetString() is { } error) return (answer.Length > 0 ? $"{answer}\n\n{error}" : error, true);
            if (json.GetProperty("cut_off").GetBoolean()) answer += "\n\n(The answer was cut off at the model's limit.)";
            if (json.GetProperty("check").GetString() is { } check) answer += $"\n\n({check})";
            if (json.GetProperty("sources") is { ValueKind: JsonValueKind.Array } sources)
                answer += string.Concat(sources.EnumerateArray().Select(s => $"\n\nFrom \"{s.GetProperty("title").GetString()}\": {s.GetProperty("url").GetString()}"));
            return (answer, false);
        }
        finally { _modelLane.Release(); }
    }

    async Task<(string, bool)> ReviewAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path, project);
        if (Refusal(full) is { } refused) return (refused, true);

        await _modelLane.WaitAsync(ct);
        try
        {
            if (await StartServersAsync(ct) is { } problem) return (problem, true);
            var said = new StringWriter();
            var report = await new ProjectReview(gateway, new Ui(said, new Theme(ColorMode.None, unicode: true), 100), reviewDir).RunAsync(full, ct);
            ct.ThrowIfCancellationRequested(); // the review turns a stop into a null report
            return report is null ? (said.ToString().Trim(), true) : ($"{await File.ReadAllTextAsync(report, ct)}\nSaved at {report}", false);
        }
        finally { _modelLane.Release(); }
    }

    /// <summary>
    /// Why this path won't be read. /review takes any file, because a person named it. Here another model names it, one a
    /// poisoned file could steer, and what's read goes back to that model: only the project, no links, none of what a folder
    /// /read leaves out, and nothing the project's .jazariignore does.
    /// </summary>
    string? Refusal(string full)
    {
        var root = Path.GetFullPath(project);
        var relative = Path.GetRelativePath(root, full);
        var outside = $"review_file only reads files inside the project, {root}.";
        // Before anything touches the disk: asking about \\server\share would send the user's Windows credentials there.
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return outside;
        if (Directory.Exists(full)) return "review_file reads one file; jazari review in a terminal reviews a folder.";
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (ProjectFiles.IsLink(Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current))) return outside;
        }
        if (!File.Exists(full)) return $"Can't find {relative}.";
        var forward = relative.Replace(Path.DirectorySeparatorChar, '/');
        return ProjectFiles.Wanted(forward) && !ProjectFiles.IsIgnored(forward, ProjectFiles.IgnorePatterns(root)) ? null
            : $"review_file won't review {relative}: it's a secret, generated or not code, or the project's .jazariignore leaves it out.";
    }

    /// <summary>Null when the servers are up, or else what to do about it. It never makes or replaces a key: nobody is watching an editor's server.</summary>
    async Task<string?> StartServersAsync(CancellationToken ct)
    {
        await _starting.WaitAsync(ct);
        try
        {
            return await startServers(ct) switch
            {
                ConnectionState.Available => null,
                ConnectionState.NoKey => "Jazari has no API key here. Run jazari once in a terminal, then restart the editor so it sees JAZARI_KEY.",
                ConnectionState.KeyRejected => "The gateway rejected the API key. Run jazari once in a terminal, then restart the editor so it sees the new JAZARI_KEY.",
                _ => "Jazari's servers aren't running and couldn't be started. Run jazari doctor in a terminal to see why.",
            };
        }
        finally { _starting.Release(); }
    }

    static JsonElement? Property(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } found && found.TryGetProperty(name, out var value) ? value : null;

    Task RespondAsync(JsonElement id, object result) => SendAsync(new { jsonrpc = "2.0", id, result });
    Task ErrorAsync(JsonElement? id, int code, string message) => SendAsync(new { jsonrpc = "2.0", id, error = new { code, message } });

    /// <summary>One line at a time. System.Text.Json's default escaping keeps it to ASCII with no raw line breaks, whatever the text.</summary>
    async Task SendAsync(object message)
    {
        await _writing.WaitAsync();
        try { await output.WriteLineAsync(JsonSerializer.Serialize(message)); }
        catch (IOException) { } // the client has gone; stdin closing ends the server
        finally { _writing.Release(); }
    }
}
