namespace MyOwnLLM.Cli.Terminal;

/// <summary>
/// "live  Thinking · 12s" in the conversation, updated every second until the answer starts, so a long wait never looks
/// frozen. Once the model reports how far it has read the prompt, it becomes "live  Reading 2,400 of 5,000 tokens · 48% ·
/// about 1 min left", and the percentage also shows on the Windows Terminal tab and taskbar button. The only animation in
/// Jazari, and it has a job: showing it's still working. Plain/piped output shows nothing.
/// </summary>
public sealed class ThinkingIndicator
{
    readonly IBlockSink? _sink;
    readonly ThinkingBlock? _block;
    bool _stopped, _progressShown;

    ThinkingIndicator(IBlockSink? sink, ThinkingBlock? block)
    {
        _sink = sink;
        _block = block;
        if (block is not null) sink!.Append(block);
    }

    /// <summary>Shows nothing: plain output.</summary>
    public static ThinkingIndicator None => new(null, null);

    /// <summary>Starts the line in <paramref name="sink"/>; <paramref name="label"/> replaces "Thinking" (as in "Starting the model").</summary>
    public static ThinkingIndicator Start(IBlockSink sink, Theme theme, TimeProvider? time = null, string label = "Thinking") =>
        new(sink, new ThinkingBlock(theme, time ?? TimeProvider.System, label));

    /// <summary>Takes the latest reading progress; the next frame shows it.</summary>
    public void Report(ReadProgress progress)
    {
        if (_sink is null || _block is null) return;
        lock (_sink.Gate)
        {
            if (_stopped) return;
            _block.Report(progress);
        }
        if (_block.Percent is { } percent)
        {
            _progressShown = true;
            _sink.Signal($"\e]9;4;1;{percent}\a"); // Windows Terminal: progress on the tab and taskbar
        }
        else _sink.Changed();
    }

    /// <summary>A named step of a long answer, with how many tokens are written so far if it knows.</summary>
    public void Stage(string stage, int? tokens)
    {
        if (_sink is null || _block is null) return;
        lock (_sink.Gate)
        {
            if (_stopped) return;
            _block.Stage(stage, tokens);
        }
        _sink.Changed();
    }

    /// <summary>Takes the line away, so the answer starts where it was.</summary>
    public Task StopAsync()
    {
        if (_sink is null || _block is null) return Task.CompletedTask;
        lock (_sink.Gate)
        {
            if (_stopped) return Task.CompletedTask;
            _stopped = true;
        }
        _sink.Remove(_block);
        if (_progressShown) _sink.Signal("\e]9;4;0;0\a");
        return Task.CompletedTask;
    }
}
