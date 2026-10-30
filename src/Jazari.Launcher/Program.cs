using System.Diagnostics;
using Jazari.Launcher;

// The `jazari` command. It stays tiny and rarely changes, so `jazari update` can replace everything else while it waits.
var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jazari", "config.json");
var config = File.Exists(configPath) ? File.ReadAllText(configPath) : null;

// The child owns Ctrl+C (the app stops an answer with it); the launcher just keeps waiting for it.
Console.CancelKeyPress += (_, e) => e.Cancel = true;

var plan = LaunchPlan.Decide(args, config, File.Exists);
switch (plan.Kind)
{
    case "fail":
        Console.Error.WriteLine(plan.Message);
        return 1;
    case "update":
        var code = Run(plan.Exe, plan.Args, plan.WorkingDirectory);
        if (code != 0) return code;
        // Reopen Jazari on the fresh version, read from the config the updater just wrote.
        var reopen = LaunchPlan.Decide([], File.Exists(configPath) ? File.ReadAllText(configPath) : null, File.Exists);
        if (reopen.Kind == "fail") { Console.Error.WriteLine(reopen.Message); return 1; }
        return Run(reopen.Exe, reopen.Args, reopen.WorkingDirectory);
    default:
        return Run(plan.Exe, plan.Args, plan.WorkingDirectory);
}

static int Run(string exe, string[] args, string? workingDirectory)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false }; // same console, no redirection
    if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var child = Process.Start(psi)!;
    child.WaitForExit();
    return child.ExitCode;
}
