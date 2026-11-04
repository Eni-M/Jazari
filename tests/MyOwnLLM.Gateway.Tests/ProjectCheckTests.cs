using System.Diagnostics;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public sealed class SmallProject : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("jazari-project").FullName;
    public string File1 => Path.Combine(Dir, "A.cs");
    public const string Code = "public class A { public int F() => B.G(); }\npublic static class B { public static int G() => 1; }\n";

    public SmallProject()
    {
        File.WriteAllText(Path.Combine(Dir, "Small.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
        File.WriteAllText(File1, Code);
        var psi = new ProcessStartInfo("dotnet", ["restore", "--disable-build-servers", "-v", "q"]) { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true };
        using var restore = Process.Start(psi)!;
        restore.StandardOutput.ReadToEnd();
        restore.WaitForExit();
    }

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

public class ProjectCheckTests(SmallProject small) : IClassFixture<SmallProject>
{
    [Fact]
    public async Task An_edit_calling_a_method_that_does_not_exist_reports_only_that_error()
    {
        var errors = await ProjectCheck.NewErrorsAsync(small.File1, small.Dir, SmallProject.Code, SmallProject.Code.Replace("B.G()", "B.Nope()"), CancellationToken.None);
        Assert.Contains("Nope", Assert.Single(errors!));
    }

    [Fact]
    public async Task An_error_already_there_is_not_reported_even_when_lines_move()
    {
        var broken = SmallProject.Code + "public class C { Missing m; }\n";
        var errors = await ProjectCheck.NewErrorsAsync(small.File1, small.Dir, broken, "// a new first line\n" + broken, CancellationToken.None);
        Assert.Empty(errors!);
    }

    [Fact]
    public async Task A_file_outside_any_project_gives_null()
    {
        var folder = Directory.CreateTempSubdirectory("jazari-noproject").FullName;
        try
        {
            var file = Path.Combine(folder, "A.cs");
            Assert.Null(await ProjectCheck.NewErrorsAsync(file, folder, "", "", CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task A_project_that_will_not_load_gives_null()
    {
        var folder = Directory.CreateTempSubdirectory("jazari-badproject").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Bad.csproj"), "this isn't xml");
            var file = Path.Combine(folder, "A.cs");
            File.WriteAllText(file, "class A { }");
            Assert.Null(await ProjectCheck.NewErrorsAsync(file, folder, "class A { }", "class A { int }", CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Trust_is_remembered_for_that_project_only_and_a_damaged_file_trusts_nothing()
    {
        var file = Path.Combine(small.Dir, "trust.json");
        var project = Path.Combine(small.Dir, "Small.csproj");
        var trust = new ProjectTrust(file);
        Assert.False(trust.IsTrusted(project, small.Dir));
        trust.Trust(project, small.Dir);
        Assert.True(new ProjectTrust(file).IsTrusted(project, small.Dir));
        Assert.False(trust.IsTrusted(Path.Combine(small.Dir, "Other.csproj"), small.Dir));
        File.WriteAllText(file, "not json");
        Assert.False(trust.IsTrusted(project, small.Dir));
    }
}

public class ProjectTrustChangeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("jazari-trust").FullName;
    readonly string _project;
    readonly ProjectTrust _trust;

    public ProjectTrustChangeTests()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        _project = Path.Combine(folder, "P.csproj");
        File.WriteAllText(_project, "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        _trust = new ProjectTrust(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        _trust.Trust(_project, _root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("src/P.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/Directory.Build.targets")]
    [InlineData("src/custom.targets")]
    [InlineData("global.json")]
    [InlineData("src/P.csproj.user")]
    [InlineData("src/obj/P.csproj.nuget.g.targets")]
    [InlineData("src/Directory.Build.rsp")]
    public void Changing_or_adding_a_build_file_asks_again(string file)
    {
        Assert.True(_trust.IsTrusted(_project, _root));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, file))!);
        File.WriteAllText(Path.Combine(_root, file), "<Project><Target Name=\"Evil\" /></Project>");
        Assert.False(_trust.IsTrusted(_project, _root));
    }

    [Theory]
    [InlineData("<ProjectReference Include=\"..\\lib\\L.csproj\" />", "lib/L.csproj")]
    [InlineData("<ProjectReference Include=\"..\\lib\\L.csproj\" />", "lib/L.targets")]
    [InlineData("<Import Project=\"..\\shared\\S.targets\" />", "shared/S.targets")]
    public void A_change_in_what_the_project_names_asks_again(string line, string changed)
    {
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        File.WriteAllText(Path.Combine(_root, "lib", "L.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "shared", "S.targets"), "<Project />");
        File.WriteAllText(_project, $"<Project><ItemGroup>{line}</ItemGroup></Project>");
        _trust.Trust(_project, _root);
        Assert.True(_trust.IsTrusted(_project, _root));

        File.WriteAllText(Path.Combine(_root, changed), "<Project><Target Name=\"Evil\" /></Project>");

        Assert.False(_trust.IsTrusted(_project, _root));
    }

    [Fact]
    public void Projects_that_reference_each_other_and_files_that_are_not_xml_are_fine()
    {
        File.WriteAllText(_project, "<Project><ItemGroup><ProjectReference Include=\"P.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "broken.props"), "not xml");
        _trust.Trust(_project, _root);
        Assert.True(_trust.IsTrusted(_project, _root));
    }

    [Fact]
    public void Bytes_moved_from_one_file_to_another_ask_again()
    {
        File.WriteAllText(_project, "<Project />AB");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "CD");
        _trust.Trust(_project, _root);

        File.WriteAllText(_project, "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "ABCD");

        Assert.False(_trust.IsTrusted(_project, _root));
    }

    [Fact]
    public void Changing_source_code_does_not_ask_again()
    {
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A { }");
        Assert.True(_trust.IsTrusted(_project, _root));
    }
}

public class DeepEditTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari-deepedit").FullName;
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();
    readonly List<string> _asked = [];
    int _checks;
    bool _answer = true;

    public DeepEditTests()
    {
        File.WriteAllText(Path.Combine(_dir, "P.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "class A { int F() => 1; }");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Session NewSession(Func<IReadOnlyList<string>?> errors) => new(_gateway, new Ui(_output, new Theme(ColorMode.None, true), 200), () => new MarkdownStream(_output, new Theme(ColorMode.None, true), 200), _dir)
    {
        Confirm = question => { _asked.Add(question); return _answer; },
        TrustFile = Path.Combine(_dir, "trust.json"),
        ProjectErrors = (_, _, _, _, _) => { _checks++; return Task.FromResult(errors()); },
    };

    const string Edit = """{"edits":[{"search":"=> 1","replace":"=> 2"}]}""";

    [Fact]
    public async Task Saying_no_to_trust_never_loads_the_project_and_asks_again_next_time()
    {
        _gateway.Complete = (_, _) => Task.FromResult(Edit);
        _answer = false;
        var session = NewSession(() => []);

        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);
        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);

        Assert.Equal(0, _checks);
        Assert.Equal(2, _asked.Count(q => q.Contains("build scripts")));
    }

    [Fact]
    public async Task Saying_yes_checks_the_project_and_asks_only_once()
    {
        _gateway.Complete = (_, _) => Task.FromResult(Edit);
        var session = NewSession(() => []);

        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);
        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);

        Assert.True(_checks >= 1);
        Assert.Equal(1, _asked.Count(q => q.Contains("build scripts")));
        Assert.True(_gateway.LastCompleteDeep);
    }

    [Fact]
    public async Task Deep_edit_retries_once_with_the_new_errors_and_never_a_second_time()
    {
        var calls = 0;
        _gateway.Complete = (question, _) => { calls++; return Task.FromResult(Edit); };
        var session = NewSession(() => ["CS0103 no such name"]);

        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Contains("CS0103", _gateway.LastCompleteQuestion);
        Assert.Contains("CS0103", _output.ToString());
    }

    [Fact]
    public async Task Plain_edit_never_asks_about_trust_and_never_loads_a_project()
    {
        _gateway.Complete = (_, _) => Task.FromResult(Edit);
        await NewSession(() => []).HandleAsync("/edit A.cs change it", CancellationToken.None);

        Assert.Equal(0, _checks);
        Assert.DoesNotContain(_asked, q => q.Contains("build scripts"));
        Assert.False(_gateway.LastCompleteDeep);
    }

    [Fact]
    public async Task The_file_is_written_only_after_yes_as_today()
    {
        _gateway.Complete = (_, _) => Task.FromResult(Edit);
        new ProjectTrust(Path.Combine(_dir, "trust.json")).Trust(Path.Combine(_dir, "P.csproj"), _dir);
        var answers = new Queue<bool>([false]);
        var session = new Session(_gateway, new Ui(_output, new Theme(ColorMode.None, true), 200), () => new MarkdownStream(_output, new Theme(ColorMode.None, true), 200), _dir)
        {
            Confirm = _ => answers.Dequeue(),
            TrustFile = Path.Combine(_dir, "trust.json"),
            ProjectErrors = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>?>([]),
        };

        await session.HandleAsync("/deep /edit A.cs change it", CancellationToken.None);

        Assert.Contains("=> 1", File.ReadAllText(Path.Combine(_dir, "A.cs")));
    }

    [Fact]
    public async Task A_project_that_will_not_load_still_lets_the_edit_through()
    {
        _gateway.Complete = (_, _) => Task.FromResult(Edit);
        await NewSession(() => null).HandleAsync("/deep /edit A.cs change it", CancellationToken.None);
        Assert.Contains("=> 2", File.ReadAllText(Path.Combine(_dir, "A.cs")));
    }
}
