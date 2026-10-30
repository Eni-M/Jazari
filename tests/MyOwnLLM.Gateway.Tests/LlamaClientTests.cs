using System.Net;
using System.Text;
using System.Text.Json;
using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Tests;

public class LlamaClientTests
{
    static readonly ChatMessage[] Hello = [new("user", "hello")];

    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request, ct);
        }
    }

    static (LlamaClient, FakeHandler) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") };
        return (new LlamaClient(http), handler);
    }

    static Task<HttpResponseMessage> Json(string json) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task ChatAsync_posts_openai_body_with_cache_prompt()
    {
        var (client, handler) = Create((_, _) => Json("""{"choices":[{"message":{"role":"assistant","content":"hi"}}]}"""));

        var answer = await client.ChatAsync(Hello, 200, CancellationToken.None);

        Assert.Equal("hi", answer);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/v1/chat/completions", handler.Request.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(200, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(doc.RootElement.GetProperty("cache_prompt").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("hello", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task InfillAsync_posts_infill_body()
    {
        var (client, handler) = Create((_, _) => Json("""{"content":"x = 1"}"""));

        var content = await client.InfillAsync("pre", "suf", 64, CancellationToken.None);

        Assert.Equal("x = 1", content);
        Assert.Equal("/infill", handler.Request!.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("pre", doc.RootElement.GetProperty("input_prefix").GetString());
        Assert.Equal("suf", doc.RootElement.GetProperty("input_suffix").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("n_predict").GetInt32());
    }

    [Fact]
    public async Task ChatStreamAsync_yields_sse_deltas()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n" +
                           "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\n" +
                           "data: [DONE]\n\n";
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }));

        var parts = new List<string>();
        await foreach (var part in client.ChatStreamAsync(Hello, 10, CancellationToken.None))
            parts.Add(part);

        Assert.Equal(["a", "b"], parts);
    }

    [Fact]
    public async Task Connect_failure_throws_LlamaUnavailableException()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<LlamaUnavailableException>(() => client.ChatAsync(Hello, 10, CancellationToken.None));

        Assert.Equal("model server not reachable at http://127.0.0.1:8080/", ex.Message);
    }

    [Fact]
    public async Task Error_status_carries_llama_message()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"code":400,"message":"the request exceeds the available context size"}}""")
        }));

        var ex = await Assert.ThrowsAsync<LlamaErrorException>(() => client.ChatAsync(Hello, 10, CancellationToken.None));

        Assert.Equal(400, ex.Status);
        Assert.Equal("the request exceeds the available context size", ex.Message);
    }

    [Fact]
    public async Task Error_status_without_json_body_uses_status_text()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<LlamaErrorException>(() => client.ChatAsync(Hello, 10, CancellationToken.None));

        Assert.Equal(503, ex.Status);
        Assert.Equal("model server returned 503 Service Unavailable", ex.Message);
    }

    [Fact]
    public async Task Cancellation_aborts_request()
    {
        var (client, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(Hello, 10, cts.Token));
    }
}
