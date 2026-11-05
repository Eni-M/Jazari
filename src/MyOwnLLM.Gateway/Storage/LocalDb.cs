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
                CREATE TABLE IF NOT EXISTS interactions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, project TEXT, kind TEXT NOT NULL,
                    prompt TEXT NOT NULL, answer TEXT NOT NULL, duration_ms INTEGER NOT NULL, accepted INTEGER NULL);
                """;
            cmd.ExecuteNonQuery();
            // Added later: which model, gateway build and law book made an answer, so a miss can be traced. A file from
            // before them gets them here.
            foreach (var column in new[] { "model", "build", "law_book" })
            {
                cmd.CommandText = $"SELECT count(*) FROM pragma_table_info('interactions') WHERE name = '{column}'";
                if ((long)cmd.ExecuteScalar()! > 0) continue;
                cmd.CommandText = $"ALTER TABLE interactions ADD COLUMN {column} TEXT";
                cmd.ExecuteNonQuery();
            }
            _created = true;
        }
        return connection;
    }
}
