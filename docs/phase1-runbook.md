# Phase 1 runbook

## Start

1. **Model server** (keep this window open):
   ```powershell
   .\scripts\start-llama.ps1
   ```
   Wait for `model loaded` (~6 s).
2. **Gateway** (second window):
   ```powershell
   dotnet run --project src/MyOwnLLM.Gateway --no-launch-profile
   ```
   Listens on `http://127.0.0.1:5080` only.
3. **API key** (once):
   ```powershell
   dotnet run --project src/MyOwnLLM.Gateway --no-launch-profile -- add-key
   ```
   Copy the `mollm_...` line and store it as a user environment variable `MYOWNLLM_KEY`. Only its hash is kept in `%LOCALAPPDATA%\MyOwnLLM\keys.json`.
4. **CLI** (build once, then put `src\MyOwnLLM.Cli\bin\Debug\net10.0` on your PATH or call it directly):
   ```powershell
   myllm ask "How do I register a scoped service?"
   myllm ask "What should I fix next?" --repo C:\Users\enim\EduCore
   myllm status --repo C:\Users\enim\EduCore     # "Where are we left at?"
   myllm feedback ix-4 up
   ```
   Add `--stream` to `ask` to see the answer as it's written. Ctrl+C stops the model mid-answer.

Data lives in `%LOCALAPPDATA%\MyOwnLLM\` (`keys.json`, `myownllm.db` with project notes and the feedback log). Nothing leaves the laptop.

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
| `myllm status` on EduCore (after 2 questions) | **30.9 s** (target ≤ 120 s) |
| Question queued behind a background state-note update | 75.9 s (waits for the update to finish) |
| Client gives up mid-answer | llama-server logs `cancel task`; next request answered in 0.7 s |
| llama-server stopped | CLI prints `model server not reachable at http://127.0.0.1:8080/`, exit 1 |
| Feedback on a known / unknown id | `feedback saved` / `404 Not Found`, exit 1 |

## Known limits

- A chat question that arrives while the background state-note update is running waits for it (up to ~60 s). The update can't be paused mid-generation.
- `ask` without `--repo` has no project knowledge; with `--repo` it uses the state note only (no code search yet; that comes in a later plan).
- Answers can be generic or outdated (one used the old `ConfigureServices` style). Specialization is Phase 3.
