using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Build_orders_system_state_context_history_question()
    {
        var parts = new PromptParts("NOTE", "CONTEXT", [new("user", "earlier"), new("assistant", "reply")], "QUESTION");

        var messages = PromptBuilder.Build(parts, 16384, 512);

        Assert.Equal(["system", "system", "system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal(PromptBuilder.SystemPrompt, messages[0].Content);
        Assert.Contains("NOTE", messages[1].Content);
        Assert.Contains("CONTEXT", messages[2].Content);
        Assert.Equal("QUESTION", messages[^1].Content);
    }

    [Fact]
    public void Build_omits_missing_parts()
    {
        var messages = PromptBuilder.Build(new PromptParts(null, null, [], "Q"), 16384, 512);

        Assert.Equal([new ChatMessage("system", PromptBuilder.SystemPrompt), new ChatMessage("user", "Q")], messages);
    }

    [Fact]
    public void Build_keeps_identical_prefix_for_same_state()
    {
        var a = PromptBuilder.Build(new PromptParts("NOTE", "CTX", [], "first?"), 16384, 512);
        var b = PromptBuilder.Build(new PromptParts("NOTE", "CTX", [], "second?"), 16384, 512);

        Assert.Equal(a.Take(a.Count - 1), b.Take(b.Count - 1));
    }

    [Fact]
    public void Trimmed_history_keeps_the_same_start_for_the_next_turn()
    {
        static List<ChatMessage> Pairs(int n) => Enumerable.Range(0, n).SelectMany(i => new ChatMessage[]
        {
            new("user", $"q{i} " + new string('u', 8000)),
            new("assistant", $"a{i} " + new string('a', 8000)),
        }).ToList();

        var turn9 = PromptBuilder.Build(new PromptParts(null, null, Pairs(9), "Q"), 16384, 512);
        var turn10 = PromptBuilder.Build(new PromptParts(null, null, Pairs(10), "Q"), 16384, 512);

        Assert.StartsWith("q8 ", turn9[1].Content);
        Assert.StartsWith("q8 ", turn10[1].Content); // same prefix, so the prompt cache still holds
    }

    [Fact]
    public void Build_drops_oldest_history_to_fit()
    {
        var history = Enumerable.Range(0, 10).SelectMany(i => new ChatMessage[]
        {
            new("user", $"q{i} " + new string('u', 8000)),
            new("assistant", $"a{i} " + new string('a', 8000)),
        }).ToList();

        var messages = PromptBuilder.Build(new PromptParts(null, null, history, "QUESTION"), 16384, 512);

        Assert.True(messages.Sum(m => PromptBuilder.EstimateTokens(m.Content)) <= 16384 - 512);
        Assert.Contains(messages, m => m.Content.StartsWith("q9 "));
        Assert.Contains(messages, m => m.Content.StartsWith("a9 "));
        Assert.DoesNotContain(messages, m => m.Content.StartsWith("q0 "));
        Assert.Equal("QUESTION", messages[^1].Content);
    }

    [Fact]
    public void SystemPrompt_names_Jazari() => Assert.StartsWith("You are Jazari", PromptBuilder.SystemPrompt);

    [Fact]
    public void Build_throws_when_over_budget()
    {
        var parts = new PromptParts(null, null, [], new string('x', 70_000));

        var ex = Assert.Throws<InputTooLargeException>(() => PromptBuilder.Build(parts, 16384, 512));

        Assert.True(ex.Estimated >= 17500);
        Assert.Equal(15872, ex.Limit);
        Assert.StartsWith("input too large (~", ex.Message);
    }
}
