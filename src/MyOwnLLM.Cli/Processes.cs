using System.ComponentModel;
using System.Diagnostics;

namespace MyOwnLLM.Cli;

public interface IProcessRunner
{
    /// <summary>Runs a program to completion and returns its exit code and standard output.</summary>
    Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct);

    /// <summary>The same, from a chosen folder and with extra environment variables: a build finds global.json from its folder.</summary>
    Task<(int ExitCode, string Output)> RunInAsync(string exe, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Starts a long-running program in the background (no window); it keeps running after jazari exits.</summary>
    void StartDetached(string exe, string[] args, string logFile);
}

public sealed class ProcessRunner : IProcessRunner
{
    // Own folder, like StartDetached: the gateway's add-key must not read an appsettings.json from the folder jazari runs in.
    public Task<(int ExitCode, string Output)> RunAsync(string exe, string[] args, CancellationToken ct) =>
        RunInAsync(exe, args, Path.GetDirectoryName(exe)!, new Dictionary<string, string>(), ct);

    public async Task<(int ExitCode, string Output)> RunInAsync(string exe, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (name, value) in environment) psi.Environment[name] = value;
        try
        {
            using var p = Process.Start(psi)!;
            await using var killOnCancel = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }); // a timed-out git mustn't linger
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
        // Full path for cmd, and the server starts in its own folder: jazari runs inside arbitrary (maybe cloned, untrusted)
        // folders, and a bare name or that folder as working directory could pick up a planted cmd.exe or git.exe.
        // No path or argument is ever written into the command line itself: cmd would act on a & or ^ in a folder name
        // (C:\Users\R&D\...) and expand %...% even inside quotes. Each goes through an environment variable the child
        // inherits instead; cmd expands it once, never re-reads the result for %, and the quotes keep & | < > literal.
        string[] parts = [exe, .. args, logFile];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Contains('"')) throw new ArgumentException($"can't pass a quote to {Path.GetFileName(exe)}: {parts[i]}");
            Environment.SetEnvironmentVariable($"JAZARI_ARG{i}", parts[i]);
        }
        string Ref(int i) => parts[i].Length == 0 ? "\"\"" : $"\"%JAZARI_ARG{i}%\"";
        var command = $"{string.Join(' ', Enumerable.Range(0, parts.Length - 1).Select(Ref))} > {Ref(parts.Length - 1)} 2>&1";
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Process.Start(new ProcessStartInfo(cmd, $"/d /v:off /c \"{command}\"")
        {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
    }
}
