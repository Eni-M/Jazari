namespace MyOwnLLM.Gateway.Tests;

public class WordPieceTests
{
    static readonly WordPiece Tokens = new(new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "un", "##aff", "##able", "hello", ",", "world", "!", "cafe", "a", "b", "c" }
        .Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i));

    static long Id(string token) => token switch { "[UNK]" => 1, "[CLS]" => 2, "[SEP]" => 3, "un" => 4, "##aff" => 5, "##able" => 6, "hello" => 7, "," => 8, "world" => 9, "!" => 10, "cafe" => 11, "a" => 12, "b" => 13, _ => 14 };

    [Fact] public void WordPiece_splits_words_into_known_pieces() =>
        Assert.Equal(new[] { "[CLS]", "un", "##aff", "##able", "[SEP]" }.Select(Id), Tokens.Encode("unaffable", null, 512).Ids);

    [Fact] public void WordPiece_lowercases_and_splits_punctuation() =>
        Assert.Equal(new[] { "[CLS]", "hello", ",", "world", "!", "[SEP]" }.Select(Id), Tokens.Encode("Hello,World!", null, 512).Ids);

    [Fact] public void WordPiece_handles_accents_symbols_and_unknown_words()
    {
        Assert.Equal(new[] { "[CLS]", "cafe", "[UNK]", "[UNK]", "[SEP]" }.Select(Id), Tokens.Encode("Café zebra 漢", null, 512).Ids);
        Tokens.Encode(new string('x', 500) + " \u0000� 😀 ﷽", null, 512); // never throws
    }

    [Fact] public void WordPiece_cuts_to_the_limit()
    {
        var (ids, types) = Tokens.Encode("a a a a", "b b b b b b", 8);
        Assert.Equal(8, ids.Length);
        Assert.Equal(new[] { "[CLS]", "a", "a", "a", "a", "[SEP]", "b", "[SEP]" }.Select(Id), ids);
        Assert.Equal(8, types.Length);
        Assert.Equal(5, Tokens.Encode("a a a a a a a a", null, 5).Ids.Length);
    }

    [Fact] public void A_pair_marks_the_second_text_with_type_1() =>
        Assert.Equal(new long[] { 0, 0, 0, 1, 1 }, Tokens.Encode("a", "b", 512).TypeIds);
}
