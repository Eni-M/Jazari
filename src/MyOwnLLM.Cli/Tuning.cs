using System.Text.Json;

namespace MyOwnLLM.Cli;

/// <summary>
/// The llama-server settings measured best on this PC by <see cref="DeviceTuner"/>, saved next to config.json.
/// <see cref="Fingerprint"/> names the hardware, llama.cpp build and model they were measured with; when any of
/// those change, Jazari measures again.
/// </summary>
public sealed record Tuning(
    string Fingerprint, string Device, int Threads, int GpuLayers, bool FlashAttention, int ContextTokens, int CacheRamMiB,
    double PromptPerSecond = 0, double AnswerPerSecond = 0, string? DeviceName = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "tuning.json");

    /// <summary>
    /// What to run before anything is measured, or when measuring can't run: CPU only, three threads in four (the
    /// T480's best was 6 of 8), and a context and prompt cache sized to the RAM.
    /// </summary>
    public static Tuning Default(string fingerprint, int logicalCores, long ramBytes) =>
        new(fingerprint, "none", Math.Max(1, logicalCores * 3 / 4), 0, false, ContextFor(ramBytes), ramBytes < 12L << 30 ? 1024 : 2048);

    /// <summary>16k tokens of context cost about 1.25 GB of cache on Granite 4.1 3B; below 12 GB of RAM, 8k leaves room for everything else.</summary>
    public static int ContextFor(long ramBytes) => ramBytes < 12L << 30 ? 8192 : 16384;

    /// <summary>For the welcome screen: "Intel(R) Iris(R) Xe Graphics · flash attention · 14.9 tok/s".</summary>
    public string Summary(char dot) =>
        (Device == "none" ? $"CPU, {Threads} threads" : $"{DeviceName ?? Device}{(GpuLayers is > 0 and < 99 ? $", {GpuLayers} layers" : "")}")
        + (FlashAttention ? $" {dot} flash attention" : "") + (AnswerPerSecond > 0 ? $" {dot} {AnswerPerSecond:0.0} tok/s" : "");

    public string[] ServerArgs(string model) =>
    [
        "-m", model, "-dev", Device, "-ngl", GpuLayers.ToString(), "-t", Threads.ToString(), "--poll", "0",
        "-fa", FlashAttention ? "on" : "off", "-c", ContextTokens.ToString(), "--cache-ram", CacheRamMiB.ToString(),
        "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", "8080",
    ];

    public static Tuning? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Tuning>(File.ReadAllText(path), JsonSerializerOptions.Web) : null; }
        catch (JsonException) { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
    }
}
