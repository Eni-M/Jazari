// Runs the real /fix for eval/fix_bench.py: args are the gateway's address, its key, the project and a trust file. Every change is
// accepted, but "undo?" is declined, so the benchmark sees what the model's change actually did to the build.
using System.Net.Http.Headers;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

using var http = new HttpClient { BaseAddress = new Uri(args[0]), Timeout = TimeSpan.FromMinutes(15) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", args[1]);
var theme = new Theme(ColorMode.None, unicode: true);
var session = new Session(new GatewayClient(http), new Ui(Console.Out, theme, 120), () => new MarkdownStream(Console.Out, theme, 120), args[2])
{
    Confirm = question => { Console.WriteLine($"? {question}"); return !question.StartsWith("That didn't help", StringComparison.Ordinal); },
    TrustFile = args[3],
};
await session.HandleAsync("/fix", CancellationToken.None);
