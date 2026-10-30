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

public sealed record ChatRequest(List<ChatMessage> Messages, int? MaxTokens, bool? Stream);
public sealed record InfillRequest(string Prefix, string Suffix, int? MaxTokens);
public sealed record StatusRequest(string ProjectPath);
public sealed record FeedbackRequest(string Id, bool Accepted);

public static class ChatEndpoints
{
    const int DefaultChatTokens = 512, MaxChatTokens = 1024, DefaultInfillTokens = 64, StatusTokens = 150, StateNoteTokens = 220;
    const string StatusQuestion = "Where are we left at? Answer with a short summary.";
    static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };
    static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/chat/completions", Chat);
        group.MapPost("/infill", Infill);
        group.MapPost("/status", Status);
        group.MapPost("/feedback", Feedback);
        return group;
    }

    sealed record Services(ILlamaClient Llama, RequestQueue Queue, ProjectStateStore State, InteractionLog Log, HybridCache Cache, GatewayOptions Options, ILogger<RequestQueue> Logger);

    static Services Resolve(HttpContext http) => new(
        http.RequestServices.GetRequiredService<ILlamaClient>(), http.RequestServices.GetRequiredService<RequestQueue>(),
        http.RequestServices.GetRequiredService<ProjectStateStore>(), http.RequestServices.GetRequiredService<InteractionLog>(),
        http.RequestServices.GetRequiredService<HybridCache>(), http.RequestServices.GetRequiredService<GatewayOptions>(),
        http.RequestServices.GetRequiredService<ILogger<RequestQueue>>());

    static Task Chat(HttpContext http, ChatRequest request) => Guard(http, async s =>
    {
        var project = http.Request.Headers["X-Project-Path"].FirstOrDefault();
        var maxTokens = Math.Clamp(request.MaxTokens ?? DefaultChatTokens, 1, MaxChatTokens);
        var question = request.Messages.LastOrDefault()?.Content ?? "";
        var note = project is null ? null : await s.State.GetAsync(project);
        var prompt = PromptBuilder.Build(new PromptParts(note, null, request.Messages.SkipLast(1).ToList(), question), s.Options.ContextTokens, maxTokens);
        var watch = Stopwatch.StartNew();

        string answer;
        long id;
        if (request.Stream == true)
        {
            http.Response.ContentType = "text/event-stream";
            answer = await s.Queue.EnqueueAsync(WorkKind.Chat, async ct =>
            {
                var text = new StringBuilder();
                await foreach (var part in s.Llama.ChatStreamAsync(prompt, maxTokens, ct))
                {
                    text.Append(part);
                    await WriteEventAsync(http, new { choices = new[] { new { index = 0, delta = new { content = part } } } }, ct);
                }
                return text.ToString();
            }, http.RequestAborted);
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds));
            await WriteEventAsync(http, new { id = $"ix-{id}", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }, http.RequestAborted);
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
        }
        else
        {
            answer = await s.Cache.GetOrCreateAsync(CacheKey("chat", project, note, prompt, maxTokens),
                ct => new ValueTask<string>(s.Queue.EnqueueAsync(WorkKind.Chat, t => s.Llama.ChatAsync(prompt, maxTokens, t), ct)),
                CacheFor, cancellationToken: http.RequestAborted);
            id = await s.Log.AddAsync(new Interaction(project, "chat", question, answer, watch.ElapsedMilliseconds));
            await http.Response.WriteAsJsonAsync(new
            {
                id = $"ix-{id}",
                @object = "chat.completion",
                choices = new[] { new { index = 0, message = new ChatMessage("assistant", answer), finish_reason = "stop" } },
            }, SnakeCase, http.RequestAborted);
        }

        if (project is not null) UpdateStateNoteLater(s, project, note, question, answer);
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

    /// <summary>Rewrites the project's state note in idle time, after the user already has the answer.</summary>
    static void UpdateStateNoteLater(Services s, string project, string? oldNote, string question, string answer) =>
        _ = s.Queue.EnqueueAsync(WorkKind.Background, async ct =>
        {
            var git = await GitSummary.GetAsync(project, ct);
            List<ChatMessage> prompt =
            [
                new("system", "You maintain a short project state note for a developer."),
                new("user", $"Old note:\n{oldNote ?? "(none)"}\n\nLatest exchange:\nQ: {question}\nA: {answer}\n\n{git}\n\n" +
                            "Write the updated project state note: current task, recent changes, open problems, last decisions. At most 150 words."),
            ];
            await s.State.SetAsync(project, await s.Llama.ChatAsync(prompt, StateNoteTokens, ct));
            return 0;
        }, CancellationToken.None).ContinueWith(t => s.Logger.LogWarning(t.Exception, "State note update failed for {Project}", project),
            TaskContinuationOptions.OnlyOnFaulted);

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
