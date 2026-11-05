using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

public class ProjectLookupTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("lookup").FullName;

    public ProjectLookupTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "OrderService.cs"), "public class OrderService\n{\n    public Task RefundAsync(int orderId) => Task.CompletedTask;\n}\n");
        File.WriteAllText(Path.Combine(_dir, "src", "Caller.cs"), "class Caller { void Go(OrderService s) => s.RefundAsync(1); }\n");
        File.WriteAllText(Path.Combine(_dir, ".env"), "KEY=secret");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Why does RefundAsync never refund?")]
    [InlineData("what does OrderService do")]
    [InlineData("explain src/OrderService.cs")]
    [InlineData("explain OrderService.cs")]
    public void Finds_the_file_that_declares_what_the_question_names(string question)
    {
        var found = ProjectLookup.For(_dir, question, null);

        Assert.Equal(["src/OrderService.cs"], found!.Files);
        Assert.Contains("--- src/OrderService.cs ---", found.Text);
        Assert.Contains("RefundAsync(int orderId)", found.Text);
    }

    [Theory]
    [InlineData("What is dependency injection?")]
    [InlineData("How do I use IServiceCollection?")] // a real name, but not one this project declares
    [InlineData("what is in .env")]
    public void Finds_nothing_for_a_question_about_no_code_of_the_project(string question) =>
        Assert.Null(ProjectLookup.For(_dir, question, null));

    [Fact]
    public void Looks_only_inside_a_git_repository()
    {
        Directory.Delete(Path.Combine(_dir, ".git"));
        Assert.Null(ProjectLookup.For(_dir, "what does OrderService do", null));
    }

    [Fact]
    public void Skips_a_file_already_read()
    {
        Assert.Null(ProjectLookup.For(_dir, "what does OrderService do", "--- OrderService.cs ---\n..."));
    }

    [Fact]
    public void A_big_file_sends_only_the_member_asked_about_with_its_lines()
    {
        var filler = string.Concat(Enumerable.Range(0, 400).Select(i => $"    public int Field{i} = {i};\n"));
        File.WriteAllText(Path.Combine(_dir, "src", "Big.cs"), $"public class Big\n{{\n{filler}    public int ComputeTotal(int a) => a + 1;\n}}\n");

        var found = ProjectLookup.For(_dir, "Is ComputeTotal right?", null)!;

        Assert.Contains("--- src/Big.cs (lines 403-403) ---", found.Text);
        Assert.Contains("public int ComputeTotal(int a) => a + 1;", found.Text);
        Assert.True(found.Text.Length <= ProjectLookup.MaxChars);
    }

    [Fact]
    public async Task The_session_sends_the_lookup_and_shows_what_it_read()
    {
        var gateway = new FakeGateway();
        var output = new StringWriter();
        var theme = new Theme(ColorMode.None, unicode: false);
        var session = new Session(gateway, new Ui(output, theme, 80), () => new MarkdownStream(output, theme, 80), _dir);

        await session.AskAsync("what does OrderService do", CancellationToken.None);

        Assert.Contains("class OrderService", gateway.LastLookup);
        Assert.Contains("Read src/OrderService.cs", output.ToString());
        Assert.Equal("what does OrderService do", session.History[0].Content); // the history keeps the question alone
    }

    [Fact]
    public void The_code_goes_inside_the_question_so_the_cached_start_stays()
    {
        var plain = PromptBuilder.Build(new PromptParts("CTX", [new("user", "a"), new("assistant", "b")], "Q"), 16384, 512);
        var looked = PromptBuilder.Build(new PromptParts("CTX", [new("user", "a"), new("assistant", "b")], "Q", Lookup: "--- A.cs ---\nclass A {}"), 16384, 512);

        Assert.Equal(plain.SkipLast(1).Select(m => m.Content), looked.SkipLast(1).Select(m => m.Content));
        Assert.Equal(PromptBuilder.LookupStart + "--- A.cs ---\nclass A {}" + PromptBuilder.FilesEnd + "\n\nQ", looked[^1].Content);
    }
}
