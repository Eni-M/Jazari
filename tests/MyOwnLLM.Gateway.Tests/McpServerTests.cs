using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class McpServerTests : IAsyncLifetime
{
    readonly string _project = Directory.CreateTempSubdirectory("jazari mcp").FullName;
    readonly string _reviews = Directory.CreateTempSubdirectory("jazari mcp reviews").FullName;
    readonly FakeGateway _gateway = new();
    readonly Pipe _toServer = new(), _fromServer = new();
    StreamWriter _send = null!;
    StreamReader _receive = null!;
    Task _running = null!;
    int _nextId;

    /// <summary>What starting the servers comes to; counted, since a lookup mustn't load the model.</summary>
    ConnectionState _state = ConnectionState.Available;
    int _prepared;
    Func<Task>? _whilePreparing;

    public Task InitializeAsync()
    {
        _send = new StreamWriter(_toServer.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _receive = new StreamReader(_fromServer.Reader.AsStream(), Encoding.UTF8);
        var server = new McpServer(
            new StreamReader(_toServer.Reader.AsStream(), Encoding.UTF8),
            new StreamWriter(_fromServer.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" },
            _gateway, _project, _reviews, Prepare, "1.2.3", heartbeat: TimeSpan.FromMilliseconds(20));
        _running = Task.Run(async () =>
        {
            await server.RunAsync();
            await _fromServer.Writer.CompleteAsync();
        });
        return Task.CompletedTask;
    }

    async Task<ConnectionState> Prepare(CancellationToken ct)
    {
        Interlocked.Increment(ref _prepared);
        if (_whilePreparing is not null) await _whilePreparing();
        return _state;
    }

    public async Task DisposeAsync()
    {
        await _toServer.Writer.CompleteAsync();
        await _running.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(_project, recursive: true);
        Directory.Delete(_reviews, recursive: true);
    }

    Task Send(object message) => _send.WriteLineAsync(JsonSerializer.Serialize(message));

    async Task<JsonElement> Receive()
    {
        var line = await _receive.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);
        return JsonDocument.Parse(line).RootElement;
    }

    /// <summary>The next message that isn't a progress notification.</summary>
    async Task<JsonElement> Response()
    {
        while (true)
        {
            var message = await Receive();
            if (!message.TryGetProperty("method", out _)) return message;
        }
    }

    async Task<JsonElement> Request(string method, object? parameters = null)
    {
        var id = ++_nextId;
        await Send(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
        var response = await Response();
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        return response;
    }

    async Task<(string Text, bool IsError)> Call(string tool, object arguments)
    {
        var result = (await Request("tools/call", new { name = tool, arguments })).GetProperty("result");
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return (content.GetProperty("text").GetString()!, result.TryGetProperty("isError", out var e) && e.GetBoolean());
    }

    // The handshake

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-11-25")] // not one it knows: it offers its own and the client decides
    public async Task Initialize_agrees_on_a_protocol_version(string asked, string agreed)
    {
        var result = (await Request("initialize", new { protocolVersion = asked, capabilities = new { }, clientInfo = new { name = "t", version = "1" } })).GetProperty("result");

        Assert.Equal(agreed, result.GetProperty("protocolVersion").GetString());
        Assert.Equal("jazari", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("1.2.3", result.GetProperty("serverInfo").GetProperty("version").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Tools_list_names_the_three_tools_and_what_each_needs()
    {
        var tools = (await Request("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t);

        Assert.Equal(["jazari_ask", "law_book_search", "review_file"], tools.Keys.Order());
        foreach (var (name, argument) in new[] { ("law_book_search", "query"), ("jazari_ask", "question"), ("review_file", "path") })
        {
            Assert.False(string.IsNullOrWhiteSpace(tools[name].GetProperty("description").GetString()));
            var schema = tools[name].GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.Equal(argument, Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
            Assert.Equal("string", schema.GetProperty("properties").GetProperty(argument).GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Ping_answers_with_an_empty_result()
    {
        var result = (await Request("ping")).GetProperty("result");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public async Task A_string_id_comes_back_as_a_string()
    {
        await Send(new { jsonrpc = "2.0", id = "abc", method = "ping" });

        Assert.Equal("abc", (await Receive()).GetProperty("id").GetString());
    }

    // What isn't a good request

    [Fact]
    public async Task Text_that_isnt_json_is_a_parse_error_and_the_server_carries_on()
    {
        await _send.WriteLineAsync("{not json");

        var error = await Receive();
        Assert.Equal(-32700, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("id").ValueKind);
        Assert.True((await Request("ping")).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task A_batch_is_an_invalid_request()
    {
        await _send.WriteLineAsync("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""");

        Assert.Equal(-32600, (await Receive()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_unknown_method_is_an_error_but_an_unknown_notification_gets_no_reply()
    {
        await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        await Send(new { jsonrpc = "2.0", method = "notifications/whatever" });

        var response = await Request("no/such/method"); // Request checks the id, so anything sent for the two above would fail here

        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_unknown_tool_is_an_invalid_params_error()
    {
        var response = await Request("tools/call", new { name = "format_disk", arguments = new { } });

        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("law_book_search")]
    [InlineData("jazari_ask")]
    [InlineData("review_file")]
    public async Task A_missing_argument_is_a_tool_error_the_model_can_correct(string tool)
    {
        var (text, isError) = await Call(tool, new { wrong = 5 });

        Assert.True(isError);
        Assert.Contains("needs", text);
        Assert.Empty(_gateway.Asked);
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task An_argument_that_isnt_text_is_a_tool_error()
    {
        var (_, isError) = await Call("jazari_ask", new { question = 5 });

        Assert.True(isError);
        Assert.Empty(_gateway.Asked);
    }

    // law_book_search

    [Fact]
    public async Task Law_book_search_returns_each_passage_with_its_page()
    {
        _gateway.Docs = (true, [
            new DocPassage("Rate limiting", "https://learn.microsoft.com/rate", "Use AddRateLimiter. Déjà vu."),
            new DocPassage("Middleware", "https://learn.microsoft.com/mw", "Order matters."),
        ]);

        var (text, isError) = await Call("law_book_search", new { query = "rate limiting" });

        Assert.False(isError);
        Assert.Contains("Use AddRateLimiter. Déjà vu.", text);
        Assert.Contains("Rate limiting", text);
        Assert.Contains("https://learn.microsoft.com/rate", text);
        Assert.Contains("Order matters.", text);
        Assert.Contains("https://learn.microsoft.com/mw", text);
    }

    [Fact]
    public async Task Law_book_search_with_no_match_says_so_and_isnt_an_error()
    {
        _gateway.Docs = (true, []);

        var (text, isError) = await Call("law_book_search", new { query = "pancakes" });

        Assert.False(isError);
        Assert.Contains("no passage", text);
    }

    [Fact]
    public async Task Law_book_search_without_a_law_book_is_an_error_not_an_empty_result()
    {
        _gateway.Docs = (false, []);

        var (text, isError) = await Call("law_book_search", new { query = "rate limiting" });

        Assert.True(isError);
        Assert.Contains("No law book", text);
    }

    [Fact]
    public async Task Law_book_search_doesnt_start_the_servers_when_the_gateway_answers()
    {
        await Call("law_book_search", new { query = "rate limiting" });

        Assert.Equal(0, _prepared);
    }

    [Fact]
    public async Task Law_book_search_starts_the_servers_when_the_gateway_is_down_and_tries_again()
    {
        var calls = 0;
        _gateway.DocsCall = () => ++calls == 1 ? throw new GatewayException("gateway not reachable", null, null) : (true, [new DocPassage("T", "U", "found")]);

        var (text, isError) = await Call("law_book_search", new { query = "rate limiting" });

        Assert.False(isError);
        Assert.Contains("found", text);
        Assert.Equal(1, _prepared);
    }

    [Fact]
    public async Task Law_book_search_doesnt_start_the_servers_over_a_rejected_key()
    {
        _gateway.DocsCall = () => throw new GatewayException("the API key was rejected", "HTTP 401", 401);

        var (text, isError) = await Call("law_book_search", new { query = "rate limiting" });

        Assert.True(isError);
        Assert.Contains("the API key was rejected", text);
        Assert.Equal(0, _prepared);
    }

    // jazari_ask

    [Fact]
    public async Task Ask_returns_the_answer_and_its_sources()
    {
        _gateway.Chat = (_, _, _) => Sourced();

        var (text, isError) = await Call("jazari_ask", new { question = "what is a span?" });

        Assert.False(isError);
        Assert.Contains("A span is a view.", text);
        Assert.Contains("Memory docs", text);
        Assert.Contains("https://learn.microsoft.com/span", text);
        Assert.Equal("what is a span?", Assert.Single(_gateway.Asked).Question);
    }

    static async IAsyncEnumerable<SseChunk> Sourced()
    {
        await Task.Yield();
        yield return new SseChunk("A span is a view.", null, null);
        yield return new SseChunk(null, "ix-1", null) { Sources = [new DocSource("Memory docs", "https://learn.microsoft.com/span")] };
    }

    [Fact]
    public async Task Ask_starts_the_servers_first_and_keeps_no_conversation()
    {
        await Call("jazari_ask", new { question = "first" });
        await Call("jazari_ask", new { question = "second" });

        Assert.Equal(2, _prepared);
        Assert.All(_gateway.Asked, a => Assert.Empty(a.History));
    }

    [Fact]
    public async Task Ask_says_when_the_answer_was_cut_off()
    {
        _gateway.Chat = (_, _, _) => CutOff();

        var (text, isError) = await Call("jazari_ask", new { question = "explain everything" });

        Assert.False(isError);
        Assert.Contains("Partial answ", text);
        Assert.Contains("cut off", text);
    }

    static async IAsyncEnumerable<SseChunk> CutOff()
    {
        await Task.Yield();
        yield return new SseChunk("Partial answ", null, null) { CutOff = true };
    }

    [Fact]
    public async Task Ask_reports_what_the_gateway_said_went_wrong()
    {
        _gateway.Chat = (_, _, _) => throw new GatewayException("the model isn't loaded", null, 503);

        var (text, isError) = await Call("jazari_ask", new { question = "hello" });

        Assert.True(isError);
        Assert.Contains("the model isn't loaded", text);
    }

    [Theory]
    [InlineData(ConnectionState.NoKey, "no API key")]
    [InlineData(ConnectionState.KeyRejected, "rejected")]
    [InlineData(ConnectionState.GatewayNotRunning, "jazari doctor")]
    public async Task Ask_without_working_servers_says_what_to_do_and_asks_nothing(ConnectionState state, string says)
    {
        _state = state;

        var (text, isError) = await Call("jazari_ask", new { question = "hello" });

        Assert.True(isError);
        Assert.Contains(says, text);
        Assert.Empty(_gateway.Asked);
    }

    [Fact]
    public async Task Prepare_blowing_up_is_a_tool_error_and_the_server_lives()
    {
        _whilePreparing = () => throw new IOException("disk full");

        var (text, isError) = await Call("jazari_ask", new { question = "hello" });

        Assert.True(isError);
        Assert.Contains("disk full", text);
        Assert.True((await Request("ping")).TryGetProperty("result", out _));
    }

    // review_file

    string Write(string relative, string text)
    {
        var path = Path.Combine(_project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    const string Finding = """{"findings":[{"file":"Pay.cs","quote":"var sql = \"SELECT \" + id;","problem":"SQL built from text","severity":"high"}]}""";

    [Fact]
    public async Task Review_returns_the_report_and_where_it_is_saved()
    {
        Write("src/Pay.cs", "class Pay {\n  var sql = \"SELECT \" + id;\n}\n");
        _gateway.Complete = (_, _) => Task.FromResult(Finding);

        var (text, isError) = await Call("review_file", new { path = "src/Pay.cs" });

        Assert.False(isError);
        Assert.Contains("SQL built from text", text);
        Assert.Contains("Pay.cs:2", text);
        var report = Assert.Single(Directory.GetFiles(_reviews, "*.md"));
        Assert.Contains(report, text);
        Assert.Equal(1, _prepared);
    }

    [Fact]
    public async Task Review_takes_a_full_path_inside_the_project()
    {
        var path = Write("Pay.cs", "class Pay { }\n");

        var (text, isError) = await Call("review_file", new { path });

        Assert.False(isError);
        Assert.Contains("No findings", text);
    }

    [Fact]
    public async Task Review_twice_reads_an_unchanged_file_once()
    {
        Write("Pay.cs", "class Pay { }\n");

        await Call("review_file", new { path = "Pay.cs" });
        await Call("review_file", new { path = "Pay.cs" });

        Assert.Equal(1, _gateway.Completions);
    }

    [Fact]
    public async Task Review_of_a_file_too_big_to_read_says_so()
    {
        Write("Huge.cs", new string('x', 300 * 1024));

        var (text, isError) = await Call("review_file", new { path = "Huge.cs" });

        Assert.True(isError);
        Assert.Contains("Nothing to review", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("src/../../secret.cs")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\a.cs")]
    public async Task Review_refuses_a_path_outside_the_project(string path)
    {
        var (text, isError) = await Call("review_file", new { path });

        Assert.True(isError);
        Assert.Contains("inside the project", text);
        Assert.Equal(0, _gateway.Completions);
        Assert.Equal(0, _prepared);
    }

    [Fact]
    public async Task Review_refuses_a_link_that_leaves_the_project()
    {
        var outside = Path.Combine(_reviews, "outside.cs");
        File.WriteAllText(outside, "class Outside { }\n");
        var link = Path.Combine(_project, "link.cs");
        try { File.CreateSymbolicLink(link, outside); }
        catch (IOException) { return; } // Windows without Developer Mode can't make links
        catch (UnauthorizedAccessException) { return; }

        var (text, isError) = await Call("review_file", new { path = "link.cs" });

        Assert.True(isError);
        Assert.Contains("inside the project", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task Review_refuses_a_file_through_a_junction_that_leaves_the_project()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_reviews, "outside.cs"), "class Outside { }\n");
        var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", Path.Combine(_project, "linked"), _reviews])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        await junction.WaitForExitAsync();
        Assert.Equal(0, junction.ExitCode); // unlike a symbolic link, a junction needs no special rights

        var (text, isError) = await Call("review_file", new { path = "linked/outside.cs" });

        Assert.True(isError);
        Assert.Contains("inside the project", text);
        Assert.Equal(0, _gateway.Completions);
        Directory.Delete(Path.Combine(_project, "linked")); // the link only, so the cleanup can't walk through it
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("appsettings.Production.json")]
    [InlineData("keys/server.pem")]
    [InlineData(".ssh/config.json")]
    [InlineData("photo.png")]
    public async Task Review_refuses_a_file_read_would_leave_out(string file)
    {
        Write(file, "PASSWORD=hunter2\n");

        var (text, isError) = await Call("review_file", new { path = file });

        Assert.True(isError);
        Assert.Contains("won't review", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetShortPathNameW(string path, StringBuilder shortPath, int size);

    /// <summary>The path below the project with each part as its 8.3 short name, or null where the volume keeps none.</summary>
    string? ShortNames(string relative)
    {
        var (longPath, result) = (_project, new List<string>());
        foreach (var part in relative.Split('/'))
        {
            longPath = Path.Combine(longPath, part);
            var buffer = new StringBuilder(1024);
            if (GetShortPathNameW(longPath, buffer, buffer.Capacity) == 0) return null;
            result.Add(Path.GetFileName(buffer.ToString()));
        }
        var shortened = string.Join('/', result);
        return shortened.Equals(relative, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(_project, shortened)) ? null : shortened;
    }

    // Path.GetFullPath turns short names back into the real ones on Windows, which is what keeps these refused: a change that
    // checked the path as sent would let SSH~1 past the secret-folder rule and PRIVAT~1 past .jazariignore.
    [Theory]
    [InlineData(".ssh/config.json")] // SSH~1\CONFIG~1.JSO: the folder rule never sees .ssh
    [InlineData("secrets.local.cs")] // SECRET~1.CS: not a secret's name, and still code
    public async Task Review_refuses_a_secret_named_by_its_short_name(string file)
    {
        Write(file, "PASSWORD=hunter2\n");
        if (!OperatingSystem.IsWindows() || ShortNames(file) is not { } shortened) return; // this volume has no short names

        var (text, isError) = await Call("review_file", new { path = shortened });

        Assert.True(isError);
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task Review_refuses_an_ignored_folder_named_by_its_short_name()
    {
        Write(".jazariignore", "private stuff/\n");
        Write("private stuff/Notes.cs", "class Notes { }\n");
        if (!OperatingSystem.IsWindows() || ShortNames("private stuff/Notes.cs") is not { } shortened) return;

        var (_, isError) = await Call("review_file", new { path = shortened });

        Assert.True(isError);
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task Review_refuses_a_hidden_data_stream_of_a_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Write("notes.pem", "key\n");
        File.WriteAllText(file + ":hidden.cs", "PASSWORD=hunter2\n");

        var (text, isError) = await Call("review_file", new { path = "notes.pem:hidden.cs" });

        Assert.True(isError);
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task Review_finds_a_hidden_file_and_a_name_in_another_case()
    {
        var file = Write("Hidden.cs", "class Hidden { }\n");
        File.SetAttributes(file, FileAttributes.Hidden);

        Assert.False((await Call("review_file", new { path = "hidden.CS" })).IsError);
        Assert.Equal(1, _gateway.Completions);
    }

    [Fact]
    public async Task Review_refuses_a_file_the_jazariignore_leaves_out()
    {
        Write(".jazariignore", "private/\n");
        Write("private/Notes.cs", "class Notes { }\n");

        var (text, isError) = await Call("review_file", new { path = "private/Notes.cs" });

        Assert.True(isError);
        Assert.Contains("won't review", text);
        Assert.Equal(0, _gateway.Completions);
    }

    [Theory]
    [InlineData("Missing.cs", "Can't find")]
    [InlineData("src", "one file")]
    [InlineData(".", "one file")]
    public async Task Review_needs_a_file_that_exists(string path, string says)
    {
        Directory.CreateDirectory(Path.Combine(_project, "src"));

        var (text, isError) = await Call("review_file", new { path });

        Assert.True(isError);
        Assert.Contains(says, text);
        Assert.Equal(0, _prepared);
    }

    [Fact]
    public async Task Review_reports_a_gateway_failure()
    {
        Write("Pay.cs", "class Pay { }\n");
        _gateway.Complete = (_, _) => throw new GatewayException("the model isn't loaded", null, 503);

        var (text, isError) = await Call("review_file", new { path = "Pay.cs" });

        Assert.True(isError);
        Assert.Contains("the model isn't loaded", text);
        Assert.Empty(Directory.GetFiles(_reviews, "*.md"));
    }

    // Slow tools

    /// <summary>An answer that takes as long as the test says, and sees the cancel.</summary>
    (TaskCompletionSource Finish, TaskCompletionSource Started, TaskCompletionSource Cancelled) SlowAnswer()
    {
        var (finish, started, cancelled) = (new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource());
        _gateway.Chat = (_, _, ct) => Slow(finish, started, cancelled, ct);
        return (finish, started, cancelled);
    }

    static async IAsyncEnumerable<SseChunk> Slow(TaskCompletionSource finish, TaskCompletionSource started, TaskCompletionSource cancelled, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        started.TrySetResult();
        try { await finish.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        yield return new SseChunk("Done at last.", null, null);
    }

    Task CallWithToken(int id, object? progressToken, string question = "slow one") => Send(new
    {
        jsonrpc = "2.0", id, method = "tools/call",
        @params = new { name = "jazari_ask", arguments = new { question }, _meta = progressToken is null ? null : new { progressToken } },
    });

    [Fact]
    public async Task A_slow_tool_sends_progress_for_its_token_and_none_after_the_response()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(7, "tok-1");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        double last = 0;
        for (var i = 0; i < 2; i++)
        {
            var note = await Receive();
            Assert.Equal("notifications/progress", note.GetProperty("method").GetString());
            Assert.Equal("tok-1", note.GetProperty("params").GetProperty("progressToken").GetString());
            var progress = note.GetProperty("params").GetProperty("progress").GetDouble();
            Assert.True(progress > last); // the spec: it must increase each time
            last = progress;
        }
        finish.SetResult();

        // Everything up to the response may be progress; nothing may follow it. A progress note for a finished request is what a client can choke on.
        JsonElement message;
        do message = await Receive();
        while (message.TryGetProperty("method", out _));
        Assert.Equal(7, message.GetProperty("id").GetInt32());
        await Task.Delay(100); // several heartbeats
        await _toServer.Writer.CompleteAsync();
        Assert.Null(await _receive.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_slow_tool_without_a_token_sends_no_progress()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(7, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100); // several heartbeats
        finish.SetResult();

        Assert.Equal(7, (await Receive()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Ping_is_answered_while_a_tool_runs()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(7, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await Request("ping")).TryGetProperty("result", out _));

        finish.SetResult();
        Assert.Equal(7, (await Receive()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_cancel_stops_the_work_sends_no_response_and_the_server_carries_on()
    {
        var (_, started, cancelled) = SlowAnswer();
        await CallWithToken(7, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 7, reason = "timed out" } });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _gateway.Chat = (_, _, _) => FakeGateway.Chunks("next");

        var response = await Request("ping"); // Request checks the id: a response for 7 would fail here
        Assert.True(response.TryGetProperty("result", out _));
        var (text, _) = await Call("jazari_ask", new { question = "next" });
        Assert.Contains("next", text);
    }

    [Fact]
    public async Task A_cancel_for_a_string_id_finds_its_request()
    {
        var (_, started, cancelled) = SlowAnswer();
        await Send(new { jsonrpc = "2.0", id = "call-1", method = "tools/call", @params = new { name = "jazari_ask", arguments = new { question = "slow" } } });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = "call-1" } });

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_cancel_for_nothing_in_flight_is_ignored()
    {
        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 99 } });
        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { } });

        Assert.True((await Request("ping")).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task A_cancelled_review_keeps_the_parts_it_finished()
    {
        // A file this big is two parts: the first is saved, the second is cut off.
        Write("Pay.cs", string.Concat(Enumerable.Repeat("// a line of about forty characters here\n", 300)));
        var second = new TaskCompletionSource();
        var calls = 0;
        _gateway.Complete = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) >= 2) { second.TrySetResult(); await Task.Delay(Timeout.Infinite, _cancelReview.Token); }
            return """{"findings":[]}""";
        };

        await Send(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "review_file", arguments = new { path = "Pay.cs" } } });
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 7 } });
        Assert.True((await Request("ping")).TryGetProperty("result", out _)); // answered after the cancel, so the cancel has been read
        await _cancelReview.CancelAsync(); // FakeGateway's Complete takes no token: this stands for the gateway call stopping
        await _toServer.Writer.CompleteAsync();
        await _running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await _receive.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))); // no response for the cancelled call
        Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(_reviews, "*.jsonl"))));
        Assert.Empty(Directory.GetFiles(_reviews, "*.md"));
    }

    readonly CancellationTokenSource _cancelReview = new();

    [Fact]
    public async Task Closing_stdin_stops_a_running_tool_and_ends_the_server()
    {
        var (_, started, cancelled) = SlowAnswer();
        await CallWithToken(7, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _toServer.Writer.CompleteAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await _receive.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // One at a time

    [Fact]
    public async Task Model_tools_run_one_at_a_time()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(1, null, "first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await CallWithToken(2, null, "second");
        await Task.Delay(100);
        Assert.Single(_gateway.Asked);

        finish.SetResult();
        Assert.Equal(1, (await Receive()).GetProperty("id").GetInt32());
        Assert.Equal(2, (await Receive()).GetProperty("id").GetInt32());
        Assert.Equal(2, _gateway.Asked.Count);
    }

    [Fact]
    public async Task A_law_book_search_doesnt_wait_for_a_running_answer()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(7, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var (text, isError) = await Call("law_book_search", new { query = "rate limiting" });

        Assert.False(isError);
        Assert.Contains("no passage", text);
        finish.SetResult();
        Assert.Equal(7, (await Receive()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_cancel_reaches_a_call_still_waiting_its_turn()
    {
        var (finish, started, _) = SlowAnswer();
        await CallWithToken(1, null, "first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await CallWithToken(2, null, "second");

        await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 2 } });
        Assert.True((await Request("ping")).TryGetProperty("result", out _));
        finish.SetResult();

        Assert.Equal(1, (await Receive()).GetProperty("id").GetInt32());
        Assert.Single(_gateway.Asked);
    }

    [Fact]
    public async Task Starting_the_servers_never_runs_twice_at_once()
    {
        var running = 0;
        var most = 0;
        _whilePreparing = async () =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
        };
        var docs = 0;
        _gateway.DocsCall = () => ++docs == 1 ? throw new GatewayException("gateway not reachable", null, null) : (true, []);

        await CallWithToken(1, null);
        await Send(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "law_book_search", arguments = new { query = "q" } } });
        await Receive();
        await Receive();

        Assert.Equal(1, most);
    }

    // The command

    [Fact]
    public void Mcp_is_a_command() => Assert.Equal("mcp", CliArgs.Parse(["mcp"]).Command);

    [Fact]
    public void Mcp_is_in_the_usage() => Assert.Contains("jazari mcp", CliArgs.Usage);

    [Fact]
    public void Mcp_takes_no_options() => Assert.Throws<ArgumentException>(() => CliArgs.Parse(["mcp", "--repo", "x"]));
}
