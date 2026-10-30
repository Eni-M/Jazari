namespace MyOwnLLM.Cli;

/// <summary>Reads what was piped into Jazari, as in <c>git diff | jazari ask "review this"</c>.</summary>
public static class PipedInput
{
    /// <summary>
    /// Everything on <paramref name="input"/>, or null when nothing arrives within <paramref name="firstCharWithin"/>:
    /// some IDE terminals redirect stdin but never write to it or close it, and Jazari must not hang on them.
    /// </summary>
    public static async Task<string?> ReadAsync(TextReader input, TimeSpan firstCharWithin)
    {
        // Console.In's async reads block the caller, so the first read gets a thread of its own; the timeout can then fire.
        var first = new char[1];
        var read = Task.Run(() => input.Read(first, 0, 1));
        if (await Task.WhenAny(read, Task.Delay(firstCharWithin)) != read || await read == 0) return null;
        var text = first[0] + await Task.Run(input.ReadToEnd);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
