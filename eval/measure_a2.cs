#:project ../src/MyOwnLLM.Cli/MyOwnLLM.Cli.csproj
#:property PublishAot=false
// Measures /deep /edit's project check (A2, Task 8 of plans/2026-11-04-deep-mode.md) on a restored clone of a repo: for each
// edit that changes nothing but a comment line at the top, which moves every line, the time to load and check the project, the
// new errors it reports (the gate is 0), and the process's peak working set. Prints counts and timings only: the repo is private.
//
//     dotnet run eval/measure_a2.cs -- <restored clone> [edits, default 5]
using System.Diagnostics;
using MyOwnLLM.Cli;

var root = Path.GetFullPath(args[0]);
var edits = args.Length > 1 ? int.Parse(args[1]) : 5;
var sep = Path.DirectorySeparatorChar;
var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && ProjectCheck.FindProject(f, root) is not null)
    .Order(StringComparer.Ordinal).ToList();
var step = Math.Max(1, files.Count / edits); // spread over the repo, not the first folder only
var falseErrors = 0;
foreach (var (file, i) in files.Where((_, i) => i % step == 0).Take(edits).Select((f, i) => (f, i + 1)))
{
    var text = File.ReadAllText(file);
    var watch = Stopwatch.StartNew();
    var found = await ProjectCheck.NewErrorsAsync(file, root, text, "// a no-op edit\n" + text, CancellationToken.None);
    var process = Process.GetCurrentProcess();
    Console.WriteLine($"edit {i}: {watch.Elapsed.TotalSeconds:0.0} s, {(found is null ? "project didn't load" : $"{found.Count} new errors")}, peak working set {process.PeakWorkingSet64 / 1_000_000} MB");
    falseErrors += found?.Count ?? 0;
}
Console.WriteLine($"{falseErrors} false new errors in {Math.Min(edits, files.Count)} no-op edits (gate: 0)");
