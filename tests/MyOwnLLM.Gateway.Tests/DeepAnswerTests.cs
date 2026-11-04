using System.Runtime.CompilerServices;
using MyOwnLLM.Gateway.Checks;
using MyOwnLLM.Gateway.Deep;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>Answers each model call with the next reply, and remembers what was asked.</summary>
sealed class ScriptedLlama(params string[] replies) : ILlamaClient
{
    readonly Queue<string> _replies = new(replies);
    public List<(IReadOnlyList<ChatMessage> Messages, int MaxTokens, string? Model)> Calls { get; } = [];
    public Func<CancellationToken, Task>? BeforeReply { get; set; }

    string Next() => _replies.Count > 0 ? _replies.Dequeue() : "";

    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null)
    {
        Calls.Add((messages, maxTokens, model));
        return Task.FromResult(Next());
    }

    public async IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct, string? model = null)
    {
        Calls.Add((messages, maxTokens, model));
        if (BeforeReply is not null) await BeforeReply(ct);
        yield return new(Next());
    }

    public Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct, string? model = null) => Task.FromResult("");
    public Task<int?> ContextTokensAsync(CancellationToken ct) => Task.FromResult<int?>(16384);
}

public class DeepAnswerTests
{
    static readonly SnippetChecker Checker = new();
    const string Good = "Use this:\n```csharp\nservices.AddControllers();\n```\nDone.";
    const string Bad = "Use this:\n```csharp\nservices.AddFoo();\n```\nDone.";
    readonly List<string> _stages = [];

    async Task<DeepResult> Run(ScriptedLlama llama, CancellationToken ct = default) =>
        await DeepAnswer.RunAsync(llama, Checker, "deep", new PromptParts(null, null, [], "question"), 16384,
            (stage, _) => { if (_stages.LastOrDefault() != stage) _stages.Add(stage); return Task.CompletedTask; }, ct);

    [Fact]
    public async Task Stages_come_in_the_spec_order()
    {
        await Run(new ScriptedLlama("the plan", Good));
        Assert.Equal(["reading", "thinking", "checking the code", "answer"], _stages);
    }

    [Fact]
    public async Task The_plan_is_capped_and_never_appears_in_the_answer()
    {
        var llama = new ScriptedLlama("the plan", Good);
        var result = await Run(llama);

        Assert.Equal(400, llama.Calls[0].MaxTokens);
        Assert.Equal(1024, llama.Calls[1].MaxTokens);
        Assert.Contains("the plan", llama.Calls[1].Messages[^1].Content);
        Assert.DoesNotContain("the plan", result.Text);
        Assert.All(llama.Calls, c => Assert.Equal("deep", c.Model));
    }

    [Fact]
    public async Task Good_code_gets_no_repair()
    {
        var llama = new ScriptedLlama("plan", Good);
        var result = await Run(llama);
        Assert.Equal(2, llama.Calls.Count);
        Assert.Equal("checked: code compiles", result.CheckRow);
        Assert.Equal(Good, result.Text);
    }

    [Fact]
    public async Task A_bad_block_is_repaired_once_and_only_that_block_changes()
    {
        var llama = new ScriptedLlama("plan", "First:\n```bash\nls\n```\n" + Bad, "```csharp\nservices.AddControllers();\n```");
        var result = await Run(llama);

        Assert.Equal(3, llama.Calls.Count);
        Assert.Contains("AddFoo", llama.Calls[2].Messages[^1].Content);
        Assert.Equal(512, llama.Calls[2].MaxTokens);
        Assert.Contains("```bash\nls\n```", result.Text);
        Assert.Contains("AddControllers", result.Text);
        Assert.DoesNotContain("AddFoo", result.Text);
        Assert.EndsWith("Done.", result.Text);
        Assert.Equal("checked: 1 error fixed", result.CheckRow);
    }

    [Fact]
    public async Task A_repair_that_still_fails_keeps_the_answer_and_says_so()
    {
        var llama = new ScriptedLlama("plan", Bad, "```csharp\nservices.AddBar();\n```");
        var result = await Run(llama);

        Assert.Equal(3, llama.Calls.Count);
        Assert.Equal(Bad, result.Text);
        Assert.StartsWith("could not fix:", result.CheckRow);
        Assert.Contains("AddFoo", result.CheckRow);
    }

    [Fact]
    public async Task A_repair_with_no_code_block_keeps_the_original()
    {
        var result = await Run(new ScriptedLlama("plan", Bad, "I can't fix that."));
        Assert.Equal(Bad, result.Text);
        Assert.StartsWith("could not fix:", result.CheckRow);
    }

    [Fact]
    public async Task No_code_says_not_checked() =>
        Assert.Equal("not checked: no code", (await Run(new ScriptedLlama("plan", "Just words."))).CheckRow);

    [Fact]
    public async Task Package_code_says_what_it_needs() =>
        Assert.Equal("not checked: needs Microsoft.EntityFrameworkCore",
            (await Run(new ScriptedLlama("plan", "```csharp\nusing Microsoft.EntityFrameworkCore;\nclass Db : DbContext { }\n```"))).CheckRow);

    [Fact]
    public async Task An_empty_plan_or_a_cut_off_plan_still_answers()
    {
        var llama = new ScriptedLlama("", Good);
        var result = await Run(llama);
        Assert.Equal(Good, result.Text);
        Assert.DoesNotContain("notes", llama.Calls[1].Messages[^1].Content);
    }

    [Fact]
    public async Task Stopping_while_thinking_stops_without_an_answer()
    {
        using var stop = new CancellationTokenSource();
        var llama = new ScriptedLlama("plan", Good) { BeforeReply = ct => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(llama, stop.Token));
        Assert.DoesNotContain("answer", _stages);
    }
}
