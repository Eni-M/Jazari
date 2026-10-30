using System.Net.Http.Headers;
using System.Reflection;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

Console.OutputEncoding = System.Text.Encoding.UTF8;
CliArgs cli;
try { cli = CliArgs.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);
if (settings.Key is null)
{
    Console.Error.WriteLine("set JAZARI_KEY (run: dotnet run --project src/MyOwnLLM.Gateway -- add-key)");
    return 2;
}

using var http = new HttpClient { BaseAddress = settings.GatewayUrl, Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key);
var gateway = new GatewayClient(http);

var version = typeof(CliArgs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
var theme = new Theme(ColorModeDetector.Detect(Environment.GetEnvironmentVariable, Console.IsOutputRedirected), unicode: Console.OutputEncoding.CodePage == 65001);
// One less than the terminal's columns, so a full line never makes the terminal wrap (the renderer relies on this).
var width = Console.IsOutputRedirected ? 100 : Math.Max(Console.WindowWidth - 1, 20);
var directory = Path.GetFullPath(cli.RepoPath ?? Directory.GetCurrentDirectory());

if (cli.Command == "session")
{
    var ui = new Ui(Console.Out, theme, width);
    // PrettyPrompt needs a real console; pipes and some IDE terminals get plain line input instead.
    ILineReader reader = Console.IsInputRedirected || Console.IsOutputRedirected ? new TextLineReader(Console.In) : new PromptLineReader(theme);
    await InputLoop.RunAsync(new Session(gateway, ui, () => new MarkdownStream(Console.Out, theme, width), directory), ui, version, directory, reader);
    return 0;
}

// One-off commands. --no-stream renders into a buffer and prints it when the answer is complete.
var buffer = cli.Stream ? null : new StringWriter();
var output = (TextWriter?)buffer ?? Console.Out;
var oneOffUi = new Ui(output, theme, width);
var session = new Session(gateway, oneOffUi, () => new MarkdownStream(output, theme, width), directory);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (cli.Command)
{
    case "ask":
        await session.AskAsync(cli.Text!, cts.Token);
        break;
    case "status":
        await session.HandleAsync("/status", cts.Token);
        break;
    default:
        try
        {
            if (await gateway.FeedbackAsync(cli.Id!, cli.Accepted!.Value, cts.Token)) { oneOffUi.Metadata("feedback saved"); return 0; }
            oneOffUi.Error($"unknown id {cli.Id}", null, null);
        }
        catch (GatewayException e) { oneOffUi.Error(e.Message, e.Cause, null); }
        return 1;
}

if (buffer is not null) Console.Write(buffer);
return session.LastFailed ? 1 : 0;
