# Jazari runbook

## Start

1. **Model server** (keep this window open):
   ```powershell
   .\scripts\start-llama.ps1
   ```
   Wait for `model loaded` (~6 s).
2. **Gateway** (second window):
   ```powershell
   dotnet run --project src/MyOwnLLM.Gateway
   ```
   Listens on `http://127.0.0.1:5080` only.
3. **API key** (once):
   ```powershell
   dotnet run --project src/MyOwnLLM.Gateway -- add-key
   ```
   Copy the `mollm_...` line and store it as a user environment variable `JAZARI_KEY`. Only its hash is kept in `%LOCALAPPDATA%\MyOwnLLM\keys.json`. (`MYOWNLLM_KEY` still works.)
4. **Install the `jazari` command** (once, and again after updates):
   ```powershell
   .\scripts\install-jazari.ps1
   ```
   Open a new terminal afterwards.

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

The `jazari` command installs anywhere with the script above, but answers need a model server and gateway on that PC too (the 2 GB model file and ~5 GB RAM). Reaching this laptop's gateway over the network isn't supported: keys are personal and the gateway listens on localhost only.

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
