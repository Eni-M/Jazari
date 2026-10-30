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
    public void Error_chunk() =>
        Assert.Equal(new SseChunk(null, null, "lost connection to the model server"), SseChunk.Parse("""data: {"error":"lost connection to the model server"}"""));

    [Theory]
    [InlineData("data: [DONE]")]
    [InlineData("")]
    [InlineData(": keep-alive")]
    public void Other_lines_are_ignored(string line) => Assert.Null(SseChunk.Parse(line));
}
