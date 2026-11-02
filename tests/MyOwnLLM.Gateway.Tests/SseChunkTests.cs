using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class SseChunkTests
{
    [Fact]
    public void Content_chunk() =>
        Assert.Equal(new SseChunk("hi", null, null), SseChunk.Parse("""data: {"choices":[{"index":0,"delta":{"content":"hi"}}]}"""));

    [Fact]
    public void Final_chunk_carries_id() =>
        Assert.Equal(new SseChunk(null, "ix-7", null), SseChunk.Parse("""data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

    [Fact]
    public void Final_chunk_with_null_timings() =>
        Assert.Equal(new SseChunk(null, "ix-7", null), SseChunk.Parse("""data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"timings":null}"""));

    [Fact]
    public void Final_chunk_carries_timings() =>
        Assert.Equal(new AnswerTimings(5000, 1200, 28.1, 90, 6.2), SseChunk.Parse("""
            data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"timings":{"prompt_tokens":5000,"cached_tokens":1200,"prompt_per_second":28.1,"answer_tokens":90,"answer_per_second":6.2}}
            """)!.Timings);

    [Fact]
    public void Final_chunk_says_when_the_answer_was_cut_off() =>
        Assert.True(SseChunk.Parse("""data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""")!.CutOff);

    [Fact]
    public void Final_chunk_carries_dropped_turns() =>
        Assert.Equal(4, SseChunk.Parse("""data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"dropped_turns":4}""")!.DroppedTurns);

    [Fact]
    public void Missing_dropped_turns_means_none() =>
        Assert.Equal(0, SseChunk.Parse("""data: {"id":"ix-7","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""")!.DroppedTurns);

    [Fact]
    public void Progress_chunk() =>
        Assert.Equal(new ReadProgress(100, 40, 60), SseChunk.Parse("""data: {"progress":{"total":100,"cached":40,"processed":60}}""")!.Progress);

    [Fact]
    public void Error_chunk() =>
        Assert.Equal(new SseChunk(null, null, "lost connection to the model server"), SseChunk.Parse("""data: {"error":"lost connection to the model server"}"""));

    [Theory]
    [InlineData("data: [DONE]")]
    [InlineData("")]
    [InlineData(": keep-alive")]
    public void Other_lines_are_ignored(string line) => Assert.Null(SseChunk.Parse(line));
}
