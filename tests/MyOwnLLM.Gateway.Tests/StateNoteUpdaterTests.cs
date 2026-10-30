using Microsoft.Extensions.Logging.Abstractions;
using MyOwnLLM.Gateway.Queue;
using MyOwnLLM.Gateway.State;
using MyOwnLLM.Gateway.Storage;

namespace MyOwnLLM.Gateway.Tests;

public class StateNoteUpdaterTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("mollm-notes").FullName;
    readonly CancellationTokenSource _stop = new();
    readonly FakeLlama _llama = new();
    RequestQueue _queue = null!;
    ProjectStateStore _state = null!;
    StateNoteUpdater _updater = null!;
    Task _runner = null!;
    string Project => _dir;

    public Task InitializeAsync() => Start(capacity: 8);

    Task Start(int capacity)
    {
        _queue = new RequestQueue(capacity);
        _state = new ProjectStateStore(new LocalDb(Path.Combine(_dir, "t.db")));
        _updater = new StateNoteUpdater(_queue, _llama, _state, NullLogger<StateNoteUpdater>.Instance);
        _runner = _queue.RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        try { await _runner; } catch (OperationCanceledException) { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Occupies the queue so background jobs wait until released.</summary>
    TaskCompletionSource Block()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        _ = _queue.EnqueueAsync(WorkKind.Chat, async _ => { started.SetResult(); await release.Task; return 0; }, default);
        started.Task.Wait(TimeSpan.FromSeconds(5));
        return release;
    }

    async Task WaitForNote(string expected)
    {
        for (var i = 0; i < 100 && await _state.GetAsync(Project) != expected; i++) await Task.Delay(50);
        Assert.Equal(expected, await _state.GetAsync(Project));
    }

    [Fact]
    public async Task Exchanges_queued_together_become_one_update_with_all_of_them()
    {
        _llama.Chat = _ => Task.FromResult("merged note");
        var release = Block();

        _updater.Record(Project, "first question", "first answer");
        _updater.Record(Project, "second question", "second answer");
        release.SetResult();

        await WaitForNote("merged note");
        var call = Assert.Single(_llama.Calls);
        Assert.Contains("first question", call.Messages[^1].Content);
        Assert.Contains("second question", call.Messages[^1].Content);
    }

    [Fact]
    public async Task Update_reads_the_note_when_it_runs_not_when_queued()
    {
        _llama.Chat = _ => Task.FromResult("new note");
        var release = Block();

        _updater.Record(Project, "q", "a");
        await _state.SetAsync(Project, "NOTE WRITTEN WHILE WAITING");
        release.SetResult();

        await WaitForNote("new note");
        Assert.Contains("NOTE WRITTEN WHILE WAITING", Assert.Single(_llama.Calls).Messages[^1].Content);
    }

    [Fact]
    public async Task Exchange_after_update_started_gets_its_own_update()
    {
        var firstStarted = new TaskCompletionSource();
        var finishFirst = new TaskCompletionSource();
        var calls = 0;
        _llama.Chat = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { firstStarted.SetResult(); await finishFirst.Task; return "note 1"; }
            return "note 2";
        };

        _updater.Record(Project, "q1", "a1");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _updater.Record(Project, "q2", "a2");
        finishFirst.SetResult();

        await WaitForNote("note 2");
        Assert.Contains("q2", _llama.Calls.Last().Messages[^1].Content);
    }

    [Fact]
    public void Full_queue_does_not_throw()
    {
        var full = new RequestQueue(capacity: 1); // never run, so the first item fills it
        _ = full.EnqueueAsync(WorkKind.Chat, _ => Task.FromResult(0), default);
        var updater = new StateNoteUpdater(full, _llama, _state, NullLogger<StateNoteUpdater>.Instance);

        updater.Record(Project, "q", "a");
        updater.Record(Project, "q2", "a2"); // a dropped update must not block later ones from being queued
    }
}
