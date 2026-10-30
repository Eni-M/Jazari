using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.State;

/// <summary>
/// Keeps each project's state note current in idle time. Exchanges pile up per project and
/// at most one background update per project waits in the queue; it reads the note when it runs.
/// </summary>
public sealed class StateNoteUpdater(RequestQueue queue, ILlamaClient llama, ProjectStateStore state, ILogger<StateNoteUpdater> logger)
{
    const int NoteTokens = 220;

    readonly object _gate = new();
    readonly Dictionary<string, List<(string Question, string Answer)>> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string project, string question, string answer)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(project, out var list)) _pending[project] = list = [];
            list.Add((question, answer));
            if (!_queued.Add(project)) return; // an update is already waiting and will pick this up
        }

        try
        {
            _ = queue.EnqueueAsync(WorkKind.Background, ct => UpdateAsync(project, ct), CancellationToken.None)
                .ContinueWith(t => logger.LogWarning(t.Exception, "State note update failed for {Project}", project), TaskContinuationOptions.OnlyOnFaulted);
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
}
