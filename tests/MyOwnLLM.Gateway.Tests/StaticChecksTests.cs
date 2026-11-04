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
