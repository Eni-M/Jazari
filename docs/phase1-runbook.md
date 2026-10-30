# Jazari runbook

## Start

Needs Windows and the .NET 10 SDK.

1. **Install** (once per machine, and again after pulling updates), from anywhere in your clone:
   ```powershell
   .\scripts\install-jazari.ps1
   ```
   It downloads llama.cpp and the Granite model if they're missing, builds the gateway into `%LOCALAPPDATA%\Jazari\gateway`, installs the `jazari` command, and writes `%APPDATA%\Jazari\config.json` with this machine's paths. Nothing needs editing on a new PC.
2. **Run** `jazari` in any folder. The first run creates an API key through the gateway and saves it as your `JAZARI_KEY` user variable. Every run starts llama-server and the gateway in the background if they aren't running (about 10–25 s while the model loads); after that it opens instantly.
3. **Stop** the background servers to free their RAM: `jazari stop`. Their logs are in `%LOCALAPPDATA%\MyOwnLLM\logs`.

Manual start (if you'd rather run the servers yourself): `.\scripts\start-llama.ps1`, and in a second window `dotnet run --project src/MyOwnLLM.Gateway`.

## Use

```powershell
jazari                       # interactive session in the current folder
jazari ask "How do I register a scoped service?"
jazari status                # "Where are we left at?" for the current folder
jazari feedback ix-4 up
```

In the session: `/help`, `/status`, `/model`, `/clear`, `/retry`, `/details`, `/exit`. Enter sends; Shift+Enter, Alt+Enter or Ctrl+J add a new line; ↑/↓ browse history; Tab completes `/` commands; Ctrl+C stops an answer (twice at the prompt exits).

Answers stream by default. `jazari ask "…" --no-stream` prints the answer when it's complete. Piped output (`jazari ask "…" > out.txt`) and `NO_COLOR=1` give plain text with no color codes.

Data lives in `%LOCALAPPDATA%\MyOwnLLM\` (`keys.json`, `myownllm.db` with project notes and the feedback log) and `%APPDATA%\Jazari\history`. Nothing leaves the laptop.

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
