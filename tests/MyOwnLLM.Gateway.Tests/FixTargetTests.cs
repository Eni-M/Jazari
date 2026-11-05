using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class FixTargetTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mollm-target").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    string Project(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return path;
    }

    [Fact] public void A_named_project_is_used()
    {
        var named = Project(@"deep\down\App.csproj");
        Project("Other.csproj");
        Assert.Equal(named, FixTarget.Find(_root, @"deep\down\App.csproj").Project);
    }

    [Fact] public void The_only_project_is_found() => Assert.Equal(Project("App.csproj"), FixTarget.Find(_root, null).Project);

    [Fact] public void One_level_down_is_found() => Assert.Equal(Project(@"src\App.csproj"), FixTarget.Find(_root, null).Project);

    [Fact] public void Build_output_folders_are_not_searched()
    {
        Project(@"obj\Stray.csproj");
        Project(@"bin\Stray.csproj");
        Assert.Equal(Project("App.csproj"), FixTarget.Find(_root, null).Project);
    }

    [Fact] public void Two_projects_list_both_and_ask()
    {
        Project("A.csproj");
        Project(@"tests\B.csproj");
        var (project, problem) = FixTarget.Find(_root, null);
        Assert.Null(project);
        Assert.Contains("A.csproj", problem);
        Assert.Contains("B.csproj", problem);
    }

    [Fact] public void No_project_says_so() => Assert.NotNull(FixTarget.Find(_root, null).Problem);

    [Fact] public void A_path_outside_the_project_is_refused()
    {
        var outside = Directory.CreateTempSubdirectory("mollm-outside").FullName;
        try
        {
            var other = Path.Combine(outside, "App.csproj");
            File.WriteAllText(other, "<Project />");
            var (project, problem) = FixTarget.Find(_root, other);
            Assert.Null(project);
            Assert.NotNull(problem);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact] public void A_named_file_that_is_not_a_project_is_refused()
    {
        var file = Path.Combine(_root, "A.cs");
        File.WriteAllText(file, "");
        Assert.Null(FixTarget.Find(_root, "A.cs").Project);
    }

    BuildError At(string relative, string code = "CS1") => new(Path.Combine(_root, relative), 1, code, "m");

    [Fact] public void Generated_and_outside_files_are_not_editable()
    {
        Assert.Empty(FixTarget.Editable([
            At(@"obj\Debug\G.cs"), At(@"src\bin\H.cs"), At("Notes.txt"),
            new BuildError(@"C:\elsewhere\Package.cs", 1, "CS1", "m"),
        ], _root));
    }

    [Fact] public void The_next_editable_file_is_used_when_the_first_is_generated() =>
        Assert.EndsWith("A.cs", FixTarget.Editable([At(@"obj\G.cs"), At("A.cs")], _root)[0].File);

    [Fact] public void Only_the_first_files_errors_and_at_most_five()
    {
        var found = FixTarget.Editable([.. Enumerable.Range(1, 7).Select(i => At("A.cs", $"CS{i}")), At("B.cs")], _root);
        Assert.Equal(FixTarget.MaxErrors, found.Count);
        Assert.All(found, e => Assert.EndsWith("A.cs", e.File));
    }
}
