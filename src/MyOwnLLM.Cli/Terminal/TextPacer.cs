using System.Diagnostics;
using System.Text;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// Shows a streamed answer at a steady pace instead of in the model's bursts: it holds the first words for a moment, so
/// there's something to flow, then lets out small pieces on a timer. The more is waiting, the bigger the pieces, so it
/// keeps up with the model and never falls far behind.
/// </summary>
public sealed class TextPacer
{
    const int Steps = 16; // what's waiting comes out over about this many ticks
    const int FinishSteps = 6;

    readonly Action<string> _emit;
    readonly TimeSpan _tick;
    readonly int _holdChars;
    readonly TimeSpan _hold;
    readonly StringBuilder _waiting = new();
    readonly object _gate = new();
    readonly Stopwatch _sinceFirst = new();
    readonly CancellationTokenSource _finish = new();
    readonly Task _loop;
    bool _released;
    bool _finishing;

    public TextPacer(Action<string> emit, TimeSpan? tick = null, int holdChars = 60, TimeSpan? hold = null)
    {
        _emit = emit;
        _tick = tick ?? TimeSpan.FromMilliseconds(30);
        _holdChars = holdChars;
        _hold = hold ?? TimeSpan.FromSeconds(1.5);
        _loop = Task.Run(LoopAsync);
    }

    public void Add(string text)
    {
        lock (_gate)
        {
            if (_waiting.Length == 0 && !_sinceFirst.IsRunning) _sinceFirst.Start();
            _waiting.Append(text);
        }
    }

    /// <summary>Shows what's left quickly, and returns once it has all been shown. A failure while showing text surfaces here.</summary>
    public Task DrainAsync()
    {
        lock (_gate) _finishing = true;
        _finish.Cancel();
        return _loop;
    }

    async Task LoopAsync()
    {
        while (true)
        {
            string? piece;
            lock (_gate)
            {
                if (_waiting.Length == 0 && _finishing) return;
                piece = NextPiece();
            }
            if (piece is not null) _emit(piece);
            try { await Task.Delay(_tick, _finish.Token); }
            catch (OperationCanceledException) { await Task.Yield(); } // finishing: no more waiting between pieces
        }
    }

    string? NextPiece()
    {
        if (_waiting.Length == 0) return null;
        if (!_released && !_finishing && _waiting.Length < _holdChars && _sinceFirst.Elapsed < _hold) return null;
        _released = true;
        var size = Math.Clamp(_waiting.Length / (_finishing ? FinishSteps : Steps), 1, _waiting.Length);
        if (size < _waiting.Length && char.IsHighSurrogate(_waiting[size - 1])) size++;
        var piece = _waiting.ToString(0, size);
        _waiting.Remove(0, size);
        return piece;
    }
}
