# Jazari runbook

## Start

Needs Windows, git and the .NET 10 SDK.

1. **Install** (once per machine), from anywhere in your clone:
   ```powershell
   .\scripts\install-jazari.ps1
   ```
   To put Jazari's files somewhere else (any drive or folder, spaces are fine): `.\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari`. The command then lives in `<InstallDir>\bin`, which is added to your PATH, and later installs remember the folder.
   It keeps its own copy of GitHub `main` in `<InstallDir>\source`, copies the model and llama.cpp from your clone's `spike` folder if they're there and downloads them if not, builds the gateway and the app into `<InstallDir>`, installs the `jazari` command, and writes `%APPDATA%\Jazari\config.json` with this machine's paths. Nothing needs editing on a new PC.

   **Update** with `jazari update`, from any folder. It makes the private copy match GitHub `main`, checks every component (`✓` fine, `!` repaired, `ERROR` failed), and reopens Jazari. A component that fails keeps its previous version. Your own clones are never touched. Jazari shows `update available, run jazari update` when GitHub has something newer. To update the `jazari` command itself, run the installer again.
2. **Run** `jazari` in any folder. The first run creates an API key through the gateway and saves it as your `JAZARI_KEY` user variable. Every run starts llama-server and the gateway in the background if they aren't running (about 10–25 s while the model loads); after that it opens instantly.
3. **Stop** the background servers to free their RAM: `jazari stop`. Their logs are in `%LOCALAPPDATA%\MyOwnLLM\logs`.

Manual start (if you'd rather run the servers yourself): `.\scripts\start-llama.ps1`, and in a second window `dotnet run --project src/MyOwnLLM.Gateway`.

## Use

```powershell
jazari                       # interactive session in the current folder
jazari ask "How do I register a scoped service?"
jazari status                # "Where are we left at?" for the current folder
jazari feedback ix-4 up
jazari doctor                # checks the install, servers, key and logs, and says what to fix; changes nothing
jazari --plain               # plain lines, no colors or symbols, for screen readers (or set JAZARI_PLAIN=1)
```

In the session: `/help`, `/status`, `/model`, `/deep [question]` (the smarter model, up to about 3 minutes, see the README), `/docs <question>` (the .NET docs passage, without the model), `/save [file]` (the conversation as Markdown, never overwriting a file), `/clear`, `/retry`, `/details`, `/exit`. A mistyped command gets a "did you mean". Paths with spaces go in quotes: `/edit "My File.cs" make it async`. `/edit` on a `.cs` file asks the model once more, with the errors, when its change breaks the C# syntax, and warns before asking you if it still does. Edits whose spacing or blank lines differ a little from the file still apply, as long as they match one place only.

**Per project.** An `AGENTS.md` in the project (or a folder above it, up to the repository root) goes with every question and edit, its first 2,000 characters. A `.jazariignore` leaves files out of `/read` and `/review`: one pattern a line, `fixtures/` for a folder anywhere, `*.generated.cs` for a kind of file, `src/legacy/*` for a path. `/review`'s report lists the parts it couldn't review, and accepts quotes the model wrapped in backticks or numbered. Enter sends; Shift+Enter, Alt+Enter or Ctrl+J add a new line; ↑/↓ browse history; Tab completes `/` commands; Ctrl+C stops an answer (twice at the prompt exits).

Answers stream by default. `jazari ask "…" --no-stream` prints the answer when it's complete. Piped output (`jazari ask "…" > out.txt`) and `NO_COLOR=1` give plain text with no color codes.

**In your IDE.** The gateway speaks the OpenAI chat API, so VS Code (BYOK custom endpoint), Continue, Rider and Visual Studio can use Jazari for chat: base URL `http://127.0.0.1:5080/v1`, API key your `JAZARI_KEY`, model `jazari`. Chat and edits work; agent modes need tool calling, which Jazari doesn't offer. Requests from web pages are refused (403), so a site can't reach it through your browser.

Data lives in `%LOCALAPPDATA%\MyOwnLLM\` (`keys.json`, `myownllm.db` with project notes and the feedback log, and `llama-server.key`) and `%APPDATA%\Jazari\history`. Nothing leaves the laptop.

**The model server.** llama-server answers only the gateway: jazari starts it with the key in `llama-server.key`, which the gateway sends, and it refuses other web pages. After 15 minutes without a question it unloads the model and gives back its memory (about 8.7 GB); the next question loads it again, which takes a few seconds. `jazari stop` ends both servers at once.

## Terminal setup (recommended)

Jazari can't change your terminal's font or background. For the intended look in Windows Terminal: font **JetBrains Mono** (or Cascadia Code), background **#0B0B0B**.

## Other PCs

Clone the repo, run `.\scripts\install-jazari.ps1`, type `jazari`. Each PC runs its own model (2 GB download, ~5 GB RAM while running). Reaching another PC's gateway over the network isn't supported: keys are personal and the gateway listens on localhost only.

## PC tuning checklist (do these yourself)

- Don't put `llama-server` in Windows Efficiency mode.
- Try turbo off / "Maximum processor state" 99 % and compare with `spike\bin\llama-bench.exe`; keep whichever is better.
- Exclude `*.gguf` and the `spike\models` folder from Defender scanning and Search indexing.
- Raise the laptop or use a cooling pad; heat alone moved speed ~10 %.
- Close heavy background apps during long jobs.

## Measured on this laptop (2026-10-30)

| Check | Result |
|---|---|
| RAM, llama-server + gateway | 4.72 GB + 0.07 GB (cap: 7 GB) |
| Short C# question, cold | 8.0 s |
| `jazari status` on EduCore | 31–69 s (target ≤ 120 s; longer when a background note update runs first) |
| 2-item list answer, streamed | 14.3 s |
| Client gives up mid-answer | llama-server logs `cancel task`; next request answered in 0.7 s |
| llama-server stopped | `ERROR` block: `model server not reachable at http://127.0.0.1:8080/` |

## Known limits

- A question that arrives while the background state-note update is running waits for it (up to ~60 s).
- Jazari knows the project through its state note and recent git history only; it doesn't search your code yet.
- Answers can be generic or outdated (one used the old `ConfigureServices` style). Specialization is Phase 3.
