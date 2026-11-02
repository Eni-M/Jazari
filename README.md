# Jazari

*jah-ZAH-ree* — a lightweight, local coding assistant for ASP.NET Core, C#, .NET, networking, system design and security.

Jazari runs entirely on your own machine: IBM Granite 4.0-H Tiny on llama.cpp, behind a small ASP.NET Core gateway, with a monochrome terminal client. It's named after Ismail al-Jazari, the 12th-century engineer often called the father of robotics.

## Quick start

Needs Windows, git and the .NET 10 SDK. In your clone of this repo:

```powershell
.\scripts\install-jazari.ps1   # once per machine: downloads what's missing, installs the `jazari` command
jazari                         # in any folder, from any terminal
jazari update                  # later: match GitHub main, check everything, repair what's broken, then ask whether to tune
jazari tune                    # measure the fastest settings for this PC (a few minutes); it never runs on its own
```

Install somewhere else with `.\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari`. Jazari tells you when an update is available.

Inside a session:

| Command | What it does |
|---|---|
| `/copy [n]` | Copies a code block of the last answer. Blocks are numbered on screen. |
| `/read <path>` | Reads a file or folder for the next questions, up to about 5,000 tokens. |
| `/review [path]` | Reviews a whole project, riskiest files first, and writes a report. Also `jazari review [path]`. It can be stopped and carries on; later runs only re-read what changed. |
| `/edit <file> <what>` | Asks for a change, shows it, and writes it only after you say yes. `/undo` puts it back. |

Reviews come from a small local model, so check each finding. It reads about 30 tokens/s: about 3 to 7 hours for a project with 350,000 tokens of real code.

```powershell
jazari stop                    # shut the background servers down
```

The first `jazari` creates your API key (saved as `JAZARI_KEY`) and starts the model server and gateway in the background. `jazari stop` shuts them down.

Full setup, commands and measured performance: [docs/phase1-runbook.md](docs/phase1-runbook.md).
Design and plans: [docs/superpowers/](docs/superpowers/).
