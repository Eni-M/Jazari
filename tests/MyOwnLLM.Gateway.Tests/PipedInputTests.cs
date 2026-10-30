using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class PipedInputTests
{
    [Fact]
    public async Task Reads_everything_piped_in() =>
        Assert.Equal("diff --git a b\n+line\n", await PipedInput.ReadAsync(new StringReader("diff --git a b\n+line\n"), TimeSpan.FromSeconds(5)));

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task Empty_input_is_nothing(string text) =>
        Assert.Null(await PipedInput.ReadAsync(new StringReader(text), TimeSpan.FromSeconds(5)));

    [Fact]
    public async Task A_redirected_input_that_never_writes_doesnt_hang() =>
        Assert.Null(await PipedInput.ReadAsync(new SilentReader(), TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5)));

    /// <summary>Blocks on every read, like Console.In on a pipe nobody writes to.</summary>
    sealed class SilentReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count) { Thread.Sleep(Timeout.Infinite); return 0; }
        public override Task<int> ReadAsync(char[] buffer, int index, int count) => Task.FromResult(Read(buffer, index, count));
    }
}
