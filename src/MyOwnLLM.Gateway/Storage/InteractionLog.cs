namespace MyOwnLLM.Gateway.Storage;

public sealed record Interaction(string? Project, string Kind, string Prompt, string Answer, long DurationMs);

/// <summary>Local record of prompts, answers and accept/reject feedback (training data for later LoRA refreshes).</summary>
public sealed class InteractionLog(LocalDb db)
{
    public async Task<long> AddAsync(Interaction item)
    {
        await using var connection = db.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO interactions(created_utc, project, kind, prompt, answer, duration_ms)
            VALUES($t, $project, $kind, $prompt, $answer, $ms);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$project", (object?)item.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", item.Kind);
        cmd.Parameters.AddWithValue("$prompt", item.Prompt);
        cmd.Parameters.AddWithValue("$answer", item.Answer);
        cmd.Parameters.AddWithValue("$ms", item.DurationMs);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<bool> SetFeedbackAsync(long id, bool accepted)
    {
        await using var connection = db.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE interactions SET accepted = $a WHERE id = $id";
        cmd.Parameters.AddWithValue("$a", accepted ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }
}
