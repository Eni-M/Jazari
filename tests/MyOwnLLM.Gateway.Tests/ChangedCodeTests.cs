using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class ChangedCodeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari changed").FullName;
    readonly string _store = Directory.CreateTempSubdirectory("jazari changed store").FullName;
    readonly StringWriter _output = new();
    readonly FakeGateway _gateway = new();

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _store }) try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    void Add(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    const string Service = """
        using System;

        namespace Shop;

        public class OrderService
        {
            public int Count { get; set; }

            public void Refund(int orderId)
            {
                var order = Load(orderId);
                order.Refund();
            }

            public void Ship(int orderId)
            {
                Load(orderId).Ship();
            }
        }
        """;

    [Fact]
    public void Ranges_come_from_the_new_side_of_each_hunk()
    {
        var ranges = ChangedCode.Ranges("""
            diff --git a/A.cs b/A.cs
            --- a/A.cs
            +++ b/A.cs
            @@ -10,2 +10,3 @@ class A
            +x
            @@ -30 +31 @@
            -y
            +z
            @@ -40,2 +41,0 @@
            diff --git a/Gone.cs b/Gone.cs
            --- a/Gone.cs
            +++ /dev/null
            @@ -1,3 +0,0 @@
            """);

        Assert.Equal([(10, 12), (31, 31), (41, 42)], ranges["A.cs"]);
        Assert.False(ranges.ContainsKey("Gone.cs")); // deleted: nothing left to review
    }

    [Fact]
    public void A_change_inside_a_method_reviews_the_whole_method_with_the_files_line_numbers()
    {
        Add("OrderService.cs", Service);

        var (parts, _) = ChangedCode.Parts(_dir, new() { ["OrderService.cs"] = [(12, 12)] }, []);

        var part = Assert.Single(parts);
        Assert.Equal(9, part.FirstLine);
        Assert.StartsWith("    public void Refund(int orderId)", part.Text);
        Assert.EndsWith("    }", part.Text.TrimEnd());
        Assert.DoesNotContain("Ship", part.Text);
    }

    [Fact]
    public void Changes_in_two_members_give_both_and_a_using_gets_lines_around_it()
    {
        Add("OrderService.cs", Service);

        var (parts, _) = ChangedCode.Parts(_dir, new() { ["OrderService.cs"] = [(1, 1), (11, 11), (17, 17)] }, []);

        Assert.Equal([1, 9, 15], parts.Select(p => p.FirstLine));
        Assert.Contains("using System;", parts[0].Text);
        Assert.Contains("Refund", parts[1].Text);
        Assert.Contains("Ship", parts[2].Text);
    }

    [Fact]
    public void Other_files_get_the_changed_lines_with_some_around_them_and_new_files_whole()
    {
        Add("app.js", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")));
        Add("New.cs", "class New { }");

        var (parts, _) = ChangedCode.Parts(_dir, new() { ["app.js"] = [(20, 21)] }, ["New.cs", "notes.bin"]);

        Assert.Equal(("app.js", 14), (parts[0].Path, parts[0].FirstLine));
        Assert.StartsWith("line14\n", parts[0].Text);
        Assert.EndsWith("line27", parts[0].Text);
        Assert.Equal(("New.cs", "class New { }"), (parts[1].Path, parts[1].Text));
        Assert.Equal(2, parts.Count); // notes.bin isn't code
    }

    [Fact]
    public void A_path_outside_the_project_or_through_a_link_is_never_read()
    {
        var outside = Directory.CreateTempSubdirectory("jazari outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "id_rsa.cs"), "PRIVATE KEY");
            var escape = Path.GetRelativePath(_dir, Path.Combine(outside, "id_rsa.cs")).Replace('\\', '/'); // ../jazari outside…/id_rsa.cs

            var (parts, _) = ChangedCode.Parts(_dir, new() { [escape] = [(1, 1)] }, [escape]);
            Assert.Empty(parts);

            try { File.CreateSymbolicLink(Path.Combine(_dir, "notes.cs"), Path.Combine(outside, "id_rsa.cs")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // Windows without the right to make links
            Assert.Empty(ChangedCode.Parts(_dir, new() { ["notes.cs"] = [(1, 1)] }, ["notes.cs"]).Parts);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void A_changed_file_the_jazariignore_hides_is_named()
    {
        Add(".jazariignore", "Secret*.cs\n");
        Add("SecretPlan.cs", "class S { }");

        var (parts, ignored) = ChangedCode.Parts(_dir, new() { ["SecretPlan.cs"] = [(1, 1)] }, []);

        Assert.Empty(parts);
        Assert.Equal(["SecretPlan.cs"], ignored);
    }

    [Fact]
    public async Task Findings_in_a_changed_member_carry_the_files_line_numbers()
    {
        Add("OrderService.cs", Service);
        _gateway.Complete = (_, _) => Task.FromResult("""{"findings":[{"file":"OrderService.cs","quote":"var order = Load(orderId);","problem":"no owner check","severity":"high"}]}""");
        var changed = ChangedCode.Parts(_dir, new() { ["OrderService.cs"] = [(12, 12)] }, []);

        var report = File.ReadAllText((await new ProjectReview(_gateway, new Ui(_output, new Theme(ColorMode.None, true), 100), _store).RunAsync(_dir, default, changed))!);

        Assert.StartsWith("# Review of the changes in", report);
        Assert.Contains("[high] OrderService.cs:11", report);
    }

    [Fact]
    public void A_large_changed_member_cut_in_pieces_keeps_the_files_line_numbers()
    {
        var body = string.Concat(Enumerable.Range(1, 300).Select(i => $"        var v{i} = Compute({i}, \"padding to make the method long enough\");\n"));
        var units = ProjectReview.Units([new ProjectFile("Big.cs", "    void Big()\n    {\n" + body + "    }", FirstLine: 100)], unitTokens: 500);

        var pieces = units.SelectMany(u => u).ToList();
        Assert.True(pieces.Count > 1);
        Assert.Equal(100, pieces[0].FirstLine);
        Assert.All(pieces.Skip(1), p => Assert.Equal(100 + int.Parse(p.Text.Split("var v")[1].Split(' ')[0]) + 1, p.FirstLine));
    }

    [Fact]
    public async Task Review_changed_reads_the_diff_relative_to_the_project_and_the_new_files()
    {
        Add("OrderService.cs", Service);
        Add("New.cs", "class New { }");
        var gitCalls = new List<string[]>();
        var theme = new Theme(ColorMode.None, true);
        var session = new Session(_gateway, new Ui(_output, theme, 100), () => new MarkdownStream(_output, theme, 100), _dir)
        {
            ReviewDir = _store,
            Git = (args, _) =>
            {
                gitCalls.Add(args);
                return Task.FromResult(args.Contains("--get-regexp") ? (1, "")
                    : args.Contains("ls-files") ? (0, "New.cs\n")
                    : (0, "diff --git a/OrderService.cs b/OrderService.cs\n--- a/OrderService.cs\n+++ b/OrderService.cs\n@@ -12 +12 @@\n-x\n+y\n"));
            },
        };

        await session.HandleAsync("/review --changed", CancellationToken.None);

        var diff = gitCalls.Single(a => a.Contains("diff"));
        Assert.Contains("--relative", diff);
        Assert.Contains("--no-ext-diff", diff);
        Assert.Contains("what changed since the last commit: 2 files, 2 parts", _output.ToString());
    }

    [Fact]
    public async Task Findings_are_tracked_across_reviews_new_still_there_resolved_and_carried()
    {
        Add("Auth.cs", "var password = \"hunter2\";\nvar sql = \"SELECT \" + id;\nvar other = Run(id);\n");
        string Finding(string quote, string problem) => $$"""{"file":"Auth.cs","quote":"{{quote}}","problem":"{{problem}}","severity":"high"}""";
        Task<string> Review(params string[] findings)
        {
            _gateway.Complete = (_, _) => Task.FromResult($$"""{"findings":[{{string.Join(",", findings)}}]}""");
            File.AppendAllText(Path.Combine(_dir, "Auth.cs"), "\n"); // a new run reads the part again, not its cached answer
            return new ProjectReview(_gateway, new Ui(_output, new Theme(ColorMode.None, true), 100), _store).RunAsync(_dir, default)!;
        }

        var first = File.ReadAllText((await Review(Finding("var password = \\\"hunter2\\\";", "hardcoded"), Finding("var sql = \\\"SELECT \\\" + id;", "injection")))!);
        Assert.Equal(2, first.Split("(new)").Length - 1);

        File.WriteAllText(Path.Combine(_dir, "Auth.cs"), "var password = Vault.Get();\nvar sql = \"SELECT \" + id;\nvar other = Run(id);\n"); // the password is fixed
        var second = File.ReadAllText((await Review(Finding("var other = Run(id);", "unchecked id")))!);

        Assert.Contains("Since the last review: 1 new, 0 still there, 1 resolved.", second);
        Assert.Contains("## Resolved since the last review (1)", second);
        Assert.Contains("hardcoded", second[second.IndexOf("## Resolved")..]);
        Assert.Contains("## From earlier reviews, not reported this time (1)", second); // the injection line is still there
        Assert.Contains("injection", second[second.IndexOf("## From earlier")..]);

        var third = File.ReadAllText((await Review(Finding("var sql = \\\"SELECT \\\" + id;", "injection"), Finding("var other = Run(id);", "unchecked id")))!);
        Assert.Contains("Since the last review: 0 new, 2 still there, 0 resolved.", third);
        Assert.DoesNotContain("(new)", third);
    }

    [Fact]
    public void The_command_line_form_parses() => Assert.Equal(("review", "--changed"), (CliArgs.Parse(["review", "--changed"]) is var c ? (c.Command, c.Text) : default));
}
