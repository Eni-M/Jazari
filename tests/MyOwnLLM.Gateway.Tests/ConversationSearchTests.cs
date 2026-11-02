using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

public class ConversationSearchTests
{
    /// <summary>The invented facts and questions from eval/recall.py.</summary>
    public static readonly TheoryData<string, string> Twelve = new()
    {
        { "The refund service retries 3 times before giving up.", "How many times does the refund service retry?" },
        { "Invoices are kept for 7 years.", "How long are invoices kept?" },
        { "The staging database listens on port 5433.", "Which port does the staging database listen on?" },
        { "Order ids start with the prefix ORD-.", "What prefix do order ids start with?" },
        { "The payment timeout is 45 seconds.", "What is the payment timeout?" },
        { "Deployments are frozen on Fridays.", "On which day are deployments frozen?" },
        { "The email service retries 5 times before giving up.", "How many times does the email service retry?" },
        { "Audit logs are kept for 2 years.", "How long are audit logs kept?" },
        { "The production database listens on port 6432.", "Which port does the production database listen on?" },
        { "Customer ids start with the prefix CUS-.", "What prefix do customer ids start with?" },
        { "The login timeout is 20 seconds.", "What is the login timeout?" },
        { "Backups run on Sundays.", "On which day do backups run?" },
    };

    /// <summary>Unrelated pairs, enough to put the history over 2,000 tokens and earlier lines outside the last 4 messages.</summary>
    internal static List<ChatMessage> Padding(int pairs = 8) => Enumerable.Range(0, pairs).SelectMany(i => new ChatMessage[]
    {
        new("user", $"Garden note {i}: twelve yellow tulips grow beside the old stone wall.\nThe pond near bench {i} has three ducks.\nPlease describe a quiet walk."),
        new("assistant", "A quiet walk follows the gravel path past the orchard and the greenhouse. " + string.Concat(Enumerable.Repeat("Leaves drift slowly over the lawn. ", 30))),
    }).ToList();

    /// <summary>A user/assistant pair for each line, then <see cref="Padding"/>.</summary>
    internal static List<ChatMessage> History(params string[] userLines) =>
        [.. userLines.SelectMany(line => new ChatMessage[] { new("user", line), new("assistant", "Noted.") }), .. Padding()];

    internal static List<ChatMessage> HistoryWithAllFacts() => History([.. Twelve.Select(row => (string)row[0])]);

    [Theory, MemberData(nameof(Twelve))]
    public void Finds_the_line_for_each_question(string fact, string question) =>
        Assert.Contains(fact, ConversationSearch.Pin(HistoryWithAllFacts(), question));

    [Fact]
    public void Short_conversation_pins_nothing() =>
        Assert.Null(ConversationSearch.Pin([new("user", "The payment timeout is 45 seconds."), new("assistant", "ok")], "What is the payment timeout?"));

    [Fact]
    public void The_last_two_exchanges_are_not_searched()
    {
        List<ChatMessage> history = [.. Padding(), new("user", "The payment timeout is 45 seconds."), new("assistant", "ok"), new("user", "Thanks."), new("assistant", "ok")];

        Assert.Null(ConversationSearch.Pin(history, "What is the payment timeout?"));
    }

    [Fact]
    public void The_models_own_answers_are_not_searched()
    {
        List<ChatMessage> history = [new("user", "Remind me of the settings."), new("assistant", "The payment timeout is 45 seconds."), .. Padding()];

        Assert.Null(ConversationSearch.Pin(history, "What is the payment timeout?"));
    }

    [Fact]
    public void A_question_with_no_matching_words_pins_nothing()
    {
        Assert.Null(ConversationSearch.Pin(HistoryWithAllFacts(), "What is dependency injection?"));
        Assert.Null(ConversationSearch.Pin(HistoryWithAllFacts(), "go on"));
    }

    [Fact]
    public void Pin_stays_inside_its_budget()
    {
        var lines = Enumerable.Range(10, 30).Select(i => $"The payment timeout for region {i} is {i} seconds, " + new string('x', 200)).ToArray();

        var pin = ConversationSearch.Pin(History(lines), "What is the payment timeout?");

        Assert.NotNull(pin);
        Assert.InRange(PromptBuilder.EstimateTokens(pin), 1, 250);
    }

    [Fact]
    public void Lines_come_back_in_conversation_order()
    {
        var pin = ConversationSearch.Pin(History("The login timeout is 20 seconds.", "The payment timeout is 45 seconds."), "What is the payment timeout?");

        Assert.Equal("The login timeout is 20 seconds.\nThe payment timeout is 45 seconds.", pin);
    }

    [Fact]
    public void Fact_beats_code_lines_that_share_its_words()
    {
        var code = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"var paymentClient{i} = factory.Create(timeout: settings.Limit{i}, name: \"svc{i}\", retries: policy.Count);"));

        var pin = ConversationSearch.Pin(History(code, "The payment timeout is 45 seconds."), "What is the payment timeout?");

        Assert.Contains("The payment timeout is 45 seconds.", pin);
    }

    [Fact]
    public void A_very_long_line_is_skipped()
    {
        var pin = ConversationSearch.Pin(History("The payment timeout is 45 seconds. " + new string('x', 300)), "What is the payment timeout?");

        Assert.True(pin is null || !pin.Contains("45 seconds"));
    }

    [Fact]
    public void An_earlier_copy_of_the_question_is_not_pinned()
    {
        var pin = ConversationSearch.Pin(History("What is the payment timeout?", "The payment timeout is 45 seconds."), "What is the payment timeout?");

        Assert.Equal("The payment timeout is 45 seconds.", pin);
    }

    [Fact]
    public void Accented_words_match() =>
        Assert.Contains("Porta e bazës është 5433", ConversationSearch.Pin(History("Porta e bazës është 5433"), "Cila është porta e bazës?"));
}
