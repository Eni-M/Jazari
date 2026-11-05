using Microsoft.Extensions.Time.Testing;
using MyOwnLLM.Gateway.Llama;
using MyOwnLLM.Gateway.Queue;

namespace MyOwnLLM.Gateway.Tests;

/// <summary>A router in memory: loading one model unloads the other, as --models-max 1 does.</summary>
public sealed class FakeRouter : ILlamaClient
{
    public Dictionary<string, ModelState> States = new() { ["fast"] = ModelState.Loaded, ["deep"] = ModelState.Unloaded };
    public List<string> Loads { get; } = [];
    public HashSet<string> WontLoad { get; } = [];
    public TaskCompletionSource? LoadGate { get; set; }

    public Task<IReadOnlyDictionary<string, ModelState>?> ModelStatesAsync(CancellationToken ct)
    {
        lock (States) return Task.FromResult<IReadOnlyDictionary<string, ModelState>?>(new Dictionary<string, ModelState>(States));
    }

    public async Task LoadModelAsync(string name, CancellationToken ct)
    {
        lock (States) Loads.Add(name);
        if (LoadGate is { } gate)
        {
            lock (States) States[name] = ModelState.Loading;
            await gate.Task;
        }
        lock (States)
        {
            foreach (var k in States.Keys.ToList()) States[k] = ModelState.Unloaded;
            States[name] = WontLoad.Contains(name) ? ModelState.Failed : ModelState.Loaded;
        }
    }

    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? jsonSchema = null, string? model = null) => Task.FromResult("answer");
    public IAsyncEnumerable<StreamPart> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string? model = null, double? temperature = null) => throw new NotSupportedException();
    public Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct, string? model = null) => Task.FromResult("");
    public Task<int?> ContextTokensAsync(CancellationToken ct) => Task.FromResult<int?>(16384);
}

public class LanesTests
{
    readonly FakeRouter _router = new();
    readonly FakeTimeProvider _time = new();
    readonly RequestQueue _queue = new();
    readonly Lanes _lanes;

    public LanesTests() => _lanes = new Lanes(() => _router, _queue, _time, TimeSpan.Zero);

    [Fact]
    public async Task Starts_on_fast_and_loads_nothing_for_a_fast_question()
    {
        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Fast, null, CancellationToken.None));
        Assert.Empty(_router.Loads);
    }

    [Fact]
    public async Task A_deep_request_loads_deep_and_reports_the_stage()
    {
        var stages = new List<string>();
        Assert.Equal("deep", await _lanes.EnsureAsync(Lane.Deep, s => { stages.Add(s); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(["deep"], _router.Loads);
        Assert.Equal(["loading smart model"], stages);
    }

    [Fact]
    public async Task The_hold_ends_after_two_minutes_and_fast_comes_back()
    {
        using var stop = new CancellationTokenSource();
        var worker = _queue.RunAsync(stop.Token);
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        _lanes.HoldDeep();

        _time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(["deep"], _router.Loads);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _router.States["fast"] == ModelState.Loaded);
        Assert.Equal(["deep", "fast"], _router.Loads);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task A_second_deep_inside_the_hold_restarts_it_and_loads_nothing()
    {
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        _lanes.HoldDeep();
        _time.Advance(TimeSpan.FromSeconds(100));

        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        _lanes.HoldDeep();
        _time.Advance(TimeSpan.FromSeconds(100)); // past the first hold's end, inside the second's

        await Task.Delay(50);
        Assert.Equal(["deep"], _router.Loads);
    }

    [Fact]
    public async Task A_plain_question_during_the_hold_loads_fast_at_once()
    {
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        _lanes.HoldDeep();

        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Fast, null, CancellationToken.None));
        Assert.Equal(["deep", "fast"], _router.Loads);

        _time.Advance(Lanes.Hold); // the hold was cancelled: nothing more loads
        await Task.Delay(50);
        Assert.Equal(["deep", "fast"], _router.Loads);
    }

    [Fact]
    public async Task Cancelling_during_a_load_finishes_the_load_and_the_next_question_works()
    {
        _router.LoadGate = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();
        var asked = _lanes.EnsureAsync(Lane.Deep, null, stop.Token);
        await Until(() => _router.Loads.Count == 1);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);

        var next = _lanes.EnsureAsync(Lane.Fast, null, CancellationToken.None); // waits for the load, then swaps back
        _router.LoadGate.SetResult();
        Assert.Equal("fast", await next);
        Assert.Equal(ModelState.Loaded, _router.States["fast"]);
        Assert.Equal(["deep", "fast"], _router.Loads);
    }

    [Fact]
    public async Task A_failed_deep_load_answers_on_the_fast_model_and_isnt_tried_again()
    {
        _router.WontLoad.Add("deep");

        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None));
        Assert.Equal(ModelState.Loaded, _router.States["fast"]);
        var loads = _router.Loads.Count;

        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None));
        Assert.Equal(loads, _router.Loads.Count);
    }

    [Fact]
    public async Task The_swap_back_to_fast_reports_its_stage_too()
    {
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        var stages = new List<string>();
        await _lanes.EnsureAsync(Lane.Fast, s => { stages.Add(s); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(["loading fast model"], stages);
    }

    [Fact]
    public async Task A_load_already_under_way_is_not_asked_for_twice()
    {
        _router.States = new() { ["fast"] = ModelState.Loading, ["deep"] = ModelState.Unloaded };
        _ = Task.Run(async () => { await Task.Delay(30); lock (_router.States) _router.States["fast"] = ModelState.Loaded; });

        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Fast, null, CancellationToken.None));
        Assert.Empty(_router.Loads);
    }

    [Fact]
    public async Task Background_work_during_the_hold_uses_the_loaded_model_without_a_swap()
    {
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        Assert.Equal("deep", await _lanes.EnsureAsync(null, null, CancellationToken.None));
        Assert.Equal(["deep"], _router.Loads);
    }

    [Fact]
    public async Task A_chat_that_stops_the_return_to_fast_leaves_a_working_model()
    {
        using var stop = new CancellationTokenSource();
        var worker = _queue.RunAsync(stop.Token);
        await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None);
        _router.LoadGate = new TaskCompletionSource();
        _lanes.HoldDeep();
        _time.Advance(Lanes.Hold);
        await Until(() => _router.Loads.Count == 2); // the return to fast has started loading

        var chat = _queue.EnqueueAsync(WorkKind.Chat, ct => _lanes.EnsureAsync(Lane.Fast, null, ct), CancellationToken.None);
        _router.LoadGate.SetResult();

        Assert.Equal("fast", await chat);
        Assert.Equal(ModelState.Loaded, _router.States["fast"]);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task Without_a_deep_model_deep_uses_the_fast_one_without_a_load()
    {
        _router.States = new() { ["fast"] = ModelState.Loaded };

        Assert.Equal("fast", await _lanes.EnsureAsync(Lane.Deep, null, CancellationToken.None));
        Assert.Empty(_router.Loads);
    }

    [Fact]
    public async Task A_plain_llama_server_sends_no_model_name_for_either_lane()
    {
        var plain = new Lanes(() => new FakeLlama(), _queue, _time);

        Assert.Null(await plain.EnsureAsync(Lane.Fast, null, CancellationToken.None));
        Assert.Null(await plain.EnsureAsync(Lane.Deep, null, CancellationToken.None));
    }

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
