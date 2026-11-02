using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>
/// The llama-server settings measured best on this PC by <see cref="DeviceTuner"/>, saved next to config.json.
/// <see cref="Fingerprint"/> names the hardware, llama.cpp build and model they were measured with. Measuring only
/// ever happens when the user asks: <c>jazari tune</c>, or yes to the question at the end of <c>jazari update</c>.
/// </summary>
public sealed partial record Tuning(
    string Fingerprint, string Device, int Threads, int GpuLayers, bool FlashAttention, int ContextTokens, int CacheRamMiB,
    double PromptPerSecond = 0, double AnswerPerSecond = 0, string? DeviceName = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "tuning.json");

    /// <summary>
    /// What to run before anything is measured, or when measuring can't run: CPU only, half the logical cores (see
    /// DeviceTuner.ThreadCounts), and a context and prompt cache sized to the RAM.
    /// </summary>
    public static Tuning Default(string fingerprint, int logicalCores, long ramBytes) =>
        new(fingerprint, "none", Math.Max(1, logicalCores / 2), 0, false, ContextFor(ramBytes), ramBytes < 12L << 30 ? 1024 : 2048);

    /// <summary>16k tokens of context cost about 1.25 GB of cache on Granite 4.1 3B; below 12 GB of RAM, 8k leaves room for everything else.</summary>
    public static int ContextFor(long ramBytes) => ramBytes < 12L << 30 ? 8192 : 16384;

    /// <summary>For the welcome screen: "Intel(R) Iris(R) Xe Graphics · flash attention · 14.9 tok/s".</summary>
    public string Summary(char dot) =>
        (Device == "none" ? $"CPU, {Threads} threads" : $"{DeviceName ?? Device}{(GpuLayers is > 0 and < 99 ? $", {GpuLayers} layers" : "")}")
        + (FlashAttention ? $" {dot} flash attention" : "") + (AnswerPerSecond > 0 ? $" {dot} {AnswerPerSecond:0.0} tok/s" : "");

    /// <summary>For the line under the prompt: "iris xe", "mx150", "cpu 6 threads".</summary>
    public string ShortName() => Device == "none" ? $"cpu {Threads} threads" : Vendor().Replace(DeviceName ?? Device, "").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\(R\)|\(TM\)|\b(Intel|NVIDIA|AMD|GeForce|Radeon|Graphics)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Vendor();

    public string[] ServerArgs(string model) =>
    [
        "-m", model, "-dev", Device, "-ngl", GpuLayers.ToString(), "-t", Threads.ToString(), "--poll", "0",
        "-fa", FlashAttention ? "on" : "off", "-c", ContextTokens.ToString(), "--cache-ram", CacheRamMiB.ToString(),
        "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", "8080",
    ];

    public static Tuning? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Tuning>(File.ReadAllText(path), JsonSerializerOptions.Web) : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; } // unreadable: measure again
    }

    /// <summary>Written to a temporary file first, so a start cut short never leaves half a file behind.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
