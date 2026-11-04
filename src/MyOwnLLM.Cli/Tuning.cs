using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyOwnLLM.Cli;

/// <summary>
/// The llama-server settings measured best on this PC by <see cref="DeviceTuner"/>, saved next to config.json.
/// <see cref="Fingerprint"/> names the hardware, llama.cpp build and model they were measured with. Measuring only
/// ever happens when the user asks: <c>jazari tune</c>, or yes to the question at the end of <c>jazari update</c>.
/// </summary>
public sealed partial record Tuning(
    string Fingerprint, string Device, int Threads, int GpuLayers, bool FlashAttention, int ContextTokens,
    double PromptPerSecond = 0, double AnswerPerSecond = 0, string? DeviceName = null, string? ModelFile = null)
{
    /// <summary>
    /// Prompts kept in RAM for reuse, with at most 8 checkpoints per conversation (-ctxcp 8). On Granite 4.0-H Tiny a
    /// checkpoint is 55 MiB of Mamba state, one about every 512 tokens up to llama-server's default of 32, and a saved
    /// prompt with 32 of them was 1.9 GB: the server reached 9.9 GB on a 16 GB laptop. A retried question used only the
    /// latest checkpoint and still took about 1 s with 8. Not part of the fingerprint, so no PC re-tunes for it.
    /// </summary>
    public const int CacheRamMiB = 1024;

    /// <summary>
    /// llama-server's spacing between checkpoints at b11982, written out: later builds keep 8192 as the default but may
    /// not, and every measurement here was made with it. Change it only with a recall/ run (docs/HANDOFF-task-6.md).
    /// </summary>
    public const int CheckpointMinStep = 8192;

    /// <summary>
    /// After 15 minutes without a request llama-server unloads the model and gives back its ~8.7 GB; the next question
    /// loads it again (seconds on an SSD). /health and /props don't wake it, so jazari's checks and the gateway's context
    /// size polling leave it asleep (checked on b11982 with a small model).
    /// </summary>
    public const int SleepIdleSeconds = 900;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "tuning.json");

    /// <summary>
    /// What to run before anything is measured, or when measuring can't run: CPU only, half the logical cores (see
    /// DeviceTuner.ThreadCounts), and a context sized to the RAM.
    /// </summary>
    public static Tuning Default(string fingerprint, int logicalCores, long ramBytes) =>
        new(fingerprint, "none", Math.Max(1, logicalCores / 2), 0, false, ContextFor(ramBytes));

    /// <summary>16k tokens of context are fine with 12 GB of RAM or more; below that, 8k leaves room for a 4 GB model and everything else.</summary>
    public static int ContextFor(long ramBytes) => ramBytes < 12L << 30 ? 8192 : 16384;

    /// <summary>For the welcome screen: "Intel(R) Iris(R) Xe Graphics · flash attention · 14.9 tok/s".</summary>
    public string Summary(char dot) =>
        (Device == "none" ? $"CPU, {Threads} threads" : $"{DeviceName ?? Device}{(GpuLayers is > 0 and < 99 ? $", {GpuLayers} layers" : "")}")
        + (FlashAttention ? $" {dot} flash attention" : "") + (AnswerPerSecond > 0 ? $" {dot} {AnswerPerSecond:0.0} tok/s" : "");

    /// <summary>For the line under the prompt: "iris xe", "mx150", "cpu 6 threads".</summary>
    public string ShortName() => Device == "none" ? $"cpu {Threads} threads" : Vendor().Replace(DeviceName ?? Device, "").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\(R\)|\(TM\)|\b(Intel|NVIDIA|AMD|GeForce|Radeon|Graphics)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Vendor();

    /// <param name="keyFile">The key only the gateway knows (ServerLauncher.EnsureLlamaKey): without it any program on the
    /// PC, or a web page through the browser, could skip the gateway's key and queue. CORS stays closed to other sites.</param>
    public string[] ServerArgs(string model, string keyFile) =>
    [
        "-m", model, "-dev", Device, "-ngl", GpuLayers.ToString(), "-t", Threads.ToString(), "--poll", "0",
        "-fa", FlashAttention ? "on" : "off", "-c", ContextTokens.ToString(), "--cache-ram", CacheRamMiB.ToString(), "-ctxcp", "8",
        "-cms", CheckpointMinStep.ToString(), "--sleep-idle-seconds", SleepIdleSeconds.ToString(),
        "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", "8080",
        "--api-key-file", keyFile, "--cors-origins", "localhost", "--no-cors-credentials",
    ];

    /// <summary>
    /// The router's models, one section per lane, with today's settings for the fast one. The smart one runs on the CPU at
    /// this PC's thread count: the MX150 wouldn't start Qwen3 at 16k context, and this tuning was measured on another model.
    /// No smart model means a fast lane alone.
    /// </summary>
    public string PresetText(string fastModel, string? deepModel)
    {
        string Section(string name, string model, string device, int layers, int threads, bool flash) => $"""
            [{name}]
            model = {model.Replace('\\', '/')}
            device = {device}
            n-gpu-layers = {layers}
            threads = {threads}
            flash-attn = {(flash ? "on" : "off")}


            """;
        var shared = $"""
            version = 1

            [*]
            ctx-size = {ContextTokens}
            poll = 0
            cache-ram = {CacheRamMiB}
            ctx-checkpoints = 8
            checkpoint-min-step = {CheckpointMinStep}
            parallel = 1
            cache-reuse = 256


            """;
        return shared + Section("fast", fastModel, Device, GpuLayers, Threads, FlashAttention)
            + (deepModel is null ? "" : Section("deep", deepModel, "none", 0, Threads, true));
    }

    /// <summary>llama-server as a router: the port stays up while the gateway swaps models, and nothing loads unless asked.</summary>
    public static string[] RouterArgs(string presetFile, string keyFile) =>
    [
        "--models-preset", presetFile, "--models-max", "1", "--no-models-autoload", "--sleep-idle-seconds", SleepIdleSeconds.ToString(),
        "--no-webui", "--host", "127.0.0.1", "--port", "8080", "--api-key-file", keyFile, "--cors-origins", "localhost", "--no-cors-credentials",
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
