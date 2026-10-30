using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class CliArgsTests
{
    [Fact]
    public void Ask_with_repo_and_stream()
    {
        var args = CliArgs.Parse(["ask", "how do I add DI?", "--repo", @"C:\my repo", "--stream"]);

        Assert.Equal(new CliArgs("ask", "how do I add DI?", @"C:\my repo", true, null, null), args);
    }

    [Fact]
    public void Ask_without_options()
    {
        Assert.Equal(new CliArgs("ask", "hi", null, false, null, null), CliArgs.Parse(["ask", "hi"]));
    }

    [Fact]
    public void Status_defaults_repo_to_null()
    {
        Assert.Equal(new CliArgs("status", null, null, false, null, null), CliArgs.Parse(["status"]));
        Assert.Equal(@"C:\Repo", CliArgs.Parse(["status", "--repo", @"C:\Repo"]).RepoPath);
    }

    [Fact]
    public void Feedback_up_and_down()
    {
        Assert.Equal(new CliArgs("feedback", null, null, false, "ix-3", true), CliArgs.Parse(["feedback", "ix-3", "up"]));
        Assert.False(CliArgs.Parse(["feedback", "ix-3", "down"]).Accepted);
    }

    [Theory]
    [InlineData]
    [InlineData("feedback", "ix-3", "maybe")]
    [InlineData("ask")]
    [InlineData("ask", "q", "--repo")]
    [InlineData("dance")]
    public void Bad_input_throws_with_usage(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliArgs.Parse(args));

        Assert.Contains("usage: myllm", ex.Message);
    }
}
