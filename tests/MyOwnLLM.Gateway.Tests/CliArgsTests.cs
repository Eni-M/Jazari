using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class CliArgsTests
{
    [Fact]
    public void No_arguments_means_session()
    {
        Assert.Equal(new CliArgs("session", null, null, true, null, null), CliArgs.Parse([]));
    }

    [Fact]
    public void Ask_with_repo_streams_by_default()
    {
        var args = CliArgs.Parse(["ask", "how do I add DI?", "--repo", @"C:\my repo"]);

        Assert.Equal(new CliArgs("ask", "how do I add DI?", @"C:\my repo", true, null, null), args);
    }

    [Fact]
    public void Ask_no_stream()
    {
        Assert.False(CliArgs.Parse(["ask", "q", "--no-stream"]).Stream);
        Assert.True(CliArgs.Parse(["ask", "q", "--stream"]).Stream);
    }

    [Fact]
    public void Status_defaults_repo_to_null()
    {
        Assert.Equal(new CliArgs("status", null, null, true, null, null), CliArgs.Parse(["status"]));
        Assert.Equal(@"C:\Repo", CliArgs.Parse(["status", "--repo", @"C:\Repo"]).RepoPath);
    }

    [Fact]
    public void Feedback_up_and_down()
    {
        Assert.Equal(new CliArgs("feedback", null, null, true, "ix-3", true), CliArgs.Parse(["feedback", "ix-3", "up"]));
        Assert.False(CliArgs.Parse(["feedback", "ix-3", "down"]).Accepted);
    }

    [Theory]
    [InlineData("feedback", "ix-3", "maybe")]
    [InlineData("ask")]
    [InlineData("ask", "q", "--repo")]
    [InlineData("status", "--stream")]
    [InlineData("dance")]
    public void Bad_input_throws_with_usage(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliArgs.Parse(args));

        Assert.Contains("usage:", ex.Message);
    }
}
