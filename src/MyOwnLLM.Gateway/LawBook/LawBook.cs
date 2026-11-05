using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway;

/// <summary>A piece of Microsoft's .NET docs, and the page it came from.</summary>
public sealed record Passage(string Title, string Url, string Text);

/// <summary>
/// Finds the passage of Microsoft's .NET docs that covers a question, from the file lawbook/build.py makes, or nothing.
/// Nothing is safe: the model then answers as it would without a book. A passage from the wrong page is not, since the model
/// repeats it. So the search finds the page first, from what the page says it's about, and only then a passage inside it.
/// On 47 practice questions, ranking passages directly sent a wrong page 13 times and a right one 25; this sends 2 and 27.
///
/// There is no score threshold on purpose. reame measured similarity at a median of 0.895 for answerable questions and
/// 0.879 for traps, so no threshold separates them (reports/Local inference sources for Jazari.md). Instead each step
/// below rules a page or passage out on something it can check: a name or version it lacks, a word no passage holds, too few of
/// the question's words.
/// </summary>
public sealed partial class LawBook(GatewayOptions options)
{
    const int MaxTerms = 32, PagesTried = 3, PassagesRead = 40, MaxPassages = 2, MaxTokens = 300;
    const int MaxQuestionChars = 4000; // a pasted stack trace's start says what it's about; the rest only costs time

    /// <summary>Words that aren't searched for. ConversationSearch's stopwords, except "many", which one-to-many needs. Small
    /// talk, so "hello" or "go on" doesn't find a page. Product names, which every page of their docs repeats in its title,
    /// so they only favoured long pages: dropping them, and keeping "many", took the dev set from 14 to 17 of 20 found.
    /// Words any how-to question can hold, which say nothing about its topic but counted towards a page's share.</summary>
    static readonly HashSet<string> Ignored =
    [
        .. ConversationSearch.Stopwords.Where(w => w != "many"),
        "hello", "hi", "hey", "thanks", "thank", "ok", "okay", "yes", "no", "sure", "please", "go", "bye",
        "asp", "net", "core", "ef", "entity", "framework", "dotnet", "aspnetcore", "csharp",
        "should", "would", "could", "use", "using", "used", "want", "need", "get", "make", "way", "best", "app", "apps", "application", "code",
    ];

    /// <summary>Names everyone writes that a passage needn't repeat: a minimal API page rarely says ASP.NET.</summary>
    static readonly HashSet<string> CommonNames = new(StringComparer.OrdinalIgnoreCase) { "ASP.NET", "VB.NET", "Node.js", "Vue.js", "Next.js" };

    readonly string _connection = new SqliteConnectionStringBuilder
    {
        DataSource = options.LawBookFile,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false, // jazari update replaces the file while the gateway runs; on Windows an open handle would block it
    }.ToString();

    /// <param name="maxTokens">How much passage text may come back. /deep allows more.</param>
    public IReadOnlyList<Passage> Search(string question, int maxTokens = MaxTokens)
    {
        if (!File.Exists(options.LawBookFile)) return [];
        try
        {
            using var db = new SqliteConnection(_connection);
            db.Open();
            return Search(db, question.Length > MaxQuestionChars ? question[..MaxQuestionChars] : question, maxTokens);
        }
        catch (SqliteException)
        {
            return []; // missing, damaged or not a law book: Jazari answers as it would without one
        }
    }

    /// <summary>
    /// Too few words to search on, in a conversation: "more" found a page titled Learn more, and "continue" one on
    /// continuation tasks, and the model answered those instead of following up.
    /// ponytail: a short new topic with no API name ("what is middleware?") loses its passage too. Search with the
    /// previous question's words if a follow-up test set shows that costs answers.
    /// </summary>
    public static bool IsFollowUp(string question) => Terms(question).Count < 3 && Named(question).Count == 0;

    static IReadOnlyList<Passage> Search(SqliteConnection db, string question, int maxTokens)
    {
        // 1. Terms.
        var terms = Terms(question);
        if (terms.Count == 0) return [];

        // 2. Uncovered word: a word in no passage at all means the docs don't know what the question is about.
        var counts = terms.ToDictionary(t => t, t => Query(db, "SELECT count(*) FROM chunks WHERE chunks MATCH $q", Quote(t), r => r.GetInt64(0))[0]);
        if (counts.Any(c => c.Value == 0 && !IsNumber(c.Key))) return [];
        var rarest = terms.Where(t => counts[t] > 0).MinBy(t => counts[t]) ?? terms[0];

        // 3. The likeliest pages, by title, description and address. Words in the address weigh most: it's the docs' own
        // short name for the topic, and on the practice questions it found more than the title did.
        var pages = Query(db, $"SELECT url FROM pages WHERE pages MATCH $q ORDER BY bm25(pages, 1.0, 1.0, 3.0) LIMIT {PagesTried}", Either(terms), r => r.GetString(0));
        var named = Named(question);
        foreach (var page in pages)
        {
            // 4. Half the question's words must be in what the page says it's about, or it only shares a word or two with it.
            if (terms.Count(t => Query(db, "SELECT 1 FROM pages WHERE pages MATCH $q AND url = $u LIMIT 1", Quote(t), r => r.GetInt32(0), page).Count > 0) * 2 < terms.Count) continue;

            // 5. Inside it, the passages that hold the rarest word, which says most about what's asked, and every API name and
            // version the question names.
            const string InPage = "WHERE chunks MATCH $q AND (url = $u OR substr(url, 1, length($u) + 1) = $u || '#')";
            var holding = Query(db, $"SELECT rowid FROM chunks {InPage}", Quote(rarest), r => r.GetInt64(0), page).ToHashSet();
            var found = Query(db, $"SELECT rowid, title, url, text FROM chunks {InPage} ORDER BY bm25(chunks, 1.0, 1.0) LIMIT {PassagesRead}", Either(terms),
                    r => (Id: r.GetInt64(0), Passage: new Passage(r.GetString(1), r.GetString(2), r.GetString(3))), page)
                .Where(f => holding.Contains(f.Id) && named.All(n => n.IsMatch(f.Passage.Title + "\n" + f.Passage.Text)))
                .OrderByDescending(f => HeadingWords(f.Passage.Title, terms)).ToList(); // stable, so word matches still decide a tie
            if (found.Count == 0) continue;

            // 6. Take, sections named for the question first: on the hard practice questions this doubled how often the
            // passage held the API asked about, from 3 of 17 to 6. From this page alone: two pages joined together read to the model as one text.
            var taken = new List<Passage>();
            foreach (var (_, passage) in found)
            {
                if (taken.Count == MaxPassages || taken.Sum(p => PromptBuilder.EstimateTokens(p.Text)) + PromptBuilder.EstimateTokens(passage.Text) > maxTokens) break;
                taken.Add(passage);
            }
            return taken;
        }
        return [];
    }

    static List<T> Query<T>(SqliteConnection db, string sql, string match, Func<SqliteDataReader, T> read, string? page = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", match);
        if (page is not null) cmd.Parameters.AddWithValue("$u", page);
        using var r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(read(r));
        return rows;
    }

    /// <summary>How many of the question's words the section's heading holds, by their first letters: register finds Registering.</summary>
    static int HeadingWords(string title, List<string> terms)
    {
        var heading = title[(title.IndexOf(':') + 1)..];
        return terms.Count(t => heading.Contains(t.Length > 5 ? t[..5] : t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each term in double quotes: FTS5 reads it as a plain word, whatever the question holds.</summary>
    static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";
    static string Either(IEnumerable<string> terms) => string.Join(" OR ", terms.Select(Quote));
    static bool IsNumber(string term) => term.All(char.IsDigit);

    internal static List<string> Terms(string question) =>
        Word().Matches(question.Normalize(NormalizationForm.FormC)).Select(m => m.Value.ToLowerInvariant())
            .Where(w => (w.Length > 1 || IsNumber(w)) && !Ignored.Contains(w))
            .Distinct().Take(MaxTerms).ToList();

    /// <summary>API names (an inner capital or a dot: FrobnicateAsync, Results.NotFound, IDisposable) and versions
    /// (.NET 8, C# 12, EF Core 9), as patterns a passage must match, ignoring case.</summary>
    internal static List<Regex> Named(string question)
    {
        var names = Identifier().Matches(question).Select(m => m.Value)
            .Where(n => !CommonNames.Contains(n) && (n.Contains('.') ? n.Split('.').All(s => s.Length > 1) : n.Skip(1).Any(char.IsUpper) && n.Any(char.IsLower)))
            .Select(n => new Regex(Regex.Escape(n), RegexOptions.IgnoreCase));
        var versions = Version().Matches(question).Select(m =>
            new Regex(Regex.Escape(m.Groups["what"].Value).Replace(@"\ ", @"\s*") + @"\s*" + Regex.Escape(m.Groups["number"].Value) + @"(?!\d)", RegexOptions.IgnoreCase));
        return [.. names.Concat(versions).DistinctBy(r => r.ToString())];
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")] private static partial Regex Word();
    [GeneratedRegex(@"(?<![\w.])[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*")] private static partial Regex Identifier();
    [GeneratedRegex(@"(?<what>\.NET(?: Core)?|C#|EF Core|ASP\.NET Core) ?(?<number>\d+(?:\.\d+)?)(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
}
