using MyOwnLLM.Cli;

namespace MyOwnLLM.Gateway.Tests;

public class ProcessRunnerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jazari proc").FullName; // space on purpose

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    [Fact]
    public async Task StartDetached_runs_the_program_and_writes_its_log()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = Path.Combine(_dir, "logs", "out.log");

        new ProcessRunner().StartDetached(PowerShell, ["-NoProfile", "-Command", "Write-Output jazari-ok"], log);

        for (var i = 0; i < 50 && !(File.Exists(log) && (await ReadShared(log)).Contains("jazari-ok")); i++) await Task.Delay(100);
        Assert.Contains("jazari-ok", await ReadShared(log));
    }

    [Fact]
    public async Task StartDetached_runs_from_the_programs_own_folder_not_the_current_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = Path.Combine(_dir, "cwd.log");

        new ProcessRunner().StartDetached(PowerShell, ["-NoProfile", "-Command", "(Get-Location).Path"], log);

        // Up to 30 s: a cold Windows PowerShell on a busy CI runner once took over 10 s to print anything.
        for (var i = 0; i < 300 && (await ReadShared(log)).Trim().Length == 0; i++) await Task.Delay(100);
        Assert.Equal(Path.GetDirectoryName(PowerShell)!.TrimEnd('\\'), (await ReadShared(log)).Trim().TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public async Task StartDetached_passes_cmd_special_characters_through_untouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var log = Path.Combine(_dir, "R&D %OS% ^x", "out.log"); // a folder name cmd would split at & and expand
        var marker = Path.Combine(_dir, "injected.txt");

        new ProcessRunner().StartDetached(PowerShell, ["-NoProfile", "-Command", $"Write-Output 'R&D%OS%^x&echo x>{marker}'"], log);

        for (var i = 0; i < 100 && !(await ReadShared(log)).Contains("R&D"); i++) await Task.Delay(100);
        Assert.Contains($"R&D%OS%^x&echo x>{marker}", await ReadShared(log));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RunAsync_returns_output_and_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (code, output) = await new ProcessRunner().RunAsync(Environment.GetEnvironmentVariable("ComSpec")!, ["/c", "echo", "hi"], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("hi", output);
    }

    [Fact]
    public async Task RunAsync_runs_from_the_programs_own_folder_not_the_current_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var comspec = Environment.GetEnvironmentVariable("ComSpec")!;

        var (_, output) = await new ProcessRunner().RunAsync(comspec, ["/c", "cd"], CancellationToken.None);

        Assert.Equal(Path.GetDirectoryName(comspec)!.TrimEnd('\\'), output.Trim().TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public async Task RunInAsync_runs_in_the_folder_it_was_given_with_its_variables()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (_, output) = await new ProcessRunner().RunInAsync(Environment.GetEnvironmentVariable("ComSpec")!, ["/c", "cd && echo %JAZARI_TEST%"], _dir,
            new Dictionary<string, string> { ["JAZARI_TEST"] = "set" }, CancellationToken.None);

        Assert.Contains(_dir, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set", output);
    }

    static async Task<string> ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await new StreamReader(s).ReadToEndAsync();
    }
}
