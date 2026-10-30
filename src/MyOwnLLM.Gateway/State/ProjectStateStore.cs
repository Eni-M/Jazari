using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.State;

/// <summary>The rolling "where we are" note per project (~800 tokens max).</summary>
public sealed class ProjectStateStore(LocalDb db)
{
    public const int MaxChars = 3200;

    public async Task<string?> GetAsync(string projectPath)
    {
        await using var connection = db.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT note FROM project_state WHERE project = $p";
        cmd.Parameters.AddWithValue("$p", Key(projectPath));
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task SetAsync(string projectPath, string note)
    {
        await using var connection = db.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO project_state(project, note, updated_utc) VALUES($p, $n, $t)
            ON CONFLICT(project) DO UPDATE SET note = excluded.note, updated_utc = excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("$p", Key(projectPath));
        cmd.Parameters.AddWithValue("$n", note.Length > MaxChars ? note[..MaxChars] : note);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    static string Key(string projectPath) => Path.GetFullPath(projectPath).TrimEnd('\\', '/').ToLowerInvariant();
}
