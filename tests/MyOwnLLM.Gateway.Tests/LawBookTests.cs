using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MyOwnLLM.Gateway.Prompt;

namespace MyOwnLLM.Gateway.Tests;

public class LawBookTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-law").FullName;

    public static readonly (string Title, string Text, string Url)[] SmallBook =
    [
        ("Minimal APIs quick reference > IResult return values",
            "To send a not found result, return Results.NotFound() from a minimal API route handler when the item isn't found, and Results.Ok(item) when it is.\n\n" +
            "```csharp\napp.MapGet(\"/todos/{id}\", async (int id, TodoDb db) =>\n    await db.Todos.FindAsync(id) is Todo todo ? Results.Ok(todo) : Results.NotFound());\n```",
            "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis#iresult-return-values"),
        ("Minimal APIs overview > Route handlers",
            "In .NET 10, a minimal API maps a route with MapGet, MapPost, MapPut or MapDelete on the app, and a lambda handles the request.",
            "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/route-handlers"),
        ("Records",
            "A record is a class or struct with value equality: two records are equal when their types and values are equal. Records are immutable by default with positional properties.",
            "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/records"),
        ("Classes > Fields",
            "A class can keep a record of its callers in a private field, but that is a design choice, not a language feature.",
            "https://learn.microsoft.com/dotnet/csharp/fundamentals/types/classes#fields"),
        ("Tutorial: Create a Hello World console app",
            "Run dotnet new console, then dotnet run, and the app prints Hello, World! to the console.",
            "https://learn.microsoft.com/dotnet/core/tutorials/hello-world"),
        ("LINQ > IQueryable vs IEnumerable",
            "IQueryable<T> builds an expression tree the provider translates, such as to SQL in EF Core; IEnumerable<T> runs the query in memory.",
            "https://learn.microsoft.com/dotnet/csharp/linq/iqueryable"),
        ("Dependency injection > Service lifetimes",
            "Services have one of three lifetimes. AddScoped registers a service created once per request. AddSingleton creates one instance for the app, and AddTransient a new one each time.",
            "https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-lifetimes"),
    ];

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }

    GatewayOptions Options() => new() { DataDir = _dir };

    /// <summary>A law book as lawbook/build.py makes it. The tables beside chunks and pages stay empty unless given:
    /// <paramref name="pageTerms"/> by page address, <paramref name="chunkNames"/> by the row's index in <paramref name="rows"/>.</summary>
    internal static void Write(string file, IEnumerable<(string Title, string Text, string Url)> rows,
        IReadOnlyDictionary<string, (string Headings, string Names)>? pageTerms = null, IReadOnlyDictionary<int, string>? chunkNames = null,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        using var db = new SqliteConnection($"Data Source={file};Pooling=False");
        db.Open();
        void Run(string sql, params (string Name, object Value)[] args)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
        Run("CREATE VIRTUAL TABLE chunks USING fts5(title, text, url UNINDEXED, tokenize=\"porter unicode61\")");
        Run("CREATE VIRTUAL TABLE pages USING fts5(title, description, slug, url UNINDEXED, tokenize=\"porter unicode61\")");
        Run("CREATE VIRTUAL TABLE page_terms USING fts5(headings, names, url UNINDEXED, tokenize=\"porter unicode61\")");
        Run("CREATE VIRTUAL TABLE chunk_names USING fts5(names, tokenize=\"porter unicode61\")");
        Run("CREATE TABLE aliases(alias TEXT PRIMARY KEY, expansion TEXT NOT NULL)");
        var list = rows.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            Run("INSERT INTO chunks(rowid, title, text, url) VALUES ($r, $t, $x, $u)", ("$r", i + 1), ("$t", list[i].Title), ("$x", list[i].Text), ("$u", list[i].Url));
            if (chunkNames?.TryGetValue(i, out var names) == true) Run("INSERT INTO chunk_names(rowid, names) VALUES ($r, $n)", ("$r", i + 1), ("$n", names));
        }
        // Each page once: its title without the section path, and its first passage standing in for the docs' one-line description.
        foreach (var page in list.GroupBy(r => r.Url.Split('#')[0]))
            Run("INSERT INTO pages(title, description, slug, url) VALUES ($t, $d, $s, $u)", ("$t", page.First().Title.Split(" > ")[0]), ("$d", page.First().Text),
                ("$s", page.Key.Replace("https://learn.microsoft.com/", "").Replace('/', ' ').Replace('-', ' ')), ("$u", page.Key));
        foreach (var (url, (headings, names)) in pageTerms ?? new Dictionary<string, (string, string)>())
            Run("INSERT INTO page_terms(headings, names, url) VALUES ($h, $n, $u)", ("$h", headings), ("$n", names), ("$u", url));
        foreach (var (alias, expansion) in aliases ?? new Dictionary<string, string>())
            Run("INSERT INTO aliases(alias, expansion) VALUES ($a, $e)", ("$a", alias), ("$e", expansion));
    }

    LawBook Book(IEnumerable<(string, string, string)>? rows = null, IReadOnlyDictionary<string, (string, string)>? pageTerms = null,
        IReadOnlyDictionary<int, string>? chunkNames = null, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var options = Options();
        if (File.Exists(options.LawBookFile)) File.Delete(options.LawBookFile);
        Write(options.LawBookFile, rows ?? SmallBook, pageTerms, chunkNames, aliases);
        return new LawBook(options);
    }

    [Fact] public void The_book_lives_in_the_data_folder_by_default() =>
        Assert.Equal(Path.Combine(_dir, "law-book.db"), Options().LawBookFile);

    [Fact] public void Finds_the_passage_that_covers_the_question() =>
        Assert.Contains("Results.NotFound", Book().Search("How do I send a not found result from a minimal API?")[0].Text);

    [Fact] public void A_passage_carries_its_title_and_address()
    {
        var p = Book().Search("Which service lifetimes does dependency injection have?")[0];
        Assert.Equal("Dependency injection > Service lifetimes", p.Title);
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
            ("One-to-one relationships > Required one-to-one", "Configure a one-to-one relationship with HasOne and WithOne.", "https://learn.microsoft.com/ef/core/modeling/relationships/one-to-one"),
            ("One-to-many relationships > Required one-to-many", "Configure a one-to-many relationship with HasMany and WithOne.", "https://learn.microsoft.com/ef/core/modeling/relationships/one-to-many"),
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
            ("dotnet nuget update source command > Synopsis", "dotnet nuget update source [--password <PASSWORD>] [--store-password-in-clear-text]", "https://learn.microsoft.com/dotnet/core/tools/dotnet-nuget-update-source#synopsis"),
            ("Hash passwords in ASP.NET Core > Overview", "Store user passwords as a salted hash with PasswordHasher.", "https://learn.microsoft.com/aspnet/core/security/data-protection/consumer-apis/password-hashing"),
        ]).Search("How should I store user passwords?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Contains("password-hashing", p.Url));
    }

    [Fact] public void Passages_come_from_one_page()
    {
        // Two pages joined together read as one text to the model, which has no way to tell them apart.
        var found = Book([
            ("Output caching > Add the middleware", "Call AddOutputCache to add the output caching middleware.", "https://learn.microsoft.com/aspnet/core/performance/caching/output#add"),
            ("Output caching > Policies", "Output caching policies decide how long output stays cached.", "https://learn.microsoft.com/aspnet/core/performance/caching/output#policies"),
            ("Output caching in gRPC", "Output caching does not apply to gRPC output.", "https://learn.microsoft.com/aspnet/core/grpc/caching"),
        ]).Search("What does output caching do?");
        Assert.NotEmpty(found);
        Assert.Single(found.Select(p => p.Url.Split('#')[0]).Distinct());
    }

    [Fact] public void The_answering_passage_holds_the_questions_rarest_word()
    {
        // The page is right, but only one of its passages is about what's asked.
        var found = Book([
            ("Caching > Overview", "Caching keeps results in memory and makes results quicker.", "https://learn.microsoft.com/aspnet/core/caching#overview"),
            ("Caching > Eviction", "Caching eviction removes results from memory.", "https://learn.microsoft.com/aspnet/core/caching#eviction"),
        ]).Search("What is caching eviction?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Contains("#eviction", p.Url));
    }

    [Fact] public void Inside_a_page_the_section_named_for_the_question_comes_first()
    {
        // The word matches favour a passage that repeats the words, over the section that's about them.
        var found = Book([
            ("Caching > Limits", "Caching register caching register caching register, as described under the other sections of this caching page.", "https://learn.microsoft.com/aspnet/core/caching#limits"),
            ("Caching > Register the service", "Call AddHybridCache in Program.cs for caching.", "https://learn.microsoft.com/aspnet/core/caching#register"),
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

    [Fact] public void Plain_words_find_a_passage_by_the_API_names_in_its_code()
    {
        // The tokenizer reads ExecuteUpdate as one word, so "update" is in no passage unless its API names are kept as words.
        (string, string, string)[] rows = [("Bulk changes", "Call context.Blogs.ExecuteUpdate(setters) to change many blogs at once.", "https://learn.microsoft.com/ef/core/saving/bulk")];
        const string question = "How do I update many blogs at once?";
        Assert.Empty(Book(rows).Search(question));
        Assert.Contains("ExecuteUpdate", Book(rows, chunkNames: new Dictionary<int, string> { [0] = "context blogs execute update setters" }).Search(question)[0].Text);
    }

    [Fact] public void A_page_whose_code_names_the_API_is_found()
    {
        // Its title and description never name AddKeyedSingleton; its code does. Headings only bring a page in to be tried:
        // counted as what a page is about, generic ones such as "Set up" sent wrong pages.
        (string, string, string)[] rows =
        [
            ("Dependency injection", "The container builds objects for you.", "https://learn.microsoft.com/dotnet/core/extensions/di"),
            ("Dependency injection > Keyed services", "Register one with AddKeyedSingleton and inject it with FromKeyedServices.", "https://learn.microsoft.com/dotnet/core/extensions/di#keyed-services"),
        ];
        const string question = "How do I use AddKeyedSingleton?";
        Assert.Empty(Book(rows).Search(question));
        var found = Book(rows, pageTerms: new Dictionary<string, (string, string)> { ["https://learn.microsoft.com/dotnet/core/extensions/di"] = ("Keyed services", "AddKeyedSingleton FromKeyedServices") }).Search(question);
        Assert.NotEmpty(found);
        Assert.Contains("#keyed-services", found[0].Url);
    }

    [Fact] public void An_abbreviation_the_docs_spell_out_finds_its_long_form()
    {
        (string, string, string)[] rows = [("Enforce HTTPS > HTTP Strict Transport Security", "To turn it on, call UseHsts, which sends the Strict-Transport-Security header.", "https://learn.microsoft.com/aspnet/core/security/enforcing-ssl#hsts")];
        Assert.Empty(Book(rows).Search("How do I turn on HSTS?"));
        Assert.NotEmpty(Book(rows, aliases: new Dictionary<string, string> { ["hsts"] = "http strict transport security" }).Search("How do I turn on HSTS?"));
    }

    [Fact] public void A_named_product_keeps_the_search_to_its_docs()
    {
        (string, string, string) general = ("Indexes > Add an index", "Add an index to a list to add an index quickly; an index is added with Add.", "https://learn.microsoft.com/dotnet/standard/collections/indexes#add");
        var found = Book([general, ("Indexes > Configure an index", "Use HasIndex to add an index on a column.", "https://learn.microsoft.com/ef/core/modeling/indexes#configure")])
            .Search("How do I add an index in EF Core?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Contains("/ef/", p.Url));
        Assert.NotEmpty(Book([general]).Search("How do I add an index in EF Core and ASP.NET Core?")); // two products: neither filters
    }

    [Fact] public void Breaking_change_pages_are_left_out_unless_asked()
    {
        (string, string, string)[] rows =
        [
            ("Breaking change: Options validation fails at startup", "Options validation now fails at startup for invalid options; this breaking change affects ValidateOnStart.", "https://learn.microsoft.com/dotnet/core/compatibility/extensions/8.0/options-validation"),
            ("Options pattern > Options validation", "Call ValidateOnStart so invalid options fail at startup, when the app starts.", "https://learn.microsoft.com/dotnet/core/extensions/options#options-validation"),
        ];
        var book = Book(rows);
        var found = book.Search("How do I make invalid options fail at startup?");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.DoesNotContain("/compatibility/", p.Url));
        Assert.Contains(book.Search("Breaking change: does options validation fail at startup?"), p => p.Url.Contains("/compatibility/"));
    }

    [Fact] public void An_error_code_reaches_the_error_pages()
    {
        // /fix asks with the compiler's code; a question without one is no reason to send an error page.
        var book = Book([("Compiler Error CS0103", "The name 'identifier' does not exist in the current context.", "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs0103")]);
        Assert.NotEmpty(book.Search("What is error CS0103?"));
        Assert.Empty(book.Search("What is a compiler error?"));
    }

    [Fact] public void A_made_up_name_split_into_real_words_is_still_not_covered() =>
        Assert.Empty(Book([("Saving data", "Call SaveChangesAsync to save everything you changed.", "https://learn.microsoft.com/ef/core/saving/basic")],
            chunkNames: new Dictionary<int, string> { [0] = "save changes async everything" }).Search("How do I call SaveEverythingAsync?"));

    /// <summary>Stands in for the two models: vectors and scores from the test's own rules.</summary>
    sealed class FakeModels(Func<string, float[]> embed, Func<string, float> rerank) : ISearchModels
    {
        public int Reranks;
        public float[][] Embed(IReadOnlyList<string> texts) => [.. texts.Select(t => { var v = embed(t); var n = MathF.Sqrt(v.Sum(x => x * x)); return v.Select(x => n > 0 ? x / n : 0).ToArray(); })];
        public float[] Rerank(string question, IReadOnlyList<string> passages) { Reranks++; return [.. passages.Select(rerank)]; }
    }

    static FakeModels Models(Func<string, float> rerank, Func<string, float[]>? embed = null) => new(embed ?? (t => [t.Length % 7 + 1, 1]), rerank);

    LawBook Ranked(IEnumerable<(string, string, string)> rows, ISearchModels models)
    {
        var options = Options();
        if (File.Exists(options.LawBookFile)) File.Delete(options.LawBookFile);
        Write(options.LawBookFile, rows);
        using (var db = new SqliteConnection($"Data Source={options.LawBookFile};Pooling=False"))
        {
            db.Open();
            BookVectors.Write(db, models);
        }
        return LawBook.WithModels(options, models);
    }

    [Fact] public void The_reranker_picks_the_passage()
    {
        var found = Ranked([
            ("Minimal APIs > Results", "Minimal API results: not found, ok, and more results for minimal API handlers that return a result.", "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/results"),
            ("Minimal APIs > Not found", "Return Results.NotFound() when the item is missing.", "https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/not-found"),
        ], Models(p => p.Contains("Results.NotFound") ? 5 : -1)).Search("How do I return a not found result from a minimal API?");
        Assert.NotEmpty(found);
        Assert.Contains("Results.NotFound", found[0].Text);
    }

    [Fact] public void Below_the_cutoff_nothing_is_sent() =>
        Assert.Empty(Ranked(SmallBook, Models(_ => LawBook.Cutoff - 3)).Search("How do I send a not found result from a minimal API?"));

    [Fact] public void Vectors_find_a_passage_words_miss()
    {
        // The answer shares no word with the question; only its meaning, here the fake's "queue" direction, brings it in.
        float[] Topic(string t) => t.Contains("producer", StringComparison.OrdinalIgnoreCase) || t.Contains("queue") ? [1, 0] : [0, 1];
        var found = Ranked([
            ("Channels > Bounded", "Use Channel.CreateBounded for a bounded queue of work.", "https://learn.microsoft.com/dotnet/core/extensions/channels#bounded"),
            ("Logging > Messages", "Pass messages to the log between calls.", "https://learn.microsoft.com/dotnet/core/extensions/logging#messages"),
            ("Roles", "Producer and consumer are roles in a system.", "https://learn.microsoft.com/dotnet/standard/roles"),
        ], Models(p => p.Contains("Channel.CreateBounded") ? 3 : -1, Topic)).Search("How do I pass messages between a producer and a consumer?");
        Assert.NotEmpty(found);
        Assert.Contains("Channel.CreateBounded", found[0].Text);
    }

    [Fact] public void The_second_passage_comes_from_the_same_page()
    {
        var found = Ranked([
            ("Caching > Register", "Register caching with AddHybridCache.", "https://learn.microsoft.com/aspnet/core/caching/hybrid#register"),
            ("Output caching > Register", "Register output caching with AddOutputCache.", "https://learn.microsoft.com/aspnet/core/caching/output#register"),
            ("Caching > Options", "Caching options for the hybrid cache you register.", "https://learn.microsoft.com/aspnet/core/caching/hybrid#options"),
        ], Models(p => p.Contains("AddHybridCache") ? 5 : p.Contains("AddOutputCache") ? 4 : 3)).Search("How do I register caching?");
        Assert.Equal(["https://learn.microsoft.com/aspnet/core/caching/hybrid#register", "https://learn.microsoft.com/aspnet/core/caching/hybrid#options"], found.Select(p => p.Url));
    }

    [Fact] public void A_Blazor_or_e_book_page_gives_way_to_the_reference_unless_asked_for()
    {
        (string, string, string)[] rows = [
            ("Blazor > Sign out", "Sign out of the app with SignOutAsync in a Blazor component.", "https://learn.microsoft.com/aspnet/core/blazor/security/#sign-out"),
            ("E-book > Sign out", "Sign out of the app with SignOutAsync in the e-book's sample.", "https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/sign-out"),
            ("Authentication > Sign out", "Sign out of the app with SignOutAsync.", "https://learn.microsoft.com/aspnet/core/security/authentication/sign-out"),
        ];
        var models = Models(p => p.Contains("Blazor") || p.Contains("e-book") ? 6 : 5);
        Assert.Equal("https://learn.microsoft.com/aspnet/core/security/authentication/sign-out", Ranked(rows, models).Search("How do I sign out of the app?")[0].Url);
        Assert.Equal("https://learn.microsoft.com/aspnet/core/blazor/security/#sign-out", Ranked(rows, models).Search("How do I sign out of a Blazor app?")[0].Url);
    }

    [Fact] public void A_trap_is_refused_before_the_models_run()
    {
        var models = Models(_ => 9);
        var book = Ranked(SmallBook, models);
        Assert.Empty(book.Search("How do I configure Kafka consumers in Spring Boot?"));
        Assert.Equal(0, models.Reranks);
    }

    [Fact] public void Without_models_the_word_search_runs()
    {
        var options = Options();
        Write(options.LawBookFile, SmallBook);
        Assert.Contains("Results.NotFound", LawBook.WithModels(options, null).Search("How do I send a not found result from a minimal API?")[0].Text);
        // Models but no vectors in the book, as a book built before them: the word search too.
        var models = Models(_ => -9);
        Assert.Contains("Results.NotFound", LawBook.WithModels(options, models).Search("How do I send a not found result from a minimal API?")[0].Text);
        Assert.Equal(0, models.Reranks);
    }

    [Fact] public void Packed_vectors_keep_their_direction()
    {
        var v = Enumerable.Range(0, 384).Select(i => MathF.Sin(i * 0.37f)).ToArray();
        var n = MathF.Sqrt(v.Sum(x => x * x));
        v = [.. v.Select(x => x / n)];
        var packed = BookVectors.Pack(v);
        var scale = BitConverter.ToSingle(packed, 0);
        var back = packed.Skip(4).Select(b => unchecked((sbyte)b) * scale).ToArray();
        var cosine = v.Zip(back, (a, b) => a * b).Sum() / MathF.Sqrt(back.Sum(x => x * x));
        Assert.True(cosine > 0.99f, $"cosine {cosine}");
    }

    [Fact] public void Nearest_finds_the_closest_allowed_passages()
    {
        var options = Options();
        Write(options.LawBookFile, [("A", "east", "https://learn.microsoft.com/ef/a"), ("B", "north", "https://learn.microsoft.com/ef/b"), ("C", "east north", "https://learn.microsoft.com/aspnet/c")]);
        using var db = new SqliteConnection($"Data Source={options.LawBookFile};Pooling=False");
        db.Open();
        BookVectors.Write(db, Models(_ => 0, t => [t.Contains("east") ? 1 : 0, t.Contains("north") ? 1 : 0]));
        var index = VectorIndex.Load(db)!;
        Assert.Equal(3, index.Count);
        Assert.Equal([1L, 3L], index.Nearest([1, 0], 2, _ => true));
        Assert.Equal([1L, 2L], index.Nearest([1, 0], 2, url => url.Contains("/ef/")));
    }

    [Fact] public void A_book_without_the_new_tables_means_no_passages()
    {
        var options = Options();
        Write(options.LawBookFile, SmallBook);
        using (var db = new SqliteConnection($"Data Source={options.LawBookFile};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE page_terms; DROP TABLE chunk_names; DROP TABLE aliases";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(new LawBook(options).Search("How do I send a not found result from a minimal API?"));
    }
}
