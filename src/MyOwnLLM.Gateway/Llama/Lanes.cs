using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.Llama;

public enum Lane { Fast, Deep }

/// <summary>A /deep the smart model can't answer: none is installed, or it wouldn't load. The message is for the user.</summary>
public sealed class DeepModelUnavailableException(string why) : Exception(why);

public interface ILanes
{
    Task<bool> DeepAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Has the lane's model loaded and returns the name to send to it; null lane means whichever model is loaded, for work that
    /// doesn't care. Null result: a plain llama-server with one model, where no name is sent. A load runs to its end once
    /// begun, whatever <paramref name="ct"/> says, so stopping a question can't leave a half-loaded model.
    /// </summary>
    Task<string?> EnsureAsync(Lane? lane, Func<string, Task>? stage, CancellationToken ct);

    /// <summary>Starts or restarts the hold: another /deep is likely, so the smart model stays; afterwards the fast model comes back.</summary>
    void HoldDeep();
}

/// <summary>
/// One model at a time, through llama-server's router (docs/superpowers/specs/2026-11-04-deep-measurements.md). Runs inside
/// the request queue's items, so only one caller at a time, except for a load a stopped caller left running.
/// </summary>
public sealed class Lanes(Func<ILlamaClient> llama, RequestQueue queue, TimeProvider? time = null, TimeSpan? pollEvery = null) : ILanes
{
    public const string FastModel = "fast", DeepModel = "deep";
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);
    static readonly TimeSpan LoadLimit = TimeSpan.FromMinutes(5), NotStartedLimit = TimeSpan.FromSeconds(10);

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly TimeSpan _pollEvery = pollEvery ?? TimeSpan.FromMilliseconds(200);
    readonly object _gate = new();
    Task? _loading;
    ITimer? _hold;

    public async Task<bool> DeepAvailableAsync(CancellationToken ct) => await llama().ModelStatesAsync(ct) is { } states && states.ContainsKey(DeepModel);

    public async Task<string?> EnsureAsync(Lane? lane, Func<string, Task>? stage, CancellationToken ct)
    {
        if (lane is Lane.Fast) CancelHold();
        Task? loading;
        lock (_gate) loading = _loading;
        // A stopped question's load: wait, then look again. Whether it worked shows in the states below.
        if (loading is not null) await loading.WaitAsync(ct).ContinueWith(t => { if (t.IsCanceled) ct.ThrowIfCancellationRequested(); }, TaskScheduler.Default);

        var states = await llama().ModelStatesAsync(ct);
        if (states is null || !states.ContainsKey(FastModel))
        {
            if (lane is Lane.Deep) throw new DeepModelUnavailableException("The smart model isn't set up. Run jazari update.");
            return null;
        }
        if (lane is Lane.Deep && !states.ContainsKey(DeepModel)) throw new DeepModelUnavailableException("The smart model isn't installed. Run jazari update.");

        var name = lane switch
        {
            Lane.Deep => DeepModel,
            Lane.Fast => FastModel,
            _ => states.FirstOrDefault(s => s.Value is ModelState.Loaded).Key ?? FastModel,
        };
        if (states[name] is ModelState.Loaded) return name;

        if (lane is Lane.Deep && stage is not null) await stage("loading smart model");
        var load = Task.Run(() => LoadAsync(name), CancellationToken.None);
        lock (_gate) _loading = load;
        _ = load.ContinueWith(_ => { lock (_gate) if (_loading == load) _loading = null; }, TaskScheduler.Default);
        await load.WaitAsync(ct);
        return name;
    }

    async Task LoadAsync(string name)
    {
        try { await WaitForLoadAsync(name); }
        catch (Exception e) when (name == DeepModel)
        {
            // The smart model wouldn't load: bring the fast one back, so plain questions carry on.
            try { await WaitForLoadAsync(FastModel); } catch (Exception) { }
            throw new DeepModelUnavailableException($"The smart model didn't load ({e.Message}). Plain questions still work.");
        }
    }

    async Task WaitForLoadAsync(string name)
    {
        var client = llama();
        await client.LoadModelAsync(name, CancellationToken.None);
        var started = _time.GetUtcNow();
        var seen = false;
        while (true)
        {
            var state = (await client.ModelStatesAsync(CancellationToken.None))?.GetValueOrDefault(name) ?? ModelState.Unloaded;
            if (state is ModelState.Loaded) return;
            if (state is ModelState.Failed) throw new LlamaErrorException(503, "the model server couldn't load it");
            if (state is ModelState.Loading) seen = true;
            else if (seen || _time.GetUtcNow() - started > NotStartedLimit) throw new LlamaErrorException(503, "the model server stopped loading it");
            if (_time.GetUtcNow() - started > LoadLimit) throw new LlamaErrorException(503, "loading it took too long");
            await Task.Delay(_pollEvery);
        }
    }

    public void HoldDeep()
    {
        lock (_gate)
        {
            _hold?.Dispose();
            _hold = _time.CreateTimer(_ => ReturnToFast(), null, Hold, Timeout.InfiniteTimeSpan);
        }
    }

    void CancelHold()
    {
        lock (_gate)
        {
            _hold?.Dispose();
            _hold = null;
        }
    }

    /// <summary>As background work: a question that arrives first makes its own choice of model, and this gives way to it.</summary>
    void ReturnToFast()
    {
        try
        {
            _ = queue.EnqueueAsync(WorkKind.Background, async ct => await EnsureAsync(Lane.Fast, null, ct), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        catch (QueueFullException) { } // every waiting question picks its own model, and the next hold tries again
    }
}
