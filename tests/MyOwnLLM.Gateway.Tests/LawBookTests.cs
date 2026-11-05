using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

public class LawBookTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-law").FullName;

    public static readonly (string Title, string Text, string Url)[] SmallBook =
    [
        ("Minimal APIs quick reference: IResult return values",
            "To send a not found result, return Results.NotFound() from a minimal API route handler when the item isn't found, and Results.Ok(item) when it is.\n\n" +
            "```csharp\napp.MapGet(\"/todos/{id}\", async (int id, TodoDb db) =>\n    await db.Todos.FindAsync(id) is Todo todo ? Results.Ok(todo) : Results.NotFound());\n```",
            "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis#iresult-return-values"),
        ("Minimal APIs overview: Route handlers",
            "In .NET 10, a minimal API maps a route with MapGet, MapPost, MapPut or MapDelete on the app, and a lambda handles the request.",
            "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/route-handlers"),
        ("Records",
            "A record is a class or struct with value equality: two records are equal when their types and values are equal. Records are immutable by default with positional properties.",
            "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/records"),
        ("Classes: Fields",
            "A class can keep a record of its callers in a private field, but that is a design choice, not a language feature.",
            "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/classes#fields"),
        ("Tutorial: Create a Hello World console app",
            "Run dotnet new console, then dotnet run, and the app prints Hello, World! to the console.",
            "https://learn.microsoft.com/dotnet/core/tutorials/hello-world"),
        ("LINQ: IQueryable vs IEnumerable",
            "IQueryable<T> builds an expression tree the provider translates, such as to SQL in EF Core; IEnumerable<T> runs the query in memory.",
            "https://learn.microsoft.com/dotnet/csharp/linq/iqueryable"),
        ("Dependency injection: Service lifetimes",
            "Services have one of three lifetimes. AddScoped registers a service created once per request. AddSingleton creates one instance for the app, and AddTransient a new one each time.",
            "https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-lifetimes"),
    ];

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }

    GatewayOptions Options() => new() { DataDir = _dir };

    internal static void Write(string file, IEnumerable<(string Title, string Text, string Url)> rows)
    {
        using var db = new SqliteConnection($"Data Source={file};Pooling=False");
        db.Open();
        using (var create = db.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE chunks USING fts5(title, text, url UNINDEXED, tokenize=\"porter unicode61\")";
            create.ExecuteNonQuery();
        }
        var list = rows.ToList();
        foreach (var (title, text, url) in list)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO chunks(title, text, url) VALUES ($t, $x, $u)";
            insert.Parameters.AddWithValue("$t", title);
            insert.Parameters.AddWithValue("$x", text);
            insert.Parameters.AddWithValue("$u", url);
            insert.ExecuteNonQuery();
        }
        using (var create = db.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE pages USING fts5(title, description, slug, url UNINDEXED, tokenize=\"porter unicode61\")";
            create.ExecuteNonQuery();
        }
        // Each page once: its title without the section's heading, and its first passage standing in for the docs' one-line description.
        foreach (var page in list.GroupBy(r => r.Url.Split('#')[0]))
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO pages(title, description, slug, url) VALUES ($t, $d, $s, $u)";
            insert.Parameters.AddWithValue("$t", page.First().Title.Split(':')[0]);
            insert.Parameters.AddWithValue("$d", page.First().Text);
            insert.Parameters.AddWithValue("$s", page.Key.Replace("https://learn.microsoft.com/", "").Replace('/', ' ').Replace('-', ' '));
            insert.Parameters.AddWithValue("$u", page.Key);
            insert.ExecuteNonQuery();
        }
    }

    LawBook Book(IEnumerable<(string, string, string)>? rows = null)
    {
        var options = Options();
        Write(options.LawBookFile, rows ?? SmallBook);
        return new LawBook(options);
    }

    [Fact] public void The_book_lives_in_the_data_folder_by_default() =>
        Assert.Equal(Path.Combine(_dir, "law-book.db"), Options().LawBookFile);

    [Fact] public void Finds_the_passage_that_covers_the_question() =>
        Assert.Contains("Results.NotFound", Book().Search("How do I send a not found result from a minimal API?")[0].Text);

    [Fact] public void A_passage_carries_its_title_and_address()
    {
        var p = Book().Search("Which service lifetimes does dependency injection have?")[0];
        Assert.Equal("Dependency injection: Service lifetimes", p.Title);
        Assert.Equal("https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-lifetimes", p.Url);
    }

    [Fact] public void A_question_about_something_else_gets_nothing() =>
        Assert.Empty(Book().Search("How do I configure Kafka consumers in Spring Boot?"));

    [Theory, InlineData("hello"), InlineData("what's for dinner"), InlineData("go on"), InlineData("thanks!")]
    public void Small_talk_gets_nothing(string q) => Assert.Empty(Book().Search(q));

    [Fact] public void A_named_API_the_passage_lacks_rules_it_out() =>
        Assert.Empty(Book().Search("What does FrobnicateAsync do in a minimal API?"));

    [Fact] public void A_version_the_passage_lacks_rules_it_out()
    {
        var book = Book();
        Assert.Empty(book.Search("How do I map a route in a minimal API in .NET 6?"));
        Assert.NotEmpty(book.Search("How do I map a route in a minimal API in .NET 10?"));
    }

    [Fact] public void One_word_questions_need_it_in_the_title()
    {
        var found = Book().Search("record?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Equal("Records", p.Title));
    }

    [Fact] public void Product_names_are_not_search_words()
    {
        // Every page of the ASP.NET Core or EF Core docs names its product, so those words would only favour long pages.
        var found = Book().Search("ASP.NET Core records?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Equal("Records", p.Title));
    }

    [Fact] public void One_to_many_is_not_one_to_one()
    {
        var book = Book([
            ("One-to-one relationships: Required one-to-one", "Configure a one-to-one relationship with HasOne and WithOne.", "https://learn.microsoft.com/ef/core/modeling/relationships/one-to-one"),
            ("One-to-many relationships: Required one-to-many", "Configure a one-to-many relationship with HasMany and WithOne.", "https://learn.microsoft.com/ef/core/modeling/relationships/one-to-many"),
        ]);
        Assert.Contains("HasMany", book.Search("How do I configure a one-to-many relationship?")[0].Text);
    }

    [Fact] public void A_page_must_be_about_half_the_question()
    {
        // Every word is somewhere in the book, but no page is about most of them: a near-theme trap, not an answer.
        var book = Book();
        Assert.Empty(book.Search("Struct equality, console hello, lambda request?"));
        Assert.NotEmpty(book.Search("Record equality values?"));
    }

    [Fact] public void A_passage_that_shares_words_on_a_page_about_something_else_is_not_sent()
    {
        // A short command page held "store" and "password" and beat the real answer on word matching alone.
        var found = Book([
            ("dotnet nuget update source command: Synopsis", "dotnet nuget update source [--password <PASSWORD>] [--store-password-in-clear-text]", "https://learn.microsoft.com/dotnet/core/tools/dotnet-nuget-update-source#synopsis"),
            ("Hash passwords in ASP.NET Core: Overview", "Store user passwords as a salted hash with PasswordHasher.", "https://learn.microsoft.com/aspnet/core/security/data-protection/consumer-apis/password-hashing"),
        ]).Search("How should I store user passwords?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Contains("password-hashing", p.Url));
    }

    [Fact] public void Passages_come_from_one_page()
    {
        // Two pages joined together read as one text to the model, which has no way to tell them apart.
        var found = Book([
            ("Output caching: Add the middleware", "Call AddOutputCache to add the output caching middleware.", "https://learn.microsoft.com/aspnet/core/performance/caching/output#add"),
            ("Output caching: Policies", "Output caching policies decide how long output stays cached.", "https://learn.microsoft.com/aspnet/core/performance/caching/output#policies"),
            ("Output caching in gRPC", "Output caching does not apply to gRPC output.", "https://learn.microsoft.com/aspnet/core/grpc/caching"),
        ]).Search("What does output caching do?");
        Assert.NotEmpty(found);
        Assert.Single(found.Select(p => p.Url.Split('#')[0]).Distinct());
    }

    [Fact] public void The_answering_passage_holds_the_questions_rarest_word()
    {
        // The page is right, but only one of its passages is about what's asked.
        var found = Book([
            ("Caching: Overview", "Caching keeps results in memory and makes results quicker.", "https://learn.microsoft.com/aspnet/core/caching#overview"),
            ("Caching: Eviction", "Caching eviction removes results from memory.", "https://learn.microsoft.com/aspnet/core/caching#eviction"),
        ]).Search("What is caching eviction?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Contains("#eviction", p.Url));
    }

    [Fact] public void Inside_a_page_the_section_named_for_the_question_comes_first()
    {
        // The word matches favour a passage that repeats the words, over the section that's about them.
        var found = Book([
            ("Caching: Limits", "Caching register caching register caching register, as described under the other sections of this caching page.", "https://learn.microsoft.com/aspnet/core/caching#limits"),
            ("Caching: Register the service", "Call AddHybridCache in Program.cs for caching.", "https://learn.microsoft.com/aspnet/core/caching#register"),
        ]).Search("How do I register caching?");
        Assert.Contains("#register", found[0].Url);
    }

    [Fact] public void A_book_without_the_pages_table_means_no_passages()
    {
        var options = Options();
        Write(options.LawBookFile, SmallBook);
        using (var db = new SqliteConnection($"Data Source={options.LawBookFile};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE pages";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(new LawBook(options).Search("How do I send a not found result from a minimal API?"));
    }

    [Fact] public void At_most_300_tokens_come_back()
    {
        var filler = string.Concat(Enumerable.Repeat("Minimal API route handlers map requests. ", 20));
        var rows = Enumerable.Range(0, 10).Select(i => ($"Minimal APIs {i}", filler, $"https://learn.microsoft.com/x/{i}"));
        var found = Book(rows).Search("How do minimal API route handlers map requests?");
        Assert.NotEmpty(found);
        Assert.True(found.Sum(p => PromptBuilder.EstimateTokens(p.Text)) <= 300);
        Assert.True(found.Count <= 2);
    }

    [Fact] public void No_file_means_no_passages() => Assert.Empty(new LawBook(Options()).Search("minimal API"));

    [Fact] public void A_damaged_file_means_no_passages()
    {
        var options = Options();
        File.WriteAllBytes(options.LawBookFile, Enumerable.Range(0, 8192).Select(i => (byte)(i * 7919 % 251)).ToArray());
        Assert.Empty(new LawBook(options).Search("minimal API route handlers"));
    }

    [Fact] public void A_file_without_the_table_means_no_passages()
    {
        var options = Options();
        using (var db = new SqliteConnection($"Data Source={options.LawBookFile};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE other(x)";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(new LawBook(options).Search("minimal API route handlers"));
    }

    [Fact] public void The_file_is_not_held_open_between_searches()
    {
        // jazari update replaces law-book.db while the gateway runs; on Windows an open handle would block that.
        var options = Options();
        Write(options.LawBookFile, SmallBook);
        new LawBook(options).Search("How do I send a not found result from a minimal API?");
        File.Move(options.LawBookFile, options.LawBookFile + ".old");
        File.Delete(options.LawBookFile + ".old");
    }

    [Theory, InlineData("C# record?"), InlineData("\"quoted\" minimal API"), InlineData("foo* AND bar"), InlineData("IQueryable<T> vs IEnumerable<T>"),
     InlineData("NEAR(minimal api) OR title:x ^ -y"), InlineData("'); DROP TABLE chunks; --")]
    public void Search_syntax_in_a_question_is_harmless(string q) => Book().Search(q);

    [Fact] public void Symbols_do_not_stop_a_good_match() =>
        Assert.NotEmpty(Book().Search("IQueryable<T> vs IEnumerable<T>?"));

    [Fact] public void A_huge_question_stays_quick()
    {
        var book = Book();
        var trace = string.Concat(Enumerable.Range(0, 4000).Select(i => $"   at MyApp.Orders.OrderService{i}.SaveAsync() in C:\\src\\Orders.cs:line {i}\n"));
        Assert.True(trace.Length > 200_000);
        book.Search("warm up");
        var clock = Stopwatch.StartNew();
        book.Search("Why does this fail?\n" + trace);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }
}
