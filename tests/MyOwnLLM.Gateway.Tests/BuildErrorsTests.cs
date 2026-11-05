using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class BuildErrorsTests
{
    [Fact] public void Reads_a_compiler_error()
    {
        var e = BuildErrors.Parse(@"C:\src\My App (2)\Café.cs(12,5): error CS0103: The name 'x' does not exist in the current context [C:\src\App.csproj]")[0];
        Assert.Equal(@"C:\src\My App (2)\Café.cs", e.File);
        Assert.Equal(12, e.Line);
        Assert.Equal("CS0103", e.Code);
        Assert.Equal("The name 'x' does not exist in the current context", e.Message);
    }

    [Fact] public void Warnings_and_other_lines_are_not_errors() =>
        Assert.Empty(BuildErrors.Parse("C:\\a.cs(1,1): warning CS0168: unused\nBuild FAILED.\nerror MSB1009: Project file does not exist."));

    [Fact] public void Windows_line_ends_do_not_stay_in_the_message() =>
        Assert.Equal("m", BuildErrors.Parse("C:\\a.cs(1,1): error CS1: m [C:\\A.csproj]\r\n")[0].Message);

    [Fact] public void The_same_error_for_two_target_frameworks_counts_once() =>
        Assert.Single(BuildErrors.Parse("C:\\a.cs(1,1): error CS1: m [C:\\A.csproj::TargetFramework=net8.0]\nC:\\a.cs(1,1): error CS1: m [C:\\A.csproj::TargetFramework=net10.0]"));

    [Fact] public void A_moved_error_is_not_new()
    {
        var before = BuildErrors.Parse("C:\\a.cs(1,1): error CS1: m");
        Assert.Empty(BuildErrors.New(before, BuildErrors.Parse("C:\\a.cs(9,1): error CS1: m")));
        Assert.Single(BuildErrors.New(before, BuildErrors.Parse("C:\\a.cs(9,1): error CS2: other")));
    }
}
