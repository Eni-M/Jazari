using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Cli;

/// <summary>A GPU llama.cpp can use, as <c>llama-server --list-devices</c> reports it.</summary>
public sealed record GpuDevice(string Id, string Name, int FreeMiB);

/// <summary>What the PC has, read once at start; the fingerprint says when tuning must run again.</summary>
public sealed record Machine(string CpuName, int LogicalCores, long RamBytes);

/// <summary>
/// Finds the fastest llama-server settings for whatever PC Jazari runs on, by measuring instead of guessing:
/// CPU at a few thread counts, and each GPU that has room for the whole model, each with flash attention off and on.
/// The winner is the one that finishes a typical turn soonest: reading a 2,000-token prompt and writing 300 tokens.
/// Anything that fails (llama-bench missing or blocked by Windows, a GPU driver crash) just leaves that option out,
/// and with nothing measured the safe default stays.
/// </summary>
public sealed partial class DeviceTuner(IProcessRunner runner, Ui ui, Func<string, long?>? fileSize = null)
{
    const int TypicalPromptTokens = 2000, TypicalAnswerTokens = 300;
    readonly Func<string, long?> _fileSize = fileSize ?? (path => File.Exists(path) ? new FileInfo(path).Length : null);

    /// <summary>What a start runs on: the saved settings, or safe CPU settings until the user asks for a measurement. Never measures.</summary>
    public static Tuning ForStart(string path, Machine machine) => Tuning.Load(path) ?? FallBack(machine);

    /// <summary>Measures this PC (a few minutes) and saves the result. Only when the user asked: it takes the whole machine.</summary>
    public async Task<Tuning> MeasureAsync(JazariConfig config, Machine machine, string path, CancellationToken ct)
    {
        var gpus = await ListGpusAsync(config.LlamaServer, ct) ?? [];
        var fingerprint = Fingerprint(machine, gpus, config, _fileSize);
        if (!ui.Live) ui.Metadata("Finding the fastest settings for this PC. This takes a few minutes.");
        var tuning = await TuneAsync(config, machine, gpus, fingerprint, ct);
        try { tuning.Save(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ui.Metadata($"Couldn't save the settings ({e.Message}); Jazari stays on the ones it has."); }
        return tuning;
    }

    /// <summary>
    /// Safe CPU settings, before anything is measured or for this one start after the tuned ones failed. Not saved: one
    /// bad start (a port in use, a moment without VRAM) mustn't replace what was measured; <c>jazari tune</c> measures again.
    /// </summary>
    public static Tuning FallBack(Machine machine) => Tuning.Default("", machine.LogicalCores, machine.RamBytes);

    async Task<Tuning> TuneAsync(JazariConfig config, Machine machine, List<GpuDevice> gpus, string fingerprint, CancellationToken ct)
    {
        var best = Tuning.Default(fingerprint, machine.LogicalCores, machine.RamBytes);
        var bench = Path.Combine(Path.GetDirectoryName(config.LlamaServer)!, "llama-bench.exe");
        if (_fileSize(bench) is null) return best;

        // CPU threads first, then flash attention only at the best thread count: a slow laptop measures 4 options, not 6.
        var results = new List<(Tuning Settings, double Seconds)>();
        var threads = string.Join(',', ThreadCounts(machine.LogicalCores));
        // Each GPU gets the whole model when it fits, else as many layers as fit: the T480's 2 GB MX150 reads prompts
        // about 38% faster with 24 of 40 layers, at the same answer speed.
        var modelMiB = (int)((_fileSize(config.Model) ?? 0) >> 20);
        var usable = gpus.Select(g => (Gpu: g, Layers: LayersThatFit(g.FreeMiB, modelMiB, best.ContextTokens))).Where(g => g.Layers >= MinUsefulLayers).ToList();

        string[] steps = [$"cpu {threads} threads", "flash attention", .. usable.Select(g => (best with { Device = g.Gpu.Id, DeviceName = g.Gpu.Name }).ShortName()), "pick fastest"];
        var done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step()
        {
            var left = done == 0 ? "a few minutes" : $"about {Math.Max(1, Math.Ceiling(clock.Elapsed.TotalMinutes / done * (steps.Length - 1 - done))):0} min left";
            ui.TuneProgress(steps, done, left, redraw: done > 0);
            done++;
        }

        Step();
        if (!ui.Live) ui.Metadata($"Measuring the CPU ({machine.CpuName.Trim()}) at {threads} threads.");
        var cpu = await BenchAsync(bench, config.Model, best, ["-dev", "none", "-ngl", "0", "-t", threads], "0", ct);
        results.AddRange(cpu);
        var bestThreads = cpu.Count > 0 ? cpu.MinBy(r => r.Item2).Item1.Threads : best.Threads;
        Step();
        if (!ui.Live) ui.Metadata($"Measuring flash attention at {bestThreads} threads.");
        results.AddRange(await BenchAsync(bench, config.Model, best, ["-dev", "none", "-ngl", "0", "-t", bestThreads.ToString()], "1", ct));

        foreach (var (gpu, layers) in usable)
        {
            Step();
            if (!ui.Live) ui.Metadata($"Measuring the GPU {gpu.Name}{(layers < ModelLayers ? $" with {layers} of {ModelLayers} layers" : "")}.");
            var ngl = layers >= ModelLayers ? 99 : layers;
            results.AddRange(await BenchAsync(bench, config.Model, best with { Device = gpu.Id, GpuLayers = ngl, DeviceName = gpu.Name },
                ["-dev", gpu.Id, "-ngl", ngl.ToString(), "-t", bestThreads.ToString()], "0,1", ct));
        }
        Step();

        if (results.Count == 0) { ui.Metadata("Nothing could be measured, so Jazari uses safe CPU settings."); return best; }
        foreach (var (option, turn) in results.OrderBy(r => r.Seconds))
            ui.Metadata($"  {Describe(option),-34} reads {option.PromptPerSecond,5:0} tok/s, writes {option.AnswerPerSecond,5:0.0} tok/s, typical turn {turn,4:0} s");
        var (winner, seconds) = results.MinBy(r => r.Seconds);
        ui.Metadata($"Best: {Describe(winner)}, reading {winner.PromptPerSecond:0} tok/s, writing {winner.AnswerPerSecond:0.0} tok/s " +
                    $"(a typical turn takes about {seconds:0} s).");
        return winner;
    }

    public static string Describe(Tuning t) =>
        (t.Device == "none" ? $"CPU, {t.Threads} threads" : t.GpuLayers < ModelLayers ? $"GPU {t.Device}, {t.GpuLayers} layers" : $"GPU {t.Device}")
        + (t.FlashAttention ? ", flash attention" : "");

    const int ModelLayers = 40; // Granite 4.1 3B
    const int MinUsefulLayers = 8; // fewer moves too little work to the GPU to pay for the copying

    /// <summary>
    /// How many of the model's layers fit in a GPU's free memory, each with its share of the context cache, after
    /// 400 MiB for llama.cpp's working buffers. All of them when the whole model fits.
    /// </summary>
    public static int LayersThatFit(int freeMiB, int modelMiB, int contextTokens)
    {
        var cacheMiB = contextTokens * 80 / 1024; // 80 KiB a token on Granite 4.1 3B
        if (freeMiB >= modelMiB + cacheMiB + 512) return ModelLayers;
        var perLayer = (double)(modelMiB + cacheMiB) / ModelLayers;
        return Math.Clamp((int)((freeMiB - 400) / perLayer), 0, ModelLayers);
    }

    /// <summary>Half, three quarters and all of the logical cores: memory speed, not core count, usually sets the pace.</summary>
    public static IEnumerable<int> ThreadCounts(int logicalCores) =>
        new[] { logicalCores / 2, logicalCores * 3 / 4, logicalCores }.Where(t => t > 0).Distinct();

    async Task<List<(Tuning, double)>> BenchAsync(string bench, string model, Tuning template, string[] device, string flashAttention, CancellationToken ct)
    {
        string[] args = ["-m", model, .. device, "-fa", flashAttention, "-p", "512", "-n", "64", "-r", "2", "-o", "json"];
        // A crash part-way (a GPU driver failing on flash attention, say) still leaves the rows measured before it.
        var (_, output) = await runner.RunAsync(bench, args, ct);
        return Results(output, template);
    }

    /// <summary>Pairs llama-bench's prompt and answer rows that share settings into one measured option each.</summary>
    public static List<(Tuning, double)> Results(string json, Tuning template)
    {
        List<(int Threads, bool Fa, bool Prompt, double Speed)> rows = [];
        try
        {
            using var doc = ParseRows(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var fa = r.GetProperty("flash_attn") is { ValueKind: JsonValueKind.True or JsonValueKind.False } b ? b.GetBoolean() : r.GetProperty("flash_attn").GetInt32() != 0;
                rows.Add((r.GetProperty("n_threads").GetInt32(), fa, r.GetProperty("n_prompt").GetInt32() > 0, r.GetProperty("avg_ts").GetDouble()));
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return []; }

        return rows.GroupBy(r => (r.Threads, r.Fa))
            .Select(g => (Settings: g.Key, Prompt: g.FirstOrDefault(r => r.Prompt).Speed, Answer: g.FirstOrDefault(r => !r.Prompt).Speed))
            .Where(g => g.Prompt > 0 && g.Answer > 0)
            .Select(g => (template with
            {
                Threads = g.Settings.Threads, FlashAttention = g.Settings.Fa, PromptPerSecond = g.Prompt, AnswerPerSecond = g.Answer,
            }, TypicalPromptTokens / g.Prompt + TypicalAnswerTokens / g.Answer))
            .ToList();
    }

    /// <summary>llama-bench writes its JSON array row by row; when it dies part-way, the array is cut after the last whole row.</summary>
    static JsonDocument ParseRows(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException)
        {
            var start = json.IndexOf('[');
            var lastRow = json.LastIndexOf('}');
            if (start < 0 || lastRow < start) throw;
            return JsonDocument.Parse(json[start..(lastRow + 1)] + "]");
        }
    }

    /// <summary>The GPUs, or null when the listing failed or took longer than 30 s (a hung driver mustn't block startup).</summary>
    async Task<List<GpuDevice>?> ListGpusAsync(string server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var (exit, output) = await runner.RunAsync(server, ["--list-devices"], timeout.Token);
            return exit == 0 ? ParseDevices(output) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Lines like "  Vulkan0: Intel(R) Iris(R) Xe Graphics (16211 MiB, 15443 MiB free)".</summary>
    public static List<GpuDevice> ParseDevices(string output) =>
        DeviceLine().Matches(output).Select(m => new GpuDevice(m.Groups[1].Value, m.Groups[2].Value.Trim(), int.Parse(m.Groups[3].Value))).ToList();

    [GeneratedRegex(@"^\s*(\w+?\d+):\s*(.+?)\s*\(\d+ MiB,\s*(\d+) MiB free\)", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    /// <summary>Changes when the CPU, RAM, GPUs, llama.cpp build or model change: then the old measurements no longer hold.</summary>
    public static string Fingerprint(Machine machine, IEnumerable<GpuDevice> gpus, JazariConfig config, Func<string, long?> fileSize)
    {
        string FileStamp(string path) => $"{Path.GetFileName(path)}:{fileSize(path)}";
        // The compute code lives in the ggml DLLs next to llama-server.exe, which an update can change on their own.
        var folder = Path.GetDirectoryName(config.LlamaServer)!;
        var dlls = Directory.Exists(folder) ? Directory.GetFiles(folder, "ggml*.dll").Order(StringComparer.OrdinalIgnoreCase).Select(FileStamp) : [];
        var text = string.Join('|', machine.CpuName.Trim(), machine.LogicalCores, machine.RamBytes >> 30,
            string.Join(',', gpus.Select(g => g.Name)), FileStamp(config.LlamaServer), FileStamp(config.Model), string.Join(',', dlls));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public static Machine ReadMachine()
    {
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return new(cpu ?? "unknown CPU", Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }
}
