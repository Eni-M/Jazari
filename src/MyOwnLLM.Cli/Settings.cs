namespace MyOwnLLM.Cli;

/// <summary>Where the gateway is and which key to use; JAZARI_* first, the old MYOWNLLM_* names as a fallback.</summary>
public sealed record Settings(string? Key, Uri GatewayUrl)
{
    public static Settings FromEnvironment(Func<string, string?> getEnv)
    {
        string? Read(string name) => getEnv("JAZARI_" + name) is { Length: > 0 } v ? v : getEnv("MYOWNLLM_" + name) is { Length: > 0 } old ? old : null;
        return new(Read("KEY"), new Uri(Read("URL") ?? "http://127.0.0.1:5080"));
    }
}
