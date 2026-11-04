using System.Text;
using MyOwnLLM.Gateway.Checks;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Deep;

public sealed record DeepResult(string Text, string CheckRow, bool CutOff, AnswerStats? Stats, int DroppedMessages, bool UsedPassages);

/// <summary>
/// /deep: a short plan, the answer, the compiler's opinion, and one repair of a block that doesn't compile. The evidence says
/// more than one round doesn't pay at this model size (docs/superpowers/specs/2026-11-04-deep-mode-design.md). The text is
/// held until it's checked, because what's on screen can't be taken back after a repair; stages say what's going on meanwhile.
/// </summary>
public static class DeepAnswer
{
    public const int PlanTokens = 400, AnswerTokens = 1024, RepairTokens = 512, HistoryMessages = 8, PassageTokens = 600;
    const string PlanRequest = "\n\nBefore answering, write a short plan of what the answer needs: a few plain lines, no code.";
    const string PlanNotes = "\n\nYour own notes for this answer. Use them, and don't repeat or mention them:\n";

    public static async Task<DeepResult> RunAsync(ILlamaClient llama, SnippetChecker checker, string? model, PromptParts parts, int contextTokens,
        Func<string, int?, Task> stage, CancellationToken ct)
    {
        var prompt = PromptBuilder.Build(parts with { KeepHistory = HistoryMessages }, contextTokens, AnswerTokens + PlanTokens, out var dropped, out var usedPassages);
        var question = prompt[^1];
        var start = prompt.SkipLast(1).ToList();

        await stage("reading", null);
        var (plan, _, _) = await GenerateAsync(llama, [.. start, question with { Content = question.Content + PlanRequest }], PlanTokens, model, stage, 0, ct);
        var written = Estimate(plan);
        var withPlan = string.IsNullOrWhiteSpace(plan) ? question : question with { Content = question.Content + PlanNotes + plan.Trim() };
        var (answer, cutOff, stats) = await GenerateAsync(llama, [.. start, withPlan], AnswerTokens, model, stage, written, ct);

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
        await stage("answer", null);
        return new(text, CheckRow(before, after), cutOff, stats, dropped, usedPassages);
    }

    static string RepairRequest(string code, IReadOnlyList<string> errors) =>
        $"This C# code doesn't compile:\n```csharp\n{code}\n```\nThe compiler says:\n{string.Join('\n', errors)}\nGive the corrected code, using only APIs that exist, in one code block and nothing else.";

    static async Task<(string Text, bool CutOff, AnswerStats? Stats)> GenerateAsync(ILlamaClient llama, IReadOnlyList<ChatMessage> messages, int maxTokens, string? model,
        Func<string, int?, Task> stage, int writtenBefore, CancellationToken ct)
    {
        var text = new StringBuilder();
        var (cutOff, stats, parts) = (false, (AnswerStats?)null, 0);
        await foreach (var part in llama.ChatStreamAsync(messages, maxTokens, ct, model))
        {
            cutOff |= part.CutOff;
            stats = part.Stats ?? stats;
            if (part.Text is not { } piece) continue;
            text.Append(piece);
            // A stream part is about a token. Not on every one: nobody reads a count that fast.
            if (parts++ % 8 == 0) await stage("thinking", writtenBefore + parts);
        }
        return (text.ToString(), cutOff, stats);
    }

    static int Estimate(string text) => PromptBuilder.EstimateTokens(text);
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
