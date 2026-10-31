using System.Net;
using System.Text;
using System.Text.Json;
using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class GatewayClientTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    static (GatewayClient, Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        return (new GatewayClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5080") }), handler);
    }

    [Fact]
    public async Task Chat_sends_history_question_project_and_stream()
    {
        const string sse = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });

        var chunks = new List<SseChunk>();
        await foreach (var c in client.ChatAsync([new ChatTurn("user", "a"), new ChatTurn("assistant", "b")], "q", @"C:\Repo", CancellationToken.None)) chunks.Add(c);

        Assert.Equal("ok", Assert.Single(chunks).Content);
        Assert.Equal(@"C:\Repo", Uri.UnescapeDataString(handler.Request!.Headers.GetValues("X-Project-Path").Single()));
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(["a", "b", "q"], doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task Chat_works_from_a_folder_with_accents()
    {
        const string sse = "data: [DONE]\n\n";
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });

        await foreach (var _ in client.ChatAsync([], "q", @"C:\Users\Arbër\proj", CancellationToken.None)) { }

        var sent = handler.Request!.Headers.GetValues("X-Project-Path").Single();
        Assert.True(sent.All(char.IsAscii), sent);
        Assert.Equal(@"C:\Users\Arbër\proj", Uri.UnescapeDataString(sent));
    }

    [Fact]
    public async Task Unauthorized_becomes_GatewayException_with_status()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.InfoAsync(CancellationToken.None));

        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Gateway_error_text_is_the_message()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":"model server not reachable at http://127.0.0.1:8080/"}""")
        });

        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.StatusAsync(@"C:\Repo", CancellationToken.None));

        Assert.Equal("model server not reachable at http://127.0.0.1:8080/", ex.Message);
        Assert.Equal(503, ex.Status);
    }

    [Fact]
    public async Task Connect_failure_is_gateway_not_reachable()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.InfoAsync(CancellationToken.None));

        Assert.Null(ex.Status);
        Assert.Equal("gateway not reachable at http://127.0.0.1:5080/", ex.Message);
    }

    [Fact]
    public async Task Non_json_success_response_becomes_GatewayException()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not jazari</html>") });

        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.InfoAsync(CancellationToken.None));

        Assert.Equal("unexpected response from the gateway", ex.Message);
    }

    [Fact]
    public async Task Malformed_stream_line_becomes_GatewayException()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {broken\n\n") });

        await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            await foreach (var _ in client.ChatAsync([], "q", @"C:\Repo", CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Timeout_becomes_GatewayException()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.InfoAsync(CancellationToken.None));

        Assert.Equal("the gateway took too long to answer", ex.Message);
    }
}
