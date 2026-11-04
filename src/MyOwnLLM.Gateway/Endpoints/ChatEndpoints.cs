using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;
using MyOwnLLM.Gateway.Queue;
using MyOwnLLM.Gateway.State;
using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.Endpoints;

/// <param name="Context">Code the client read for this question; goes before the conversation so it stays in the prompt cache.</param>
/// <param name="JsonSchema">Forces the answer into this JSON shape.</param>
/// <param name="Conventions">The project's AGENTS.md. Apart from Context, so a project that has one still gets the law book.</param>
public sealed record ChatRequest(List<ChatMessage> Messages, int? MaxTokens, bool? Stream, string? Context = null, string? JsonSchema = null, string? Conventions = null);
public sealed record InfillRequest(string Prefix, string Suffix, int? MaxTokens);
public sealed record StatusRequest(string ProjectPath);
public sealed record FeedbackRequest(string Id, bool Accepted);
public sealed record DocsRequest(string Question);

/// <summary>A request the gateway can't act on: answered 400 with this message.</summary>
public sealed class BadRequestException(string message) : Exception(message);

public static class ChatEndpoints
{
    const int DefaultChatTokens = 512, MaxChatTokens = 1024, DefaultInfillTokens = 64, StatusTokens = 150;
    static readonly string Version = typeof(ChatEndpoints).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute v, ..] ? v.InformationalVersion.Split('+')[0] : "0.0.0";
    /// <summary>The name OpenAI-style clients (VS Code, Continue, Rider) list and send back; any name they send is accepted.</summary>
    const string ModelId = "jazari";
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
        group.MapGet("/info", (GatewayOptions o) => Results.Json(new { name = "Jazari", model = o.ModelName, version = Version }));
        return group;
    }

    sealed record Services(ILlamaClient Llama, RequestQueue Queue, ProjectStateStore State, InteractionLog Log, HybridCache Cache, ModelLimits Limits, ConversationNotes Notes, LawBook LawBook);

    static Services Resolve(HttpContext http) => new(
        http.RequestServices.GetRequiredService<ILlamaClient>(), http.RequestServices.GetRequiredService<RequestQueue>(),
        http.RequestServices.GetRequiredService<ProjectStateStore>(), http.RequestServices.GetRequiredService<InteractionLog>(),
        http.RequestServices.GetRequiredService<HybridCache>(), http.RequestServices.GetRequiredService<ModelLimits>(),
        http.RequestServices.GetRequiredService<ConversationNotes>(), http.RequestServices.GetRequiredService<LawBook>());

    static async Task<string?> NoteFor(Services s, string? project) =>
        project is not null && GitSummary.IsRepo(project) ? await s.State.GetAsync(project) : null;

    /// <summary>The note as the conversation began with it, so an update mid-conversation doesn't force a re-read (see <see cref="ConversationNotes"/>).</summary>
    static async Task<string?> ConversationNoteFor(Services s, string? project, bool newConversation) =>
        project is null ? null : s.Notes.For(project, newConversation, await NoteFor(s, project));

    static Task Chat(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        if (request.Messages is not { Count: > 0 } || request.Messages.Any(m => m?.Content is null)) throw new BadRequestException("messages must hold the question, each with its content");
        if (request.JsonSchema is { } schema && !IsJson(schema)) throw new BadRequestException("json_schema isn't valid JSON");
        var project = ProjectPath(http);
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultChatTokens, 1, MaxChatTokens);
        var question = request.Messages.LastOrDefault()?.Content ?? "";
        var history = request.Messages.SkipLast(1).ToList();
        var note = await ConversationNoteFor(s, project, history.Count == 0);
        // ponytail: questions on /read files, and /edit and /commit (JSON answers), don't search yet: passages beside the
        // user's own code are untested, and earlier work showed a small model describes the wrong thing when given extra
        // material. Add them when a test set covers them.
        var passages = request.Context is null && request.JsonSchema is null ? s.LawBook.Search(question) : [];
        var prompt = PromptBuilder.Build(new PromptParts(note, request.Context, history, question, passages, request.Conventions), await s.Limits.ContextTokensAsync(http.RequestAborted), maxTokens, out var droppedMessages, out var usedPassages);
        var watch = Stopwatch.StartNew();

        string answer;
        long id;
        if (request.Stream == true)
        {
            http.Response.ContentType = "text/event-stream";
            AnswerStats? stats = null;
            var cutOff = false;
            try
            {
                answer = await s.Queue.EnqueueAsync(WorkKind.Chat, async ct =>
                {
                    var text = new StringBuilder();
                    await foreach (var part in s.Llama.ChatStreamAsync(prompt, maxTokens, ct))
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
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds));
            var choices = new[] { new { index = 0, delta = new { }, finish_reason = cutOff ? "length" : "stop" } };
            // sources only when the law book's passages went in, so the CLI names the page the answer drew on.
            object final = usedPassages
                ? new { id = $"ix-{id}", choices, timings = stats, dropped_turns = droppedMessages / 2, sources = passages.Select(p => new { title = p.Title, url = p.Url }) }
                : new { id = $"ix-{id}", choices, timings = stats, dropped_turns = droppedMessages / 2 };
            await WriteEventAsync(http, final, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        }
        else
        {
            // Only whole answers are cached: one cut off at max_tokens (or JSON that doesn't parse) would otherwise come
            // back unchanged on every retry for an hour.
            var key = CacheKey("chat", project, note, prompt, maxTokens, request.JsonSchema);
            var cached = await s.Cache.GetOrCreateAsync(key, _ => ValueTask.FromResult<string?>(null), ProbeOnly, cancellationToken: http.RequestAborted);
            var cutOff = false;
            if (cached is null)
            {
                (answer, cutOff) = await s.Queue.EnqueueAsync(WorkKind.Chat, t => s.Llama.ChatWithEndAsync(prompt, maxTokens, t, request.JsonSchema), http.RequestAborted);
                if (!cutOff && (request.JsonSchema is null || IsJson(answer))) await s.Cache.SetAsync(key, answer, CacheFor, cancellationToken: http.RequestAborted);
            }
            else answer = cached;
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds));
            await http.Response.WriteAsJsonAsync(new
            {
                id = $"ix-{id}",
                @object = "chat.completion",
                choices = new[] { new { index = 0, message = new ChatMessage("assistant", answer), finish_reason = cutOff ? "length" : "stop" } },
            }, SnakeCase, http.RequestAborted);
        }

        if (project is not null) http.RequestServices.GetRequiredService<StateNoteUpdater>().Record(project, question, answer);
    });

    /// <summary>
    /// Reads the conversation so far into the prompt cache, built exactly as <see cref="Chat"/> builds it, so the next
    /// question only adds itself. Queued as chat: the question waits behind it and then finds everything cached.
    /// </summary>
    static Task Warm(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        if (request.Messages is null || request.Messages.Any(m => m?.Content is null)) throw new BadRequestException("messages is required, each with its content");
        var project = ProjectPath(http);
        var note = await ConversationNoteFor(s, project, request.Messages.Count == 0);
        var prompt = PromptBuilder.Build(new PromptParts(note, request.Context, request.Messages, "", Conventions: request.Conventions), await s.Limits.ContextTokensAsync(http.RequestAborted), DefaultChatTokens);
        await s.Queue.EnqueueAsync(WorkKind.Chat, t => s.Llama.ChatAsync(prompt, 1, t), http.RequestAborted);
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
            ct => new ValueTask<string>(s.Queue.EnqueueAsync(WorkKind.Completion, t => s.Llama.InfillAsync(request.Prefix, request.Suffix, maxTokens, t), ct)),
            CacheFor, cancellationToken: http.RequestAborted);
        var id = await s.Log.AddAsync(new Interaction(null, "infill", request.Prefix + "<FILL>" + request.Suffix, content, watch.ElapsedMilliseconds));
        await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", content }, SnakeCase, http.RequestAborted);
    });

    static Task Status(HttpContext http, StatusRequest request) => Guard(http, async s =>
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath)) throw new BadRequestException("project_path is required");
        var note = await NoteFor(s, request.ProjectPath);
        var git = await GitSummary.GetAsync(request.ProjectPath, http.RequestAborted);
        var prompt = PromptBuilder.Build(new PromptParts(note ?? "(no notes yet)", git, [], StatusQuestion), await s.Limits.ContextTokensAsync(http.RequestAborted), StatusTokens);
        var watch = Stopwatch.StartNew();
        var summary = await s.Queue.EnqueueAsync(WorkKind.Chat, t => s.Llama.ChatAsync(prompt, StatusTokens, t), http.RequestAborted);
        var id = await s.Log.AddAsync(new Interaction(request.ProjectPath, "status", StatusQuestion, summary, watch.ElapsedMilliseconds));
        await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", summary }, SnakeCase, http.RequestAborted);
    });

    static async Task<IResult> Feedback(FeedbackRequest request, InteractionLog log) =>
        request.Id.StartsWith("ix-") && long.TryParse(request.Id[3..], out var id) && await log.SetFeedbackAsync(id, request.Accepted)
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
    }

    static int? StatusFor(Exception e) => e switch
    {
        LlamaUnavailableException => StatusCodes.Status503ServiceUnavailable,
        LlamaErrorException { Status: 400 } => StatusCodes.Status413PayloadTooLarge, // our requests are well-formed; 400 means context overflow
        LlamaErrorException { Status: 503 } => StatusCodes.Status503ServiceUnavailable, // still loading the model
        LlamaErrorException => StatusCodes.Status502BadGateway,
        QueueFullException => StatusCodes.Status429TooManyRequests,
        InputTooLargeException => StatusCodes.Status413PayloadTooLarge,
        SupersededException => StatusCodes.Status409Conflict,
        BadRequestException => StatusCodes.Status400BadRequest,
        _ => null,
    };

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
