using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class CliCompletenessTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari cli").FullName;
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();
    readonly List<string[]> _gitCalls = [];
    (int, string) _filters = (1, "");
    string? _clipboard;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Session NewSession(Func<string[], (int, string)> git)
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        return new Session(_gateway, new Ui(_output, theme, 100), () => new MarkdownStream(_output, theme, 100), _dir)
        {
            Git = (args, _) =>
            {
                if (args.Contains("--get-regexp")) return Task.FromResult(_filters); // the hardening's look at the repo's filters
                _gitCalls.Add(args);
                return Task.FromResult(git(args));
            },
            SetClipboard = text => _clipboard = text,
        };
    }

    const string Diff = "diff --git a/A.cs b/A.cs\n--- a/A.cs\n+++ b/A.cs\n@@ -1 +1 @@\n-int a;\n+int b;\n";

    [Fact]
    public async Task Diff_reads_the_changes_for_the_next_questions_without_running_repo_tools()
    {
        var session = NewSession(_ => (0, Diff));

        await session.HandleAsync("/diff", CancellationToken.None);
        await session.AskAsync("review these changes", CancellationToken.None);

        Assert.Contains("+int b;", _gateway.LastContext);
        Assert.Contains("+int b;", Assert.Single(_gateway.Warmed));
        var args = Assert.Single(_gitCalls);
        Assert.Contains("core.fsmonitor=false", args);
        Assert.Contains("--no-ext-diff", args);
        Assert.Contains("--no-textconv", args);
        Assert.Equal("HEAD", args[^1]);
        Assert.Contains("1 file", _output.ToString());
    }

    [Fact]
    public async Task Diff_switches_off_the_repos_filter_programs()
    {
        _filters = (0, "filter.evil.clean\nfilter.lfs.process\n");
        var session = NewSession(_ => (0, Diff));

        await session.HandleAsync("/diff", CancellationToken.None);

        var args = Assert.Single(_gitCalls);
        Assert.Contains("filter.evil.clean=", args);
        Assert.Contains("filter.lfs.process=", args);
        Assert.Contains("log.showSignature=false", args);
    }

    [Fact]
    public async Task Diff_leaves_out_secret_files()
    {
        const string withSecret = Diff + "diff --git a/.env b/.env\n--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-KEY=old\n+KEY=hunter2\n";
        var session = NewSession(_ => (0, withSecret));

        await session.HandleAsync("/diff", CancellationToken.None);
        await session.AskAsync("review", CancellationToken.None);

        Assert.Contains("+int b;", _gateway.LastContext);
        Assert.DoesNotContain("hunter2", _gateway.LastContext);
        Assert.Contains("Left out the changes to .env", _output.ToString());
    }

    [Fact]
    public async Task Ctrl_C_while_git_runs_stops_the_command_not_the_session()
    {
        using var cts = new CancellationTokenSource();
        var theme = new Theme(ColorMode.None, unicode: true);
        var session = new Session(_gateway, new Ui(_output, theme, 100), () => new MarkdownStream(_output, theme, 100), _dir)
        {
            Git = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult((0, "")); },
        };

        await session.HandleAsync("/diff", cts.Token);

        Assert.Contains("stopped", _output.ToString());
    }

    [Fact]
    public async Task Diff_since_a_ref_and_no_changes()
    {
        var session = NewSession(_ => (0, ""));

        await session.HandleAsync("/diff main", CancellationToken.None);

        Assert.Equal("main", _gitCalls[0][^1]);
        Assert.Contains("No changes since main.", _output.ToString());
        Assert.False(session.HasContext);
    }

    [Fact]
    public async Task Diff_outside_a_repository_says_so()
    {
        await NewSession(_ => (128, "fatal: not a git repository")).HandleAsync("/diff", CancellationToken.None);

        Assert.Contains("isn't a git repository", _output.ToString());
    }

    [Fact]
    public async Task Commit_writes_a_message_for_the_staged_changes_and_copies_it()
    {
        _gateway.Complete = (question, context) => Task.FromResult(question.Contains("+int b;") && !question.Contains("earlier") && question.Contains("feat:") && context == ""
            ? """{"subject":"Rename a to b","body":""}""" : """{"subject":"wrong"}""");
        var session = NewSession(args => args.Contains("log") ? (0, "feat: earlier\nfix: before\n") : (0, Diff));

        await session.HandleAsync("/commit", CancellationToken.None);

        Assert.Equal("Rename a to b", _clipboard);
        Assert.Contains("--cached", _gitCalls[0]);
        Assert.Contains("Copied", _output.ToString());
    }

    [Fact]
    public async Task Commit_with_nothing_staged_asks_for_git_add()
    {
        await NewSession(_ => (0, "")).HandleAsync("/commit", CancellationToken.None);

        Assert.Equal(0, _gateway.Completions);
        Assert.Contains("git add", _output.ToString());
    }

    [Theory]
    [InlineData("""{"subject":"Add /diff","body":"So changes can be reviewed."}""", "Add /diff\n\nSo changes can be reviewed.")]
    [InlineData("""{"subject":"Add /diff"}""", "Add /diff")]
    [InlineData("""{"body":"no subject"}""", null)]
    [InlineData("not json", null)]
    public void Commit_messages_are_read_from_the_reply(string reply, string? message) =>
        Assert.Equal(message, Session.CommitMessage(reply));

    [Fact]
    public async Task Context_says_what_the_next_question_sends()
    {
        var session = NewSession(_ => (0, Diff));

        await session.HandleAsync("/context", CancellationToken.None);
        Assert.Contains("No files read", _output.ToString());

        await session.HandleAsync("/diff", CancellationToken.None);
        await session.HandleAsync("/context", CancellationToken.None);
        Assert.Contains("Read: git diff HEAD, 1 file", _output.ToString());
    }

    [Fact]
    public void Tab_completes_every_command()
    {
        var (start, items) = Completions.For("/re", 3, _dir);

        Assert.Equal(0, start);
        Assert.Equal(["/read", "/review", "/retry"], items);
        Assert.Contains("/commit", Completions.For("/", 1, _dir).Items);
    }

    [Fact]
    public void Tab_completes_paths_after_path_commands_skipping_build_output()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "src", "Session.cs"), "");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "");

        var (start, items) = Completions.For("/read s", 7, _dir);
        Assert.Equal(6, start);
        Assert.Equal(["src/"], items);
        Assert.Equal(["src/Session.cs"], Completions.For("/edit src/S", 11, _dir).Items);
        Assert.Equal(["src/", "README.md"], Completions.For("/read ", 6, _dir).Items);
        Assert.Empty(Completions.For("/edit src/Session.cs rename", 27, _dir).Items); // the request isn't a path
        Assert.Empty(Completions.For("hello", 5, _dir).Items);
    }

    [Theory]
    [InlineData(new[] { "--help" }, "help")]
    [InlineData(new[] { "-h" }, "help")]
    [InlineData(new[] { "--version" }, "version")]
    [InlineData(new[] { "commit" }, "commit")]
    public void New_commands_parse(string[] args, string command) => Assert.Equal(command, CliArgs.Parse(args).Command);

    [Fact]
    public void Diff_takes_a_question_and_options()
    {
        var cli = CliArgs.Parse(["diff", "review these", "--repo", @"C:\Repo", "--no-stream"]);

        Assert.Equal(("diff", "review these", @"C:\Repo", false), (cli.Command, cli.Text, cli.RepoPath, cli.Stream));
    }
}
