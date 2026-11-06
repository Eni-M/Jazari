using System.Text;
using MyOwnLLM.Gateway.Checks;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Deep;

public sealed record DeepResult(string Text, string CheckRow, bool CutOff, AnswerStats? Stats, int DroppedMessages, bool UsedPassages);

/// <summary>
/// /deep: the answer, the compiler's opinion, and one repair of a block that doesn't compile. The evidence says
/// more than one round doesn't pay at this model size. The text is
/// held until it's checked, because what's on screen can't be taken back after a repair; stages say what's going on meanwhile.
/// </summary>
public static class DeepAnswer
{
    // No plan call first: with one, Qwen3 4B scored 39/45 at a 61 s median, and 41/45 at 17 s without (2026-11-04-deep-model-results.md).
    const double FreshTemperature = 0.8; // llama-server's own default
    public const int AnswerTokens = 1024, RepairTokens = 512, HistoryMessages = 8, PassageTokens = 600;

    public static async Task<DeepResult> RunAsync(ILlamaClient llama, SnippetChecker checker, string? model, PromptParts parts, int contextTokens,
        Func<string, int?, Task> stage, CancellationToken ct, Func<PromptProgress, Task>? reading = null)
    {
        var prompt = PromptBuilder.Build(parts with { KeepHistory = HistoryMessages }, contextTokens, AnswerTokens, out var dropped, out var usedPassages);
        var start = prompt.SkipLast(1).ToList();

        await stage("reading", null);
        var (answer, cutOff, stats) = await GenerateAsync(llama, prompt, AnswerTokens, model, stage, reading, ct);

        await stage("checking the code", null);
        checker.Load();
        var before = checker.Check(answer, ct);
        var after = before;
        var text = answer;
        if (before.FirstOrDefault(c => c.State == CheckState.DoesNotCompile) is { } broken && SnippetChecker.BlockCode(answer, broken.Block) is { } code)
        {
            var reply = await llama.ChatAsync([.. start, new("user", RepairRequest(code, broken.Errors))], RepairTokens, ct, model: model);
            if (SnippetChecker.BlockCode(reply, 1) is { Length: > 0 } repaired)
            {
                var candidate = SnippetChecker.ReplaceBlock(answer, broken.Block, repaired);
                var checkedAgain = checker.Check(candidate, ct);
                if (ErrorCount(checkedAgain) < ErrorCount(before)) (text, after) = (candidate, checkedAgain);
            }
        }
        // Best of two (H3 in 2026-11-04-what-next.md): still broken after the repair, so one fresh answer, kept only if all its
        // code compiles. Time goes only where the compiler has shown a problem. Sampled warmer than code's 0.2, which would
        // mostly give the same broken answer back.
        if (after.Any(c => c.State == CheckState.DoesNotCompile))
        {
            await stage("trying again", null);
            var (fresh, freshCutOff, freshStats) = await GenerateAsync(llama, prompt, AnswerTokens, model, stage, reading, ct, FreshTemperature);
            var freshChecks = checker.Check(fresh, ct);
            if (freshChecks.Any(c => c.State == CheckState.Compiles) && !freshChecks.Any(c => c.State == CheckState.DoesNotCompile))
                (text, after, cutOff, stats) = (fresh, freshChecks, freshCutOff, freshStats ?? stats);
        }
        await stage("answer", null);
        return new(text, CheckRow(before, after), cutOff, stats, dropped, usedPassages);
    }

    static string RepairRequest(string code, IReadOnlyList<string> errors) =>
        $"This C# code doesn't compile:\n```csharp\n{code}\n```\nThe compiler says:\n{string.Join('\n', errors)}\nGive the corrected code, using only APIs that exist, in one code block and nothing else.";

    static async Task<(string Text, bool CutOff, AnswerStats? Stats)> GenerateAsync(ILlamaClient llama, IReadOnlyList<ChatMessage> messages, int maxTokens, string? model,
        Func<string, int?, Task> stage, Func<PromptProgress, Task>? reading, CancellationToken ct, double? temperature = null)
    {
        var text = new StringBuilder();
        var (cutOff, stats, parts) = (false, (AnswerStats?)null, 0);
        await foreach (var part in llama.ChatStreamAsync(messages, maxTokens, ct, model, temperature))
        {
            cutOff |= part.CutOff;
            stats = part.Stats ?? stats;
            if (part.Progress is { } progress && reading is not null) await reading(progress);
            if (part.Text is not { } piece) continue;
            text.Append(piece);
            // A stream part is about a token. Not on every one: nobody reads a count that fast.
            if (parts++ % 8 == 0) await stage("thinking", parts);
        }
        return (text.ToString(), cutOff, stats);
    }

    static int ErrorCount(IReadOnlyList<BlockCheck> checks) => checks.Sum(c => c.Errors.Count);

    /// <summary>The row that ends a deep answer.</summary>
    public static string CheckRow(IReadOnlyList<BlockCheck> before, IReadOnlyList<BlockCheck> after)
    {
        if (after.FirstOrDefault(c => c.State == CheckState.DoesNotCompile) is { } still) return $"could not fix: {still.Errors[0]}";
        var fixedErrors = ErrorCount(before);
        if (fixedErrors > 0) return $"checked: {fixedErrors} error{(fixedErrors == 1 ? "" : "s")} fixed";
        if (after.Any(c => c.State == CheckState.Compiles)) return "checked: code compiles";
        return after.FirstOrDefault(c => c.Why is not null) is { Why: { } why } ? $"not checked: {why}" : "not checked: no code";
    }
}
