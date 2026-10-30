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
public sealed record ChatRequest(List<ChatMessage> Messages, int? MaxTokens, bool? Stream, string? Context = null, string? JsonSchema = null);
public sealed record InfillRequest(string Prefix, string Suffix, int? MaxTokens);
public sealed record StatusRequest(string ProjectPath);
public sealed record FeedbackRequest(string Id, bool Accepted);

public static class ChatEndpoints
{
    const int DefaultChatTokens = 512, MaxChatTokens = 1024, DefaultInfillTokens = 64, StatusTokens = 150;
    static readonly string Version = typeof(ChatEndpoints).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute v, ..] ? v.InformationalVersion.Split('+')[0] : "0.0.0";
    const string StatusQuestion = "Where are we left at? Answer with a short summary.";
    static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };
    static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/chat/completions", Chat);
        group.MapPost("/infill", Infill);
        group.MapPost("/status", Status);
        group.MapPost("/feedback", Feedback);
        group.MapGet("/info", (GatewayOptions o) => Results.Json(new { name = "Jazari", model = o.ModelName, version = Version }));
        return group;
    }

    sealed record Services(ILlamaClient Llama, RequestQueue Queue, ProjectStateStore State, InteractionLog Log, HybridCache Cache, GatewayOptions Options);

    static Services Resolve(HttpContext http) => new(
        http.RequestServices.GetRequiredService<ILlamaClient>(), http.RequestServices.GetRequiredService<RequestQueue>(),
        http.RequestServices.GetRequiredService<ProjectStateStore>(), http.RequestServices.GetRequiredService<InteractionLog>(),
        http.RequestServices.GetRequiredService<HybridCache>(), http.RequestServices.GetRequiredService<GatewayOptions>());

    static Task Chat(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        var project = http.Request.Headers["X-Project-Path"].FirstOrDefault();
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultChatTokens, 1, MaxChatTokens);
        var question = request.Messages.LastOrDefault()?.Content ?? "";
        var note = project is null ? null : await s.State.GetAsync(project);
        var prompt = PromptBuilder.Build(new PromptParts(note, request.Context, request.Messages.SkipLast(1).ToList(), question), s.Options.ContextTokens, maxTokens);
        var watch = Stopwatch.StartNew();

        string answer;
        long id;
        if (request.Stream == true)
        {
            http.Response.ContentType = "text/event-stream";
            AnswerStats? stats = null;
            try
            {
                answer = await s.Queue.EnqueueAsync(WorkKind.Chat, async ct =>
                {
                    var text = new StringBuilder();
                    await foreach (var part in s.Llama.ChatStreamAsync(prompt, maxTokens, ct))
                    {
                        stats = part.Stats ?? stats;
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
            await WriteEventAsync(http, new { id = $"ix-{id}", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, timings = stats }, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        }
        else
        {
            answer = await s.Cache.GetOrCreateAsync(CacheKey("chat", project, note, prompt, maxTokens, request.JsonSchema),
                ct => new ValueTask<string>(s.Queue.EnqueueAsync(WorkKind.Chat, t => s.Llama.ChatAsync(prompt, maxTokens, t, request.JsonSchema), ct)),
                CacheFor, cancellationToken: http.RequestAborted);
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds));
            await http.Response.WriteAsJsonAsync(new
            {
                id = $"ix-{id}",
                @object = "chat.completion",
                choices = new[] { new { index = 0, message = new ChatMessage("assistant", answer), finish_reason = "stop" } },
            }, SnakeCase, http.RequestAborted);
        }

        if (project is not null) http.RequestServices.GetRequiredService<StateNoteUpdater>().Record(project, question, answer);
    });

    static Task Infill(HttpContext http, InfillRequest request) => Guard(http, async s =>
    {
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultInfillTokens, 1, MaxChatTokens);
        var estimated = PromptBuilder.EstimateTokens(request.Prefix) + PromptBuilder.EstimateTokens(request.Suffix);
        if (estimated > s.Options.ContextTokens - maxTokens) throw new InputTooLargeException(estimated, s.Options.ContextTokens - maxTokens);
        var watch = Stopwatch.StartNew();
        var content = await s.Cache.GetOrCreateAsync(CacheKey("infill", null, null, request.Prefix, request.Suffix, maxTokens),
            ct => new ValueTask<string>(s.Queue.EnqueueAsync(WorkKind.Completion, t => s.Llama.InfillAsync(request.Prefix, request.Suffix, maxTokens, t), ct)),
            CacheFor, cancellationToken: http.RequestAborted);
        var id = await s.Log.AddAsync(new Interaction(null, "infill", request.Prefix + "<FILL>" + request.Suffix, content, watch.ElapsedMilliseconds));
        await http.Response.WriteAsJsonAsync(new { id = $"ix-{id}", content }, SnakeCase, http.RequestAborted);
    });

    static Task Status(HttpContext http, StatusRequest request) => Guard(http, async s =>
    {
        var note = await s.State.GetAsync(request.ProjectPath);
        var git = await GitSummary.GetAsync(request.ProjectPath, http.RequestAborted);
        var prompt = PromptBuilder.Build(new PromptParts(note ?? "(no notes yet)", git, [], StatusQuestion), s.Options.ContextTokens, StatusTokens);
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
        _ => null,
    };

    static async Task WriteEventAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, SnakeCase)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    static string CacheKey(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
