using System.ComponentModel;
using System.Diagnostics;

namespace MyOwnLLM.Cli;

public interface IProcessRunner
{
    /// <summary>Runs a program to completion and returns its exit code and standard output.</summary>
    Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct);

    /// <summary>Starts a long-running program in the background (no window); it keeps running after jazari exits.</summary>
    void StartDetached(string exe, string[] args, string logFile);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await output);
        }
        catch (Win32Exception e) { return (-1, e.Message); } // program missing
    }

    public void StartDetached(string exe, string[] args, string logFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        // cmd does the log redirection. The command line is passed raw: cmd doesn't understand .NET's \" escaping, and
        // /c "<whole command>" is its own quoting rule. ShellExecute (hidden) keeps the child from inheriting this
        // terminal's handles, so it outlives jazari and never holds a pipe open.
        var command = $"\"{exe}\" {string.Join(' ', args.Select(Quote))} > \"{logFile}\" 2>&1";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
