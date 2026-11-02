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

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git")); // notes only exist for repositories
        return Start(capacity: 8);
    }

    Task Start(int capacity)
    {
        _queue = new RequestQueue(capacity);
        _state = new ProjectStateStore(new LocalDb(Path.Combine(_dir, "t.db")));
        _updater = new StateNoteUpdater(_queue, _llama, _state, NullLogger<StateNoteUpdater>.Instance, TimeSpan.Zero);
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
    public async Task A_folder_outside_a_repository_gets_no_note()
    {
        Directory.Delete(Path.Combine(_dir, ".git"));

        _updater.Record(Project, "hello", "hello");
        await Task.Delay(300);

        Assert.Null(await _state.GetAsync(Project));
        Assert.Empty(_llama.Calls);
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
    public async Task No_update_runs_while_the_conversation_goes_on()
    {
        var updater = new StateNoteUpdater(_queue, _llama, _state, NullLogger<StateNoteUpdater>.Instance, TimeSpan.FromHours(1));

        updater.Record(Project, "q", "a");
        await Task.Delay(200);

        Assert.Empty(_llama.Calls);
    }

    [Fact]
    public async Task A_stopped_update_runs_again_on_its_own()
    {
        // A warm-up or /status stops the update but records no exchange of its own: nothing else would reschedule it.
        var started = new TaskCompletionSource();
        var calls = 0;
        _llama.Chat = async ct =>
        {
            if (Interlocked.Increment(ref calls) > 1) return "note after the stop";
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        };
        _updater.Record(Project, "first question", "first answer");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _queue.EnqueueAsync(WorkKind.Chat, _ => Task.FromResult(0), default).WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForNote("note after the stop");
        Assert.Contains("first question", _llama.Calls.Last().Messages[^1].Content);
    }

    [Fact]
    public async Task A_failed_update_keeps_its_exchanges_for_the_next_one()
    {
        var calls = 0;
        _llama.Chat = _ => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("model server down") : Task.FromResult("note");
        _updater.Record(Project, "first question", "first answer");
        for (var i = 0; i < 100 && calls == 0; i++) await Task.Delay(20);

        _updater.Record(Project, "second question", "second answer");

        await WaitForNote("note");
        Assert.Contains("first question", _llama.Calls.Last().Messages[^1].Content);
        Assert.Contains("second question", _llama.Calls.Last().Messages[^1].Content);
    }

    [Fact]
    public async Task Piled_up_exchanges_are_cut_to_fit_the_context()
    {
        _llama.Chat = _ => Task.FromResult("note");
        var release = Block();
        for (var i = 0; i < 40; i++) _updater.Record(Project, $"question {i}", new string('x', 2000));
        release.SetResult();

        await WaitForNote("note");
        var sent = Assert.Single(_llama.Calls).Messages[^1].Content;
        Assert.InRange(sent.Length, 1, 9000);
        Assert.Contains("question 39", sent); // the newest are kept
    }

    [Fact]
    public void Full_queue_does_not_throw()
    {
        var full = new RequestQueue(capacity: 1); // never run, so the first item fills it
        _ = full.EnqueueAsync(WorkKind.Chat, _ => Task.FromResult(0), default);
        var updater = new StateNoteUpdater(full, _llama, _state, NullLogger<StateNoteUpdater>.Instance, TimeSpan.Zero);

        updater.Record(Project, "q", "a");
        updater.Record(Project, "q2", "a2"); // a dropped update must not block later ones from being queued
    }
}
