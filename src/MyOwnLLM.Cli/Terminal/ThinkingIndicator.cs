namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// "THINKING · 12s" on one line, updated every second until the answer starts, so a long wait never looks frozen.
/// The only animation in Jazari, and it has a job: showing it's still working. Plain/piped output shows nothing.
/// </summary>
public sealed class ThinkingIndicator
{
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    readonly TextWriter _output;
    readonly bool _live;
    readonly object _gate = new();
    bool _stopped;

    ThinkingIndicator(TextWriter output, Theme theme, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _output = output;
        _live = theme.Mode != ColorMode.None;
        _loop = _live ? RunAsync(theme, delay) : Task.CompletedTask;
    }

    public static ThinkingIndicator Start(TextWriter output, Theme theme, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(output, theme, delay ?? Task.Delay);

    async Task RunAsync(Theme theme, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var dot = theme.Glyph('·');
        for (var seconds = 0; ; seconds++)
        {
            lock (_gate)
            {
                if (_stopped) return;
                _output.Write("\r\e[2K" + theme.Paint(seconds == 0 ? "THINKING" : $"THINKING {dot} {seconds}s", Tone.Muted));
            }
            try { await delay(TimeSpan.FromSeconds(1), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
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
        if (_live) _output.Write("\r\e[2K");
        _stop.Dispose();
    }
}
