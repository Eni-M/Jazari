using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MyOwnLLM.Gateway.Auth;
using MyOwnLLM.Gateway.Llama;
using OpenAI;
using OpenAI.Chat;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>What VS Code, Continue, Rider and Visual Studio need: the official OpenAI client works against the gateway.</summary>
public class OpenAiClientTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-openai").FullName;
    readonly FakeLlama _llama = new();
    readonly WebApplicationFactory<Program> _factory;
    readonly string _key;

    public OpenAiClientTests()
    {
        _key = new ApiKeyStore(Path.Combine(_dir, "keys.json")).AddKey();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:DataDir", _dir);
            b.ConfigureTestServices(s => s.AddSingleton<ILlamaClient>(_llama));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    OpenAIClient Client() => new(new ApiKeyCredential(_key), new OpenAIClientOptions
    {
        Endpoint = new Uri("http://localhost/v1"),
        Transport = new HttpClientPipelineTransport(_factory.CreateClient()),
    });

    [Fact]
    public async Task Lists_the_model()
    {
        var models = await Client().GetOpenAIModelClient().GetModelsAsync();
        Assert.Equal("jazari", Assert.Single(models.Value).Id);
    }

    [Fact]
    public async Task Answers_a_chat()
    {
        var completion = await Client().GetChatClient("jazari").CompleteChatAsync([new UserChatMessage("hi")]);
        Assert.Equal("answer", completion.Value.Content[0].Text);
        Assert.Equal(ChatFinishReason.Stop, completion.Value.FinishReason);
    }

    [Fact]
    public async Task Streams_a_chat()
    {
        var text = "";
        await foreach (var update in Client().GetChatClient("jazari").CompleteChatStreamingAsync([new UserChatMessage("hi")]))
            text += string.Concat(update.ContentUpdate.Select(p => p.Text));
        Assert.Equal("ab", text);
    }

    [Fact]
    public async Task A_system_message_from_the_client_is_kept()
    {
        await Client().GetChatClient("jazari").CompleteChatAsync([new SystemChatMessage("Be terse."), new UserChatMessage("hi")]);
        Assert.Contains(_llama.Calls.Single().Messages, m => m is { Role: "system", Content: "Be terse." });
    }

    [Theory]
    [InlineData("evil.example", null)]
    [InlineData("localhost", "https://evil.example")]
    [InlineData("localhost", "null")]
    public async Task Web_pages_are_turned_away(string host, string? origin)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/info");
        request.Headers.Host = host;
        request.Headers.Authorization = new("Bearer", _key);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:5080", "http://localhost:3000")]
    [InlineData("[::1]:5080", null)]
    public async Task Local_callers_get_in(string host, string? origin)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/info");
        request.Headers.Host = host;
        request.Headers.Authorization = new("Bearer", _key);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("/v1/chat/completions", """{"max_tokens":5}""")]
    [InlineData("/v1/chat/completions", """{"messages":[]}""")]
    [InlineData("/v1/chat/completions", """{"messages":[{"role":"user"}]}""")]
    [InlineData("/v1/chat/completions", """{"messages":[{"role":"user","content":"x"}],"json_schema":"{not json"}""")]
    [InlineData("/v1/warm", """{}""")]
    [InlineData("/v1/infill", """{"prefix":"x"}""")]
    [InlineData("/v1/status", """{}""")]
    public async Task A_request_missing_what_it_needs_gets_400_with_the_reason(string path, string body)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", _key);
        var response = await client.PostAsync(path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString()));
    }
}
