namespace MyOwnLLM.Gateway.Llama;

/// <summary>
/// The context size prompts must fit in. Each PC's tuning picks its own (8k on small RAM), so it comes from llama-server,
/// asked again every minute in case the server restarted with new settings; the configured size is the fallback.
/// </summary>
public sealed class ModelLimits(ILlamaClient llama, GatewayOptions options, TimeProvider? time = null)
{
    static readonly TimeSpan AskEvery = TimeSpan.FromMinutes(1);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    int? _known;
    DateTimeOffset _askedAt;

    public async Task<int> ContextTokensAsync(CancellationToken ct)
    {
        if (_known is { } known && _time.GetUtcNow() - _askedAt < AskEvery) return known;
        if (await llama.ContextTokensAsync(ct) is { } fresh) (_known, _askedAt) = (fresh, _time.GetUtcNow());
        return _known ?? options.ContextTokens;
    }
}
