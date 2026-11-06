using System.Text;
using System.Text.RegularExpressions;
using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Prompt;

/// <summary>
/// Finds the user's own earlier lines that answer the question, so they can go right next to it. Deep in a conversation,
/// or after a history cut, Granite 4.0-H Tiny swaps similar values or invents them; a short list of the facts placed
/// before the question brought it back to 12 of 12. Plain BM25 over the
/// conversation the CLI already sends: nothing is stored, and nothing a model wrote is pinned, because its answers can be wrong.
/// </summary>
public static partial class ConversationSearch
{
    const int MinHistoryTokens = 2000, RecentMessages = 4, MaxPinTokens = 250, MaxLineChars = 300, MaxLines = 8;
    const double K1 = 1.5, B = 0.75;

    internal static readonly HashSet<string> Stopwords =
    [
        "a", "about", "an", "and", "are", "at", "be", "can", "do", "does", "for", "from", "how", "i", "in", "is", "it", "many", "me",
        "much", "my", "of", "on", "or", "our", "that", "the", "this", "to", "was", "we", "what", "when", "where", "which", "who", "why",
        "will", "with", "you", "your",
    ];

    // ponytail: /read text and the model's answers aren't searched; add them when a test covers them. A line of its own is
    // what the recall test measured, and a full 250 tokens re-read each turn is about 8 s on a T480, paid only when that
    // many lines match. Eni-M accepts a little more time for accuracy.
    /// <returns>The best-matching earlier lines in conversation order, one per line, or null when nothing matches well.</returns>
    public static string? Pin(IReadOnlyList<ChatMessage> history, string question)
    {
        if (history.Sum(m => PromptBuilder.EstimateTokens(m.Content)) < MinHistoryTokens) return null;
        var asked = Terms(question).Distinct().ToList();
        if (asked.Count == 0) return null;

        // Lines of the user's own messages outside the last two exchanges. A line repeating the question is no answer to it.
        var own = new HashSet<string>(Lines(question), StringComparer.OrdinalIgnoreCase);
        var lines = history.Take(Math.Max(0, history.Count - RecentMessages)).Where(m => m.Role == "user")
            .SelectMany(m => Lines(m.Content)).Where(l => l.Length <= MaxLineChars && !own.Contains(l)).Distinct().ToList();
        if (lines.Count == 0) return null;

        var docs = lines.Select(l => Terms(l).ToList()).ToList();
        var averageLength = Math.Max(1, docs.Average(d => d.Count));
        var idf = asked.ToDictionary(t => t, t =>
        {
            var df = docs.Count(d => d.Contains(t));
            return Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
        });

        var ranked = new List<(int Index, double Score)>();
        for (var i = 0; i < docs.Count; i++)
        {
            var matched = asked.Count(docs[i].Contains);
            if (matched * 2 < asked.Count) continue; // at least half the question's words
            var score = asked.Sum(t =>
            {
                var tf = docs[i].Count(x => x == t);
                return idf[t] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * docs[i].Count / averageLength));
            });
            ranked.Add((i, score));
        }

        var taken = new List<int>();
        foreach (var (index, _) in ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Index))
        {
            if (taken.Count == MaxLines || PromptBuilder.EstimateTokens(Join([.. taken, index])) > MaxPinTokens) break;
            taken.Add(index);
        }
        return taken.Count == 0 ? null : Join(taken);

        string Join(IEnumerable<int> picked) => string.Join("\n", picked.Order().Select(i => lines[i]));
    }

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>Words split at case changes (OrderRepository is order, repository), stopwords dropped, longer words cut to
    /// their first 4 letters so retry and retries match.</summary>
    static IEnumerable<string> Terms(string text) =>
        Word().Matches(text.Normalize(NormalizationForm.FormC)).Select(m => m.Value.ToLowerInvariant())
            .Where(w => !Stopwords.Contains(w)).Select(w => w.Length > 4 ? w[..4] : w);

    [GeneratedRegex(@"\p{Lu}?\p{Ll}+|\p{Lu}+(?!\p{Ll})|\d+")] private static partial Regex Word();
}
