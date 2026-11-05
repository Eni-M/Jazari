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

Install somewhere else with `.\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari`. Jazari tells you when an update is available. A model download that stops half-way carries on from where it stopped at the next `jazari update`. To take Jazari off the PC, close every jazari window and run `.\scripts\uninstall-jazari.ps1` from the clone: it asks before deleting the model and your data.

Inside a session, just ask. When a question names a file or a C# type, method or property of the project (`why does RefundAsync fail?`, `explain OrderService.cs`), Jazari finds it and sends it with that question, up to about 2,000 tokens, and a `read` row above the answer names the files. It looks only inside a git repository, with the same rules as `/read`.

| Command | What it does |
|---|---|
| `/copy [n]` | Copies a code block of the last answer. Blocks are numbered on screen. |
| `/read <path>` | Reads a file or folder for the next questions, up to about 5,000 tokens, riskiest files first; a file too big to fit is skipped, and smaller ones after it still go. C# files that don't fit go as an outline of their types and signatures, so the model still knows what exists and where. |
| `/review [path]` | Reviews a whole project, riskiest files first, and writes a report. Also `jazari review [path]`. It can be stopped and carries on; later runs only re-read what changed. `/review --changed` reads only what changed since the last commit, as whole methods: minutes, not hours. Reports mark findings new, still there or resolved. Before the model reads anything, rules find secrets (cloud keys, tokens, passwords in connection strings) and a few C# patterns (SQL built from values, async void, an empty catch of everything, accepting any TLS certificate, MD5 or SHA1); secrets are masked in the report. `/review` builds and runs nothing. |
| `/edit <file> <what>` | Asks for a change, shows it, and writes it only after you say yes. `/undo` puts it back. A `.cs` change that breaks the syntax is flagged first. Quote paths with spaces. |
| `/docs <question>` | Shows the .NET docs passage for a question, with its page, without asking the model. |
| `/save [file]` | Saves the conversation as Markdown in the project. |
| `/deep [question]` | Asks more carefully, when it's worth the wait: up to about 3 minutes, with each stage shown. It uses the smart model when one is installed; none is by default, so it uses the same model and spends the extra time on checking. It compiles any C# it wrote against the .NET reference packs (the code never runs), and repairs a block that doesn't compile once. The answer ends with a `check` row: `checked: code compiles`, `checked: 1 error fixed`, `not checked: no code` or `could not fix: <error>`. Alone, it asks the last question again, with the first answer still above to compare. The next plain question goes back to the fast model. |
| `/deep /edit <file> <what>` | `/edit` with the smarter model. A `.cs` change is also checked against the whole project, and only errors it adds count. Loading a project runs its build scripts, so Jazari asks first, and again whenever the project's build files change; no means the syntax check alone. |

Reviews come from a small local model, so check each finding. It reads about 30 tokens/s: about 3 to 7 hours for a project with 350,000 tokens of real code.

```powershell
jazari stop                    # shut the background servers down
jazari doctor                  # what's wrong and what to do about it; changes nothing
jazari ask "..." --json        # one JSON object (answer, id, sources, cut_off, seconds, error, check), for scripts
jazari ask "..." --deep        # the smarter model, as /deep
```

C# in plain answers is compiled too, without running it or slowing the answer. If a block calls something that doesn't exist, a `code` row says so under the answer, and `Ctrl+D` asks again with `/deep`. Blocks that use NuGet packages show no such row, because they can't be judged here.

Answers can draw on the law book, excerpts of Microsoft's .NET docs searched offline: the answer then names the page in a `source` row, and `/docs` shows the passage itself. `jazari update` installs it once the manifest names it (`lawBookUrl`, `lawBookSha256`); until then Jazari answers without it. Sources and licences (text CC BY 4.0, code MIT): [lawbook/NOTICE.md](lawbook/NOTICE.md).

`jazari --plain` (or `JAZARI_PLAIN=1`) gives plain lines for screen readers. IDEs can use Jazari for chat as an OpenAI-compatible endpoint: see the runbook.

The first `jazari` creates your API key (saved as `JAZARI_KEY`) and starts the model server and gateway in the background. `jazari stop` shuts them down.

Full setup, commands and measured performance: [docs/phase1-runbook.md](docs/phase1-runbook.md).
Design and plans: [docs/superpowers/](docs/superpowers/).
