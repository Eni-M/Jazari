using System.Diagnostics;

namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// "live  Thinking · 12s" on one line, updated every second until the answer starts, so a long wait never looks frozen.
/// Once the model reports how far it has read the prompt, it becomes "live  Reading 2,400 of 5,000 tokens · 48% · about 1 min left",
/// and the percentage also shows on the Windows Terminal tab and taskbar button.
/// The only animation in Jazari, and it has a job: showing it's still working. Plain/piped output shows nothing.
/// </summary>
public sealed class ThinkingIndicator
{
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    readonly TextWriter _output;
    readonly Theme _theme;
    readonly bool _live;
    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    bool _stopped;
    int _seconds;
    ReadProgress? _first, _latest;
    TimeSpan _firstAt, _latestAt;

    ThinkingIndicator(TextWriter output, Theme theme, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _output = output;
        _theme = theme;
        _live = theme.Mode != ColorMode.None;
        _loop = _live ? RunAsync(delay) : Task.CompletedTask;
    }

    public static ThinkingIndicator Start(TextWriter output, Theme theme, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(output, theme, delay ?? Task.Delay);

    /// <summary>Takes the latest reading progress; the next redraw shows it.</summary>
    public void Report(ReadProgress progress)
    {
        lock (_gate)
        {
            if (_first is null) (_first, _firstAt) = (progress, _clock.Elapsed);
            (_latest, _latestAt) = (progress, _clock.Elapsed);
            if (_live && !_stopped) Draw();
        }
    }

    async Task RunAsync(Func<TimeSpan, CancellationToken, Task> delay)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_stopped) return;
                Draw();
            }
            try { await delay(TimeSpan.FromSeconds(1), _stop.Token); }
            catch (OperationCanceledException) { return; }
            Interlocked.Increment(ref _seconds);
        }
    }

    void Draw()
    {
        var dot = _theme.Glyph('·');
        var seconds = _seconds;
        string text;
        if (_latest is { Total: > 0 } p)
        {
            var percent = Math.Clamp(p.Processed * 100 / p.Total, 0, 100);
            var left = Remaining(p);
            text = $"Reading {p.Processed:N0} of {p.Total:N0} tokens {dot} {percent}%{(left is null ? "" : $" {dot} {left}")}";
            _output.Write($"\e]9;4;1;{percent}\a"); // Windows Terminal: progress on the tab and taskbar
        }
        else text = seconds == 0 ? "Thinking" : $"Thinking {dot} {seconds}s";
        _output.Write("\r\e[2K" + _theme.Paint("live".PadLeft(Ui.Gutter - 2) + "  ", Tone.Accent) + _theme.Paint(text, Tone.Muted));
    }

    /// <summary>Time left at the reading speed seen so far, once there's enough to judge it.</summary>
    string? Remaining(ReadProgress p)
    {
        var read = p.Processed - _first!.Processed;
        var took = (_latestAt - _firstAt).TotalSeconds;
        if (read <= 0 || took < 2) return null;
        var left = (p.Total - p.Processed) / (read / took);
        return left < 60 ? "under a minute left" : $"about {Math.Ceiling(left / 60):0} min left";
    }

    /// <summary>Stops updating and clears the line, so the answer starts on a clean line.</summary>
    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        _stop.Cancel();
        await _loop;
        if (_live) _output.Write("\r\e[2K" + (_latest is null ? "" : "\e]9;4;0;0\a"));
        _stop.Dispose();
    }
}
