using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.State;

/// <summary>
/// Keeps each project's state note current in idle time. Exchanges pile up per project, and the update waits until the
/// conversation has been quiet for a while: the note sits near the start of every prompt, so rewriting it mid-conversation
/// would make the model re-read the whole prompt, and the rewrite itself holds the model for about a minute.
/// At most one update per project waits in the queue; it reads the note when it runs. An update that is stopped (by a
/// chat, a warm-up, a status) or fails keeps its exchanges and waits for the next quiet time.
/// </summary>
public sealed class StateNoteUpdater(RequestQueue queue, ILlamaClient llama, ProjectStateStore state, ILogger<StateNoteUpdater> logger, TimeSpan? quietFor = null, ILanes? lanes = null)
{
    public static readonly TimeSpan DefaultQuietTime = TimeSpan.FromMinutes(3);
    const int NoteTokens = 220;
    /// <summary>
    /// About 1,500 tokens of exchanges per update, newest first, so the prompt fits even an 8k context however many
    /// piled up; long answers are cut, and the oldest exchanges past the budget are left out.
    /// </summary>
    const int ExchangeChars = 6000, AnswerChars = 1200;

    readonly TimeSpan _quietFor = quietFor ?? DefaultQuietTime;
    readonly object _gate = new();
    readonly Dictionary<string, List<(string Question, string Answer)>> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CancellationTokenSource> _waits = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string project, string question, string answer)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(project, out var list)) _pending[project] = list = [];
            list.Add((question, answer));
        }
        WaitForQuiet(project);
    }

    /// <summary>Starts (or restarts) the quiet time after which the project's pending exchanges are written into its note.</summary>
    void WaitForQuiet(string project)
    {
        CancellationTokenSource wait;
        lock (_gate)
        {
            if (_waits.Remove(project, out var older))
            {
                try { older.Cancel(); } // the conversation goes on: start the quiet time again
                catch (ObjectDisposedException) { } // its timer just ran out; the check under the lock below drops it
            }
            _waits[project] = wait = new CancellationTokenSource();
        }
        _ = QueueWhenQuietAsync(project, wait);
    }

    async Task QueueWhenQuietAsync(string project, CancellationTokenSource wait)
    {
        try { await Task.Delay(_quietFor, wait.Token); }
        catch (OperationCanceledException) { return; }
        finally { wait.Dispose(); }

        lock (_gate)
        {
            if (!_waits.TryGetValue(project, out var current) || current != wait) return;
            _waits.Remove(project);
            if (!_queued.Add(project)) return; // an update is already waiting and will pick this up
        }

        try
        {
            _ = queue.EnqueueAsync(WorkKind.Background, ct => UpdateAsync(project, ct), CancellationToken.None)
                .ContinueWith(t => logger.LogWarning(t.Exception, "State note update failed for {Project}", project),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (QueueFullException)
        {
            lock (_gate) _queued.Remove(project); // keep the exchanges; the next Record retries
            logger.LogInformation("Queue full; state note update for {Project} deferred", project);
        }
    }

    async Task<int> UpdateAsync(string project, CancellationToken ct)
    {
        List<(string Question, string Answer)> exchanges;
        lock (_gate)
        {
            _queued.Remove(project); // exchanges recorded from now on queue a fresh update
            exchanges = _pending.Remove(project, out var list) ? list : [];
        }
        if (exchanges.Count == 0 || !GitSummary.IsRepo(project)) return 0;
        exchanges = WithinBudget(exchanges);

        try
        {
            var note = await state.GetAsync(project);
            var git = await GitSummary.GetAsync(project, ct);
            var recent = string.Join("\n\n", exchanges.Select(e => $"Q: {e.Question}\nA: {e.Answer}"));
            List<ChatMessage> prompt =
            [
                new("system", "You maintain a short project state note for a developer."),
                new("user", $"Current note:\n{note ?? "(none)"}\n\nLatest exchanges:\n{recent}\n\n{git}\n\n" +
                            "Write the updated project state note: current task, recent changes, open problems, last decisions. " +
                            "Use only what the note, the exchanges and the git output above actually say. Leave out any part they don't " +
                            "support; small talk adds nothing. Never invent tasks, changes or decisions. At most 150 words."),
            ];
            await state.SetAsync(project, await llama.ChatAsync(prompt, NoteTokens, ct, model: lanes is null ? null : await lanes.EnsureAsync(null, null, ct)));
            return 0;
        }
        catch (Exception e)
        {
            // Stopped or failed: put the exchanges back in front. A stop (for a chat, a warm-up, a status) waits for the
            // next quiet time on its own, since those requests don't record exchanges; after a failure the next
            // exchange retries, so a model server that's down isn't asked again and again.
            lock (_gate)
            {
                if (_pending.Remove(project, out var newer)) exchanges.AddRange(newer);
                _pending[project] = exchanges;
            }
            if (e is OperationCanceledException) WaitForQuiet(project);
            else logger.LogWarning(e, "State note update failed for {Project}; the next exchange tries again", project);
            return 0;
        }
    }

    /// <summary>The newest exchanges that fit <see cref="ExchangeChars"/>, oldest first, with long answers cut.</summary>
    static List<(string Question, string Answer)> WithinBudget(List<(string Question, string Answer)> exchanges)
    {
        var kept = new List<(string, string)>();
        var used = 0;
        for (var i = exchanges.Count - 1; i >= 0; i--)
        {
            var (q, a) = exchanges[i];
            if (q.Length > AnswerChars) q = q[..AnswerChars] + " …";
            if (a.Length > AnswerChars) a = a[..AnswerChars] + " …";
            if (kept.Count > 0 && used + q.Length + a.Length > ExchangeChars) break;
            kept.Insert(0, (q, a));
            used += q.Length + a.Length;
        }
        return kept;
    }
}
