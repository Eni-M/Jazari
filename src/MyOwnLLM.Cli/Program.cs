using System.Net.Http.Headers;
using System.Reflection;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

Console.OutputEncoding = System.Text.Encoding.UTF8;
CliArgs cli;
try { cli = CliArgs.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

if (cli.Command == "help") { Console.WriteLine(CliArgs.Usage); return 0; }
if (cli.Command == "version")
{
    Console.WriteLine($"jazari {typeof(CliArgs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0"}");
    return 0;
}

if (cli.Command == "stop")
{
    Console.WriteLine($"stopped {ServerLauncher.StopServers()} Jazari server process(es)");
    return 0;
}

// Everything machine-specific comes from the config the installer wrote, so nothing is hard-coded.
var config = JazariConfig.Load(JazariConfig.DefaultPath);
var runner = new ProcessRunner();

if (cli.Command == "tune")
{
    if (config is null) { Console.Error.WriteLine("Jazari isn't installed: run scripts/install-jazari.ps1"); return 2; }
    // The model must have the PC to itself while it's measured.
    if (ServerLauncher.StopServers() > 0) Console.WriteLine("Stopped Jazari's servers; they start again with the new settings.");
    var tuneUi = new Ui(Console.Out, new Theme(ColorMode.None, unicode: true), 100);
    await new DeviceTuner(runner, tuneUi).EnsureAsync(config, DeviceTuner.ReadMachine(), Tuning.DefaultPath, CancellationToken.None, force: true);
    return 0;
}
Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0"); // git must never stop to ask for credentials
var updateAvailable = cli.Command == "session"
    ? Task.Run(() => UpdateCheck.IsUpdateAvailableAsync(runner, config, TimeSpan.FromSeconds(5), CancellationToken.None))
    : Task.FromResult(false);
var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);

// First run: create a key through the gateway and save it as JAZARI_KEY, so the user never has to.
var provisioner = new KeyProvisioner(runner, KeyProvisioner.SaveToEnvironment);
var key = await provisioner.EnsureKeyAsync(settings.Key, config, CancellationToken.None);

// The interactive session opens without a key and explains the setup; one-off commands can't do anything without one.
if (key is null && cli.Command != "session")
{
    Console.Error.WriteLine("no API key: run jazari update (or set JAZARI_KEY)");
    return 2;
}

using var http = new HttpClient { BaseAddress = settings.GatewayUrl, Timeout = TimeSpan.FromMinutes(30) };
if (key is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
var gateway = new GatewayClient(http);

var version = typeof(CliArgs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
var theme = new Theme(ColorModeDetector.Detect(Environment.GetEnvironmentVariable, Console.IsOutputRedirected), unicode: Console.OutputEncoding.CodePage == 65001);
// One less than the terminal's columns, so a full line never makes the terminal wrap (the renderer relies on this).
var width = Console.IsOutputRedirected ? 100 : Math.Max(Console.WindowWidth - 1, 20);
var directory = Path.GetFullPath(cli.RepoPath ?? Directory.GetCurrentDirectory());

// Start llama-server and the gateway in the background if they aren't running yet.
using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
async Task<bool> LlamaReady(CancellationToken ct)
{
    try { return (await health.GetAsync("http://127.0.0.1:8080/health", ct)).IsSuccessStatusCode; }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
}
var startupUi = new Ui(Console.Out, theme, width);
if (cli.Command == "session") startupUi.TakeOverScreen();
var tuner = new DeviceTuner(runner, startupUi);
async Task<string[]> TunedArgs(JazariConfig c, CancellationToken ct) =>
    (await tuner.EnsureAsync(c, DeviceTuner.ReadMachine(), Tuning.DefaultPath, ct)).ServerArgs(c.Model);
async Task<string[]> SafeArgs(JazariConfig c, CancellationToken ct) =>
    (await tuner.FallBackAsync(c, DeviceTuner.ReadMachine(), Tuning.DefaultPath, ct)).ServerArgs(c.Model);
var serverState = await new ServerLauncher(runner, gateway, LlamaReady, startupUi, ServerLauncher.DefaultLogDir, Task.Delay,
        llamaArgs: TunedArgs, fallbackArgs: SafeArgs)
    .EnsureRunningAsync(config, CancellationToken.None);
// A key the gateway doesn't know (e.g. its key file was reset): make a new one, like on first run.
if (serverState == ConnectionState.KeyRejected && await provisioner.EnsureKeyAsync(key, config, CancellationToken.None, rejected: true) is { } freshKey)
{
    key = freshKey;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
}

if (cli.Command == "session")
{
    var ui = startupUi;
    // PrettyPrompt needs a real console; pipes and some IDE terminals get plain line input instead.
    var piped = Console.IsInputRedirected || Console.IsOutputRedirected;
    ILineReader reader = piped ? new TextLineReader(Console.In) : new PromptLineReader(ui);
    // In a real terminal the typed line becomes the "› question" row; piped input isn't echoed, so Jazari prints it.
    var interactive = new Session(gateway, ui, () => new MarkdownStream(Console.Out, theme, width), directory)
    {
        EchoQuestions = piped,
        Confirm = question => { Console.Write($"{question} [y/n] "); return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes"; },
    };
    await InputLoop.RunAsync(interactive, ui, version, directory, reader,
        hasKey: key is not null, updateAvailable: updateAvailable, runningOn: Tuning.Load(Tuning.DefaultPath)?.Summary(theme.Glyph('·')));
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
        if (Console.IsInputRedirected && await PipedInput.ReadAsync(Console.In, TimeSpan.FromSeconds(3)) is { } piped) session.UsePipedInput(piped);
        await session.AskAsync(cli.Text!, cts.Token);
        break;
    case "status":
        await session.HandleAsync("/status", cts.Token);
        break;
    case "commit":
        await session.HandleAsync("/commit", cts.Token);
        break;
    case "diff":
        await session.HandleAsync("/diff", cts.Token);
        if (session.HasContext) await session.AskAsync(cli.Text!, cts.Token);
        break;
    case "review":
        await session.HandleAsync($"/review {cli.Text}", cts.Token);
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
