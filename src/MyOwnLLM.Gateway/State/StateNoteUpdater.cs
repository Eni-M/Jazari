using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.State;

/// <summary>
/// Keeps each project's state note current in idle time. Exchanges pile up per project, and the update waits until the
/// conversation has been quiet for a while: the note sits near the start of every prompt, so rewriting it mid-conversation
/// would make the model re-read the whole prompt, and the rewrite itself holds the model for about a minute.
/// At most one update per project waits in the queue; it reads the note when it runs. A new chat stops a running update,
/// which keeps its exchanges for the next one.
/// </summary>
public sealed class StateNoteUpdater(RequestQueue queue, ILlamaClient llama, ProjectStateStore state, ILogger<StateNoteUpdater> logger, TimeSpan? quietFor = null)
{
    public static readonly TimeSpan DefaultQuietTime = TimeSpan.FromMinutes(3);
    const int NoteTokens = 220;

    readonly TimeSpan _quietFor = quietFor ?? DefaultQuietTime;
    readonly object _gate = new();
    readonly Dictionary<string, List<(string Question, string Answer)>> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CancellationTokenSource> _waits = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string project, string question, string answer)
    {
        CancellationTokenSource wait;
        lock (_gate)
        {
            if (!_pending.TryGetValue(project, out var list)) _pending[project] = list = [];
            list.Add((question, answer));
            if (_waits.Remove(project, out var older)) older.Cancel(); // the conversation goes on: start the quiet time again
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
        if (exchanges.Count == 0) return 0;

        try
        {
            var note = await state.GetAsync(project);
            var git = await GitSummary.GetAsync(project, ct);
            var recent = string.Join("\n\n", exchanges.Select(e => $"Q: {e.Question}\nA: {e.Answer}"));
            List<ChatMessage> prompt =
            [
                new("system", "You maintain a short project state note for a developer."),
                new("user", $"Current note:\n{note ?? "(none)"}\n\nLatest exchanges:\n{recent}\n\n{git}\n\n" +
                            "Write the updated project state note: current task, recent changes, open problems, last decisions. At most 150 words."),
            ];
            await state.SetAsync(project, await llama.ChatAsync(prompt, NoteTokens, ct));
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Stopped for a chat or a shutdown: put the exchanges back in front, so the chat's own Record schedules them again.
            lock (_gate)
            {
                if (_pending.Remove(project, out var newer)) exchanges.AddRange(newer);
                _pending[project] = exchanges;
            }
            return 0;
        }
    }
}
