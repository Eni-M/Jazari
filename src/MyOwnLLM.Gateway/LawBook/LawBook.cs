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
///
/// A question says "update many rows" where the docs say ExecuteUpdate, or "HSTS" where a heading says HTTP Strict Transport
/// Security, and most right pages were thrown away because their title and description didn't hold the question's words. So
/// the book also keeps, made from the docs alone: each passage's API names as words (chunk_names), each page's headings and API
/// names (page_terms) and the abbreviations the docs spell out (aliases). Facets from the address keep the search to the
/// product a question names, and leave out breaking-change, rule and compiler-error pages unless it asks about those.
///
/// With the two small search models and the book's vectors (round 2 of the report), candidates come from words and from meaning,
/// fused by rank, and a reranker that reads the question with each passage picks one or sends nothing. Without them, the word
/// search above runs alone.
/// </summary>
public sealed partial class LawBook
{
    const int MaxTerms = 32, PagesTried = 3, PagesRanked = 10, PassagesRead = 40, MaxPassages = 2, MaxTokens = 300;
    const int ByWords = 30, ByNames = 10, ByMeaning = 30, Reranked = 20;
    /// <summary>The reranker's score a passage needs to be sent, after <see cref="Demoted"/>. ms-marco scores are logits; on the
    /// dev set every trap's best passage scored below 2.3, and 0 let 3 of 15 traps through.</summary>
    public const float Cutoff = 2.5f;

    /// <summary>Pages that read like an answer but aren't the reference for it: the architecture e-books, the EF tutorials and the
    /// tutorials, and Blazor's pages for a question that doesn't say Blazor. They often outscored the page itself; this much less
    /// took the dev set from 36 right and 11 wrong to 38 and 9, before the cutoff.</summary>
    static readonly string[] Secondary = ["/dotnet/architecture/", "/aspnet/core/data/ef-mvc/", "/aspnet/core/data/ef-rp/", "/aspnet/core/tutorials/"];
    const float Demotion = 2f;

    static float Demoted(string url, string question) =>
        (Secondary.Any(url.Contains) ? Demotion : 0) +
        (url.Contains("/blazor/") && !question.Contains("blazor", StringComparison.OrdinalIgnoreCase) ? Demotion : 0);

    readonly GatewayOptions options;
    readonly Lazy<ISearchModels?> _models;
    (string File, DateTime Written, VectorIndex? Index)? _vectors;
    readonly object _vectorsLock = new();

    public LawBook(GatewayOptions options) : this(options, new Lazy<ISearchModels?>(() => OnnxSearchModels.Load(options.SearchModelsDir))) { }

    LawBook(GatewayOptions options, Lazy<ISearchModels?> models)
    {
        this.options = options;
        _models = models;
        _connection = new SqliteConnectionStringBuilder
        {
            DataSource = options.LawBookFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, // jazari update replaces the file while the gateway runs; on Windows an open handle would block it
        }.ToString();
    }

    /// <summary>A law book searched with these models instead of the ones in <see cref="GatewayOptions.SearchModelsDir"/>.</summary>
    public static LawBook WithModels(GatewayOptions options, ISearchModels? models) => new(options, new Lazy<ISearchModels?>(() => models));
    const int Rrf = 60; // reciprocal rank fusion's usual constant: a page's score is the sum of 1/(60 + rank) over the rankings

    /// <summary>Pages a how-to question shouldn't get: breaking changes, code analysis rules and compiler errors. They fill a
    /// fifth of the book and share a question's words while being about something else.</summary>
    static readonly string[] AskedForOnly = ["/compatibility/", "/code-analysis/", "/syslib-diagnostics/", "/aspnet/core/diagnostics/", "/compiler-messages/"];

    /// <summary>One of the question's words, and the FTS5 expression that finds it: the word, or its long form too.</summary>
    sealed record Concept(string Word, string Match);
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

    readonly string _connection;

    /// <param name="maxTokens">How much passage text may come back. /deep allows more.</param>
    public IReadOnlyList<Passage> Search(string question, int maxTokens = MaxTokens)
    {
        if (!File.Exists(options.LawBookFile)) return [];
        try
        {
            using var db = new SqliteConnection(_connection);
            db.Open();
            var models = _models.Value;
            var index = models is null ? null : Vectors(db);
            return Search(db, question.Length > MaxQuestionChars ? question[..MaxQuestionChars] : question, maxTokens, index is null ? null : (models!, index));
        }
        catch (SqliteException)
        {
            return []; // missing, damaged or not a law book: Jazari answers as it would without one
        }
    }

    /// <summary>The book's vectors, read once per version of the file: jazari update replaces it, and its time changes.</summary>
    VectorIndex? Vectors(SqliteConnection db)
    {
        var written = File.GetLastWriteTimeUtc(options.LawBookFile);
        lock (_vectorsLock)
        {
            if (_vectors is not { } v || v.File != options.LawBookFile || v.Written != written)
                _vectors = v = (options.LawBookFile, written, VectorIndex.Load(db));
            return v.Index;
        }
    }

    /// <summary>For embed-law-book: writes the vectors into the book, with the models in <see cref="GatewayOptions.SearchModelsDir"/>.</summary>
    public static bool Embed(GatewayOptions options, TextWriter progress)
    {
        using var models = OnnxSearchModels.Load(options.SearchModelsDir);
        if (models is null) { progress.WriteLine($"no search models in {options.SearchModelsDir}"); return false; }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.LawBookFile, Pooling = false }.ToString());
        db.Open();
        BookVectors.Write(db, models, progress: progress);
        return true;
    }

    /// <summary>
    /// Too few words to search on, in a conversation: "more" found a page titled Learn more, and "continue" one on
    /// continuation tasks, and the model answered those instead of following up.
    /// ponytail: a short new topic with no API name ("what is middleware?") loses its passage too. Search with the
    /// previous question's words if a follow-up test set shows that costs answers.
    /// </summary>
    public static bool IsFollowUp(string question) => Terms(question).Count < 3 && Named(question).Count == 0;

    static IReadOnlyList<Passage> Search(SqliteConnection db, string question, int maxTokens, (ISearchModels Models, VectorIndex Index)? ranked)
    {
        // 1. Terms, each with the long form the docs spell its abbreviation out as.
        var terms = Terms(question);
        if (terms.Count == 0) return [];
        var concepts = Concepts(db, terms);
        var any = string.Join(" OR ", concepts.Select(c => c.Match));

        // 2. Uncovered word: a word in no passage at all, nor in the words of its API names, means the docs don't know what the
        // question is about. A made-up name is one word, saveeverythingasync, so its parts being real words doesn't cover it.
        // How rare a word is comes from the passages' text, and from the API names' words only when the text never has it:
        // adding the two made "clock" rarer than "fake", and the fake-clock page, which never says clock, lost its answer.
        var counts = concepts.ToDictionary(c => c, c => Count(db, "chunks", c.Match) is var n and > 0 ? n : Count(db, "chunk_names", c.Match));
        if (counts.Any(c => c.Value == 0 && !IsNumber(c.Key.Word))) return [];
        var rarest = concepts.Where(c => counts[c] > 0).MinBy(c => counts[c]) ?? concepts[0];

        // 3. Facets: the product the question names, searched first; C#'s own docs answer "records in ASP.NET Core", so when
        // nothing there passes, the rest of the docs are tried. Pages only an error code or an upgrade asks for, left out.
        var (product, leaveOutKinds) = Facets(question);
        var named = Named(question);
        var fromProduct = Find(product, leaveOutKinds);
        return fromProduct.Count > 0 || product is null ? fromProduct : Find(null, leaveOutKinds);

        IReadOnlyList<Passage> Find(string? root, bool leaveOut) =>
            ranked is { } r ? FromPassages(r.Models, r.Index, root, leaveOut) : FromPages(FacetSql(root, leaveOut));

        // Round 2: candidates by words and by meaning, fused by rank, and the reranker picks, or sends nothing.
        IReadOnlyList<Passage> FromPassages(ISearchModels models, VectorIndex index, string? root, bool leaveOut)
        {
            var allowed = FacetSql(root, leaveOut);
            Func<SqliteDataReader, (long Id, Passage Passage)> read = r => (r.GetInt64(0), new Passage(r.GetString(1), r.GetString(2), r.GetString(3)));
            var byWords = Query(db, $"SELECT rowid, title, url, text FROM chunks WHERE chunks MATCH $q {allowed} ORDER BY bm25(chunks, 1.0, 1.0) LIMIT {ByWords}", any, read)
                .Concat(Query(db, $"SELECT c.rowid, c.title, c.url, c.text FROM chunk_names n JOIN chunks c ON c.rowid = n.rowid WHERE chunk_names MATCH $q {allowed.Replace("instr(url", "instr(c.url")} ORDER BY bm25(chunk_names) LIMIT {ByNames}", any, read))
                .DistinctBy(f => f.Id).ToList();
            var byMeaning = index.Nearest(models.Embed([OnnxSearchModels.AsQuestion(question)])[0], ByMeaning, url => FacetAllows(url, root, leaveOut)).ToList();
            var ids = Fuse([.. byWords.Select(f => f.Id)], byMeaning).Take(Reranked).ToList();
            var known = byWords.ToDictionary(f => f.Id, f => f.Passage);
            foreach (var missing in ids.Where(id => !known.ContainsKey(id)).ToList())
                foreach (var f in Query(db, "SELECT rowid, title, url, text FROM chunks WHERE rowid = CAST($q AS INTEGER)", missing.ToString(), read))
                    known[f.Id] = f.Passage;
            var candidates = ids.Where(known.ContainsKey).Select(id => known[id])
                .Where(p => named.All(n => n.IsMatch(p.Title + "\n" + p.Text))).ToList();
            if (candidates.Count == 0) return [];
            var scores = models.Rerank(question, [.. candidates.Select(p => p.Title + "\n" + p.Text)]);
            for (var i = 0; i < scores.Length; i++) scores[i] -= Demoted(candidates[i].Url, question);
            var order = Enumerable.Range(0, candidates.Count).Where(i => scores[i] >= Cutoff).OrderByDescending(i => scores[i]).ToList();
            if (order.Count == 0) return [];
            // The best, then the next best of the same page: two pages joined together read to the model as one text.
            var page = candidates[order[0]].Url.Split('#')[0];
            var taken = new List<Passage>();
            foreach (var p in order.Select(i => candidates[i]).Where(p => p.Url.Split('#')[0] == page))
            {
                if (taken.Count == MaxPassages || taken.Sum(t => PromptBuilder.EstimateTokens(t.Text)) + PromptBuilder.EstimateTokens(p.Text) > maxTokens) break;
                taken.Add(p);
            }
            return taken;
        }

        IReadOnlyList<Passage> FromPages(string allowed)
        {
            // The likeliest pages, two ways, fused by rank: by title, description and address (words in the address weigh most:
            // it's the docs' own short name for the topic), and by headings and API names.
            var about = Query(db, $"SELECT url FROM pages WHERE pages MATCH $q {allowed} ORDER BY bm25(pages, 1.0, 1.0, 3.0) LIMIT {PagesRanked}", any, r => r.GetString(0));
            var covers = Query(db, $"SELECT url FROM page_terms WHERE page_terms MATCH $q {allowed} ORDER BY bm25(page_terms, 1.0, 1.0) LIMIT {PagesRanked}", any, r => r.GetString(0));
            foreach (var page in about.Take(PagesTried).Concat(covers.Take(PagesTried)).Distinct())
            {
                // 4. Half the question's words must be in what the page says it's about or covers, or it only shares a word or two with it.
                if (concepts.Count(c => Holds(db, "pages", c.Match, page) || Holds(db, "page_terms", $"{{names}} : ({c.Match})", page)) * 2 < concepts.Count) continue;

                // 5. Inside it, the passages that hold the rarest word, in their words or their API names, since it says most about
                // what's asked, and every API name and version the question names.
                var holding = Query(db, $"SELECT rowid FROM chunks WHERE chunks MATCH $q AND {InPage("url")}", rarest.Match, r => r.GetInt64(0), page)
                    .Concat(Query(db, $"SELECT n.rowid FROM chunk_names n JOIN chunks c ON c.rowid = n.rowid WHERE chunk_names MATCH $q AND {InPage("c.url")}", rarest.Match, r => r.GetInt64(0), page))
                    .ToHashSet();
                Func<SqliteDataReader, (long Id, Passage Passage)> read = r => (r.GetInt64(0), new Passage(r.GetString(1), r.GetString(2), r.GetString(3)));
                var byWords = Query(db, $"SELECT rowid, title, url, text FROM chunks WHERE chunks MATCH $q AND {InPage("url")} ORDER BY bm25(chunks, 1.0, 1.0) LIMIT {PassagesRead}", any, read, page);
                var byNames = Query(db, $"SELECT c.rowid, c.title, c.url, c.text FROM chunk_names n JOIN chunks c ON c.rowid = n.rowid WHERE chunk_names MATCH $q AND {InPage("c.url")} ORDER BY bm25(chunk_names) LIMIT {PassagesRead}", any, read, page);
                var found = byWords.Concat(byNames).DistinctBy(f => f.Id)
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

    /// <summary>The question's words, each with its abbreviation's long form as a phrase when the docs spell one out.</summary>
    static List<Concept> Concepts(SqliteConnection db, List<string> terms)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT alias, expansion FROM aliases WHERE alias IN ({string.Join(", ", terms.Select((_, i) => "$a" + i))})";
        for (var i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue("$a" + i, terms[i]);
        var longForms = new Dictionary<string, string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) longForms[r.GetString(0)] = r.GetString(1);
        return [.. terms.Select(t => new Concept(t, longForms.TryGetValue(t, out var e) ? $"({Quote(t)} OR {Quote(e)})" : Quote(t)))];
    }

    static long Count(SqliteConnection db, string table, string match) =>
        Query(db, $"SELECT count(*) FROM {table} WHERE {table} MATCH $q", match, r => r.GetInt64(0))[0];

    static bool Holds(SqliteConnection db, string table, string match, string page) =>
        Query(db, $"SELECT 1 FROM {table} WHERE {table} MATCH $q AND url = $u LIMIT 1", match, r => r.GetInt32(0), page).Count > 0;

    /// <summary>A passage of the page: its address is the page's, or the page's with a section anchor.</summary>
    static string InPage(string url) => $"({url} = $u OR substr({url}, 1, length($u) + 1) = $u || '#')";

    /// <summary>Reciprocal rank fusion: what ranks high in either ranking comes first, with no scores to weigh against each other.</summary>
    static IEnumerable<T> Fuse<T>(params List<T>[] rankings) where T : notnull
    {
        var score = new Dictionary<T, double>();
        foreach (var ranking in rankings)
            for (var i = 0; i < ranking.Count; i++)
                score[ranking[i]] = score.GetValueOrDefault(ranking[i]) + 1.0 / (Rrf + i + 1);
        return score.OrderByDescending(s => s.Value).Select(s => s.Key); // stable: on a tie, the first ranking's order
    }

    /// <summary>
    /// SQL on a page address, from the question: Product keeps the search to the docs of a product it names alone, Kinds
    /// leaves out pages that are only right for an error code or an upgrade unless it holds one. Constants only, no question text.
    /// </summary>
    static (string? Root, bool LeaveOutKinds) Facets(string question)
    {
        bool ef = Ef().IsMatch(question), aspnet = AspNet().IsMatch(question);
        return (ef == aspnet ? null : $"https://learn.microsoft.com/{(ef ? "ef" : "aspnet")}/", !ErrorCode().IsMatch(question) && !AsksAboutChanges().IsMatch(question));
    }

    static string FacetSql(string? root, bool leaveOutKinds) =>
        (root is null ? "" : $" AND (instr(url, '{root}') = 1)") + (leaveOutKinds ? string.Concat(AskedForOnly.Select(part => $" AND (instr(url, '{part}') = 0)")) : "");

    static bool FacetAllows(string url, string? root, bool leaveOutKinds) =>
        (root is null || url.StartsWith(root, StringComparison.Ordinal)) && !(leaveOutKinds && AskedForOnly.Any(part => url.Contains(part, StringComparison.Ordinal)));

    /// <summary>How many of the question's words the section's own heading holds, by their first letters: register finds
    /// Registering. The last step of the path (Page > Section > Subsection): counting the parents too made every subsection of
    /// a matching section tie, and the hard questions' API passages lost to their neighbours (6 of 17 to 2). An opening passage has none.</summary>
    static int HeadingWords(string title, List<string> terms)
    {
        var at = title.LastIndexOf(" > ", StringComparison.Ordinal);
        if (at < 0) return 0;
        var heading = title[(at + 3)..];
        return terms.Count(t => heading.Contains(t.Length > 5 ? t[..5] : t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each term in double quotes: FTS5 reads it as a plain word, whatever the question holds.</summary>
    static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";
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
    [GeneratedRegex(@"\b(?:EF Core|EF|Entity Framework)\b", RegexOptions.IgnoreCase)] private static partial Regex Ef();
    [GeneratedRegex(@"ASP\.NET|\bBlazor\b", RegexOptions.IgnoreCase)] private static partial Regex AspNet();
    [GeneratedRegex(@"\b(?:CS|CA|IDE|ASP|SYSLIB|NETSDK|BL|RZ|MSB|NU)\d{3,5}\b", RegexOptions.IgnoreCase)] private static partial Regex ErrorCode();
    [GeneratedRegex(@"\b(?:breaking|break|breaks|upgrade|upgrading|migrate|migrating|obsolete|deprecated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AsksAboutChanges();
}
