using MyOwnLLM.Cli;
using MyOwnLLM.Cli.Terminal;

namespace MyOwnLLM.Gateway.Tests;

public class DeviceTunerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari tune").FullName;
    readonly StringWriter _output = new();
    static readonly Machine Laptop = new("Intel(R) Core(TM) i5-1135G7 @ 2.40GHz", 8, 32L << 30);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Devices_are_read_from_list_devices()
    {
        var gpus = DeviceTuner.ParseDevices("""
            Available devices:
              Vulkan0: Intel(R) Iris(R) Xe Graphics (16211 MiB, 15443 MiB free)
              Vulkan1: NVIDIA GeForce MX150 (2048 MiB, 1900 MiB free)
            """);

        Assert.Equal([new("Vulkan0", "Intel(R) Iris(R) Xe Graphics", 15443), new("Vulkan1", "NVIDIA GeForce MX150", 1900)], gpus);
        Assert.Empty(DeviceTuner.ParseDevices("Available devices:\n  (none)\n"));
    }

    [Theory]
    [InlineData(8, new[] { 4 })]
    [InlineData(4, new[] { 2 })]
    [InlineData(2, new[] { 1 })]
    [InlineData(1, new[] { 1 })]
    public void Threads_stay_at_half_so_the_laptop_stays_usable(int cores, int[] expected) =>
        Assert.Equal(expected, DeviceTuner.ThreadCounts(cores));

    [Theory]
    [InlineData(15443, 40)] // Iris Xe, shared memory: the whole model
    [InlineData(1900, 18)]  // T480's MX150, 2 GB: part of it
    [InlineData(500, 1)]    // too little to be worth measuring
    public void Gpu_layers_are_what_fits_in_free_memory(int freeMiB, int layers) =>
        Assert.Equal(layers, DeviceTuner.LayersThatFit(freeMiB, modelMiB: 2002, contextTokens: 16384));

    [Fact]
    public async Task A_small_gpu_is_measured_with_part_of_the_model()
    {
        var runner = new FakeRunner { Devices = "Available devices:\n  Vulkan0: NVIDIA GeForce MX150 (2048 MiB, 1900 MiB free)\n" };

        await Tuner(runner).MeasureAsync(Config(), Laptop, Path.Combine(_dir, "t.json"), CancellationToken.None);

        var gpuRun = Assert.Single(runner.Runs, r => r.Contains("Vulkan0"));
        Assert.Equal("18", gpuRun[Array.IndexOf(gpuRun, "-ngl") + 1]);
    }

    [Fact]
    public void Default_settings_fit_the_ram()
    {
        Assert.Equal((4, 16384, 2048), (Tuning.Default("x", 8, 16L << 30) is var big ? (big.Threads, big.ContextTokens, big.CacheRamMiB) : default));
        Assert.Equal((2, 8192, 1024), (Tuning.Default("x", 4, 8L << 30) is var small ? (small.Threads, small.ContextTokens, small.CacheRamMiB) : default));
    }

    [Fact]
    public void Server_args_carry_the_tuned_settings()
    {
        var args = new Tuning("f", "Vulkan0", 4, 99, true, 16384, 2048).ServerArgs("m.gguf");

        Assert.Equal("Vulkan0", args[Array.IndexOf(args, "-dev") + 1]);
        Assert.Equal("99", args[Array.IndexOf(args, "-ngl") + 1]);
        Assert.Equal("on", args[Array.IndexOf(args, "-fa") + 1]);
        Assert.Equal("16384", args[Array.IndexOf(args, "-c") + 1]);
    }

    [Fact]
    public async Task Measuring_saves_what_a_start_then_uses()
    {
        var runner = new FakeRunner();
        var path = Path.Combine(_dir, "tuning.json");

        var measured = await Tuner(runner).MeasureAsync(Config(), Laptop, path, CancellationToken.None);

        Assert.Equal(("none", 6, true), (measured.Device, measured.Threads, measured.FlashAttention));
        Assert.Equal(measured, DeviceTuner.ForStart(path, Laptop));
    }

    [Fact]
    public void A_start_without_a_measurement_runs_on_safe_settings_and_measures_nothing()
    {
        var path = Path.Combine(_dir, "none yet.json");

        Assert.Equal(DeviceTuner.FallBack(Laptop), DeviceTuner.ForStart(path, Laptop));
        Assert.False(File.Exists(path)); // and nothing was saved: the update's question is still open
    }

    [Fact]
    public async Task Each_measurement_measures_again()
    {
        var runner = new FakeRunner();
        var path = Path.Combine(_dir, "tuning.json");
        var tuner = Tuner(runner);

        await tuner.MeasureAsync(Config(), Laptop, path, CancellationToken.None);
        var afterFirst = runner.Benches;
        await tuner.MeasureAsync(Config(), Laptop, path, CancellationToken.None);

        Assert.True(runner.Benches > afterFirst);
    }

    [Fact]
    public async Task Live_tuning_shows_every_step_in_one_block()
    {
        var output = new StringWriter();
        var tuner = new DeviceTuner(new FakeRunner { Devices = "Available devices:\n  Vulkan0: Intel(R) Iris(R) Xe Graphics (16211 MiB, 15443 MiB free)\n" },
            new Ui(output, new Theme(ColorMode.TrueColor, unicode: true), 100), _ => 2L << 30);

        await tuner.MeasureAsync(Config(), Laptop, Path.Combine(_dir, "t.json"), CancellationToken.None);

        var text = System.Text.RegularExpressions.Regex.Replace(output.ToString(), "\e\\[[0-9;]*[A-Za-z]", "");
        Assert.Contains("Tuning for this PC", text);
        Assert.Contains("✓ cpu 4 threads", text);
        Assert.Contains("✓ flash attention", text);
        Assert.Contains("✓ iris xe", text);
        Assert.Contains("● pick fastest", text);
    }

    [Fact]
    public async Task Nothing_measurable_keeps_the_safe_default()
    {
        var runner = new FakeRunner { BenchExit = -1 }; // e.g. blocked by Windows Application Control
        var tuning = await Tuner(runner)
            .MeasureAsync(Config(), Laptop, Path.Combine(_dir, "t.json"), CancellationToken.None);

        Assert.Equal(("none", 4), (tuning.Device, tuning.Threads));
        Assert.Contains("safe CPU settings", _output.ToString());
    }

    [Fact]
    public void A_bench_that_crashed_part_way_keeps_the_rows_it_measured()
    {
        // llama-bench died on the second option: the array is cut after the first option's rows.
        var cut = BenchJson[..BenchJson.IndexOf("{\"n_threads\":6", StringComparison.Ordinal)];

        var results = DeviceTuner.Results(cut, Tuning.Default("f", 8, 16L << 30));

        Assert.Equal(4, Assert.Single(results).Item1.Threads);
    }

    [Fact]
    public async Task A_failed_device_listing_still_measures_the_cpu()
    {
        var runner = new FakeRunner { DevicesExit = 1 };

        var tuning = await Tuner(runner).MeasureAsync(Config(), Laptop, Path.Combine(_dir, "t.json"), CancellationToken.None);

        Assert.Equal("none", tuning.Device);
        Assert.True(runner.Benches > 0);
    }

    [Fact]
    public void Falling_back_saves_nothing()
    {
        var path = Path.Combine(_dir, "t.json");

        var safe = DeviceTuner.FallBack(Laptop);

        Assert.Equal("none", safe.Device);
        Assert.False(File.Exists(path));
    }

    static JazariConfig Config() => new(@"C:\J", @"C:\J\gateway.exe", @"C:\J\llama\llama-server.exe", @"C:\J\m.gguf");

    /// <summary>Pretends the server, llama-bench and a 2 GB model are installed, without writing any files.</summary>
    static long? Installed(string path) => path.EndsWith(".gguf") ? 2L << 30 : 1024;

    DeviceTuner Tuner(IProcessRunner runner) => new(runner, new Ui(_output, new Theme(ColorMode.None, true), 100), Installed);

    sealed class FakeRunner : IProcessRunner
    {
        public int Benches { get; private set; }
        public int BenchExit { get; init; }
        public int DevicesExit { get; init; }
        public string Devices { get; init; } = "Available devices:\n  (none)\n";
        public List<string[]> Runs { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct)
        {
            if (args is ["--list-devices"]) return Task.FromResult((DevicesExit, Devices));
            Benches++;
            Runs.Add(args);
            return Task.FromResult((BenchExit, BenchOutput ?? (BenchExit == 0 ? BenchJson : "Access is denied.")));
        }

        public string? BenchOutput { get; init; }

        public void StartDetached(string exe, string[] args, string logFile) { }
    }

    /// <summary>CPU at 4 and 6 threads; 6 threads with flash attention finishes a typical turn soonest.</summary>
    internal const string BenchJson = """
        [
          {"n_threads":4,"flash_attn":false,"n_prompt":512,"n_gen":0,"avg_ts":40.0},
          {"n_threads":4,"flash_attn":false,"n_prompt":0,"n_gen":64,"avg_ts":9.0},
          {"n_threads":6,"flash_attn":true,"n_prompt":512,"n_gen":0,"avg_ts":55.0},
          {"n_threads":6,"flash_attn":true,"n_prompt":0,"n_gen":64,"avg_ts":10.0}
        ]
        """;
}
