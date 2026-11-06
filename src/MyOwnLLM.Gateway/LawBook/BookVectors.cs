using Microsoft.Data.Sqlite;

namespace MyOwnLLM.Gateway;

/// <summary>
/// One vector per law book passage, in the book's vectors table: a float scale, then one signed byte per dimension, about
/// 21 MB for 55,500 passages instead of 85 as floats. Made by the gateway (embed-law-book) with the model that embeds
/// questions, so build.py stays standard-library Python.
/// </summary>
public static class BookVectors
{
    public static byte[] Pack(float[] v)
    {
        var max = v.Max(MathF.Abs);
        var scale = max > 0 ? max / 127f : 1f;
        var bytes = new byte[4 + v.Length];
        BitConverter.TryWriteBytes(bytes, scale);
        for (var i = 0; i < v.Length; i++) bytes[4 + i] = unchecked((byte)(sbyte)MathF.Round(v[i] / scale));
        return bytes;
    }

    /// <summary>Embeds every passage (title and text) and stores its vector, replacing any there; shortest first, so a batch
    /// pads little. Prints progress: on the T480 it takes tens of minutes.</summary>
    public static void Write(SqliteConnection db, ISearchModels models, int batch = 32, TextWriter? progress = null)
    {
        Exec(db, "DROP TABLE IF EXISTS vectors; CREATE TABLE vectors(rowid INTEGER PRIMARY KEY, v BLOB NOT NULL)");
        var rows = new List<(long Id, string Text)>();
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT rowid, title, text FROM chunks";
            using var r = read.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1) + "\n" + r.GetString(2)));
        }
        rows.Sort((a, b) => a.Text.Length.CompareTo(b.Text.Length));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var start = 0; start < rows.Count; start += batch)
        {
            var part = rows.GetRange(start, Math.Min(batch, rows.Count - start));
            var vectors = models.Embed(part.Select(p => p.Text).ToList());
            using var tx = db.BeginTransaction();
            for (var i = 0; i < part.Count; i++)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO vectors(rowid, v) VALUES ($r, $v)";
                insert.Parameters.AddWithValue("$r", part[i].Id);
                insert.Parameters.AddWithValue("$v", Pack(vectors[i]));
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            if (progress is not null && (start / batch) % 50 == 0)
                progress.WriteLine($"{start + part.Count} of {rows.Count} passages, {clock.Elapsed:hh\\:mm\\:ss}");
        }
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Every passage's vector in memory, for finding those nearest a question by dot product (the vectors are
/// normalized, so that's cosine similarity). 55,500 × 384 takes a few milliseconds.</summary>
public sealed class VectorIndex(long[] rowIds, string[] urls, sbyte[] values, float[] scales, int dimensions)
{
    /// <summary>The book's vectors, or null when it has none (a book from before them, or not yet embedded).</summary>
    public static VectorIndex? Load(SqliteConnection db)
    {
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'vectors'";
            if ((long)exists.ExecuteScalar()! == 0) return null;
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT v.rowid, c.url, v.v FROM vectors v JOIN chunks c ON c.rowid = v.rowid ORDER BY v.rowid";
        using var r = cmd.ExecuteReader();
        List<long> ids = []; List<string> addresses = []; List<float> scaleList = []; List<sbyte> all = [];
        var dimensions = 0;
        while (r.Read())
        {
            var blob = (byte[])r.GetValue(2);
            dimensions = blob.Length - 4;
            ids.Add(r.GetInt64(0)); addresses.Add(r.GetString(1)); scaleList.Add(BitConverter.ToSingle(blob, 0));
            for (var i = 4; i < blob.Length; i++) all.Add(unchecked((sbyte)blob[i]));
        }
        return ids.Count == 0 ? null : new VectorIndex([.. ids], [.. addresses], [.. all], [.. scaleList], dimensions);
    }

    public int Count => rowIds.Length;

    /// <summary>The passages nearest the query, best first, among those whose address <paramref name="allowed"/> accepts.</summary>
    public IEnumerable<long> Nearest(float[] query, int count, Func<string, bool> allowed)
    {
        var best = new PriorityQueue<long, float>(); // a min-heap of the best so far
        for (var p = 0; p < rowIds.Length; p++)
        {
            if (!allowed(urls[p])) continue;
            float dot = 0;
            var offset = p * dimensions;
            for (var i = 0; i < dimensions; i++) dot += query[i] * values[offset + i];
            dot *= scales[p];
            if (best.Count < count) best.Enqueue(rowIds[p], dot);
            else if (best.TryPeek(out _, out var worst) && dot > worst) best.EnqueueDequeue(rowIds[p], dot);
        }
        var found = new List<(long Id, float Score)>();
        while (best.TryDequeue(out var id, out var score)) found.Add((id, score));
        return found.OrderByDescending(f => f.Score).Select(f => f.Id);
    }
}
