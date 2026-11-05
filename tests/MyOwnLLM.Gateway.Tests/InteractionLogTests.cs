using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.Tests;

public class InteractionLogTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-log").FullName;
    readonly LocalDb _db;
    readonly InteractionLog _log;

    public InteractionLogTests()
    {
        _db = new LocalDb(Path.Combine(_dir, "test.db"));
        _log = new InteractionLog(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static Interaction Sample => new(@"C:\Repo", "chat", "prompt", "answer", 1200);

    long? Accepted(long id)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT accepted FROM interactions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    [Fact]
    public async Task AddAsync_returns_increasing_ids()
    {
        var first = await _log.AddAsync(Sample);
        var second = await _log.AddAsync(Sample);

        Assert.True(second > first);
        Assert.Null(Accepted(first));
    }

    [Fact]
    public async Task SetFeedbackAsync_sets_accepted_and_returns_true()
    {
        var up = await _log.AddAsync(Sample);
        var down = await _log.AddAsync(Sample);

        Assert.True(await _log.SetFeedbackAsync(up, accepted: true));
        Assert.True(await _log.SetFeedbackAsync(down, accepted: false));

        Assert.Equal(1, Accepted(up));
        Assert.Equal(0, Accepted(down));
    }

    [Fact]
    public async Task SetFeedbackAsync_unknown_id_returns_false()
    {
        Assert.False(await _log.SetFeedbackAsync(9999, accepted: true));
    }

    [Fact]
    public async Task Each_row_says_which_model_build_and_law_book_made_it()
    {
        var book = Path.Combine(_dir, "law-book.db");
        File.WriteAllText(book, "");
        var log = new InteractionLog(_db, new GatewayOptions { LawBookFile = book });

        var id = await log.AddAsync(Sample with { Model = "deep" });

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT model, build, law_book FROM interactions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("deep", r.GetString(0));
        Assert.False(string.IsNullOrEmpty(r.GetString(1)));
        Assert.Equal(File.GetLastWriteTimeUtc(book).ToString("O"), r.GetString(2));
    }

    [Fact]
    public async Task A_file_from_before_the_version_columns_gets_them()
    {
        var old = Path.Combine(_dir, "old.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={old}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE interactions(id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, project TEXT, kind TEXT NOT NULL,
                    prompt TEXT NOT NULL, answer TEXT NOT NULL, duration_ms INTEGER NOT NULL, accepted INTEGER NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.True(await new InteractionLog(new LocalDb(old)).AddAsync(Sample) > 0);
    }
}
