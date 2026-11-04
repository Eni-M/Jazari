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
| `/read <path>` | Reads a file or folder for the next questions, up to about 5,000 tokens, riskiest files first. C# files that don't fit go as an outline of their types and signatures, so the model still knows what exists and where. |
| `/review [path]` | Reviews a whole project, riskiest files first, and writes a report. Also `jazari review [path]`. It can be stopped and carries on; later runs only re-read what changed. `/review --changed` reads only what changed since the last commit, as whole methods: minutes, not hours. Reports mark findings new, still there or resolved. |
| `/edit <file> <what>` | Asks for a change, shows it, and writes it only after you say yes. `/undo` puts it back. A `.cs` change that breaks the syntax is flagged first. Quote paths with spaces. |
| `/docs <question>` | Shows the .NET docs passage for a question, with its page, without asking the model. |
| `/save [file]` | Saves the conversation as Markdown in the project. |

Reviews come from a small local model, so check each finding. It reads about 30 tokens/s: about 3 to 7 hours for a project with 350,000 tokens of real code.

```powershell
jazari stop                    # shut the background servers down
jazari doctor                  # what's wrong and what to do about it; changes nothing
jazari ask "..." --json        # one JSON object (answer, id, sources, cut_off, seconds, error), for scripts
```

Answers can draw on the law book, excerpts of Microsoft's .NET docs searched offline: the answer then names the page in a `source` row, and `/docs` shows the passage itself. `jazari update` installs it once the manifest names it (`lawBookUrl`, `lawBookSha256`); until then Jazari answers without it. Sources and licences (text CC BY 4.0, code MIT): [lawbook/NOTICE.md](lawbook/NOTICE.md).

`jazari --plain` (or `JAZARI_PLAIN=1`) gives plain lines for screen readers. IDEs can use Jazari for chat as an OpenAI-compatible endpoint: see the runbook.

The first `jazari` creates your API key (saved as `JAZARI_KEY`) and starts the model server and gateway in the background. `jazari stop` shuts them down.

Full setup, commands and measured performance: [docs/phase1-runbook.md](docs/phase1-runbook.md).
Design and plans: [docs/superpowers/](docs/superpowers/).
