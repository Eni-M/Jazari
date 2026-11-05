namespace MyOwnLLM.Gateway.Storage;

/// <param name="Model">The model that answered, as the server names it; null for a cached answer or none.</param>
public sealed record Interaction(string? Project, string Kind, string Prompt, string Answer, long DurationMs, string? Model = null);

/// <summary>Local record of prompts, answers and accept/reject feedback (training data for later LoRA refreshes).
/// eval/misses.py lists the likely misses from it.</summary>
public sealed class InteractionLog(LocalDb db, GatewayOptions? options = null)
{
    /// <summary>The gateway's version and commit.</summary>
    static readonly string Build = typeof(InteractionLog).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute v, ..] ? v.InformationalVersion : "unknown";

    /// <summary>When the law book was written, which tells one build of it from another; null without one.</summary>
    string? LawBook() => options is { } o && File.Exists(o.LawBookFile) ? File.GetLastWriteTimeUtc(o.LawBookFile).ToString("O") : null;

    public async Task<long> AddAsync(Interaction item)
    {
        await using var connection = db.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO interactions(created_utc, project, kind, prompt, answer, duration_ms, model, build, law_book)
            VALUES($t, $project, $kind, $prompt, $answer, $ms, $model, $build, $book);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$model", (object?)item.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$build", Build);
        cmd.Parameters.AddWithValue("$book", (object?)LawBook() ?? DBNull.Value);
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
