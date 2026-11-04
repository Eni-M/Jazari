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

    [Theory]
    [InlineData(@"C:\Users\me\.ssh\config", true)]
    [InlineData("a/.aws/credentials", true)]
    [InlineData("repo/.git/config", true)]
    [InlineData("src/.github/workflows/ci.yml", false)]
    [InlineData("src/ssh/Client.cs", false)]
    [InlineData(".ssh", false)] // a file of that name, not the folder
    public void Files_in_credential_folders_are_secret(string path, bool secret) => Assert.Equal(secret, ProjectFiles.IsSecret(path));

    [Fact]
    public void A_linked_file_is_not_followed()
    {
        var outside = Directory.CreateTempSubdirectory("jazari outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "id_rsa"), "PRIVATE KEY");
            Add("src/Home.cs", "class Home;");
            try { File.CreateSymbolicLink(Path.Combine(_dir, "notes.txt"), Path.Combine(outside, "id_rsa")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // Windows without the right to make links

            var (files, _) = ProjectFiles.Load(_dir);

            Assert.Equal(["src/Home.cs"], files.Select(f => f.Path));
        }
        finally { Directory.Delete(outside, recursive: true); }
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
    public void A_fragment_or_a_repeated_line_does_not_verify_a_finding()
    {
        List<ProjectFile> unit = [new("a.cs", "if (ok)\n{\n    return user.Password;\n}\nlog.Info(\"done\");\nlog.Info(\"done\");\n")];

        var found = ProjectReview.Parse("""
            {"findings":[
              {"file":"a.cs","quote":"}","problem":"a brace","severity":"low"},
              {"file":"a.cs","quote":"user.Password","problem":"part of a line","severity":"high"},
              {"file":"a.cs","quote":"return user.Password;","problem":"leaks it","severity":"high"},
              {"file":"a.cs","quote":"log.Info(\"done\");","problem":"twice","severity":"low"},
              {"file":"a.cs","quote":"=== ((( ))) ===","problem":"no name in it","severity":"low"}]}
            """, unit)!;

        Assert.False(found[0].Verified); // shorter than a meaningful line
        Assert.False(found[1].Verified); // under 80% of the line it's in
        Assert.True(found[2].Verified);
        Assert.Equal(3, found[2].Line);
        Assert.Equal("", found[2].Fix); // the fix is optional now
        Assert.True(found[3].Ambiguous);
        Assert.False(found[3].Verified);
        Assert.False(found[4].Verified);
    }

    [Fact]
    public void Only_folders_inside_the_project_can_mark_files_as_secret()
    {
        // The project itself may live under such a folder; azd puts projects' environments in .azure, for one.
        Add(".azure/app/Program.cs", "class Program;");
        Add(".azure/app/.azure/dev/config.json", "{}");
        var (files, _) = ProjectFiles.Load(Path.Combine(_dir, ".azure", "app"));
        Assert.Equal(["Program.cs"], files.Select(f => f.Path));
    }

    [Fact]
    public void Jazariignore_leaves_out_folders_kinds_of_file_and_paths()
    {
        Add(".jazariignore", "# not for the model\nfixtures/\n*.generated.cs\nsrc/legacy/*\n");
        Add("src/App.cs", "class App;");
        Add("src/Api.generated.cs", "generated");
        Add("src/legacy/Old.cs", "old");
        Add("tests/fixtures/Planted.cs", "planted");

        var (files, _) = ProjectFiles.Load(_dir);

        Assert.Equal(["src/App.cs"], files.Select(f => f.Path));
    }

    [Fact]
    public async Task A_review_names_every_file_the_projects_jazariignore_hid()
    {
        Add(".jazariignore", "Backdoor.cs\n");
        Add("App.cs", "class App;");
        Add("Backdoor.cs", "class Backdoor;");

        var text = File.ReadAllText((await Review().RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains("## Left out by the project's .jazariignore (1)", text);
        Assert.Contains("- Backdoor.cs", text);
        Assert.Contains(".jazariignore left out 1 file", _output.ToString());
    }

    [Fact]
    public async Task A_jazariignore_that_hides_everything_is_named_too()
    {
        Add(".jazariignore", "*.cs\n");
        Add("Backdoor.cs", "class Backdoor;");

        Assert.Null(await Review().RunAsync(_dir, CancellationToken.None));

        Assert.Contains(".jazariignore left out every file. It hid Backdoor.cs", _output.ToString());
    }

    [Theory]
    [InlineData("a/fixtures/x.cs", "fixtures/", true)]
    [InlineData("fixtures.cs", "fixtures/", false)] // a file, not the folder
    [InlineData("deep/Api.generated.cs", "*.generated.cs", true)]
    [InlineData("src/legacy/Old.cs", "src/legacy/*", true)]
    [InlineData("other/src/legacy/Old.cs", "src/legacy/*", false)]
    public void Ignore_patterns_match_as_documented(string path, string pattern, bool ignored) =>
        Assert.Equal(ignored, ProjectFiles.IsIgnored(path, [pattern]));

    [Fact]
    public async Task Agents_md_goes_with_questions_and_edits_but_not_as_read_files()
    {
        Add("AGENTS.md", "Use file-scoped namespaces.");
        Add("a.cs", "int a;");
        _gateway.Complete = (_, _) => Task.FromResult(OneEdit);
        var session = Session();

        await session.HandleAsync("how should I name things?", CancellationToken.None);
        Assert.Equal("Use file-scoped namespaces.", _gateway.LastConventions);
        Assert.Null(_gateway.LastContext); // so the gateway still searches the law book

        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"int a;","replace":"int b;"}]}""");
        await session.HandleAsync("/edit a.cs rename it", CancellationToken.None);
        Assert.Equal("Use file-scoped namespaces.", _gateway.LastConventions);
    }

    [Fact]
    public async Task A_linked_agents_md_is_not_followed()
    {
        var outside = Directory.CreateTempSubdirectory("jazari outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "id_rsa"), "PRIVATE KEY");
            try { File.CreateSymbolicLink(Path.Combine(_dir, "AGENTS.md"), Path.Combine(outside, "id_rsa")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // Windows without the right to make links

            await Session().HandleAsync("q", CancellationToken.None);

            Assert.Null(_gateway.LastConventions);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Outside_a_repository_only_the_projects_own_folder_is_searched()
    {
        // _dir is under the temp folder, with no repository: an AGENTS.md above it isn't this project's.
        var project = Directory.CreateDirectory(Path.Combine(_dir, "proj")).FullName;
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "from above");
        var session = new MyOwnLLM.Cli.Session(_gateway, new Ui(_output, _theme, 100), () => new MarkdownStream(_output, _theme, 100), project);

        await session.HandleAsync("q", CancellationToken.None);
        Assert.Null(_gateway.LastConventions);

        Directory.CreateDirectory(Path.Combine(_dir, ".git")); // now _dir is the repository's root
        await session.HandleAsync("q", CancellationToken.None);
        Assert.Equal("from above", _gateway.LastConventions);
    }

    [Fact]
    public async Task Context_says_when_an_agents_md_goes_with_questions()
    {
        Add("AGENTS.md", "Use records.");

        await Session().HandleAsync("/context", CancellationToken.None);

        Assert.Contains("AGENTS.md:", _output.ToString());
    }

    [Fact]
    public async Task A_huge_agents_md_is_cut_and_none_means_none()
    {
        var session = Session();
        await session.HandleAsync("q", CancellationToken.None);
        Assert.Null(_gateway.LastConventions);

        Add("AGENTS.md", new string('x', 10_000));
        await session.HandleAsync("q", CancellationToken.None);
        Assert.Equal(2000, _gateway.LastConventions!.Length);
    }

    [Fact]
    public async Task A_review_of_a_path_that_isnt_there_says_so()
    {
        Assert.Null(await Review().RunAsync(Path.Combine(_dir, "missing"), default));
        Assert.Contains("Can't find", _output.ToString());
    }

    [Theory]
    [InlineData("`return user.Password;`")]
    [InlineData("3: return user.Password;")]
    [InlineData("line 3: `return user.Password;`")]
    public void A_quote_with_backticks_or_its_line_number_still_verifies(string quote)
    {
        List<ProjectFile> unit = [new("a.cs", "if (ok)\n{\n    return user.Password;\n}\n")];

        var found = ProjectReview.Parse($$"""{"findings":[{"file":"a.cs","quote":"{{quote}}","problem":"leaks it","severity":"high"}]}""", unit)!;

        Assert.True(Assert.Single(found).Verified);
        Assert.Equal(3, found[0].Line);
    }

    [Fact]
    public async Task The_report_names_the_parts_that_werent_reviewed_and_merges_repeats()
    {
        Add("a.cs", "var sql = 1;");
        Add("b.cs", "bbbb");
        _gateway.Complete = (_, context) => Task.FromResult(context.Contains("b.cs") ? "{\"findings\":["
            : """{"findings":[{"file":"a.cs","quote":"var sql = 1;","problem":"bad","severity":"high"},{"file":"a.cs","quote":"var sql = 1;","problem":"bad","severity":"high"}]}""");

        var text = File.ReadAllText((await Review(unitTokens: 3).RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains("## Not reviewed this run (1)", text);
        Assert.Contains("- b.cs", text);
        Assert.Contains("Verified (1)", text); // the repeat is one finding
    }

    [Fact]
    public async Task A_question_after_docs_can_refer_to_what_docs_showed()
    {
        _gateway.Docs = (true, [new DocPassage("Minimal APIs", "https://learn.microsoft.com/aspnet/core/minimal", "Use Results.NotFound().")]);
        var session = Session();

        await session.HandleAsync("/docs not found result", CancellationToken.None);
        await session.HandleAsync("elaborate", CancellationToken.None);

        var (history, question) = _gateway.Asked.Last();
        Assert.Equal("elaborate", question);
        Assert.Equal("not found result", history[0].Content);
        Assert.Contains("Use Results.NotFound().", history[1].Content);
    }

    [Fact]
    public async Task A_docs_search_that_found_nothing_stays_out_of_the_conversation()
    {
        _gateway.Docs = (true, []);
        var session = Session();

        await session.HandleAsync("/docs anything", CancellationToken.None);

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task A_part_that_wasnt_reviewed_is_named_with_its_lines()
    {
        var text = string.Concat(Enumerable.Range(1, 300).Select(i => $"line {i} {new string('y', 40)}\n"));
        Add("big.cs", text);
        var piece = ProjectReview.Units([new ProjectFile("big.cs", text)], 1000).SelectMany(u => u).First(p => p.Text.Contains("line 150 "));
        var last = piece.FirstLine + piece.Text.TrimEnd('\n').Count(c => c == '\n');
        _gateway.Complete = (_, context) => Task.FromResult(context.Contains("line 150 ") ? "{\"findings\":[" : """{"findings":[]}""");

        var report = File.ReadAllText((await Review(unitTokens: 1000).RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains($"- big.cs lines {piece.FirstLine}-{last}", report);
    }

    [Fact]
    public async Task One_file_and_one_part_are_counted_in_the_singular()
    {
        Add("a.cs", "aaaa");
        _gateway.Complete = (_, _) => Task.FromResult("{\"findings\":[");

        var report = File.ReadAllText((await Review().RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains("1 file read, 0 skipped, 1 part not reviewed", report);
        Assert.Contains("Reviewing 1 file in 1 part,", _output.ToString());
    }

    [Fact]
    public async Task A_quote_that_is_only_part_of_a_line_isnt_called_missing()
    {
        Add("a.cs", "var total = price * count + shipping - discount;\n");
        _gateway.Complete = (_, _) => Task.FromResult("""{"findings":[{"file":"a.cs","quote":"var total = price","problem":"bad","severity":"low"}]}""");

        var report = File.ReadAllText((await Review().RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains("Unverified: the quote isn't one exact line of the code (1)", report);
        Assert.DoesNotContain("wasn't found", report);
    }

    [Fact]
    public async Task An_ambiguous_finding_gets_its_own_section_and_no_empty_fix()
    {
        Add("Auth.cs", "log.Info(\"done\");\nlog.Info(\"done\");");
        _gateway.Complete = (_, _) => Task.FromResult("""{"findings":[{"file":"Auth.cs","quote":"log.Info(\"done\");","problem":"bad","severity":"low"}]}""");

        var text = File.ReadAllText((await Review().RunAsync(_dir, CancellationToken.None))!);

        Assert.Contains("Ambiguous: the quoted line appears more than once (1)", text);
        Assert.DoesNotContain("**Fix:**", text);
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
        Assert.Contains("1 part not reviewed", File.ReadAllText(report!));

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
    public async Task Read_starts_the_model_reading_before_the_question()
    {
        Add("Program.cs", "class Program;");

        await Session().HandleAsync("/read Program.cs", CancellationToken.None);

        Assert.Contains("class Program;", Assert.Single(_gateway.Warmed));
        Assert.Equal(0, _gateway.Completions);
    }

    [Fact]
    public async Task Piped_input_goes_with_the_question()
    {
        var session = Session();

        session.UsePipedInput("+ var sql = \"SELECT \" + id;");
        await session.AskAsync("review this", CancellationToken.None);

        Assert.Contains("--- piped input ---\n+ var sql", _gateway.LastContext);
    }

    [Fact]
    public async Task Read_says_what_didnt_fit()
    {
        foreach (var i in Enumerable.Range(0, 3)) Add($"f{i}.cs", new string('x', 9000));

        await Session().HandleAsync("/read .", CancellationToken.None);

        Assert.Contains("Read 2 files", _output.ToString());
        Assert.Contains("1 didn't fit", _output.ToString());
    }

    [Fact]
    public async Task Read_gives_the_files_that_didnt_fit_as_an_outline()
    {
        foreach (var i in Enumerable.Range(0, 3))
            Add($"f{i}.cs", $"class Orders{i} {{ public Task RefundAsync(int orderId) {{ /* {new string('x', 9000)} */ }} }}");
        var session = Session();

        await session.HandleAsync("/read .", CancellationToken.None);
        await session.HandleAsync("where are refunds?", CancellationToken.None);

        Assert.Contains("--- outline of 1 more file, signatures only ---\nf2.cs\n  class Orders2\n    Task RefundAsync(int orderId)\n", _gateway.LastContext);
        Assert.Equal(2, _gateway.LastContext!.Split(new string('x', 9000)).Length - 1);
        Assert.Contains("1 of those go as an outline", _output.ToString());
    }

    [Fact]
    public void Outline_shows_types_and_signatures_nested_without_bodies()
    {
        var members = RepoMap.Members("""
            namespace Shop;
            public record Money(decimal Amount) : IComparable<Money> { public int CompareTo(Money? o) => 0; }
            public class Cart
            {
                public Cart(int id) { }
                public decimal Total { get; }
                enum State { Open }
                public List<T> Items<T>(Func<T,
                    bool> keep) { return []; }
            }
            """);

        Assert.Equal([
            "  record Money(decimal Amount) : IComparable<Money>",
            "    int CompareTo(Money? o)",
            "  class Cart",
            "    Cart(int id)",
            "    decimal Total",
            "    enum State",
            "    List<T> Items<T>(Func<T, bool> keep)",
        ], members);
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
    public async Task Edit_takes_a_quoted_path_with_spaces()
    {
        var path = Add("My Docs/a b.cs", "int a;");
        var asked = "";
        _gateway.Complete = (question, _) => { asked = question; return Task.FromResult(OneEdit); };

        await Session().HandleAsync("/edit \"My Docs/a b.cs\" rename it", CancellationToken.None);

        Assert.Equal("int b;", File.ReadAllText(path));
        Assert.EndsWith("rename it", asked);
    }

    [Theory]
    [InlineData("a.cs", "a.cs", "")]
    [InlineData("a.cs make it async", "a.cs", "make it async")]
    [InlineData("\"My Docs/a b.cs\"  make it async", "My Docs/a b.cs", "make it async")]
    public void A_path_and_its_request_are_split(string text, string path, string rest) =>
        Assert.Equal((path, rest), MyOwnLLM.Cli.Session.PathAndRest(text));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("\"unclosed a.cs fix")]
    public void Nothing_usable_gives_no_path(string? text) => Assert.Null(MyOwnLLM.Cli.Session.PathAndRest(text));

    [Fact]
    public async Task Save_writes_the_conversation_and_never_overwrites()
    {
        var session = Session();
        await session.HandleAsync("/save", CancellationToken.None);
        Assert.Contains("Nothing to save yet.", _output.ToString());

        await session.HandleAsync("what is DI?", CancellationToken.None);
        await session.HandleAsync("/save notes/chat.md", CancellationToken.None);
        var saved = File.ReadAllText(Path.Combine(_dir, "notes", "chat.md"));
        Assert.Contains("### You\n\nwhat is DI?\n\n### Jazari\n\nHello\n", saved);

        File.WriteAllText(Path.Combine(_dir, "notes", "chat.md"), "mine");
        await session.HandleAsync("/save notes/chat.md", CancellationToken.None);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "notes", "chat.md")));
        Assert.Contains("already exists", _output.ToString());
    }

    [Fact]
    public async Task Docs_shows_the_passage_and_its_page()
    {
        _gateway.Docs = (true, [new DocPassage("Minimal APIs", "https://learn.microsoft.com/aspnet/core/minimal", "Use Results.NotFound().")]);
        await Session().HandleAsync("/docs not found result", CancellationToken.None);
        Assert.Contains("Use Results.NotFound().", _output.ToString());
        Assert.Contains("Minimal APIs · https://learn.microsoft.com/aspnet/core/minimal", _output.ToString());
        Assert.Equal(0, _gateway.Completions);
    }

    [Theory]
    [InlineData(false, "No law book is installed")]
    [InlineData(true, "no passage that clearly covers that")]
    public async Task Docs_says_why_nothing_came_back(bool available, string said)
    {
        _gateway.Docs = (available, []);
        await Session().HandleAsync("/docs anything", CancellationToken.None);
        Assert.Contains(said, _output.ToString());
    }

    [Fact]
    public async Task Edit_warns_before_asking_when_the_change_breaks_csharp_syntax()
    {
        Add("a.cs", "class A { int a; }");
        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"int a;","replace":"int b"}]}""");
        string? shownWhenAsked = null;

        await Session(_ => { shownWhenAsked = _output.ToString(); return false; }).HandleAsync("/edit a.cs rename it", CancellationToken.None);

        Assert.Contains("breaks the C# syntax", shownWhenAsked);
        Assert.Contains("; expected", shownWhenAsked);
    }

    [Fact]
    public async Task A_change_that_breaks_the_syntax_is_asked_for_once_more_with_the_errors()
    {
        var path = Add("a.cs", "class A { int a; }");
        var asked = new List<string>();
        _gateway.Complete = (question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(asked.Count == 1 ? """{"edits":[{"search":"int a;","replace":"int b"}]}""" : """{"edits":[{"search":"int a;","replace":"int b;"}]}""");
        };

        await Session().HandleAsync("/edit a.cs rename it", CancellationToken.None);

        Assert.Equal(2, asked.Count);
        Assert.Contains("; expected", asked[1]); // the retry carries the errors
        Assert.Equal("class A { int b; }", File.ReadAllText(path));
        Assert.DoesNotContain("breaks the C# syntax.", _output.ToString()); // fixed, so no warning at the question
    }

    [Fact]
    public async Task Only_one_retry_and_a_still_broken_change_is_flagged_before_asking()
    {
        Add("a.cs", "class A { int a; }");
        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"int a;","replace":"int b"}]}""");

        await Session(_ => false).HandleAsync("/edit a.cs rename it", CancellationToken.None);

        Assert.Equal(2, _gateway.Completions);
        Assert.Contains("This change breaks the C# syntax.", _output.ToString());
    }

    [Theory]
    [InlineData("    int  limit =\t16;", "    int limit = 32;")]                 // spacing inside the line
    [InlineData("    int limit = 16;\n\n    int other = 1;", "    int limit = 32;\n    int other = 1;")] // a blank line the file doesn't have
    [InlineData("    int limit = 16;\n    int other = 1;", "    int limit = 32;\n    int other = 1;")]   // the file's blank line left out
    public void Small_differences_in_spacing_and_blank_lines_still_apply(string search, string replace)
    {
        const string file = "class C\n{\n    int limit = 16;\n\n    int other = 1;\n}\n";

        var (text, why) = FileEdit.Apply(file, [new(search, replace)]);

        Assert.Null(why);
        Assert.Contains("int limit = 32;", text);
        Assert.Equal(1, text!.Split("int other = 1;").Length - 1); // replaced, not duplicated
    }

    [Fact]
    public void A_loose_match_found_twice_still_changes_nothing()
    {
        var (text, why) = FileEdit.Apply("a  =  1;\nx\na = 1;\n", [new("a =\t1;\n", "a = 2;")]); // exactly in neither, loosely in both

        Assert.Null(text);
        Assert.Contains("more than once", why);
    }

    [Theory]
    [InlineData("a.cs", "class A { int a; }", "class A { int b; }", 0)] // fine
    [InlineData("a.cs", "class A { int a }", "class A { int b }", 0)]   // already broken the same way: not the edit's doing
    [InlineData("a.cs", "class A { }", "class A { ", 1)]
    [InlineData("a.txt", "x", "class A {", 0)]                         // not C#
    public void Only_syntax_errors_the_edit_adds_are_reported(string path, string before, string after, int errors) =>
        Assert.Equal(errors, FileEdit.NewSyntaxErrors(path, before, after).Count);

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
