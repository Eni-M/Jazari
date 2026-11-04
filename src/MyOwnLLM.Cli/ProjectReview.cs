using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <param name="Ambiguous">The quote matches several lines, so which one the finding means isn't known.</param>
public sealed record Finding(string File, int? Line, string Quote, string Problem, string Severity, string Fix, bool Verified, bool Ambiguous = false);

/// <summary>
/// Reviews a whole project by small parts, riskiest files first. Each finished part is saved straight away, so a stopped
/// review carries on where it left off and a later one only re-reads what changed. The model only proposes: a finding
/// counts as verified when the line it quotes is really in the file.
/// </summary>
public sealed partial class ProjectReview(IGatewayClient gateway, Ui ui, string storeDir, int unitTokens = ProjectReview.DefaultUnitTokens)
{
    public const int DefaultUnitTokens = 2000;
    const int AnswerTokens = 500;
    const string PromptVersion = "4"; // change it when the prompt changes, so old answers aren't reused

    /// <summary>
    /// The quote comes first, so the problem is written about a real line. The fix is optional: asking for fixes makes
    /// reviewers misjudge correct code more often (see reports/Jazari CLI and model enhancements.md, section 2).
    /// </summary>
    const string Schema = """
        {"type":"object","required":["findings"],"properties":{"findings":{"type":"array","maxItems":5,"items":{"type":"object","additionalProperties":false,
        "required":["file","quote","problem","severity"],"properties":{"file":{"type":"string","maxLength":200},"quote":{"type":"string","minLength":12,"maxLength":300},
        "problem":{"type":"string","maxLength":300},"severity":{"enum":["high","medium","low"]},"fix":{"type":"string","maxLength":400}}}}}}
        """;

    /// <summary>A quote shorter than this, like "}" or "return;", matches too many lines to show anything.</summary>
    const int MinQuoteChars = 12;

    const string Instructions =
        "Review the code above as a senior security-minded engineer. Check every method for: passwords or keys written in " +
        "the code, text joined into SQL or commands, endpoints that change data without an authorization check, one user " +
        "reaching another user's data, amounts or ids taken from the client unchecked, path traversal, unsafe file upload, " +
        "weak crypto, business rules that can be bypassed, swallowed errors and real bugs. Report only what this code itself " +
        "shows. Never write \"if\" or \"could be compromised\" guesses about code you can't see, and no style remarks. If the " +
        "code is fine, return an empty list. For each problem give the file, one whole line copied exactly from the code as " +
        "quote, what can go wrong, and severity (high means an attacker gains something). Add a short fix only when it's clear.";

    /// <summary>Returns the report's path, or null when there was nothing to review or the review stopped early.</summary>
    public async Task<string?> RunAsync(string root, CancellationToken ct)
    {
        if (!File.Exists(root) && !Directory.Exists(root)) { ui.Metadata($"Can't find {root}."); return null; }
        var (files, skipped) = ProjectFiles.Load(root);
        if (files.Count == 0) { ui.Metadata($"Nothing to review in {root}."); return null; }

        var units = Units(files, unitTokens);
        Directory.CreateDirectory(storeDir);
        var store = Path.Combine(storeDir, Hash(Path.GetFullPath(root).ToLowerInvariant())[..16] + ".jsonl");
        var done = Saved(store);
        ui.Metadata($"Reviewing {files.Count} files in {units.Count} parts, riskiest first. {skipped} skipped. Ctrl+C stops; running it again carries on.");
        // A cloned repository's .jazariignore could hide its worst file from a security review: never silently.
        var ignored = ProjectFiles.IgnoredFiles(root);
        if (ignored.Count > 0) ui.Metadata($"The project's .jazariignore left out {ignored.Count} file{(ignored.Count == 1 ? "" : "s")}; the report names them.");

        List<Finding> findings = [];
        List<string> failed = [];
        for (var i = 0; i < units.Count; i++)
        {
            var key = Key(units[i]);
            if (done.TryGetValue(key, out var earlier)) { findings.AddRange(earlier); continue; }

            ui.Metadata($"[{i + 1}/{units.Count}] {Describe(units[i])}");
            List<Finding>? found;
            try
            {
                var context = string.Join('\n', units[i].Select(f => $"--- {f.Path} ---\n{f.Text}"));
                found = Parse(await gateway.CompleteAsync(Instructions, context, Schema, AnswerTokens, ct), units[i]);
            }
            catch (GatewayException e) { ui.Error("The review stopped.", e.Message, "Run it again to carry on."); return null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { ui.Metadata("Stopped. Run it again to carry on."); return null; }

            if (found is null) { failed.Add(Describe(units[i])); continue; } // not saved, so the next run tries this part again
            File.AppendAllText(store, JsonSerializer.Serialize(new SavedPart(key, found)) + "\n");
            findings.AddRange(found);
        }

        var report = Path.Combine(storeDir, $"{Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\', '/'))}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        // The same finding twice (a part read again after a change elsewhere in it, a model repeating itself) is one finding.
        findings = [.. findings.DistinctBy(f => (f.File, f.Line, Squeeze(f.Quote), f.Problem))];
        File.WriteAllText(report, Report(root, files.Count, skipped, failed, findings, ignored));
        var ambiguous = findings.Count(f => f.Ambiguous);
        ui.Metadata($"{findings.Count(f => f.Verified)} verified, {(ambiguous > 0 ? $"{ambiguous} ambiguous, " : "")}" +
            $"{findings.Count(f => !f.Verified && !f.Ambiguous)} unverified. Report: {report}");
        return report;
    }

    sealed record SavedPart(string Key, List<Finding> Findings);

    static Dictionary<string, List<Finding>> Saved(string store)
    {
        Dictionary<string, List<Finding>> saved = [];
        if (!File.Exists(store)) return saved;
        foreach (var line in File.ReadLines(store))
        {
            try { if (JsonSerializer.Deserialize<SavedPart>(line) is { } part) saved[part.Key] = part.Findings; }
            catch (JsonException) { } // a line cut off by a crash: that part is simply read again
        }
        return saved;
    }

    /// <summary>Groups files into parts of about <paramref name="unitTokens"/>; a file too big for one part is cut at line ends.</summary>
    public static List<List<ProjectFile>> Units(IEnumerable<ProjectFile> files, int unitTokens)
    {
        List<List<ProjectFile>> units = [];
        List<ProjectFile> current = [];
        var used = 0;
        foreach (var piece in files.SelectMany(f => Pieces(f, unitTokens)))
        {
            var tokens = Tokens(piece.Text);
            // A risky file gets a part of its own: sharing one left its real problems unreported in testing.
            var risky = ProjectFiles.Risk(piece.Path) > 0;
            if ((risky || used + tokens > unitTokens) && current.Count > 0) { units.Add(current); current = []; used = 0; }
            current.Add(piece);
            used += tokens;
            if (risky) { units.Add(current); current = []; used = 0; }
        }
        if (current.Count > 0) units.Add(current);
        return units;
    }

    static IEnumerable<ProjectFile> Pieces(ProjectFile file, int unitTokens)
    {
        if (Tokens(file.Text) <= unitTokens) { yield return file; yield break; }
        var part = new StringBuilder();
        var partStart = 1;
        var lineNumber = 0;
        var maxChars = unitTokens * 4;
        foreach (var whole in file.Text.Split('\n'))
        {
            lineNumber++;
            // A single line longer than a part (minified or generated code, a one-line JSON file) is cut into pieces too,
            // or its part would never fit the model and the review would stop there on every run.
            for (var at = 0; at == 0 || at < whole.Length; at += maxChars)
            {
                var line = whole.Length <= maxChars ? whole : whole.Substring(at, Math.Min(maxChars, whole.Length - at));
                if (Tokens(part.ToString()) + Tokens(line) > unitTokens && part.Length > 0)
                {
                    yield return file with { Text = part.ToString(), FirstLine = partStart };
                    part.Clear();
                    partStart = lineNumber;
                }
                part.Append(line).Append('\n');
            }
        }
        if (part.Length > 0) yield return file with { Text = part.ToString(), FirstLine = partStart };
    }

    static int Tokens(string text) => (text.Length + 3) / 4;
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string Key(List<ProjectFile> unit) => Hash(PromptVersion + string.Concat(unit.Select(f => f.Path + "\0" + f.Text + "\0")));
    static string Describe(List<ProjectFile> unit) => unit.Count == 1 ? unit[0].Path : $"{unit[0].Path} and {unit.Count - 1} more";

    /// <summary>The model's findings, each checked against the code. Null when the reply wasn't the expected JSON.</summary>
    public static List<Finding>? Parse(string reply, List<ProjectFile> unit)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.GetProperty("findings").EnumerateArray().Select(f => Check(
                f.GetProperty("file").GetString() ?? "", f.GetProperty("quote").GetString() ?? "", f.GetProperty("problem").GetString() ?? "",
                f.GetProperty("severity").GetString() ?? "low", f.TryGetProperty("fix", out var fix) ? fix.GetString() ?? "" : "", unit)).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Verified means the quote is one real line, or at least 80% of one, with a name or keyword in it, and only one line
    /// matches. A fragment like "}" used to verify anything. A quote that matches several lines is kept as ambiguous.
    /// </summary>
    static Finding Check(string file, string quote, string problem, string severity, string fix, List<ProjectFile> unit)
    {
        var wanted = Quoted(quote);
        if (wanted.Length >= MinQuoteChars && Word().IsMatch(wanted))
            // The named file first; the model sometimes names the wrong one of the part.
            foreach (var candidate in unit.OrderBy(f => f.Path.Equals(file, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var lines = candidate.Text.Split('\n');
                var matches = Enumerable.Range(0, lines.Length).Where(i => Covers(wanted, Squeeze(lines[i]))).Take(2).ToList();
                if (matches.Count == 1) return new(candidate.Path, candidate.FirstLine + matches[0], quote.Trim(), problem, severity, fix, Verified: true);
                if (matches.Count > 1) return new(candidate.Path, null, quote.Trim(), problem, severity, fix, Verified: false, Ambiguous: true);
            }
        return new(file, null, quote.Trim(), problem, severity, fix, Verified: false);
    }

    static bool Covers(string wanted, string line) =>
        line.Length > 0 && line.Contains(wanted, StringComparison.Ordinal) && wanted.Length * 5 >= line.Length * 4;

    static string Squeeze(string text) => Spaces().Replace(text, " ").Trim();

    /// <summary>
    /// The line as the model meant it: small models wrap a quote in backticks or copy the line number in front of it
    /// ("12: var x = 1;"), which used to leave a real line "unverified".
    /// </summary>
    static string Quoted(string quote) => Squeeze(LineNumber().Replace(Squeeze(quote).Trim('`'), "")).Trim('`').Trim();

    static string Report(string root, int files, int skipped, List<string> failed, List<Finding> findings, List<string> ignored)
    {
        var text = new StringBuilder($"# Review of {root}\n\n{DateTime.Now:yyyy-MM-dd HH:mm}. {files} files read, {skipped} skipped");
        text.Append(failed.Count > 0 ? $", {failed.Count} parts failed and will be retried on the next run.\n" : ".\n");
        text.Append("\nThis is a small local model's opinion: check each finding. \"Verified\" only means the quoted line exists.\n");
        (string Title, Func<Finding, bool> In)[] groups =
        [
            ("Verified", f => f.Verified),
            ("Ambiguous: the quoted line appears more than once", f => f.Ambiguous),
            ("Unverified: the quoted line wasn't found", f => !f.Verified && !f.Ambiguous),
        ];
        foreach (var (title, inGroup) in groups)
        {
            var group = findings.Where(inGroup).OrderBy(f => Array.IndexOf(["high", "medium", "low"], f.Severity)).ToList();
            if (group.Count == 0) continue;
            text.Append($"\n## {title} ({group.Count})\n");
            foreach (var f in group)
                text.Append($"\n### [{f.Severity}] {f.File}{(f.Line is { } line ? $":{line}" : "")}\n\n{f.Problem}\n\n`{f.Quote.Replace('`', '\'')}`\n")
                    .Append(string.IsNullOrWhiteSpace(f.Fix) ? "" : $"\n**Fix:** {f.Fix}\n");
        }
        if (findings.Count == 0) text.Append("\nNo findings.\n");
        // Coverage: no findings in a part that was never reviewed isn't a clean bill of health, so the report names them.
        if (failed.Count > 0)
            text.Append($"\n## Not reviewed this run ({failed.Count})\n\nThe model's answer for these couldn't be read. The next run tries them again.\n\n")
                .Append(string.Concat(failed.Select(f => $"- {f}\n")));
        if (ignored.Count > 0)
            text.Append($"\n## Left out by the project's .jazariignore ({ignored.Count})\n\nThe repository's own ignore file kept these from the review. " +
                    "If you didn't write it, check them yourself.\n\n")
                .Append(string.Concat(ignored.Select(f => $"- {f}\n")));
        return text.ToString();
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:line\s*)?\d+\s*[:|.]\s+", RegexOptions.IgnoreCase)] private static partial Regex LineNumber();
    [GeneratedRegex(@"[A-Za-z_]{3,}")] private static partial Regex Word();
}
