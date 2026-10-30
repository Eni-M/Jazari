using System.Collections.Concurrent;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.Tests;

public class RequestQueueTests : IAsyncLifetime
{
    CancellationTokenSource _stop = new();
    RequestQueue _queue = null!;
    Task _runner = null!;

    public Task InitializeAsync() => Start(capacity: 8);

    Task Start(int capacity)
    {
        _queue = new RequestQueue(capacity);
        _runner = _queue.RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        try { await _runner; } catch (OperationCanceledException) { }
    }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>Enqueues work that signals when it starts and finishes only when released.</summary>
    (Task<int> result, TaskCompletionSource started, TaskCompletionSource release) Gated(WorkKind kind, int value, CancellationToken ct = default)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = _queue.EnqueueAsync(kind, async t =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(t);
            return value;
        }, ct);
        return (result, started, release);
    }

    [Fact]
    public async Task Runs_one_item_at_a_time()
    {
        var first = Gated(WorkKind.Chat, 1);
        var second = Gated(WorkKind.Chat, 2);
        await first.started.Task.WaitAsync(Wait);

        await Task.Delay(100);
        Assert.False(second.started.Task.IsCompleted);

        first.release.SetResult();
        await second.started.Task.WaitAsync(Wait);
        second.release.SetResult();
        Assert.Equal(1, await first.result.WaitAsync(Wait));
        Assert.Equal(2, await second.result.WaitAsync(Wait));
    }

    [Fact]
    public async Task Chat_runs_before_waiting_completion_and_background()
    {
        var order = new ConcurrentQueue<WorkKind>();
        var blocker = Gated(WorkKind.Chat, 0);
        await blocker.started.Task.WaitAsync(Wait);

        Task<int> Record(WorkKind kind) => _queue.EnqueueAsync(kind, _ => { order.Enqueue(kind); return Task.FromResult(0); }, default);
        var tasks = new[] { Record(WorkKind.Background), Record(WorkKind.Completion), Record(WorkKind.Chat) };
        blocker.release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(Wait);

        Assert.Equal([WorkKind.Chat, WorkKind.Completion, WorkKind.Background], order);
    }

    [Fact]
    public async Task New_completion_cancels_pending_completion()
    {
        var blocker = Gated(WorkKind.Chat, 0);
        await blocker.started.Task.WaitAsync(Wait);
        var older = Gated(WorkKind.Completion, 1);

        var newer = Gated(WorkKind.Completion, 2);

        await Assert.ThrowsAsync<SupersededException>(() => older.result.WaitAsync(Wait));
        blocker.release.SetResult();
        await newer.started.Task.WaitAsync(Wait);
        newer.release.SetResult();
        Assert.Equal(2, await newer.result.WaitAsync(Wait));
    }

    [Fact]
    public async Task New_completion_cancels_running_completion()
    {
        var older = Gated(WorkKind.Completion, 1);
        await older.started.Task.WaitAsync(Wait);

        var newer = Gated(WorkKind.Completion, 2);

        await Assert.ThrowsAsync<SupersededException>(() => older.result.WaitAsync(Wait));
        await newer.started.Task.WaitAsync(Wait);
        newer.release.SetResult();
        Assert.Equal(2, await newer.result.WaitAsync(Wait));
    }

    [Fact]
    public async Task New_chat_stops_running_background_work()
    {
        var background = Gated(WorkKind.Background, 1);
        await background.started.Task.WaitAsync(Wait);

        var chat = Gated(WorkKind.Chat, 2);

        await Assert.ThrowsAsync<SupersededException>(() => background.result.WaitAsync(Wait));
        await chat.started.Task.WaitAsync(Wait);
        chat.release.SetResult();
        Assert.Equal(2, await chat.result.WaitAsync(Wait));
    }

    [Fact]
    public async Task Caller_cancellation_cancels_work_and_queue_continues()
    {
        using var caller = new CancellationTokenSource();
        var running = Gated(WorkKind.Chat, 1, caller.Token);
        var next = Gated(WorkKind.Chat, 2);
        await running.started.Task.WaitAsync(Wait);

        caller.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.result.WaitAsync(Wait));
        Assert.IsNotType<SupersededException>(ex);
        await next.started.Task.WaitAsync(Wait);
        next.release.SetResult();
        Assert.Equal(2, await next.result.WaitAsync(Wait));
    }

    [Fact]
    public async Task Stopping_cancels_running_and_waiting_work()
    {
        var running = Gated(WorkKind.Chat, 1);
        var waiting = Gated(WorkKind.Chat, 2);
        await running.started.Task.WaitAsync(Wait);

        _stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.result.WaitAsync(Wait));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.result.WaitAsync(Wait));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.WaitAsync(Wait));
    }

    [Fact]
    public async Task Full_queue_throws_QueueFullException()
    {
        await DisposeAsync();
        _stop = new CancellationTokenSource();
        await Start(capacity: 2);
        var running = Gated(WorkKind.Chat, 0);
        await running.started.Task.WaitAsync(Wait);
        Gated(WorkKind.Chat, 1);
        Gated(WorkKind.Chat, 2);

        await Assert.ThrowsAsync<QueueFullException>(() => _queue.EnqueueAsync(WorkKind.Chat, _ => Task.FromResult(3), default));

        running.release.SetResult();
    }
}
