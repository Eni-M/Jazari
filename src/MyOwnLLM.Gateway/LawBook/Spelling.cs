using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MyOwnLLM.Gateway;

/// <summary>
/// Typos, corrected from the book's own words: "serialze" is in no passage, so the uncovered-word check refused the question
/// before anything searched for it. A word of six letters or more that the book doesn't have becomes the book's most common word
/// one edit away (a letter added, dropped, changed or two swapped) with the same first letter. Made-up API names stay as they
/// are: none of the dev or held-out traps' words has such a neighbour, so the check still refuses them.
/// </summary>
public static partial class Spelling
{
    /// <summary>The book's words are porter stems ("serial", "enabl"); a stem with one of these endings is compared, and the
    /// longest that fits is used, so the question reads "serialize", not "serial", for the vectors and the reranker too.</summary>
    static readonly string[] Endings = ["", "e", "s", "es", "ed", "ing", "ize", "ise", "ation", "er", "y", "ate"];

    static (string File, DateTime Written, Dictionary<string, long> Words)? _cache;
    static readonly object Lock = new();

    public static string Correct(SqliteConnection db, string question)
    {
        Dictionary<string, long>? words = null;
        return Word().Replace(question, m =>
        {
            var w = m.Value.ToLowerInvariant();
            if (Known(db, w)) return m.Value;
            words ??= Words(db);
            return Closest(words, w) ?? m.Value;
        });
    }

    static bool Known(SqliteConnection db, string word)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM chunks WHERE chunks MATCH $q) OR EXISTS(SELECT 1 FROM chunk_names WHERE chunk_names MATCH $q)";
        cmd.Parameters.AddWithValue("$q", '"' + word + '"');
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    public static string? Closest(IReadOnlyDictionary<string, long> words, string word)
    {
        (string Word, long Count)? best = null;
        foreach (var (stem, count) in words)
        {
            if (stem[0] != word[0] || Math.Abs(stem.Length - word.Length) > 6 || (best is { } b && count <= b.Count)) continue;
            var fit = Endings.Select(e => stem + e).Where(f => Distance(word, f) <= 1).MaxBy(f => f.Length);
            if (fit is not null) best = (fit, count);
        }
        return best?.Word;
    }

    /// <summary>Edits between two words, a swap of neighbours counting as one (optimal string alignment).</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Every word of the passages and their API names, with how many passages hold it: read once per version of the book.</summary>
    static Dictionary<string, long> Words(SqliteConnection db)
    {
        var file = db.DataSource;
        var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
        lock (Lock)
        {
            if (_cache is { } c && c.File == file && c.Written == written) return c.Words;
            var words = new Dictionary<string, long>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS temp.chunk_words USING fts5vocab(main, 'chunks', 'row');
                    CREATE VIRTUAL TABLE IF NOT EXISTS temp.name_words USING fts5vocab(main, 'chunk_names', 'row');
                    SELECT term, doc FROM temp.chunk_words UNION ALL SELECT term, doc FROM temp.name_words;
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (r.GetString(0) is { Length: > 0 } t && char.IsLetter(t[0])) words[t] = words.GetValueOrDefault(t) + r.GetInt64(1);
            }
            _cache = (file, written, words);
            return words;
        }
    }

    /// <summary>A plain word of six letters or more, capitalized at most: not an API name (FrobnicateAsync), which
    /// <see cref="LawBook"/> requires word for word.</summary>
    [GeneratedRegex(@"(?<![\w.])[A-Za-z][a-z]{5,}(?![\w.])")] private static partial Regex Word();
}
