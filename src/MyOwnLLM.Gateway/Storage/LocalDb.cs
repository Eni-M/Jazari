using Microsoft.Data.Sqlite;

namespace MyOwnLLM.Gateway.Storage;

/// <summary>The gateway's single local SQLite file; creates the schema on first open.</summary>
public sealed class LocalDb(string dbPath)
{
    readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    bool _created;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_created)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS project_state(project TEXT PRIMARY KEY, note TEXT NOT NULL, updated_utc TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
            _created = true;
        }
        return connection;
    }
}
