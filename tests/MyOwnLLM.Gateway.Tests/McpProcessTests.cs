using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>
/// The built CLI over real stdin and stdout, against an HTTP fake of the gateway. Only law_book_search is called: the fake answers
/// it, so the command has no reason to start a real server, and <c>jazari mcp</c> never touches JAZARI_KEY.
/// </summary>
public class McpProcessTests : IAsyncLifetime
{
    WebApplication _fake = null!;
    string _url = null!;
    readonly string _project = Directory.CreateTempSubdirectory("jazari mcp process").FullName;
    string? _authorization;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _fake = builder.Build();
        _fake.MapPost("/v1/docs", (HttpRequest request, JsonElement body) =>
        {
            _authorization = request.Headers.Authorization;
            // The question comes back in the passage, so a test sees what the gateway really received.
            return Results.Json(new { available = true, passages = new[] { new { title = "Rate limiting", url = "https://learn.microsoft.com/rate", text = "Asked: " + body.GetProperty("question").GetString() } } });
        });
        await _fake.StartAsync();
        _url = _fake.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public async Task DisposeAsync()
    {
        await _fake.DisposeAsync();
        Directory.Delete(_project, recursive: true);
    }

    static string Cli => typeof(CliArgs).Assembly.Location;

    Dictionary<string, string?> Environment => new() { ["JAZARI_URL"] = _url, ["JAZARI_KEY"] = "test-key" };

    [Fact]
    public async Task A_client_someone_else_wrote_can_connect_list_and_call()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "jazari", Command = "dotnet", Arguments = [Cli, "mcp"], WorkingDirectory = _project, EnvironmentVariables = Environment,
        }), cancellationToken: timeout.Token);

        Assert.Equal("jazari", client.ServerInfo!.Name);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(["jazari_ask", "law_book_search", "review_file"], tools.Select(t => t.Name).Order());

        var result = await client.CallToolAsync("law_book_search", new Dictionary<string, object?> { ["query"] = "rate limiting" }, cancellationToken: timeout.Token);

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Asked: rate limiting", text);
        Assert.Contains("https://learn.microsoft.com/rate", text);
        Assert.Equal("Bearer test-key", _authorization);
    }

    [Fact]
    public async Task Raw_utf8_goes_through_stdin_and_stdout_holds_only_protocol_and_closing_stdin_ends_it()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _project, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(Cli);
        start.ArgumentList.Add("mcp");
        foreach (var (name, value) in Environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"law_book_search","arguments":{"query":"¿Déjà vu 限流?"}}}""");
        await process.StandardInput.FlushAsync(timeout.Token);
        var reply = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(timeout.Token))!).RootElement;
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(1, reply.GetProperty("id").GetInt32());
        Assert.Contains("Asked: ¿Déjà vu 限流?", reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("", await process.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await errors);
    }
}
