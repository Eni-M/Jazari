using MyOwnLLM.Gateway.Llama;

namespace MyOwnLLM.Gateway.Tests;

public class ModelLimitsTests
{
    readonly FakeLlama _llama = new();
    readonly Clock _time = new();

    ModelLimits Limits() => new(_llama, new GatewayOptions { ContextTokens = 16384 }, _time);

    [Fact]
    public async Task Uses_the_size_the_server_was_started_with()
    {
        _llama.ContextTokens = 8192;
        Assert.Equal(8192, await Limits().ContextTokensAsync(default));
    }

    [Fact]
    public async Task Falls_back_to_the_configured_size_when_the_server_cant_say() =>
        Assert.Equal(16384, await Limits().ContextTokensAsync(default));

    [Fact]
    public async Task Asks_again_after_a_minute_in_case_the_server_was_retuned()
    {
        var limits = Limits();
        _llama.ContextTokens = 8192;
        await limits.ContextTokensAsync(default);

        _llama.ContextTokens = 16384;
        Assert.Equal(8192, await limits.ContextTokensAsync(default));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(16384, await limits.ContextTokensAsync(default));
    }

    sealed class Clock : TimeProvider
    {
        DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
