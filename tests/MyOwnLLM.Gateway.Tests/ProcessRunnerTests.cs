using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class ProcessRunnerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari proc").FullName; // space on purpose

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task StartDetached_runs_the_program_and_writes_its_log()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = Path.Combine(_dir, "logs", "out.log");
        var comspec = Environment.GetEnvironmentVariable("ComSpec")!;

        new ProcessRunner().StartDetached(comspec, ["/c", "echo", "jazari-ok"], log);

        for (var i = 0; i < 50 && !(File.Exists(log) && (await ReadShared(log)).Contains("jazari-ok")); i++) await Task.Delay(100);
        Assert.Contains("jazari-ok", await ReadShared(log));
    }

    [Fact]
    public async Task StartDetached_runs_from_the_programs_own_folder_not_the_current_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = Path.Combine(_dir, "cwd.log");
        var comspec = Environment.GetEnvironmentVariable("ComSpec")!;

        new ProcessRunner().StartDetached(comspec, ["/c", "cd"], log);

        for (var i = 0; i < 50 && (await ReadShared(log)).Trim().Length == 0; i++) await Task.Delay(100);
        Assert.Equal(Path.GetDirectoryName(comspec)!.TrimEnd('\\'), (await ReadShared(log)).Trim().TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public async Task RunAsync_returns_output_and_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (code, output) = await new ProcessRunner().RunAsync(Environment.GetEnvironmentVariable("ComSpec")!, ["/c", "echo", "hi"], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("hi", output);
    }

    static async Task<string> ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await new StreamReader(s).ReadToEndAsync();
    }
}
