using System.Text.Json;
using MyOwnLLM.Gateway;
using MyOwnLLM.Gateway.Auth;
using MyOwnLLM.Gateway.Endpoints;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;
using MyOwnLLM.Gateway.State;
using MyOwnLLM.Gateway.Storage;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
Directory.CreateDirectory(options.DataDir);

if (args is ["add-key", ..])
{
    Console.WriteLine(new ApiKeyStore(options.KeyFile).AddKey());
    Console.WriteLine("Store this key now; only its hash is kept. Set it as MYOWNLLM_KEY.");
    return;
}

if (args is ["search-law", var lawQuestion, ..]) // for lawbook/try_dev.py: what the law book sends for a question, as JSON
{
    Console.WriteLine(JsonSerializer.Serialize(new LawBook(options).Search(lawQuestion), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    return;
}

builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ApiKeyStore(options.KeyFile));
builder.Services.AddSingleton(new LocalDb(options.DbFile));
builder.Services.AddSingleton<ProjectStateStore>();
builder.Services.AddSingleton<ConversationNotes>();
builder.Services.AddSingleton<InteractionLog>();
builder.Services.AddSingleton<StateNoteUpdater>();
builder.Services.AddSingleton<RequestQueue>();
builder.Services.AddSingleton<ModelLimits>();
builder.Services.AddSingleton<LawBook>();
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddHybridCache();
builder.Services.AddHttpClient<ILlamaClient, LlamaClient>(http =>
{
    http.BaseAddress = new Uri(options.LlamaUrl);
    http.Timeout = options.RequestTimeout;
});

var app = builder.Build();
app.MapGroup("/v1").AddEndpointFilter<ApiKeyFilter>().MapChatEndpoints();
app.Run();

public partial class Program;
