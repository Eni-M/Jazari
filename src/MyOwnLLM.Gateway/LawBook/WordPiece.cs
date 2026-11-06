using System.Globalization;
using System.Text;

namespace MyOwnLLM.Gateway;

/// <summary>
/// BERT's uncased tokenizer, which both search models read: lower case, accents off, punctuation split off, then each word
/// as the longest pieces the vocabulary holds (un ##aff ##able). Written here rather than taken from a package: it's short,
/// and the tokenizer package doesn't list WordPiece.
/// </summary>
public sealed class WordPiece(IReadOnlyDictionary<string, int> vocab)
{
    const int MaxWordChars = 100; // as BERT: a longer "word" is [UNK]
    readonly int _cls = vocab["[CLS]"], _sep = vocab["[SEP]"], _unk = vocab["[UNK]"];

    public static WordPiece Load(string vocabFile) =>
        new(File.ReadLines(vocabFile).Select((token, id) => (token, id)).ToDictionary(t => t.token, t => t.id));

    /// <summary>[CLS] first [SEP], or [CLS] first [SEP] second [SEP] with the second marked type 1, at most
    /// <paramref name="maxTokens"/> long: the second text is cut first, then the first.</summary>
    public (long[] Ids, long[] TypeIds) Encode(string first, string? second, int maxTokens)
    {
        var a = Pieces(first);
        var b = second is null ? null : Pieces(second);
        var room = maxTokens - (b is null ? 2 : 3);
        if (b is not null && a.Count + b.Count > room) b = b[..Math.Max(0, room - a.Count)];
        if (a.Count + (b?.Count ?? 0) > room) a = a[..Math.Max(0, room - (b?.Count ?? 0))];
        var ids = new List<long> { _cls };
        ids.AddRange(a.Select(i => (long)i));
        ids.Add(_sep);
        var firstLength = ids.Count;
        if (b is not null)
        {
            ids.AddRange(b.Select(i => (long)i));
            ids.Add(_sep);
        }
        return ([.. ids], [.. Enumerable.Range(0, ids.Count).Select(i => i < firstLength ? 0L : 1L)]);
    }

    List<int> Pieces(string text)
    {
        var ids = new List<int>();
        foreach (var word in Words(text))
        {
            if (word.Length > MaxWordChars) { ids.Add(_unk); continue; }
            var pieces = new List<int>();
            for (var start = 0; start < word.Length;)
            {
                var end = word.Length;
                var id = -1;
                for (; end > start; end--)
                    if (vocab.TryGetValue(start == 0 ? word[start..end] : "##" + word[start..end], out id)) break;
                if (end == start) { pieces = [_unk]; break; }
                pieces.Add(id);
                start = end;
            }
            ids.AddRange(pieces);
        }
        return ids;
    }

    /// <summary>Lower case, accents off, control characters gone, punctuation and CJK characters as words of their own.</summary>
    static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark || c == '�' || (char.IsControl(c) && !char.IsWhiteSpace(c))) continue;
            if (char.IsWhiteSpace(c))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            }
            else if (IsPunctuation(c, category) || c is >= '一' and <= '鿿')
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return c.ToString();
            }
            else word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    static bool IsPunctuation(char c, UnicodeCategory category) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~' ||
        category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
}
