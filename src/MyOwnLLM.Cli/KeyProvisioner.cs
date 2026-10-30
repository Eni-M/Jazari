namespace MyOwnLLM.Cli;

/// <summary>
/// First run without a key: ask the gateway for one (its add-key command), save it as JAZARI_KEY, and use it right away.
/// </summary>
public sealed class KeyProvisioner(IProcessRunner runner, Action<string> saveKey)
{
    /// <param name="rejected">The gateway refused <paramref name="currentKey"/> (401): replace it with a new one.</param>
    public async Task<string?> EnsureKeyAsync(string? currentKey, JazariConfig? config, CancellationToken ct, bool rejected = false)
    {
        if (currentKey is not null && !rejected) return currentKey;
        if (config is null) return null;

        var (exitCode, output) = await runner.RunAsync(config.GatewayExe, ["add-key"], ct);
        var key = exitCode == 0 ? output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("mollm_")) : null;
        if (key is null) return null;

        saveKey(key);
        return key;
    }

    /// <summary>Saves the key for this process and as a user environment variable (what <c>setx JAZARI_KEY</c> does).</summary>
    public static void SaveToEnvironment(string key)
    {
        Environment.SetEnvironmentVariable("JAZARI_KEY", key);
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("JAZARI_KEY", key, EnvironmentVariableTarget.User);
    }
}
