namespace MyOwnLLM.Gateway.Queue;

/// <summary>Lower value runs first.</summary>
public enum WorkKind { Chat = 0, Completion = 1, Background = 2 }

public sealed class QueueFullException() : Exception("the model is busy; try again shortly");

/// <summary>Thrown to the caller of a completion that a newer completion replaced.</summary>
public sealed class SupersededException() : OperationCanceledException("replaced by a newer completion");

/// <summary>
/// Feeds the model one request at a time: chat before completion before background,
/// FIFO within a kind, and a new completion replaces any older one.
/// </summary>
public sealed class RequestQueue(int capacity = 8)
{
    sealed class Item(WorkKind kind, CancellationTokenSource cts)
    {
        public WorkKind Kind { get; } = kind;
        public CancellationTokenSource Cts { get; } = cts;
        public bool Superseded { get; set; }
        public required Func<Task> Run { get; init; }
        public required Action<Exception> Fail { get; init; }
    }

    readonly object _gate = new();
    readonly List<Item> _pending = [];
    readonly SemaphoreSlim _signal = new(0);
    Item? _running;

    public Task<T> EnqueueAsync<T>(WorkKind kind, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Item item = null!;
        item = new Item(kind, cts)
        {
            Fail = e => tcs.TrySetException(e),
            Run = async () =>
            {
                try { tcs.TrySetResult(await work(cts.Token)); }
                catch (OperationCanceledException) when (item.Superseded) { tcs.TrySetException(new SupersededException()); }
                catch (OperationCanceledException) { tcs.TrySetCanceled(cts.Token); }
                catch (Exception e) { tcs.TrySetException(e); }
            },
        };

        lock (_gate)
        {
            if (kind == WorkKind.Completion) SupersedeCompletions();
            if (_pending.Count >= capacity) throw new QueueFullException();
            _pending.Add(item);
        }

        // A caller that gives up while still waiting leaves the queue at once.
        cts.Token.Register(() =>
        {
            bool removed;
            lock (_gate) removed = _pending.Remove(item);
            if (removed) tcs.TrySetCanceled(cts.Token);
        });
        _signal.Release();
        return tcs.Task;
    }

    void SupersedeCompletions()
    {
        foreach (var old in _pending.Where(i => i.Kind == WorkKind.Completion).ToList())
        {
            _pending.Remove(old);
            old.Superseded = true;
            old.Fail(new SupersededException());
        }
        if (_running is { Kind: WorkKind.Completion } running)
        {
            running.Superseded = true;
            running.Cts.Cancel();
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try { await RunLoopAsync(stoppingToken); }
        finally
        {
            // Shutting down: nobody will run what is still waiting.
            List<Item> left;
            lock (_gate) { left = [.. _pending]; _pending.Clear(); }
            foreach (var item in left) item.Fail(new OperationCanceledException("the gateway is shutting down"));
        }
    }

    async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await _signal.WaitAsync(stoppingToken);
            Item? next;
            lock (_gate)
            {
                next = _pending.OrderBy(i => i.Kind).FirstOrDefault(); // OrderBy is stable: FIFO within a kind
                if (next is null) continue; // removed by cancellation or superseded
                _pending.Remove(next);
                _running = next;
            }
            using var stop = stoppingToken.Register(next.Cts.Cancel);
            try { await next.Run(); }
            finally
            {
                lock (_gate) _running = null;
                next.Cts.Dispose();
            }
        }
    }
}
