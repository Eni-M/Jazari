using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyOwnLLM.Gateway.Auth;
using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Tests;

public sealed class FakeLlama : ILlamaClient
{
    public sealed record Call(string Kind, IReadOnlyList<ChatMessage> Messages, int MaxTokens);
    public ConcurrentQueue<Call> Calls { get; } = new();
    public Func<CancellationToken, Task<string>> Chat { get; set; } = _ => Task.FromResult("answer");
    public bool StreamDiesAfterFirstPart { get; set; }
    public string? LastSchema { get; private set; }

    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null)
    {
        Calls.Enqueue(new("chat", messages, maxTokens));
        LastSchema = jsonSchema;
        return Chat(ct);
    }

    public bool CutOff { get; set; }
    public async Task<(string Text, bool CutOff)> ChatWithEndAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null) =>
        (await ChatAsync(messages, maxTokens, ct, jsonSchema), CutOff);

    public async IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Enqueue(new("stream", messages, maxTokens));
        yield return new(null, Progress: new(100, 40, 100));
        foreach (var part in new[] { "a", "b" })
        {
            await Task.Yield();
            yield return new(part);
            if (StreamDiesAfterFirstPart) throw new HttpIOException(HttpRequestError.ResponseEnded);
        }
        yield return new(null, Stats: new(100, 40, 30.5, 2, 6.25));
    }

    public int? ContextTokens { get; set; }
    public Task<int?> ContextTokensAsync(CancellationToken ct) => Task.FromResult(ContextTokens);

    public Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct)
    {
        Calls.Enqueue(new("infill", [new("user", prefix + "|" + suffix)], maxTokens));
        return Task.FromResult("filled");
    }
}

sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(Lines);
    public void Dispose() { }

    sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + exception);
    }
}

public class EndpointTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-gw").FullName;
    readonly FakeLlama _llama = new();
    readonly CapturingLoggerProvider _logs = new();
    readonly WebApplicationFactory<Program> _factory;
    readonly HttpClient _client;
    readonly string _key;

    public EndpointTests()
    {
        _key = new ApiKeyStore(Path.Combine(_dir, "keys.json")).AddKey();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:DataDir", _dir);
            b.ConfigureLogging(l => l.AddProvider(_logs));
            b.ConfigureTestServices(s => s.AddSingleton<ILlamaClient>(_llama));
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _key);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static object ChatBody(string question, bool stream = false) =>
        new { messages = new[] { new { role = "user", content = question } }, stream };

    static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer mollm_wrong")]
    [InlineData("Basic abc")]
    public async Task Requests_without_valid_bearer_key_get_401(string? header)
    {
        var client = _factory.CreateClient();
        if (header is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("mollm_wrong") || l.Contains(_key));
    }

    [Fact]
    public async Task Chat_buffered_returns_openai_shape_with_ix_id_and_logs_interaction()
    {
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi"));

        response.EnsureSuccessStatusCode();
        var json = await Json(response);
        Assert.StartsWith("ix-", json.GetProperty("id").GetString());
        Assert.Equal("answer", json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        var feedback = await _client.PostAsJsonAsync("/v1/feedback", new { id = json.GetProperty("id").GetString(), accepted = true });
        Assert.Equal(HttpStatusCode.NoContent, feedback.StatusCode);
    }

    [Fact]
    public async Task Chat_stream_returns_sse_and_done()
    {
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi", stream: true));

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"a\"", body);
        Assert.Contains("\"content\":\"b\"", body);
        Assert.Contains("\"id\":\"ix-", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    [Fact]
    public async Task Chat_stream_passes_on_reading_progress_and_the_answers_numbers()
    {
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi", stream: true));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: {\"progress\":{\"total\":100,\"cached\":40,\"processed\":100}}", body);
        Assert.Contains("\"timings\":{\"prompt_tokens\":100,\"cached_tokens\":40,\"prompt_per_second\":30.5,\"answer_tokens\":2,\"answer_per_second\":6.25}", body);
    }

    [Fact]
    public async Task Stream_final_event_says_how_many_turns_were_cut()
    {
        _llama.ContextTokens = 2048;
        var messages = Enumerable.Range(0, 10).SelectMany(i => new[]
        {
            new { role = "user", content = $"q{i} " + new string('u', 400) },
            new { role = "assistant", content = $"a{i} " + new string('a', 400) },
        }).Append(new { role = "user", content = "QUESTION" }).ToArray();

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", new { messages, stream = true });

        var final = (await response.Content.ReadAsStringAsync()).Split("\n").Last(l => l.Contains("\"id\":"));
        var dropped = JsonDocument.Parse(final["data: ".Length..]).RootElement.GetProperty("dropped_turns").GetInt32();
        Assert.True(dropped > 0);
    }

    [Fact]
    public async Task Warm_reads_the_conversation_and_context_without_answering()
    {
        var response = await _client.PostAsJsonAsync("/v1/warm", new { messages = new[] { new { role = "user", content = "earlier" } }, context = "class A;" });

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        var call = Assert.Single(_llama.Calls);
        Assert.Equal(1, call.MaxTokens);
        Assert.Contains(call.Messages, m => m.Content.Contains("class A;"));
        Assert.Equal(new ChatMessage("user", "earlier"), call.Messages[^2]);
        Assert.Equal(new ChatMessage("user", ""), call.Messages[^1]);
    }

    [Fact]
    public void Llama_chunks_with_progress_text_or_timings_are_all_read()
    {
        var progress = LlamaClient.ReadChunk("""{"choices":[],"prompt_progress":{"total":5000,"cache":1200,"processed":2400,"time_ms":90000}}""");
        var text = LlamaClient.ReadChunk("""{"choices":[{"index":0,"delta":{"content":"hi"}}]}""");
        var last = LlamaClient.ReadChunk("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"timings":{"cache_n":1200,"prompt_n":3800,"prompt_per_second":28.1,"predicted_n":90,"predicted_per_second":6.2}}""");

        Assert.Equal(new PromptProgress(5000, 1200, 2400), Assert.Single(progress).Progress);
        Assert.Equal("hi", Assert.Single(text).Text);
        Assert.Equal(new AnswerStats(5000, 1200, 28.1, 90, 6.2), Assert.Single(last).Stats);
        Assert.True(LlamaClient.ReadChunk("""{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""").Single().CutOff);
    }

    [Fact]
    public async Task Stream_failing_midway_sends_error_event_then_done()
    {
        _llama.StreamDiesAfterFirstPart = true;

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi", stream: true));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"a\"", body);
        Assert.DoesNotContain("\"content\":\"b\"", body);
        Assert.Contains("data: {\"error\":\"lost connection to the model server\"}", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    [Fact]
    public async Task Identical_chat_request_is_served_from_cache()
    {
        await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("same question"));
        var second = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("same question"));

        second.EnsureSuccessStatusCode();
        Assert.Single(_llama.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_sends_the_note_only_inside_a_repository(bool repo)
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, repo ? "a-repo" : "a-folder")).FullName;
        if (repo) Directory.CreateDirectory(Path.Combine(project, ".git"));
        await _factory.Services.GetRequiredService<MyOwnLLM.Gateway.State.ProjectStateStore>().SetAsync(project, "NOTE: invented task");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(ChatBody("hello")) };
        request.Headers.Add("X-Project-Path", Uri.EscapeDataString(project));

        (await _client.SendAsync(request)).EnsureSuccessStatusCode();

        var sent = string.Join("\n", Assert.Single(_llama.Calls).Messages.Select(m => m.Content));
        Assert.Equal(repo, sent.Contains("NOTE: invented task"));
    }

    string NewRepo()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, "repo-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        return project;
    }

    Task SetNote(string project, string note) =>
        _factory.Services.GetRequiredService<MyOwnLLM.Gateway.State.ProjectStateStore>().SetAsync(project, note);

    /// <summary>Posts <paramref name="turns"/> as the conversation, in the project, and returns what reached the model.</summary>
    async Task<IReadOnlyList<ChatMessage>> InProject(string path, string project, params string[] turns)
    {
        var messages = turns.Select((t, i) => new { role = i % 2 == 0 ? "user" : "assistant", content = t }).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { messages }) };
        request.Headers.Add("X-Project-Path", Uri.EscapeDataString(project));
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
        return _llama.Calls.Last().Messages;
    }

    [Fact]
    public async Task A_note_updated_mid_conversation_stays_out_of_the_prompt()
    {
        var project = NewRepo();
        await SetNote(project, "NOTE A");
        await InProject("/v1/chat/completions", project, "first?");
        await SetNote(project, "NOTE B");

        var sent = await InProject("/v1/chat/completions", project, "first?", "answer", "second?");

        Assert.Contains("NOTE A", sent[1].Content);
        Assert.DoesNotContain("NOTE B", sent[1].Content);
    }

    [Fact]
    public async Task A_new_conversation_uses_the_latest_note()
    {
        var project = NewRepo();
        await SetNote(project, "NOTE A");
        await InProject("/v1/chat/completions", project, "first?");
        await SetNote(project, "NOTE B");
        await InProject("/v1/chat/completions", project, "first?", "answer", "second?");

        var sent = await InProject("/v1/chat/completions", project, "fresh start?");

        Assert.Contains("NOTE B", sent[1].Content);
    }

    [Fact]
    public async Task After_a_restart_the_conversation_takes_the_current_note()
    {
        var project = NewRepo();
        await SetNote(project, "NOTE NOW");

        var sent = await InProject("/v1/chat/completions", project, "first?", "answer", "second?");

        Assert.Contains("NOTE NOW", sent[1].Content);
    }

    [Fact]
    public async Task Warm_uses_the_same_note_as_the_conversation()
    {
        var project = NewRepo();
        await SetNote(project, "NOTE A");
        await InProject("/v1/chat/completions", project, "first?");
        await SetNote(project, "NOTE B");

        var sent = await InProject("/v1/warm", project, "first?", "answer");

        Assert.Contains("NOTE A", sent[1].Content);
    }

    [Fact]
    public async Task Status_uses_state_note_and_caps_tokens_at_150()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, "proj")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        await _factory.Services.GetRequiredService<MyOwnLLM.Gateway.State.ProjectStateStore>().SetAsync(project, "NOTE: building the queue");

        var response = await _client.PostAsJsonAsync("/v1/status", new { project_path = project });

        response.EnsureSuccessStatusCode();
        var json = await Json(response);
        Assert.Equal("answer", json.GetProperty("summary").GetString());
        var call = Assert.Single(_llama.Calls);
        Assert.Equal(150, call.MaxTokens);
        Assert.Contains("NOTE: building the queue", call.Messages[1].Content);
    }

    [Fact]
    public async Task Feedback_known_id_204_unknown_404()
    {
        var chat = await Json(await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("hi")));

        var known = await _client.PostAsJsonAsync("/v1/feedback", new { id = chat.GetProperty("id").GetString(), accepted = false });
        var unknown = await _client.PostAsJsonAsync("/v1/feedback", new { id = "ix-99999", accepted = true });
        var garbage = await _client.PostAsJsonAsync("/v1/feedback", new { id = "nope", accepted = true });

        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
    }

    [Fact]
    public async Task Llama_down_returns_503_and_next_request_succeeds()
    {
        _llama.Chat = _ => throw new LlamaUnavailableException("http://127.0.0.1:8080/", new HttpRequestException());

        var down = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("first"));
        _llama.Chat = _ => Task.FromResult("back");
        var up = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("second"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("model server not reachable at http://127.0.0.1:8080/", (await Json(down)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
    }

    [Fact]
    public async Task Oversized_input_returns_413()
    {
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody(new string('x', 70_000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.StartsWith("input too large", (await Json(response)).GetProperty("error").GetString());
        Assert.Empty(_llama.Calls);
    }

    [Theory]
    [InlineData(400, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(503, HttpStatusCode.ServiceUnavailable)]
    [InlineData(500, HttpStatusCode.BadGateway)]
    public async Task Llama_error_status_maps_to_gateway_status_with_message(int llamaStatus, HttpStatusCode expected)
    {
        _llama.Chat = _ => throw new LlamaErrorException(llamaStatus, "llama says no");

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("too big for real"));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("llama says no", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cut_off_answer_says_so_and_is_not_cached()
    {
        _llama.CutOff = true;

        var first = await Json(await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("long please")));
        await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("long please"));

        Assert.Equal("length", first.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(2, _llama.Calls.Count(c => c.Kind == "chat"));
    }

    [Fact]
    public async Task Failed_request_is_not_cached()
    {
        _llama.Chat = _ => throw new LlamaUnavailableException("http://127.0.0.1:8080/", new HttpRequestException());
        var first = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("same"));
        _llama.Chat = _ => Task.FromResult("works now");

        var second = await _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("same"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("works now", (await Json(second)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Info_returns_name_model_version()
    {
        var response = await _client.GetAsync("/v1/info");

        response.EnsureSuccessStatusCode();
        var json = await Json(response);
        Assert.Equal("Jazari", json.GetProperty("name").GetString());
        Assert.Equal("Granite 4.0-H Tiny", json.GetProperty("model").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Info_requires_key()
    {
        var response = await _factory.CreateClient().GetAsync("/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Infill_returns_content_with_id()
    {
        var response = await _client.PostAsJsonAsync("/v1/infill", new { prefix = "int x = ", suffix = ";" });

        response.EnsureSuccessStatusCode();
        var json = await Json(response);
        Assert.Equal("filled", json.GetProperty("content").GetString());
        Assert.Equal(64, Assert.Single(_llama.Calls).MaxTokens);
    }

    [Fact]
    public async Task Client_abort_cancels_llama_call()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        _llama.Chat = async ct =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return "never";
        };
        using var cts = new CancellationTokenSource();

        var request = _client.PostAsJsonAsync("/v1/chat/completions", ChatBody("slow"), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
