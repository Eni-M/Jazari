using MyOwnLLM.Gateway.Checks;

namespace MyOwnLLM.Gateway.Tests;

public class SnippetCheckerTests
{
    static readonly SnippetChecker Checker = new();

    static string Fence(string code, string language = "csharp") => $"Here:\n```{language}\n{code}\n```\nDone.";
    static BlockCheck One(string code, string language = "csharp") => Assert.Single(Checker.Check(Fence(code, language), CancellationToken.None));

    [Fact]
    public void A_made_up_method_on_a_known_type_is_flagged()
    {
        var check = One("services.AddFoo();");
        Assert.Equal(CheckState.DoesNotCompile, check.State);
        Assert.Contains(check.Errors, e => e.Contains("AddFoo"));
    }

    [Fact]
    public void A_real_registration_compiles() =>
        Assert.Equal(CheckState.Compiles, One("services.AddScoped<IFoo, Foo>();\ninterface IFoo { }\nclass Foo : IFoo { }").State);

    [Fact]
    public void Undeclared_variables_are_not_errors() => Assert.Equal(CheckState.Compiles, One("var total = order.Total + 1;").State);

    [Fact]
    public void Builder_and_app_have_their_real_types()
    {
        Assert.Equal(CheckState.DoesNotCompile, One("builder.Services.AddSqlQuery();").State);
        Assert.Equal(CheckState.Compiles, One("builder.Services.AddControllers();\napp.MapGet(\"/\", () => \"hi\");").State);
    }

    [Theory]
    [InlineData("public class A { public int F() => 1; }")]
    [InlineData("public int F(int x) { return x + 1; }")]
    [InlineData("public int Count { get; set; }")]
    [InlineData("var x = 1;\nConsole.WriteLine(x);")]
    [InlineData("namespace N;\npublic record R(int A);")]
    [InlineData("using System.Text;\nvar b = new StringBuilder();")]
    public void A_whole_class_a_lone_method_and_statements_all_parse(string code) => Assert.Equal(CheckState.Compiles, One(code).State);

    [Fact]
    public void A_namespace_outside_the_packs_is_not_checked()
    {
        var check = One("using Microsoft.EntityFrameworkCore;\nclass Db : DbContext { }");
        Assert.Equal(CheckState.NotChecked, check.State);
        Assert.Contains("Microsoft.EntityFrameworkCore", check.Why);
    }

    [Theory]
    [InlineData("builder.Services.AddDbContext<AppDb>(o => o.UseSqlServer(cs));")]
    [InlineData("class Db : DbContext { }")]
    [InlineData("var rows = await db.Orders.AsNoTracking().ToListAsync();")]
    public void Correct_package_code_without_a_using_is_never_called_wrong(string code) => Assert.NotEqual(CheckState.DoesNotCompile, One(code).State);

    [Theory]
    [InlineData("services.AddScoped<ISomeService, SomeService>();")]
    [InlineData("var p = new Person();")]
    public void Types_an_answer_names_as_examples_are_not_flagged(string code) => Assert.Equal(CheckState.Compiles, One(code).State);

    [Theory]
    [InlineData("var b = new StringBuilder();\nb.Append(1);")]
    [InlineData("var d = new ConcurrentDictionary<string, int>();\nd.TryAdd(\"a\", 1);")]
    [InlineData("void F(IOptionsSnapshot<MySettings> options) { var v = options.Value; }")]
    public void Real_types_without_their_usings_compile(string code) => Assert.Equal(CheckState.Compiles, One(code).State);

    [Fact]
    public void A_made_up_member_on_a_real_type_without_its_using_is_flagged() => Assert.Equal(CheckState.DoesNotCompile, One("var b = new StringBuilder();\nb.Frobnicate();").State);

    [Fact]
    public void A_made_up_name_inside_a_real_namespace_is_flagged() => Assert.Equal(CheckState.DoesNotCompile, One("System.Text.Frobnicator.Run();").State);

    [Fact]
    public void A_context_variable_is_usually_a_db_context_so_is_not_judged() => Assert.Equal(CheckState.Compiles, One("var u = context.Users.ToList();").State);

    [Theory]
    [InlineData("bash")]
    [InlineData("")]
    [InlineData("json")]
    public void Other_languages_and_unlabelled_blocks_are_not_checked(string language) => Assert.Equal(CheckState.NotChecked, One("services.AddFoo();", language).State);

    [Fact]
    public void Block_numbers_match_copy_numbers_when_a_bash_block_comes_first()
    {
        var checks = Checker.Check("```bash\nls\n```\ntext\n```csharp\nservices.AddFoo();\n```", CancellationToken.None);
        Assert.Equal(CheckState.NotChecked, checks[0].State);
        Assert.Equal(2, checks[1].Block);
        Assert.Equal(CheckState.DoesNotCompile, checks[1].State);
    }

    [Fact]
    public void An_answer_stopped_mid_block_still_checks_it() =>
        Assert.Equal(CheckState.DoesNotCompile, Assert.Single(Checker.Check("```csharp\nservices.AddFoo();", CancellationToken.None)).State);

    [Fact]
    public void A_stopped_or_oversized_snippet_is_not_checked()
    {
        Assert.Equal(CheckState.NotChecked, One(new string('x', 20_001)).State);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.Equal(CheckState.NotChecked, Assert.Single(Checker.Check(Fence("var x = 1;"), stopped.Token)).State);
    }

    [Fact]
    public void Missing_reference_packs_mean_not_checked_not_a_crash()
    {
        var check = Assert.Single(new SnippetChecker(Path.GetTempPath()).Check(Fence("var x = 1;"), CancellationToken.None));
        Assert.Equal(CheckState.NotChecked, check.State);
    }

    [Fact]
    public void No_code_means_no_checks() => Assert.Empty(Checker.Check("Just words.", CancellationToken.None));
}
