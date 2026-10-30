using System.Text;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// Renders a streamed answer as restrained Markdown. In color mode tokens appear as they arrive and each
/// finished line is redrawn once with its styling; in plain mode (no color / piped) only finished lines are written.
/// </summary>
public sealed partial class MarkdownStream(TextWriter output, Theme theme, int width)
{
    enum State { Text, InFence }

    readonly StringBuilder _line = new();
    readonly List<string[]> _table = [];
    State _state;
    int _shown; // characters of the current line already echoed raw (color mode)
    int _blocks; // code blocks so far in this answer
    bool Live => theme.Mode != ColorMode.None;

    public void Write(string token)
    {
        foreach (var c in ConsoleText.Sanitize(token))
        {
            if (c == '\n') { FinishLine(); continue; }
            _line.Append(c);
        }
        if (Live && _line.Length > _shown)
        {
            output.Write(theme.Paint(_line.ToString(_shown, _line.Length - _shown), Tone.Primary));
            _shown = _line.Length;
        }
    }

    public void Complete()
    {
        if (_line.Length > 0) FinishLine();
        FlushTable();
        if (Live) output.Write(Theme.Reset);
    }

    void FinishLine()
    {
        var line = _line.ToString();
        var restyle = EraseEcho(line);
        _line.Clear();
        _shown = 0;

        if (_state == State.InFence)
        {
            if (line.TrimStart().StartsWith("```")) { _state = State.Text; if (!restyle) output.WriteLine(); return; }
            if (!restyle) { output.WriteLine(); return; }
            // Printed untouched, no padding and no splitting: a mouse copy must give back exactly the code.
            output.WriteLine(theme.Code(line));
            return;
        }

        if (!restyle)
        {
            // The raw echo stays as printed; keep the fence state right for the lines that follow.
            if (line.TrimStart().StartsWith("```")) { _state = State.InFence; _blocks++; }
            output.WriteLine();
            return;
        }

        if (line.TrimStart().StartsWith('|')) { _table.Add(Cells(line)); return; }
        FlushTable();

        if (line.TrimStart().StartsWith("```"))
        {
            _state = State.InFence;
            // Numbered so /copy n can name it.
            output.WriteLine(theme.Paint($"[{++_blocks}] {line.Trim()[3..].Trim()}".TrimEnd(), Tone.Muted));
            return;
        }

        var (prefix, body) = Heading().Match(line) is { Success: true } h ? ("", h.Groups[1].Value)
            : Bullet().Match(line) is { Success: true } b ? ("  - ", b.Groups[1].Value)
            : Numbered().Match(line) is { Success: true } n ? ($"  {n.Groups[1].Value}. ", n.Groups[2].Value)
            : ("", line);
        body = Link().Replace(Bold().Replace(body, "$1"), "$1 ($2)");

        var lines = Ui.Wrap(body, width - prefix.Length);
        for (var i = 0; i < lines.Count; i++)
            output.WriteLine((i == 0 ? prefix : new string(' ', prefix.Length)) + Inline(lines[i]));
    }

    /// <summary>
    /// Removes the raw echo so the styled line can replace it (one redraw). Only when that's safe: the echo fits on one
    /// terminal row (width is one less than the terminal's columns) and has no tabs or wide characters whose columns we
    /// can't count. Otherwise returns false and the raw echo stays. Plain mode never echoes, so it always restyles.
    /// </summary>
    bool EraseEcho(string line)
    {
        if (!Live || _shown == 0) return true;
        if (_shown > width || line.Any(c => c == '\t' || IsWide(c))) return false;
        output.Write("\r\e[2K");
        return true;
    }

    static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣'
            or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦'
        || char.IsSurrogate(c);

    string Inline(string text) => InlineCode().Replace(text, m => theme.Code(m.Groups[1].Value)) is var styled && Live
        ? theme.Paint(styled, Tone.Primary)
        : styled;

    void FlushTable()
    {
        if (_table.Count == 0) return;
        var rows = _table.Where(r => !r.All(c => SeparatorCell().IsMatch(c))).ToList();
        if (rows.Count == 0) { _table.Clear(); return; }
        var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(i => rows.Max(r => i < r.Length ? r[i].Length : 0)).ToArray();
        for (var r = 0; r < rows.Count; r++)
        {
            var text = string.Join("  ", rows[r].Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
            output.WriteLine(r == 0 && rows.Count > 1 ? theme.Paint(text, Tone.Muted) : Live ? theme.Paint(text, Tone.Primary) : text);
        }
        _table.Clear();
    }

    static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    [GeneratedRegex(@"^#{1,6}\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"^\s*(\d+)\.\s+(.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"^:?-+:?$")] private static partial Regex SeparatorCell();
}
