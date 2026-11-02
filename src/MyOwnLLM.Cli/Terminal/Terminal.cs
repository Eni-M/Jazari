using System.Text;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>Everything the interface needs from a console, so it can run against a fake one in tests.</summary>
public interface ITerminal
{
    /// <summary>Columns and rows of the window.</summary>
    (int Width, int Height) Size { get; }

    /// <summary>One frame, written in one piece.</summary>
    void Write(string text);

    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey();
    bool TreatControlCAsInput { get; set; }
}

/// <summary>
/// The real console. A frame goes out as one write of UTF-8 bytes: Console.Out flushes every couple of hundred
/// characters, and a terminal without synchronized output can show each of those pieces as its own half-drawn frame.
/// </summary>
public sealed class SystemTerminal : ITerminal
{
    readonly Stream _out = Console.OpenStandardOutput();

    public (int Width, int Height) Size
    {
        get
        {
            try { return (Math.Max(Console.WindowWidth, 2), Math.Max(Console.WindowHeight, 2)); }
            catch (IOException) { return (80, 24); }
        }
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _out.Write(bytes, 0, bytes.Length);
        _out.Flush();
    }

    public bool KeyAvailable => Console.KeyAvailable;
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;
        set => Console.TreatControlCAsInput = value;
    }
}

/// <summary>Output to a writer (a one-off command's console, or a test), with no keys. Its width is the writer's columns plus the one never used.</summary>
public sealed class WriterTerminal(TextWriter writer, Func<int> columns, int height = 1000) : ITerminal
{
    public (int Width, int Height) Size => (Math.Max(columns(), 1) + 1, height);
    public void Write(string text) => writer.Write(text);
    public bool KeyAvailable => false;
    public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("No keys on this terminal.");
    public bool TreatControlCAsInput { get; set; }
}
