using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class StaticChecksTests
{
    // Built at run time: a token written out whole would trip GitHub's push protection on this very file.
    static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";
    static readonly string GitHubToken = "ghp_" + new string('a', 36);

    static List<Finding> Scan(string path, string text, int firstLine = 1) => StaticChecks.Scan([new ProjectFile(path, text, firstLine)]);

    [Fact]
    public void A_secret_is_found_in_any_file_and_masked_in_the_quote()
    {
        var found = Assert.Single(Scan("appsettings.json", $"{{\n  \"Aws\": \"{AwsKey}\"\n}}"));

        Assert.Equal(("aws-key", "high", 2, true), (found.Rule, found.Severity, found.Line, found.Verified));
        Assert.Equal("\"Aws\": \"AKIA********\"", found.Quote);
        Assert.DoesNotContain(AwsKey, found.Quote);
        Assert.Equal("github-token", Assert.Single(Scan("deploy.sh", $"TOKEN={GitHubToken}")).Rule);
        Assert.Equal("private-key", Assert.Single(Scan("key.pem", "-----BEGIN RSA PRIVATE KEY-----")).Rule);
    }

    [Fact]
    public void A_password_in_a_connection_string_counts_but_a_placeholder_or_a_doc_doesnt()
    {
        var found = Assert.Single(Scan("appsettings.json", "\"Db\": \"Server=db;User Id=sa;Password=hunter22;\""));
        Assert.Equal("connection-password", found.Rule);
        Assert.Contains("Password=hunt********;", found.Quote);

        foreach (var placeholder in new[] { "Password={0};", "Password=<password>;", "Password=$(DB_PASSWORD);", "Password=%DBPW%;", "Password=***;", "Password=;", "Password=@pw;" })
            Assert.Empty(Scan("appsettings.json", $"\"Db\": \"Server=db;{placeholder}\""));
        Assert.Empty(Scan("Login.cs", "var password = Request.Form[\"pw\"]; var sql = \"WHERE Password = @Password\";"));
        Assert.Empty(Scan("README.md", "Server=db;Password=hunter22;"));
    }

    [Fact]
    public void Sql_built_from_values_is_found_but_a_constant_query_isnt()
    {
        var found = Scan("Orders.cs", """
            class Orders
            {
                string A(string id) => $"SELECT * FROM Orders WHERE Id = {id}";
                string B(string name) => "DELETE FROM Users WHERE Name = '" + name + "'";
                const string C = "SELECT * FROM Orders WHERE Id = @id";
                string D(string table) => "UPDATE " + "Orders SET x = 1";
                string E(string who) => $"Hello {who}";
            }
            """, firstLine: 10);

        Assert.Equal([("sql-text", 12), ("sql-text", 13)], found.Select(f => (f.Rule, f.Line)));
        Assert.Equal("string A(string id) => $\"SELECT * FROM Orders WHERE Id = {id}\";", found[0].Quote);
    }

    [Fact]
    public void Async_void_empty_catch_any_certificate_and_weak_hashes_are_found()
    {
        var found = Scan("Worker.cs", """
            class Worker
            {
                async void Run() { await Task.Delay(1); }
                async void OnClick(object sender, EventArgs e) { await Task.Delay(1); }
                async Task Fine() { try { } catch { } }
                void Logged() { try { } catch (Exception) { /* the file may be gone: fine */ } }
                void Named() { try { } catch (IOException) { } try { } catch (Exception e) when (e is IOException) { } try { } catch { } // shutting down
                }
                void Tls(HttpClientHandler h) { h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; }
                byte[] Old(byte[] b) => MD5.Create().ComputeHash(b);
                byte[] New(byte[] b) => SHA256.Create().ComputeHash(b);
            }
            """);

        Assert.Equal(["async-void", "empty-catch", "tls-any-cert", "weak-hash"], found.Select(f => f.Rule));
        Assert.Equal([3, 5, 9, 10], found.Select(f => f.Line));
    }

    [Fact]
    public void Every_secret_on_a_line_is_masked_in_every_quote()
    {
        var found = Scan("Config.cs", $"class C {{ string Q(string id) => $\"SELECT * FROM T WHERE k = '{AwsKey}' AND t = '{GitHubToken}' AND id = {{id}}\"; }}");

        Assert.Equal(["aws-key", "github-token", "sql-text"], found.Select(f => f.Rule));
        Assert.All(found, f => Assert.Equal("class C { string Q(string id) => $\"SELECT * FROM T WHERE k = 'AKIA********' AND t = 'ghp_********' AND id = {id}\"; }", f.Quote));
        Assert.Equal("\"key\": \"-----BEGIN PRIVATE KEY", StaticChecks.Masked("\"key\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvQIBADANBgkqhkiG9w0B\\n-----END PRIVATE KEY-----\""));
    }

    [Fact]
    public void A_generated_chain_of_thousands_of_joined_strings_is_read_without_recursing()
    {
        var chain = string.Join(" + ", Enumerable.Range(0, 20_000).Select(i => i == 0 ? "\"SELECT * FROM T WHERE a = \"" : "x"));

        Assert.Equal("sql-text", Assert.Single(Scan("Big.cs", $"class B {{ string Q(string x) => {chain}; }}")).Rule);
    }

    [Fact]
    public async Task A_model_quote_with_a_key_is_masked_and_a_removed_key_counts_as_resolved()
    {
        var dir = Directory.CreateTempSubdirectory("jazari rules").FullName;
        try
        {
            var path = Path.Combine(dir, "Program.cs");
            File.WriteAllText(path, $"class P {{ const string Key = \"{AwsKey}\"; }}\n");
            var gateway = new FakeGateway { Complete = (_, _) => Task.FromResult($$"""{"findings":[{"file":"Program.cs","quote":"class P { const string Key = \"{{AwsKey}}\"; }","problem":"A key.","severity":"high"}]}""") };
            var ui = new Ui(new StringWriter(), new Theme(ColorMode.None, unicode: true), 100);
            var review = new ProjectReview(gateway, ui, Path.Combine(dir, "store"));

            var first = File.ReadAllText((await review.RunAsync(dir, CancellationToken.None))!);
            Assert.DoesNotContain(AwsKey, first);
            Assert.DoesNotContain("Resolved since", first);

            File.WriteAllText(path, "class P { }\n");
            gateway.Complete = (_, _) => Task.FromResult("""{"findings":[]}""");
            var second = File.ReadAllText((await review.RunAsync(dir, CancellationToken.None))!);
            Assert.Contains("## Resolved since the last review (2)", second);
            Assert.DoesNotContain(AwsKey, second);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Rule_findings_come_first_in_the_report_without_the_model_finding_them()
    {
        var dir = Directory.CreateTempSubdirectory("jazari rules").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Program.cs"), $"class P {{ const string Key = \"{AwsKey}\"; }}\n");
            var gateway = new FakeGateway { Complete = (_, _) => Task.FromResult("""{"findings":[]}""") };
            var output = new StringWriter();
            var ui = new Ui(output, new Theme(ColorMode.None, unicode: true), 100);

            var report = File.ReadAllText((await new ProjectReview(gateway, ui, Path.Combine(dir, "store")).RunAsync(dir, CancellationToken.None))!);

            Assert.Contains("## Found by rules, without the model (1)", report);
            Assert.Contains("### [high] Program.cs:1 `aws-key` (new)", report);
            Assert.DoesNotContain(AwsKey, report);
            Assert.Contains("1 found by rules, 0 verified", output.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
