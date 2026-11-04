using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <param name="Ambiguous">The quote matches several lines, so which one the finding means isn't known.</param>
public sealed record Finding(string File, int? Line, string Quote, string Problem, string Severity, string Fix, bool Verified, bool Ambiguous = false)
{
    /// <summary>Not in the project's previous review.</summary>
    public bool New { get; init; }

    /// <summary>The StaticChecks rule that found it; null when the model did.</summary>
    public string? Rule { get; init; }
}

/// <summary>
/// Reviews a whole project by small parts, riskiest files first. Each finished part is saved straight away, so a stopped
/// review carries on where it left off and a later one only re-reads what changed. The model only proposes: a finding
/// counts as verified when the line it quotes is really in the file.
/// </summary>
public sealed partial class ProjectReview(IGatewayClient gateway, Ui ui, string storeDir, int unitTokens = ProjectReview.DefaultUnitTokens)
{
    public const int DefaultUnitTokens = 2000;
    const int AnswerTokens = 1024; // the gateway's cap, and room for the longest reply Schema allows: 500 left a real reply 6 tokens short
    const string PromptVersion = "5"; // change it when the prompt changes, so old answers aren't reused

    /// <summary>
    /// The quote comes first, so the problem is written about a real line. The fix is optional: asking for fixes makes
    /// reviewers misjudge correct code more often (see reports/Jazari CLI and model enhancements.md, section 2).
    /// </summary>
    const string Schema = """
        {"type":"object","required":["findings"],"properties":{"findings":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,
        "required":["file","quote","problem","severity"],"properties":{"file":{"type":"string","maxLength":200},"quote":{"type":"string","minLength":12,"maxLength":300},
        "problem":{"type":"string","maxLength":300},"severity":{"enum":["high","medium","low"]},"fix":{"type":"string","maxLength":400}}}}}}
        """;

    /// <summary>A quote shorter than this, like "}" or "return;", matches too many lines to show anything.</summary>
    const int MinQuoteChars = 12;

    const string Instructions =
        "Review the code above as a senior security-minded engineer. Most code has nothing worth reporting, so an empty list is the " +
        "usual answer. Report a problem only when a line of this code itself shows it: a password or key written in the code, text " +
        "joined into SQL or commands, an endpoint that changes data with no authorization check, one user reaching another user's " +
        "data, an amount or id taken from the client unchecked, path traversal, unsafe file upload, weak crypto, a swallowed error or " +
        "a real bug. Declarations such as column lengths, indexes, keys and relationships are not problems. Never write \"if\", " +
        "\"potential\" or \"could\" guesses about code you can't see, and no style remarks. For each problem give the file, one whole " +
        "line copied exactly from the code as quote, what can go wrong, and severity (high means an attacker gains something). Add a " +
        "short fix only when it's clear.";

    /// <summary>Returns the report's path, or null when there was nothing to review or the review stopped early.</summary>
    /// <param name="changed">For /review --changed: the changed parts to read instead of the whole project (see ChangedCode),
    /// and the changed files .jazariignore kept out.</param>
    public async Task<string?> RunAsync(string root, CancellationToken ct, (List<ProjectFile> Parts, List<string> Ignored)? changed = null)
    {
        if (!File.Exists(root) && !Directory.Exists(root)) { ui.Metadata($"Can't find {root}."); return null; }
        var (files, skipped) = changed is { } c ? (c.Parts, 0) : ProjectFiles.Load(root);
        // A cloned repository's .jazariignore could hide its worst file from a security review: never silently.
        var ignored = changed?.Ignored ?? ProjectFiles.IgnoredFiles(root);
        if (files.Count == 0)
        {
            ui.Metadata(ignored.Count == 0 ? (changed is null ? $"Nothing to review in {root}." : "No changed code to review since the last commit.")
                : $"Nothing to review in {root}: the project's .jazariignore left out every file. It hid {string.Join(", ", ignored.Take(10))}{(ignored.Count > 10 ? $" and {ignored.Count - 10} more" : "")}.");
            return null;
        }

        var units = Units(files, unitTokens);
        Directory.CreateDirectory(storeDir);
        var store = Path.Combine(storeDir, Hash(Path.GetFullPath(root).ToLowerInvariant())[..16] + ".jsonl");
        var done = Saved(store);
        ui.Metadata(changed is null
            ? $"Reviewing {Plural(files.Count, "file")} in {Plural(units.Count, "part")}, riskiest first. {skipped} skipped. Ctrl+C stops; running it again carries on."
            : $"Reviewing what changed since the last commit: {files.Select(f => f.Path).Distinct().Count()} files, {units.Count} parts. Ctrl+C stops; running it again carries on.");
        if (ignored.Count > 0) ui.Metadata($"The project's .jazariignore left out {ignored.Count} file{(ignored.Count == 1 ? "" : "s")}; the report names them.");

        List<Finding> findings = StaticChecks.Scan(files); // certain, and free: before the model's slow pass, which may be stopped
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

            if (found is null) { failed.Add(string.Join(", ", units[i].Select(Lines))); continue; } // not saved, so the next run tries this part again
            File.AppendAllText(store, JsonSerializer.Serialize(new SavedPart(key, found)) + "\n");
            findings.AddRange(found);
        }

        var report = Path.Combine(storeDir, $"{Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\', '/'))}{(changed is null ? "" : "-changes")}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        // The same finding twice (a part read again after a change elsewhere in it, a model repeating itself) is one finding.
        // A model quoting a line with a key on it would copy the key into the report: every quote is masked the same way.
        findings = [.. findings.Select(f => f with { Quote = StaticChecks.Masked(f.Quote) }).DistinctBy(f => (f.File, f.Line, Squeeze(f.Quote), f.Problem))];
        var history = Track(root, Path.Combine(storeDir, Hash(Path.GetFullPath(root).ToLowerInvariant())[..16] + ".findings.json"), findings);
        findings = history.Current;
        File.WriteAllText(report, Report(changed is null ? $"Review of {root}" : $"Review of the changes in {root}", files.Count, skipped, failed, findings, ignored, history));
        var ambiguous = findings.Count(f => f.Ambiguous);
        var rules = findings.Count(f => f.Rule is not null);
        ui.Metadata($"{(rules > 0 ? $"{rules} found by rules, " : "")}{findings.Count(f => f.Verified && f.Rule is null)} verified, {(ambiguous > 0 ? $"{ambiguous} ambiguous, " : "")}" +
            $"{findings.Count(f => !f.Verified && !f.Ambiguous)} unverified. Report: {report}");
        return report;
    }

    sealed record SavedPart(string Key, List<Finding> Findings);

    /// <param name="Current">This run's findings, each marked new or not.</param>
    /// <param name="Resolved">Earlier findings whose quoted line is gone from the file: fixed, or the code removed.</param>
    /// <param name="Earlier">Earlier findings this run didn't report though their line is still there. A small model's findings
    /// come and go between runs, so they're kept and listed rather than taken as fixed.</param>
    sealed record History(List<Finding> Current, List<Finding> Resolved, List<Finding> Earlier);

    /// <summary>
    /// Each finding's story across reviews, kept per project like PR-Agent's finding state: new, still there, resolved.
    /// Only a deterministic test resolves one (its quoted line is no longer in the file); a /review --changed of a few
    /// members never resolves findings elsewhere, since those stay in the file and are carried on.
    /// </summary>
    static History Track(string root, string stateFile, List<Finding> current)
    {
        List<Finding> before = [];
        try { if (File.Exists(stateFile)) before = JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(stateFile)) ?? []; }
        catch (Exception e) when (e is JsonException or IOException) { } // unreadable: everything counts as new this once

        static (string, string) Id(Finding f) => (f.File.ToLowerInvariant(), Quoted(f.Quote));
        var now = current.Select(Id).ToHashSet();
        var then = before.Select(Id).ToHashSet();
        var folder = File.Exists(root) ? Path.GetDirectoryName(Path.GetFullPath(root))! : root;
        var gone = before.Where(f => !now.Contains(Id(f))).ToLookup(f => ProjectFiles.TryRead(Path.Combine(folder, f.File)) is not { } text || !Squeeze(StaticChecks.Masked(text)).Contains(Quoted(f.Quote), StringComparison.Ordinal));
        var marked = current.Select(f => f with { New = !then.Contains(Id(f)) }).ToList();

        try { File.WriteAllText(stateFile, JsonSerializer.Serialize(marked.Concat(gone[false]).Select(f => f with { New = false }).ToList())); }
        catch (IOException) { } // the report is still right; only the next run's comparison is lost
        return new(marked, [.. gone[true]], [.. gone[false]]);
    }

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
                    yield return file with { Text = part.ToString(), FirstLine = file.FirstLine + partStart - 1 };
                    part.Clear();
                    partStart = lineNumber;
                }
                part.Append(line).Append('\n');
            }
        }
        if (part.Length > 0) yield return file with { Text = part.ToString(), FirstLine = file.FirstLine + partStart - 1 };
    }

    static int Tokens(string text) => (text.Length + 3) / 4;
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string Key(List<ProjectFile> unit) => Hash(PromptVersion + string.Concat(unit.Select(f => f.Path + "\0" + f.Text + "\0")));
    static string Describe(List<ProjectFile> unit) => unit.Count == 1 ? unit[0].Path : $"{unit[0].Path} and {unit.Count - 1} more";
    static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

    static string Lines(ProjectFile part)
    {
        var last = part.FirstLine + part.Text.TrimEnd('\r', '\n').Count(c => c == '\n');
        return last == part.FirstLine ? $"{part.Path} line {last}" : $"{part.Path} lines {part.FirstLine}-{last}";
    }

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

    static string Report(string heading, int files, int skipped, List<string> failed, List<Finding> findings, List<string> ignored, History history)
    {
        var text = new StringBuilder($"# {heading}\n\n{DateTime.Now:yyyy-MM-dd HH:mm}. {Plural(files, "file")} read, {skipped} skipped");
        text.Append(failed.Count > 0 ? $", {Plural(failed.Count, "part")} not reviewed (listed at the end).\n" : ".\n");
        var fresh = findings.Count(f => f.New);
        if (history.Resolved.Count + history.Earlier.Count > 0 || fresh < findings.Count)
            text.Append($"\nSince the last review: {fresh} new, {findings.Count - fresh} still there, {history.Resolved.Count} resolved.\n");
        text.Append("\nThis is a small local model's opinion: check each finding. \"Verified\" only means the quoted line exists.\n");
        (string Title, Func<Finding, bool> In)[] groups =
        [
            ("Found by rules, without the model", f => f.Rule is not null),
            ("Verified", f => f.Verified && f.Rule is null),
            ("Ambiguous: the quoted line appears more than once", f => f.Ambiguous),
            ("Unverified: the quote isn't one exact line of the code", f => !f.Verified && !f.Ambiguous),
        ];
        foreach (var (title, inGroup) in groups)
        {
            var group = findings.Where(inGroup).OrderBy(f => Array.IndexOf(["high", "medium", "low"], f.Severity)).ToList();
            if (group.Count == 0) continue;
            text.Append($"\n## {title} ({group.Count})\n");
            foreach (var f in group)
                text.Append($"\n### [{f.Severity}] {f.File}{(f.Line is { } line ? $":{line}" : "")}{(f.Rule is { } rule ? $" `{rule}`" : "")}{(f.New ? " (new)" : "")}\n\n{f.Problem}\n\n`{f.Quote.Replace('`', '\'')}`\n")
                    .Append(string.IsNullOrWhiteSpace(f.Fix) ? "" : $"\n**Fix:** {f.Fix}\n");
        }
        if (findings.Count == 0) text.Append("\nNo findings.\n");
        if (history.Earlier.Count > 0)
            text.Append($"\n## From earlier reviews, not reported this time ({history.Earlier.Count})\n\nThe quoted line is still there. " +
                    "A small model doesn't report the same things every run, so these stay open until the line changes.\n\n")
                .Append(string.Concat(history.Earlier.Select(f => $"- [{f.Severity}] {f.File}: {f.Problem} `{f.Quote.Replace('`', '\'')}`\n")));
        if (history.Resolved.Count > 0)
            text.Append($"\n## Resolved since the last review ({history.Resolved.Count})\n\nThe line each one quoted is no longer in the file.\n\n")
                .Append(string.Concat(history.Resolved.Select(f => $"- [{f.Severity}] {f.File}: {f.Problem}\n")));
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
