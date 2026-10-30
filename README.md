# Jazari

*jah-ZAH-ree* — a lightweight, local coding assistant for ASP.NET Core, C#, .NET, networking, system design and security.

Jazari runs entirely on your own machine: IBM Granite 4.1 3B on llama.cpp, behind a small ASP.NET Core gateway, with a monochrome terminal client. It's named after Ismail al-Jazari, the 12th-century engineer often called the father of robotics.

## Quick start

Needs Windows and the .NET 10 SDK. In your clone of this repo:

```powershell
.\scripts\install-jazari.ps1   # once per machine: downloads what's missing, installs the `jazari` command
jazari                         # in any folder, from any terminal
```

Install somewhere else with `.\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari`.

```powershell
jazari stop                    # shut the background servers down
```

The first `jazari` creates your API key (saved as `JAZARI_KEY`) and starts the model server and gateway in the background. `jazari stop` shuts them down.

Full setup, commands and measured performance: [docs/phase1-runbook.md](docs/phase1-runbook.md).
Design and plans: [docs/superpowers/](docs/superpowers/).
