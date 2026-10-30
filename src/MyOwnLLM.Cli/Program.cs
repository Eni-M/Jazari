using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MyOwnLLM.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
CliArgs cli;
try { cli = CliArgs.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

var key = Environment.GetEnvironmentVariable("MYOWNLLM_KEY");
if (string.IsNullOrEmpty(key))
{
    Console.Error.WriteLine("set MYOWNLLM_KEY (run: dotnet run --project src/MyOwnLLM.Gateway -- add-key)");
    return 2;
}

var baseUrl = Environment.GetEnvironmentVariable("MYOWNLLM_URL") ?? "http://127.0.0.1:5080";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

// Ctrl+C aborts the request; the gateway then cancels the model's work.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var watch = Stopwatch.StartNew();
try
{
    return cli.Command switch
    {
        "ask" => await Ask(),
        "status" => await Status(),
        _ => await Feedback(),
    };
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (HttpRequestException e) when (e.StatusCode is null)
{
    Console.Error.WriteLine($"gateway not reachable at {baseUrl}");
    return 1;
}
catch (IOException)
{
    Console.WriteLine();
    Console.Error.WriteLine("lost connection to the gateway");
    return 1;
}

async Task<int> Ask()
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = JsonContent.Create(new { messages = new[] { new { role = "user", content = cli.Text } }, stream = cli.Stream }),
    };
    if (cli.RepoPath is not null) request.Headers.Add("X-Project-Path", Path.GetFullPath(cli.RepoPath));
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    if (!response.IsSuccessStatusCode) return await Fail(response);

    if (!cli.Stream)
    {
        var json = await ReadJson(response);
        Console.WriteLine(ConsoleText.Sanitize(json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
        return Done(json.GetProperty("id").GetString());
    }

    string? id = null;
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
    while (await reader.ReadLineAsync(cts.Token) is { } line)
    {
        if (SseChunk.Parse(line) is not { } chunk) continue;
        if (chunk.Error is not null)
        {
            Console.WriteLine();
            Console.Error.WriteLine(ConsoleText.Sanitize(chunk.Error));
            return 1;
        }
        id = chunk.Id ?? id;
        if (chunk.Content is not null) Console.Write(ConsoleText.Sanitize(chunk.Content));
    }
    Console.WriteLine();
    return Done(id);
}

async Task<int> Status()
{
    var repo = Path.GetFullPath(cli.RepoPath ?? Directory.GetCurrentDirectory());
    using var response = await http.PostAsJsonAsync("/v1/status", new { project_path = repo }, cts.Token);
    if (!response.IsSuccessStatusCode) return await Fail(response);
    var json = await ReadJson(response);
    Console.WriteLine(ConsoleText.Sanitize(json.GetProperty("summary").GetString()));
    return Done(json.GetProperty("id").GetString());
}

async Task<int> Feedback()
{
    using var response = await http.PostAsJsonAsync("/v1/feedback", new { id = cli.Id, accepted = cli.Accepted }, cts.Token);
    if (!response.IsSuccessStatusCode) return await Fail(response);
    Console.WriteLine("feedback saved");
    return 0;
}

int Done(string? id)
{
    Console.WriteLine($"\e[2m[{ConsoleText.Sanitize(id)} · {watch.Elapsed.TotalSeconds:0.0} s]\e[0m");
    return 0;
}

async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
    JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;

async Task<int> Fail(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    string message;
    try { message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? body; }
    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { message = $"{(int)response.StatusCode} {response.ReasonPhrase}"; }
    Console.Error.WriteLine(ConsoleText.Sanitize(message));
    return 1;
}
