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
}
