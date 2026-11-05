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
    public List<(IReadOnlyList<ChatMessage> Messages, int MaxTokens, string? Model, double? Temperature)> Calls { get; } = [];
    public Func<CancellationToken, Task>? BeforeReply { get; set; }

    string Next() => _replies.Count > 0 ? _replies.Dequeue() : "";

    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null)
    {
        Calls.Add((messages, maxTokens, model, null));
        return Task.FromResult(Next());
    }

    public async IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct, string? model = null, double? temperature = null)
    {
        Calls.Add((messages, maxTokens, model, temperature));
        if (BeforeReply is not null) await BeforeReply(ct);
        yield return new(null, Progress: new(100, 0, 50));
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
        await Run(new ScriptedLlama(Good));
        Assert.Equal(["reading", "thinking", "checking the code", "answer"], _stages);
    }

    [Fact]
    public async Task The_answer_is_one_capped_call_to_the_smart_model()
    {
        var llama = new ScriptedLlama(Good);
        await Run(llama);

        Assert.Equal(1024, Assert.Single(llama.Calls).MaxTokens);
        Assert.Equal("question", llama.Calls[0].Messages[^1].Content);
        Assert.Equal("deep", llama.Calls[0].Model);
    }

    [Fact]
    public async Task Good_code_gets_no_repair()
    {
        var llama = new ScriptedLlama(Good);
        var result = await Run(llama);
        Assert.Single(llama.Calls);
        Assert.Equal("checked: code compiles", result.CheckRow);
        Assert.Equal(Good, result.Text);
    }

    [Fact]
    public async Task A_bad_block_is_repaired_once_and_only_that_block_changes()
    {
        var llama = new ScriptedLlama("First:\n```bash\nls\n```\n" + Bad, "```csharp\nservices.AddControllers();\n```");
        var result = await Run(llama);

        Assert.Equal(2, llama.Calls.Count);
        Assert.Contains("AddFoo", llama.Calls[1].Messages[^1].Content);
        Assert.Equal(512, llama.Calls[1].MaxTokens);
        Assert.Contains("```bash\nls\n```", result.Text);
        Assert.Contains("AddControllers", result.Text);
        Assert.DoesNotContain("AddFoo", result.Text);
        Assert.EndsWith("Done.", result.Text);
        Assert.Equal("checked: 1 error fixed", result.CheckRow);
    }

    [Fact]
    public async Task A_repair_and_a_fresh_answer_that_still_fail_keep_the_answer_and_say_so()
    {
        var llama = new ScriptedLlama(Bad, "```csharp\nservices.AddBar();\n```", Bad);
        var result = await Run(llama);

        Assert.Equal(3, llama.Calls.Count);
        Assert.Equal(Bad, result.Text);
        Assert.StartsWith("could not fix:", result.CheckRow);
        Assert.Contains("AddFoo", result.CheckRow);
    }

    [Fact]
    public async Task When_the_repair_fails_a_fresh_answer_that_compiles_is_kept()
    {
        var llama = new ScriptedLlama(Bad, "```csharp\nservices.AddBar();\n```", Good);
        var result = await Run(llama);

        Assert.Equal(3, llama.Calls.Count);
        Assert.Equal(llama.Calls[0].Messages, llama.Calls[2].Messages); // asked the same question again
        Assert.Null(llama.Calls[0].Temperature);
        Assert.True(llama.Calls[2].Temperature > 0.5); // the code temperature would give the same broken answer back
        Assert.Equal(Good, result.Text);
        Assert.Equal("checked: 1 error fixed", result.CheckRow);
        Assert.Contains("trying again", _stages);
    }

    [Fact]
    public async Task A_fresh_answer_without_code_never_replaces_one_with_code() =>
        Assert.Equal(Bad, (await Run(new ScriptedLlama(Bad, "no block", "Just words."))).Text);

    [Fact]
    public async Task A_repair_with_no_code_block_keeps_the_original()
    {
        var result = await Run(new ScriptedLlama(Bad, "I can't fix that."));
        Assert.Equal(Bad, result.Text);
        Assert.StartsWith("could not fix:", result.CheckRow);
    }

    [Fact]
    public async Task No_code_says_not_checked() =>
        Assert.Equal("not checked: no code", (await Run(new ScriptedLlama("Just words."))).CheckRow);

    [Fact]
    public async Task Package_code_says_what_it_needs() =>
        Assert.Equal("not checked: needs Microsoft.EntityFrameworkCore",
            (await Run(new ScriptedLlama("```csharp\nusing Microsoft.EntityFrameworkCore;\nclass Db : DbContext { }\n```"))).CheckRow);

    [Fact]
    public async Task Reading_progress_comes_through_so_a_long_read_shows_how_far_it_is()
    {
        var seen = new List<PromptProgress>();
        await DeepAnswer.RunAsync(new ScriptedLlama(Good), Checker, "deep", new PromptParts(null, null, [], "q"), 16384, (_, _) => Task.CompletedTask, CancellationToken.None,
            p => { seen.Add(p); return Task.CompletedTask; });
        Assert.Equal(50, seen[0].Processed);
    }

    [Fact]
    public async Task Stopping_while_thinking_stops_without_an_answer()
    {
        using var stop = new CancellationTokenSource();
        var llama = new ScriptedLlama(Good) { BeforeReply = ct => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(llama, stop.Token));
        Assert.DoesNotContain("answer", _stages);
    }
}
