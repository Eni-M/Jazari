using System.Globalization;
using System.Text;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// How many terminal columns text takes, measured by grapheme cluster: what reads as one character, like an emoji with
/// its skin tone or a letter with its accent. Every wrap, cut and caret position in the interface goes through here, so
/// a row never comes out wider than the terminal and makes it wrap on its own.
/// </summary>
public static class TextWidth
{
    // East Asian Wide and Fullwidth ranges, and the emoji terminals draw two columns wide. Written as numbers on purpose:
    // a character literal in the old table was normalized to a different character on save, which made the whole
    // Private Use Area count as wide. A range a little too wide only makes a row a little shorter, never too long.
    static readonly (int From, int To)[] Wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x303E),
        (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x17000, 0x18AFF), (0x1B000, 0x1B2FF), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F200, 0x1F251), (0x1F260, 0x1F265), (0x1F300, 0x1F64F), (0x1F680, 0x1F6FF),
        (0x1F7E0, 0x1F7EB), (0x1F90C, 0x1F9FF), (0x1FA70, 0x1FAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    /// <summary>Columns for a whole string.</summary>
    public static int Of(string text)
    {
        var width = 0;
        foreach (var (start, length) in Clusters(text)) width += Cluster(text, start, length);
        return width;
    }

    /// <summary>The grapheme clusters of <paramref name="text"/>, as start and length.</summary>
    public static IEnumerable<(int Start, int Length)> Clusters(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var length = Math.Max(StringInfo.GetNextTextElementLength(text, i), 1);
            yield return (i, length);
            i += length;
        }
    }

    /// <summary>
    /// Columns for one cluster: the sum of its code points, at least 2 with an emoji presentation selector. Terminals that
    /// draw a joined emoji as one picture use fewer columns, so the row only comes out short.
    /// </summary>
    public static int Cluster(string text, int start, int length)
    {
        var width = 0;
        var emoji = false;
        foreach (var rune in text.AsSpan(start, length).EnumerateRunes())
        {
            if (rune.Value == 0xFE0F) emoji = true;
            width += Of(rune);
        }
        return emoji ? Math.Max(width, 2) : width;
    }

    public static int Of(Rune rune)
    {
        var v = rune.Value;
        if (v < 0x20 || v is >= 0x7F and < 0xA0) return 0;
        if (v < 0x300) return 1;
        if (v is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF || v is >= 0x1160 and <= 0x11FF) return 0;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return IsWide(v) ? 2 : 1;
    }

    static bool IsWide(int value)
    {
        int low = 0, high = Wide.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (value < Wide[mid].From) high = mid - 1;
            else if (value > Wide[mid].To) low = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>The longest start of <paramref name="text"/> that fits <paramref name="columns"/>, never splitting a cluster; at least one cluster.</summary>
    public static (string Head, string Tail) Take(string text, int columns)
    {
        var (end, used) = (0, 0);
        foreach (var (start, length) in Clusters(text))
        {
            var width = Cluster(text, start, length);
            if (used + width > columns && end > 0) break;
            used += width;
            end = start + length;
            if (used >= columns) break;
        }
        return (text[..end], text[end..]);
    }

    /// <summary>Cut to fit, with "..." where it was cut.</summary>
    public static string Fit(string text, int columns, string ellipsis = "...")
    {
        if (columns <= 0) return "";
        if (Of(text) <= columns) return text;
        var room = columns - Of(ellipsis);
        return room <= 0 ? Take(text, columns).Head : Take(text, room).Head + ellipsis;
    }

    public static string PadRight(string text, int columns) => text + new string(' ', Math.Max(columns - Of(text), 0));

    public static string PadLeft(string text, int columns) => new string(' ', Math.Max(columns - Of(text), 0)) + text;

    /// <summary>
    /// Word wrap to <paramref name="max"/> columns, as source ranges: rows break at spaces, which are dropped at the break,
    /// and a word wider than a row is cut by cluster. Empty text gives one empty row.
    /// </summary>
    public static List<(int Start, int End)> WrapRanges(string text, int max)
    {
        max = Math.Max(max, 1);
        List<(int, int)> rows = [];
        int start = 0, end = 0, width = 0; // the row being filled; empty while end == start
        var at = 0;
        while (true)
        {
            var space = text.IndexOf(' ', at);
            var wordEnd = space < 0 ? text.Length : space;
            var (wordStart, wordWidth) = (at, Of(text[at..wordEnd]));
            while (wordWidth > max)
            {
                if (end > start) { rows.Add((start, end)); start = end; width = 0; }
                var head = Take(text[wordStart..wordEnd], max).Head;
                rows.Add((wordStart, wordStart + head.Length));
                wordStart += head.Length;
                wordWidth = Of(text[wordStart..wordEnd]);
                (start, end, width) = (wordStart, wordStart, 0);
            }
            if (end == start) (start, end, width) = (wordStart, wordEnd, wordWidth);
            else if (width + 1 + wordWidth <= max) (end, width) = (wordEnd, width + 1 + wordWidth);
            else
            {
                rows.Add((start, end));
                (start, end, width) = (wordStart, wordEnd, wordWidth);
            }
            if (space < 0) break;
            at = space + 1;
        }
        rows.Add((start, end));
        return rows;
    }

    /// <summary>Word wrap to <paramref name="max"/> columns; see <see cref="WrapRanges"/>.</summary>
    public static List<string> Wrap(string text, int max) => WrapRanges(text, max).Select(r => text[r.Start..r.End]).ToList();

    /// <summary>The start of the cluster before <paramref name="index"/>, for the caret and Backspace.</summary>
    public static int Previous(string text, int index)
    {
        var previous = 0;
        foreach (var (start, _) in Clusters(text))
        {
            if (start >= index) break;
            previous = start;
        }
        return Math.Min(previous, Math.Max(index, 0));
    }

    /// <summary>The end of the cluster at <paramref name="index"/>.</summary>
    public static int Next(string text, int index) =>
        index >= text.Length ? text.Length : index + Math.Max(StringInfo.GetNextTextElementLength(text, index), 1);
}
