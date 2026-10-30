using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class ProjectToolsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari tools").FullName;
    readonly string _store = Directory.CreateTempSubdirectory("jazari store").FullName;
    readonly StringWriter _output = new();
    readonly FakeGateway _gateway = new();
    readonly Theme _theme = new(ColorMode.None, unicode: true);

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _store }) try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    string Add(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    Session Session(Func<string, bool>? confirm = null) =>
        new(_gateway, new Ui(_output, _theme, 100), () => new MarkdownStream(_output, _theme, 100), _dir) { Confirm = confirm ?? (_ => true), ReviewDir = _store };

    ProjectReview Review(int unitTokens = 2000) => new(_gateway, new Ui(_output, _theme, 100), _store, unitTokens);

    [Fact]
    public void Generated_code_data_and_secrets_are_skipped_and_risky_files_come_first()
    {
        Add("src/Home.cs", "class Home;");
        Add("src/AuthController.cs", "class Auth;");
        Add("Migrations/001_Init.cs", "generated");
        Add("src/Form.Designer.cs", "generated");
        Add("node_modules/x/index.js", "dep");
        Add("bin/Debug/app.json", "output");
        Add(".env", "KEY=1");
        Add("server.pem", "key");
        Add("seed-dump.sql", "INSERT");
        Add("logo.png", "binary");

        var (files, skipped) = ProjectFiles.Load(_dir);

        Assert.Equal(["src/AuthController.cs", "src/Home.cs"], files.Select(f => f.Path));
        Assert.Equal(5, skipped); // only what was walked; skipped folders aren't entered
    }

    [Fact]
    public void Files_are_grouped_and_big_ones_cut_at_line_ends()
    {
        var big = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"line {i}"));

        var units = ProjectReview.Units([new("a.cs", "aaaa"), new("b.cs", "bbbb"), new("big.cs", big)], 100);

        Assert.Equal(["a.cs", "b.cs"], units[0].Select(f => f.Path));
        Assert.True(units.Count > 2);
        Assert.All(units, u => Assert.True(u.Sum(f => f.Text.Length) / 4 <= 110));
        Assert.Equal(big + "\n", string.Concat(units.SelectMany(u => u).Where(f => f.Path == "big.cs").Select(f => f.Text)));
    }

    [Fact]
    public void A_risky_file_gets_a_part_of_its_own()
    {
        var units = ProjectReview.Units([new("a.cs", "a"), new("AuthController.cs", "b"), new("c.cs", "c"), new("d.cs", "d")], 100);

        Assert.Equal([["a.cs"], ["AuthController.cs"], ["c.cs", "d.cs"]], units.Select(u => u.Select(f => f.Path).ToArray()).ToArray());
    }

    [Fact]
    public void A_finding_is_verified_only_when_its_quote_is_in_the_code()
    {
        List<ProjectFile> unit = [new("a.cs", "first\n  var sql = \"SELECT\" + id;\nlast"), new("b.cs", "other")];

        var found = ProjectReview.Parse("""
            {"findings":[
              {"file":"b.cs","quote":"var sql =   \"SELECT\" + id;","problem":"injection","severity":"high","fix":"use parameters"},
              {"file":"a.cs","quote":"var made = up;","problem":"invented","severity":"high","fix":"none"}]}
            """, unit)!;

        Assert.True(found[0].Verified);
        Assert.Equal("a.cs", found[0].File); // corrected from the wrong file it named
        Assert.Equal(2, found[0].Line);
        Assert.False(found[1].Verified);
        Assert.Null(found[1].Line);
        Assert.Null(ProjectReview.Parse("not json", unit));
    }

    [Fact]
    public async Task A_review_writes_a_report_and_the_next_one_only_reads_what_changed()
    {
        var path = Add("Auth.cs", "var sql = 1;");
        Add("Other.cs", "int x;");
        _gateway.Complete = (_, _) => Task.FromResult("""{"findings":[{"file":"Auth.cs","quote":"var sql = 1;","problem":"bad","severity":"high","fix":"fix it"}]}""");

        var report = await Review(unitTokens: 3).RunAsync(_dir, CancellationToken.None);

        Assert.Equal(2, _gateway.Completions);
        var text = File.ReadAllText(report!);
        Assert.Contains("[high] Auth.cs:1", text);
        Assert.Contains("Verified (1)", text);

        File.WriteAllText(path, "var sql = 2;");
        await Review(unitTokens: 3).RunAsync(_dir, CancellationToken.None);

        Assert.Equal(3, _gateway.Completions);
    }

    [Fact]
    public async Task A_stopped_review_keeps_its_progress()
    {
        Add("a.cs", "aaaa");
        Add("b.cs", "bbbb");
        var calls = 0;
        _gateway.Complete = (_, _) => ++calls == 2 ? throw new GatewayException("gone", null, null) : Task.FromResult("""{"findings":[]}""");

        Assert.Null(await Review(unitTokens: 1).RunAsync(_dir, CancellationToken.None));
        Assert.NotNull(await Review(unitTokens: 1).RunAsync(_dir, CancellationToken.None));

        Assert.Equal(3, calls); // a.cs once, b.cs failed then worked
    }

    [Fact]
    public async Task A_reply_that_isnt_json_is_retried_next_time()
    {
        Add("a.cs", "aaaa");
        _gateway.Complete = (_, _) => Task.FromResult("{\"findings\":[");

        var report = await Review().RunAsync(_dir, CancellationToken.None);
        Assert.Contains("1 parts failed", File.ReadAllText(report!));

        await Review().RunAsync(_dir, CancellationToken.None);
        Assert.Equal(2, _gateway.Completions);
    }

    [Fact]
    public async Task Read_sends_the_file_with_the_next_questions_until_cleared()
    {
        Add("Program.cs", "class Program;");
        var session = Session();

        await session.HandleAsync("/read Program.cs", CancellationToken.None);
        await session.HandleAsync("what is this?", CancellationToken.None);
        Assert.Contains("class Program;", _gateway.LastContext);

        await session.HandleAsync("/clear", CancellationToken.None);
        await session.HandleAsync("and now?", CancellationToken.None);
        Assert.Null(_gateway.LastContext);
    }

    [Fact]
    public async Task Read_says_what_didnt_fit()
    {
        foreach (var i in Enumerable.Range(0, 3)) Add($"f{i}.cs", new string('x', 9000));

        await Session().HandleAsync("/read .", CancellationToken.None);

        Assert.Contains("Read 2 files", _output.ToString());
        Assert.Contains("1 didn't fit", _output.ToString());
    }

    const string OneEdit = """{"edits":[{"search":"int a;","replace":"int b;"}]}""";

    [Fact]
    public async Task Edit_shows_the_change_writes_after_yes_and_undo_puts_it_back()
    {
        var path = Add("a.cs", "line\r\nint a;\r\n");
        _gateway.Complete = (_, _) => Task.FromResult(OneEdit);
        var session = Session();

        await session.HandleAsync("/edit a.cs rename it", CancellationToken.None);

        Assert.Contains("- int a;", _output.ToString());
        Assert.Contains("+ int b;", _output.ToString());
        Assert.Equal("line\r\nint b;\r\n", File.ReadAllText(path));

        await session.HandleAsync("/undo", CancellationToken.None);
        Assert.Equal("line\r\nint a;\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_writes_nothing_on_no()
    {
        var path = Add("a.cs", "int a;");
        _gateway.Complete = (_, _) => Task.FromResult(OneEdit);

        await Session(_ => false).HandleAsync("/edit a.cs rename it", CancellationToken.None);

        Assert.Equal("int a;", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_refuses_files_outside_the_project()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside.cs");
        await Session().HandleAsync($"/edit {outside} change it", CancellationToken.None);
        await Session().HandleAsync("/edit ..\\outside.cs change it", CancellationToken.None);

        Assert.Equal(0, _gateway.Completions);
        Assert.Contains("only edits files inside", _output.ToString());
    }

    [Theory]
    [InlineData("""{"edits":[{"search":"gone","replace":"x"}]}""", "isn't in the file")]
    [InlineData("""{"edits":[{"search":"int","replace":"x"}]}""", "more than once")]
    [InlineData("""{"edits":[{"search":"","replace":"x"}]}""", "nothing to search")]
    public void Edits_that_dont_match_exactly_once_change_nothing(string reply, string problem)
    {
        var (text, why) = FileEdit.Apply("int a;\nint b;", FileEdit.Parse(reply)!);

        Assert.Null(text);
        Assert.Contains(problem, why);
    }

    [Fact]
    public void Edits_with_wrong_indentation_match_whole_lines_and_keep_the_files_indentation()
    {
        var reply = """{"edits":[{"search":"if (a)\n{\n    run();  \n}","replace":"if (b)\n{\n    stop();\n}"}]}""";

        var (text, why) = FileEdit.Apply("class C\n{\n    if (a)\n    {\n        run();\n    }\n}\n", FileEdit.Parse(reply)!);

        Assert.Null(why);
        Assert.Equal("class C\n{\n    if (b)\n    {\n        stop();\n    }\n}\n", text);
    }

    [Fact]
    public void Line_matches_that_appear_twice_change_nothing()
    {
        var reply = """{"edits":[{"search":"  run();","replace":"stop();"}]}""";

        var (text, why) = FileEdit.Apply("\trun();\n\trun();", FileEdit.Parse(reply)!);

        Assert.Null(text);
        Assert.Contains("more than once", why);
    }
}
