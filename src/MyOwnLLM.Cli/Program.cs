using System.Net.Http.Headers;
using System.Reflection;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

Console.OutputEncoding = System.Text.Encoding.UTF8;
ConsoleText.EnableEscapes();
// --plain goes before any command; it's passed on as JAZARI_PLAIN so everything below reads one setting.
if (args is ["--plain", .. var afterPlain]) { Environment.SetEnvironmentVariable("JAZARI_PLAIN", "1"); args = afterPlain; }
var plain = ColorModeDetector.IsPlain(Environment.GetEnvironmentVariable);
CliArgs cli;
try { cli = CliArgs.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

if (cli.Command == "help") { Console.WriteLine(CliArgs.Usage); return 0; }
if (cli.Command == "version")
{
    Console.WriteLine($"jazari {typeof(CliArgs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0"}");
    return 0;
}

// Everything machine-specific comes from the config the installer wrote, so nothing is hard-coded.
var config = JazariConfig.Load(JazariConfig.DefaultPath);
var runner = new ProcessRunner();

if (cli.Command is "stop" or "tune" && config is null) { Console.Error.WriteLine("Jazari isn't installed: run scripts/install-jazari.ps1"); return 2; }
if (cli.Command == "stop")
{
    Console.WriteLine($"stopped {ServerLauncher.StopServers(config!)} Jazari server process(es)");
    return 0;
}

if (cli.Command == "tune")
{
    // The model must have the PC to itself while it's measured.
    if (ServerLauncher.StopServers(config!) > 0) Console.WriteLine("Stopped Jazari's servers; they start again with the new settings.");
    var tuneTheme = new Theme(ColorModeDetector.Detect(Environment.GetEnvironmentVariable, Console.IsOutputRedirected), unicode: Console.OutputEncoding.CodePage == 65001 && !plain,
    edges: ColorModeDetector.DrawsEdges(Environment.GetEnvironmentVariable));
    var tuneUi = new Ui(Console.Out, tuneTheme, Console.IsOutputRedirected ? 100 : Math.Max(Console.WindowWidth - 1, 20));
    await new DeviceTuner(runner, tuneUi).MeasureAsync(config!, DeviceTuner.ReadMachine(), Tuning.DefaultPath, CancellationToken.None);
    return 0;
}
Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0"); // git must never stop to ask for credentials
var updateAvailable = cli.Command == "session"
    ? Task.Run(() => UpdateCheck.IsUpdateAvailableAsync(runner, config, TimeSpan.FromSeconds(5), CancellationToken.None))
    : Task.FromResult(false);
var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);

var version = typeof(CliArgs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
var theme = new Theme(ColorModeDetector.Detect(Environment.GetEnvironmentVariable, Console.IsOutputRedirected), unicode: Console.OutputEncoding.CodePage == 65001 && !plain,
    edges: ColorModeDetector.DrawsEdges(Environment.GetEnvironmentVariable));
// One less than the terminal's columns, so a full line never makes the terminal wrap (the renderer relies on this).
int Columns() => Console.IsOutputRedirected ? 100 : Math.Max(Console.WindowWidth - 1, 20);
var width = Columns();
var directory = Path.GetFullPath(cli.RepoPath ?? Directory.GetCurrentDirectory());

using var health = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8080"), Timeout = TimeSpan.FromSeconds(2) };
string? LlamaKey()
{
    try { return File.Exists(ServerLauncher.LlamaKeyFile) ? File.ReadAllText(ServerLauncher.LlamaKeyFile).Trim() : null; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
}
var router = new RouterHealth(health, LlamaKey);
Task<bool> LlamaReady(CancellationToken ct) => router.ReadyAsync(ct);

// Before the first-run key below: the doctor only looks. It makes no key and starts no server.
if (cli.Command == "doctor")
{
    using var doctorHttp = new HttpClient { BaseAddress = settings.GatewayUrl, Timeout = TimeSpan.FromSeconds(5) };
    if (settings.Key is not null) doctorHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key);
    var lawBook = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "law-book.db");
    var checks = await new Doctor(config, settings.Key, new GatewayClient(doctorHttp), LlamaReady, File.Exists, ServerLauncher.DefaultLogDir, lawBook,
        DeviceTuner.IsMeasured(Tuning.DefaultPath, config?.Model), ct => UpdateCheck.IsUpdateAvailableAsync(runner, config, TimeSpan.FromSeconds(5), ct))
        .RunAsync(CancellationToken.None);
    var doctorUi = new Ui(Console.Out, theme, Columns);
    foreach (var check in checks)
    {
        doctorUi.Line($"{(check.Ok ? "ok " : "FIX")}  {check.Name}: {check.Detail}", check.Ok ? Tone.Primary : Tone.Danger);
        if (check.Fix is not null) doctorUi.Line($"     {check.Fix}", Tone.Muted);
    }
    return checks.All(c => c.Ok) ? 0 : 1;
}

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

// Start llama-server and the gateway in the background if they aren't running yet.
// Tuning only ever runs when asked: a start uses what was measured, or safe CPU settings.
string[] RouterOn(Tuning t, JazariConfig c)
{
    Directory.CreateDirectory(Path.GetDirectoryName(ServerLauncher.LanesFile)!);
    File.WriteAllText(ServerLauncher.LanesFile, t.PresetText(c.Model, c.DeepModel is { } deep && File.Exists(deep) ? deep : null));
    return Tuning.RouterArgs(ServerLauncher.LanesFile, ServerLauncher.EnsureLlamaKey(ServerLauncher.LlamaKeyFile));
}
Task<string[]> TunedArgs(JazariConfig c, CancellationToken ct) => Task.FromResult(RouterOn(DeviceTuner.ForStart(Tuning.DefaultPath, DeviceTuner.ReadMachine(), c.Model), c));
Task<string[]> SafeArgs(JazariConfig c, CancellationToken ct) => Task.FromResult(RouterOn(DeviceTuner.FallBack(DeviceTuner.ReadMachine()), c));
// A key the gateway doesn't know (e.g. its key file was reset): make a new one, like on first run. It's kept only if the
// gateway answering here accepts it: a 401 from some other server on the port mustn't replace a working key.
async Task<bool> Accepted(string candidate, CancellationToken ct)
{
    var previous = http.DefaultRequestHeaders.Authorization;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", candidate);
    try { await gateway.InfoAsync(ct); return true; }
    catch (GatewayException) { http.DefaultRequestHeaders.Authorization = previous; return false; }
}
async Task StartServersAsync(Ui reportTo, bool quiet)
{
    var serverState = await new ServerLauncher(runner, gateway, LlamaReady, reportTo, ServerLauncher.DefaultLogDir, Task.Delay,
            llamaArgs: TunedArgs, fallbackArgs: SafeArgs, quiet: quiet, llamaListening: router.ListeningAsync,
            llamaAlive: () => !router.FastFailed && config is not null && ServerLauncher.IsRunning(config.LlamaServer))
        .EnsureRunningAsync(config, CancellationToken.None);
    if (serverState == ConnectionState.KeyRejected && await provisioner.EnsureKeyAsync(key, config, CancellationToken.None, rejected: true, accepted: Accepted) is { } freshKey)
    {
        key = freshKey;
    }
}

// With --json, stdout holds only the JSON: what starting the servers has to say goes to stderr.
var startupUi = new Ui(cli.Json ? Console.Error : Console.Out, theme, Columns);
if (cli.Command == "session")
{
    var tuning = DeviceTuner.ForStart(Tuning.DefaultPath, DeviceTuner.ReadMachine(), config?.Model);
    var measured = DeviceTuner.IsMeasured(Tuning.DefaultPath, config?.Model);
    // The panel prompt needs a real console; pipes, some IDE terminals and --plain get plain line input instead.
    var redirected = Console.IsInputRedirected || Console.IsOutputRedirected;
    var piped = redirected || plain;
    // A real console gets the screen: it owns the window, draws the conversation and keeps the panel at the bottom.
    var terminal = piped ? null : new SystemTerminal();
    var screen = terminal is null ? null : new Screen(terminal, theme, bottom: true, paced: true);
    var ui = screen is null ? new Ui(Console.Out, theme, Columns) : new Ui(screen, theme, Columns);
    var editor = screen is null ? null : new PromptEditor(ui, screen, terminal!, Directory.GetCurrentDirectory);
    // In a real terminal the prompt puts the "› question" row on screen; piped input isn't echoed, so Jazari prints it.
    var interactive = new Session(gateway, ui,
        screen is null ? () => new MarkdownStream(Console.Out, theme, Columns()) : () => new MarkdownStream(screen, theme), directory)
    {
        EchoQuestions = redirected, // a real console in --plain shows what was typed already
        Tuning = tuning,
        ConversationDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyOwnLLM", "conversations"),
        Confirm = editor is not null ? editor.Ask : question => { Console.Write($"{question} [y/n] "); return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes"; },
    };
    if (editor is not null)
    {
        editor.OffersDeep = () => interactive.OffersDeep;
        interactive.StartingModel = Session.ModelFromFile(config?.Model);
        // What starting has to say waits in a buffer: the prompt is open and being typed in meanwhile.
        async Task<(bool, BlockBuffer)> Start()
        {
            var report = new BlockBuffer();
            await StartServersAsync(new Ui(report, theme, Columns), quiet: true);
            return (key is not null, report);
        }
        await InputLoop.RunInteractiveAsync(interactive, ui, version, directory, editor, Start, updateAvailable, measured);
        return 0;
    }
    await StartServersAsync(ui, quiet: false);
    await InputLoop.RunAsync(interactive, ui, version, directory, new TextLineReader(Console.In),
        hasKey: key is not null, updateAvailable: updateAvailable, runningOn: measured ? tuning.Summary(theme.Glyph('·')) : null);
    return 0;
}
await StartServersAsync(startupUi, quiet: cli.Json);

// One-off commands. --no-stream renders into a buffer and prints it when the answer is complete.
var buffer = cli.Stream ? null : new StringWriter();
var output = cli.Json ? Console.Error : (TextWriter?)buffer ?? Console.Out; // --json: notes about piped input go to stderr too
var oneOffUi = new Ui(output, theme, width);
var session = new Session(gateway, oneOffUi, () => new MarkdownStream(output, theme, width), directory);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (cli.Command)
{
    case "ask" when cli.Json:
        if (Console.IsInputRedirected && await PipedInput.ReadAsync(Console.In, TimeSpan.FromSeconds(3)) is { } pipedJson) session.UsePipedInput(pipedJson);
        Console.WriteLine(await session.AnswerAsJsonAsync(cli.Text!, cts.Token, cli.Deep));
        return session.LastFailed ? 1 : 0;
    case "ask":
        if (Console.IsInputRedirected && await PipedInput.ReadAsync(Console.In, TimeSpan.FromSeconds(3)) is { } piped) session.UsePipedInput(piped);
        await session.AskAsync(cli.Text!, cts.Token, cli.Deep);
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
