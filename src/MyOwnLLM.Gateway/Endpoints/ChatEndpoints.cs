using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using MyOwnLLM.Gateway.Checks;
using MyOwnLLM.Gateway.Deep;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;
using MyOwnLLM.Gateway.Queue;
using MyOwnLLM.Gateway.State;
using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.Endpoints;

/// <param name="Context">Code the client read for this question; goes before the conversation so it stays in the prompt cache.</param>
/// <param name="JsonSchema">Forces the answer into this JSON shape.</param>
/// <param name="Conventions">The project's AGENTS.md. Apart from Context, so a project that has one still gets the law book.</param>
/// <param name="Deep">Ask the smart model, with the compiler's check and one repair. /deep in the CLI.</param>
/// <param name="Lookup">The project's code the question names, which the CLI found; with this question only.</param>
public sealed record ChatRequest(List<ChatMessage> Messages, int? MaxTokens, bool? Stream, string? Context = null, string? JsonSchema = null, string? Conventions = null, bool? Deep = null, string? Lookup = null);
public sealed record InfillRequest(string Prefix, string Suffix, int? MaxTokens);
public sealed record StatusRequest(string ProjectPath);
public sealed record FeedbackRequest(string Id, bool Accepted);
public sealed record DocsRequest(string Question);

/// <summary>A request the gateway can't act on: answered 400 with this message.</summary>
public sealed class BadRequestException(string message) : Exception(message);

public static class ChatEndpoints
{
    // The model stops when it's done, so a higher cap costs nothing on a short answer; it only spares a code answer from being
    // cut off and asked again with "go on", which re-reads the whole prompt.
    const int DefaultChatTokens = 1024, MaxChatTokens = 2048, DefaultInfillTokens = 64, StatusTokens = 150;
    static readonly string Version = InteractionLog.Build.Split('+')[0]; // without the commit
    /// <summary>The name OpenAI-style clients (VS Code, Continue, Rider) list and send back; any name they send is accepted.</summary>
    const string ModelId = "jazari";
    static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(2);
    const string StatusQuestion = "Where are we left at? Answer with a short summary.";
    static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };
    static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/chat/completions", Chat);
        group.MapPost("/infill", Infill);
        group.MapPost("/status", Status);
        group.MapPost("/warm", Warm);
        group.MapPost("/feedback", Feedback);
        // /docs in the CLI: what the law book holds for a question, without asking the model. Available: whether there's a law book at all.
        group.MapPost("/docs", (DocsRequest request, LawBook book, GatewayOptions o) => string.IsNullOrWhiteSpace(request.Question)
            ? Results.BadRequest(new { error = "question is required" })
            : Results.Json(new { available = File.Exists(o.LawBookFile), passages = book.Search(request.Question).Select(p => new { title = p.Title, url = p.Url, text = p.Text }) }));
        group.MapGet("/models", () => Results.Json(new { @object = "list", data = new[] { new { id = ModelId, @object = "model", created = 0, owned_by = "jazari" } } }));
        group.MapGet("/info", async (GatewayOptions o, ILanes lanes, CancellationToken ct) => Results.Json(new { name = "Jazari", model = o.ModelName, version = Version, deep_available = await lanes.DeepAvailableAsync(ct) }));
        return group;
    }

    sealed record Services(ILlamaClient Llama, RequestQueue Queue, InteractionLog Log, HybridCache Cache, ModelLimits Limits, LawBook LawBook, ILanes Lanes);

    static Services Resolve(HttpContext http) => new(
        http.RequestServices.GetRequiredService<ILlamaClient>(), http.RequestServices.GetRequiredService<RequestQueue>(),
        http.RequestServices.GetRequiredService<InteractionLog>(), http.RequestServices.GetRequiredService<HybridCache>(),
        http.RequestServices.GetRequiredService<ModelLimits>(), http.RequestServices.GetRequiredService<LawBook>(), http.RequestServices.GetRequiredService<ILanes>());

    static Task Chat(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        if (request.Messages is not { Count: > 0 } || request.Messages.Any(m => m?.Content is null)) throw new BadRequestException("messages must hold the question, each with its content");
        if (request.JsonSchema is { } schema && !IsJson(schema)) throw new BadRequestException("json_schema isn't valid JSON");
        if (request.Deep == true && request.JsonSchema is null) { await DeepChat(http, s, request); return; }
        var lane = request.Deep == true ? Lane.Deep : Lane.Fast; // a /deep /edit: the smart model, and its JSON answer as usual
        var project = ProjectPath(http);
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultChatTokens, 1, MaxChatTokens);
        var question = request.Messages.LastOrDefault()?.Content ?? "";
        var history = request.Messages.SkipLast(1).ToList();
        // ponytail: questions on /read files, and /edit and /commit (JSON answers), don't search yet: passages beside the
        // user's own code are untested, and earlier work showed a small model describes the wrong thing when given extra
        // material. Add them when a test set covers them.
        var searches = request.Context is null && request.Lookup is null && request.JsonSchema is null && !(history.Count > 0 && LawBook.IsFollowUp(question));
        var contextTokens = await s.Limits.ContextTokensAsync(http.RequestAborted);
        var passages = searches ? await SearchAsync(http, s, question, request.Stream == true) : [];
        var prompt = PromptBuilder.Build(new PromptParts(request.Context, history, question, passages, request.Conventions, Lookup: request.Lookup), contextTokens, maxTokens, out var droppedMessages, out var usedPassages);
        var watch = Stopwatch.StartNew();

        string answer;
        long id;
        if (request.Stream == true)
        {
            if (!http.Response.HasStarted) http.Response.ContentType = "text/event-stream"; // a search already opened the stream
            AnswerStats? stats = null;
            var cutOff = false;
            string? model = null;
            try
            {
                answer = await s.Queue.EnqueueAsync(WorkKind.Chat, async ct =>
                {
                    var text = new StringBuilder();
                    model = await s.Lanes.EnsureAsync(Lane.Fast, stage => WriteEventAsync(http, new { stage, tokens = (int?)null }, ct), ct);
                    await foreach (var part in s.Llama.ChatStreamAsync(prompt, maxTokens, ct, model))
                    {
                        stats = part.Stats ?? stats;
                        cutOff |= part.CutOff;
                        if (part.Progress is { } p)
                            await WriteEventAsync(http, new { progress = new { total = p.Total, cached = p.Cached, processed = p.Processed } }, ct);
                        if (part.Text is not { } piece) continue;
                        text.Append(piece);
                        await WriteEventAsync(http, new { choices = new[] { new { index = 0, delta = new { content = piece } } } }, ct);
                    }
                    return text.ToString();
                }, http.RequestAborted);
            }
            catch (Exception e) when (http.Response.HasStarted && e is not OperationCanceledException)
            {
                // Too late for a status code: tell the client in-band, then end the stream cleanly.
                await WriteEventAsync(http, new { error = e is IOException ? "lost connection to the model server" : e.Message }, http.RequestAborted);
                await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
                return;
            }
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds, model));
            var choices = new[] { new { index = 0, delta = new { }, finish_reason = cutOff ? "length" : "stop" } };
            // sources only when the law book's passages went in, so the CLI names the page the answer drew on.
            var final = new Dictionary<string, object?> { ["id"] = $"ix-{id}", ["choices"] = choices, ["timings"] = stats, ["dropped_turns"] = droppedMessages / 2 };
            if (usedPassages) final["sources"] = passages.Select(p => new { title = p.Title, url = p.Url });
            if (await ChecksFor(http, answer) is { } checks) final["checks"] = checks;
            await WriteEventAsync(http, final, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        }
        else
        {
            // Only whole answers are cached: one cut off at max_tokens (or JSON that doesn't parse) would otherwise come
            // back unchanged on every retry for an hour.
            var key = CacheKey("chat", project, prompt, maxTokens, request.JsonSchema, lane);
            var cached = await s.Cache.GetOrCreateAsync(key, _ => ValueTask.FromResult<string?>(null), ProbeOnly, cancellationToken: http.RequestAborted);
            var cutOff = false;
            string? model = null;
            if (cached is null)
            {
                (answer, cutOff) = await s.Queue.EnqueueAsync(WorkKind.Chat, async t =>
                {
                    model = await s.Lanes.EnsureAsync(lane, null, t);
                    var result = await s.Llama.ChatWithEndAsync(prompt, maxTokens, t, request.JsonSchema, model);
                    if (lane is Lane.Deep) s.Lanes.HoldDeep();
                    return result;
                }, http.RequestAborted);
                if (!cutOff && (request.JsonSchema is null || IsJson(answer))) await s.Cache.SetAsync(key, answer, CacheFor, cancellationToken: http.RequestAborted);
            }
            else answer = cached;
            // "json": /edit, /review and /commit, whose questions start with the same long instructions, so eval/misses.py
            // mustn't read two of them as one question asked again.
            id = await s.Log.AddAsync(new Interaction(project, request.JsonSchema is null ? "chat" : "json", question, answer, watch.ElapsedMilliseconds, model));
            await http.Response.WriteAsJsonAsync(new
            {
                id = $"ix-{id}",
                @object = "chat.completion",
                choices = new[] { new { index = 0, message = new ChatMessage("assistant", answer), finish_reason = cutOff ? "length" : "stop" } },
            }, SnakeCase, http.RequestAborted);
        }
    });

    /// <summary>/deep: one queue item from loading the smart model to the checked answer, so nothing else asks the model in between.</summary>
    static async Task DeepChat(HttpContext http, Services s, ChatRequest request)
    {
        var project = ProjectPath(http);
        var question = request.Messages[^1].Content;
        var history = request.Messages.SkipLast(1).ToList();
        var passages = request.Lookup is not null || (history.Count > 0 && LawBook.IsFollowUp(question)) ? [] : await SearchAsync(http, s, question, request.Stream == true, DeepAnswer.PassageTokens);
        var parts = new PromptParts(request.Context, history, question, passages, request.Conventions, Lookup: request.Lookup);
        var checker = http.RequestServices.GetRequiredService<SnippetChecker>();
        var streaming = request.Stream == true;
        Task Stage(string stage, int? tokens) => streaming ? WriteEventAsync(http, new { stage, tokens }, http.RequestAborted) : Task.CompletedTask;
        if (streaming && !http.Response.HasStarted) http.Response.ContentType = "text/event-stream";
        var watch = Stopwatch.StartNew();
        DeepResult result;
        string? model = null;
        try
        {
            result = await s.Queue.EnqueueAsync(WorkKind.Chat, async ct =>
            {
                model = await s.Lanes.EnsureAsync(Lane.Deep, stage => Stage(stage, null), ct);
                Task Reading(PromptProgress p) => streaming ? WriteEventAsync(http, new { progress = new { total = p.Total, cached = p.Cached, processed = p.Processed } }, ct) : Task.CompletedTask;
                try { return await DeepAnswer.RunAsync(s.Llama, checker, model, parts, await s.Limits.ContextTokensAsync(ct), Stage, ct, Reading); }
                finally { s.Lanes.HoldDeep(); }
            }, http.RequestAborted);
        }
        catch (Exception e) when (http.Response.HasStarted && e is not OperationCanceledException)
        {
            await WriteEventAsync(http, new { error = e is IOException ? "lost connection to the model server" : e.Message }, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
            return;
        }
        var id = await s.Log.AddAsync(new Interaction(project, "deep", question, result.Text, watch.ElapsedMilliseconds, model));
        var finish = result.CutOff ? "length" : "stop";
        if (!streaming)
        {
            await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", @object = "chat.completion", check = result.CheckRow,
                choices = new[] { new { index = 0, message = new ChatMessage("assistant", result.Text), finish_reason = finish } } }, SnakeCase, http.RequestAborted);
            return;
        }
        await WriteEventAsync(http, new { choices = new[] { new { index = 0, delta = new { content = result.Text } } } }, http.RequestAborted);
        var final = new Dictionary<string, object?> { ["id"] = $"ix-{id}", ["choices"] = new[] { new { index = 0, delta = new { }, finish_reason = finish } }, ["timings"] = result.Stats,
            ["dropped_turns"] = result.DroppedMessages / 2, ["check"] = result.CheckRow };
        if (result.UsedPassages) final["sources"] = passages.Select(p => new { title = p.Title, url = p.Url });
        await WriteEventAsync(http, final, http.RequestAborted);
        await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
    }

    /// <summary>
    /// Reads the conversation so far into the prompt cache, built exactly as <see cref="Chat"/> builds it, so the next
    /// question only adds itself. Queued as chat: the question waits behind it and then finds everything cached.
    /// </summary>
    static Task Warm(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        if (request.Messages is null || request.Messages.Any(m => m?.Content is null)) throw new BadRequestException("messages is required, each with its content");
        http.RequestServices.GetRequiredService<SnippetChecker>().Warm();
        var prompt = PromptBuilder.Build(new PromptParts(request.Context, request.Messages, "", Conventions: request.Conventions), await s.Limits.ContextTokensAsync(http.RequestAborted), DefaultChatTokens);
        await s.Queue.EnqueueAsync(WorkKind.Chat, async t => await s.Llama.ChatAsync(prompt, 1, t, model: await s.Lanes.EnsureAsync(Lane.Fast, null, t)), http.RequestAborted);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    });

    static Task Infill(HttpContext http, InfillRequest request) => Guard(http, async s =>
    {
        if (request.Prefix is null || request.Suffix is null) throw new BadRequestException("prefix and suffix are required");
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultInfillTokens, 1, MaxChatTokens);
        var estimated = PromptBuilder.EstimateTokens(request.Prefix) + PromptBuilder.EstimateTokens(request.Suffix);
        var context = await s.Limits.ContextTokensAsync(http.RequestAborted);
        if (estimated > context - maxTokens) throw new InputTooLargeException(estimated, context - maxTokens);
        var watch = Stopwatch.StartNew();
        var content = await s.Cache.GetOrCreateAsync(CacheKey("infill", null, null, request.Prefix, request.Suffix, maxTokens),
            ct => new ValueTask<string>(s.Queue.EnqueueAsync(WorkKind.Completion, async t => await s.Llama.InfillAsync(request.Prefix, request.Suffix, maxTokens, t, await s.Lanes.EnsureAsync(Lane.Fast, null, t)), ct)),
            CacheFor, cancellationToken: http.RequestAborted);
        var id = await s.Log.AddAsync(new Interaction(null, "infill", request.Prefix + "<FILL>" + request.Suffix, content, watch.ElapsedMilliseconds));
        await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", content }, SnakeCase, http.RequestAborted);
    });

    static Task Status(HttpContext http, StatusRequest request) => Guard(http, async s =>
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath)) throw new BadRequestException("project_path is required");
        var git = await GitSummary.GetAsync(request.ProjectPath, http.RequestAborted);
        var prompt = PromptBuilder.Build(new PromptParts(git.Length > 0 ? git : "(not a git repository)", [], StatusQuestion), await s.Limits.ContextTokensAsync(http.RequestAborted), StatusTokens);
        var watch = Stopwatch.StartNew();
        var summary = await s.Queue.EnqueueAsync(WorkKind.Chat, async t => await s.Llama.ChatAsync(prompt, StatusTokens, t, model: await s.Lanes.EnsureAsync(Lane.Fast, null, t)), http.RequestAborted);
        var id = await s.Log.AddAsync(new Interaction(request.ProjectPath, "status", StatusQuestion, summary, watch.ElapsedMilliseconds));
        await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", summary }, SnakeCase, http.RequestAborted);
    });

    static async Task<IResult> Feedback(FeedbackRequest request, InteractionLog log) =>
        request.Id is { } raw && raw.StartsWith("ix-") && long.TryParse(raw[3..], out var id) && await log.SetFeedbackAsync(id, request.Accepted)
            ? Results.NoContent()
            : Results.NotFound();

    static async Task Guard(HttpContext http, Func<Services, Task> handle)
    {
        try { await handle(Resolve(http)); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { } // client went away
        catch (Exception e) when (!http.Response.HasStarted && StatusFor(e) is { } status)
        {
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        catch (Exception e) when (http.Response.HasStarted && http.Response.ContentType == "text/event-stream" && StatusFor(e) is not null)
        {
            // A stream already open (it said "searching the docs"): too late for a status code, so the error goes in-band.
            await WriteEventAsync(http, new { error = e.Message }, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        }
    }

    static int? StatusFor(Exception e) => e switch
    {
        LlamaUnavailableException => StatusCodes.Status503ServiceUnavailable,
        DeepModelUnavailableException => StatusCodes.Status503ServiceUnavailable,
        LlamaErrorException { Status: 400 } => StatusCodes.Status413PayloadTooLarge, // our requests are well-formed; 400 means context overflow
        LlamaErrorException { Status: 503 } => StatusCodes.Status503ServiceUnavailable, // still loading the model
        LlamaErrorException => StatusCodes.Status502BadGateway,
        QueueFullException => StatusCodes.Status429TooManyRequests,
        InputTooLargeException => StatusCodes.Status413PayloadTooLarge,
        SupersededException => StatusCodes.Status409Conflict,
        BadRequestException => StatusCodes.Status400BadRequest,
        _ => null,
    };

    /// <summary>What the compiler says about the answer's C#, for the final event; null when no block was checked. The answer is
    /// already on screen, so a check that fails or runs long only means no note under it.</summary>
    static async Task<object?> ChecksFor(HttpContext http, string answer)
    {
        if (!answer.Contains("```", StringComparison.Ordinal)) return null; // no code: don't load the reference packs for it
        try
        {
            var checker = http.RequestServices.GetRequiredService<SnippetChecker>();
            await Task.Run(checker.Load, http.RequestAborted);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            budget.CancelAfter(CheckBudget);
            var checks = await Task.Run(() => checker.Check(answer, budget.Token), budget.Token);
            return checks.Any(c => c.State != CheckState.NotChecked) ? checks.Select(c => new { block = c.Block, state = c.State.ToString().ToLowerInvariant(), errors = c.Errors, why = c.Why }).ToList() : null;
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested && e is not OutOfMemoryException) { return null; }
    }

    /// <summary>The law book's passages for a question. A streamed answer shows "Searching the docs" meanwhile, then "Thinking"
    /// again: the first search loads the search models and the book's vectors, a few seconds that looked frozen.</summary>
    static async Task<IReadOnlyList<Passage>> SearchAsync(HttpContext http, Services s, string question, bool streaming, int? maxTokens = null)
    {
        streaming &= s.LawBook.Available; // no book, nothing to wait for
        if (streaming)
        {
            http.Response.ContentType = "text/event-stream";
            await WriteEventAsync(http, new { stage = "searching the docs", tokens = (int?)null }, http.RequestAborted);
        }
        var passages = maxTokens is { } max ? s.LawBook.Search(question, max) : s.LawBook.Search(question);
        if (streaming) await WriteEventAsync(http, new { stage = "thinking", tokens = (int?)null }, http.RequestAborted);
        return passages;
    }

    static async Task WriteEventAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, SnakeCase)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>Looks in the cache without writing the factory's null into it.</summary>
    static readonly HybridCacheEntryOptions ProbeOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    static bool IsJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>The project folder, sent percent-encoded because HTTP header values must be ASCII.</summary>
    static string? ProjectPath(HttpContext http) =>
        http.Request.Headers["X-Project-Path"].FirstOrDefault() is { } raw ? Uri.UnescapeDataString(raw) : null;

    static string CacheKey(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
