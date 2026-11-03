# Whole project review — 2026-11-03

Branch at start: `fix/taller-window-rows` (ceab39c). Scope: everything tracked under `src/`, `tests/`, `eval/`, `lawbook/`, `scripts/`, `.github/`, plus the README/runbook claims those files back. Out of scope: `research_notes/`, `reports/` (prose), `spike/` (throwaway experiments; `spike/review-test` is a deliberately buggy fixture for `/review`).

## Plan

Each area is read end to end, tracing the real call flow, not skimmed. A finding counts only if a concrete input or state produces wrong behaviour; style nits are left out. Fixes are made only for confirmed findings, each with a test that fails without the fix.

### 1. Gateway (`src/MyOwnLLM.Gateway`, ~900 lines)
- **Auth** — `ApiKeyStore`: key comparison (constant time?), missing or empty key, header parsing, file permissions on the key file.
- **Endpoints** — `ChatEndpoints`: request validation at the trust boundary (null/empty messages, huge bodies, unknown roles), SSE framing, cancellation when the client disconnects, error status codes, what leaks into error bodies.
- **Queue** — `RequestQueue`: concurrency correctness (lost wakeups, slot leaks on cancellation/exceptions, ordering), disposal.
- **Llama** — `LlamaClient`: HTTP timeouts, stream parsing of partial/malformed SSE lines, `[DONE]`, cancellation, connection reuse.
- **Prompt** — `PromptBuilder`, `ConversationSearch`: token budget arithmetic (off-by-one, negative budgets), truncation order, injection of retrieved text.
- **State** — `StateNoteUpdater`, `GitSummary`, project state files: process invocation of git (argument quoting, hangs, stderr deadlocks), concurrent writes, path handling.
- **Storage / LawBook** — file IO atomicity, encoding, missing files, lookup correctness.
- **Program.cs** — binding address (localhost only?), DI lifetimes, config defaults.

### 2. CLI (`src/MyOwnLLM.Cli`, ~3,500 lines)
- **Entry & args** — `Program`, `CliArgs`: every subcommand, unknown flags, exit codes.
- **Server lifecycle** — `ServerLauncher`, `Processes`, `DeviceTuner`, `Tuning`: process start/kill, orphaned processes, port races, PID reuse, tuning results persistence, failure paths.
- **Gateway client** — `GatewayClient`, `SseChunk`: stream parsing, cancellation (Ctrl+C), retries, error surfacing.
- **Session** — `Session`, `InputLoop`, `Completions`: slash commands (`/copy /read /review /edit /undo`), state across turns, history bounds.
- **File tools** — `FileEdit`, `ProjectFiles`, `ProjectReview`: path traversal outside the project, writing only after confirmation, undo correctness, binary/large files, encodings and line endings preserved, ignore rules, resumable review state.
- **Update** — `UpdateCheck`: network failure handling, version comparison.
- **Terminal UI** — `PromptEditor`, `Screen`, `Ui`, `Markdown`, `MarkdownStream`, `TextWidth`, `Theme`, `Blocks`, `TextPacer`, `ThinkingIndicator`, `Styled`: wide/combining chars, resize, wrapping, cursor math, escape-sequence injection from model output (terminal control codes in streamed text), streaming markdown fences split across chunks.

### 3. Launcher (`src/Jazari.Launcher`)
- `LaunchPlan`/`Program`: path quoting, argument passing, exit code propagation.

### 4. Scripts (`scripts/*.ps1`)
- `install-jazari.ps1`, `update-jazari.ps1`: download integrity (hash checks), error handling (`$ErrorActionPreference`), destructive operations (git reset/clean on user changes), PATH edits, idempotence.

### 5. Python (`eval/`, `lawbook/`)
- `run_eval.py`, `recall.py`, `score.py`, `compare.py`, `gateway.py`: scoring correctness, division by zero, file handling, HTTP error handling.
- `lawbook/build.py`: parsing correctness and the output the gateway's `LawBook` reads (format contract between them).

### 6. Tests and CI
- Baseline: does everything pass now? Tests that can't fail (assert nothing, swallowed exceptions), timing-dependent flakiness, CI covering what it claims (lawbook tests are not run in CI?).

### 7. Cross-cutting
- Secrets in the repo, `.gitignore` coverage of keys/models/logs, docs claims that no longer match code.

## Baseline

- `dotnet test`: 562 passed.
- `lawbook` Python tests: 33 passed.
- `eval` Python tests: 1 import error, from the uncommitted, in-progress `eval/test_run_eval.py` (it tests `csharp_int` and review false-alarm counting, which `run_eval.py` doesn't have yet). That's work in progress, not a defect, and was left untouched, as was the untracked `spike/review-test/StoreDbContext.cs`.

## Findings

Ranked by impact. "Fixed" ones have a test that failed before the fix.

### Fixed

1. **`jazari review <path that doesn't exist>` crashes with a stack trace** (`src/MyOwnLLM.Cli/ProjectReview.cs`). `ProjectFiles.Load` walks a folder that isn't there and throws `DirectoryNotFoundException`. The one-off command has no handler for it, so the process dies with an unhandled exception. In a session it showed "Something went wrong". `/read` already checked for this; `/review` didn't. It now says `Can't find <path>.` Test: `A_review_of_a_path_that_isnt_there_says_so`.
2. **A project inside a folder named `.azure`, `.kube`, `.aws`, `.ssh`, `.gnupg` or `.git` reads as empty** (`src/MyOwnLLM.Cli/ProjectFiles.cs`). `IsSecret` was given each file's absolute path, so a folder *above* the project counted as a secret folder and every file was skipped: `/read` said "Nothing readable there" and `/review` "Nothing to review". It now checks the path relative to the project, so the project's own secret folders are still skipped. Test: `Only_folders_inside_the_project_can_mark_files_as_secret`.
3. **The gateway's `add-key` told users to set `MYOWNLLM_KEY`** (`src/MyOwnLLM.Gateway/Program.cs`). The CLI reads `JAZARI_KEY` first and `MYOWNLLM_KEY` only as a fallback. Someone following the message while an old `JAZARI_KEY` exists would have the new key ignored and keep getting 401. The message now names `JAZARI_KEY`. `KeyProvisioner` and `eval/gateway.py` read only the first line, the key itself, so neither is affected.
4. **CI never ran the law book tests** (`.github/workflows/ci.yml`). Only `eval/test_*.py` was discovered, and `lawbook/test_build.py` (33 tests on the parser the gateway's law book depends on) ran nowhere. CI now runs them too.

### Not fixed: worth doing, needs a decision or a download

5. **The llama.cpp zip isn't checksum-pinned** (`scripts/update-jazari.ps1`, `Repair-Llama`). The model is verified against `modelSha256`, but the llama.cpp release zip is only checked by the version string its exe prints. A tampered zip that prints the right version would be installed and run. Fix: add `llamaSha256` to `scripts/jazari-manifest.json` and check it the way `Test-Model` does. That needs the real hash of `llama-b11982-bin-win-vulkan-x64.zip`, which means downloading it, so it was left for you.
6. **The gateway answers 500 with an empty body to malformed requests** (`src/MyOwnLLM.Gateway/Endpoints/ChatEndpoints.cs`). A chat body without `messages` throws `NullReferenceException`, and an invalid `json_schema` string throws `JsonException` in `LlamaClient`. Neither is mapped in `StatusFor`. The gateway only listens on 127.0.0.1 behind a key, and the CLI always sends both correctly, so this only bites someone scripting against the API. Fix if that becomes a use: return 400 for both.

### Low: noted, not worth a change now

7. The recovery advice for a rejected key (`Welcome.cs` `KeyRejected`, `Session.Fail` on 401) says `dotnet run --project src/MyOwnLLM.Gateway -- add-key` "in your MyOwnLLM folder". That works while the clone exists, but every other message points to `jazari update`. Startup already replaces a rejected key on its own, so this advice only shows when that failed.
8. `GatewayOptions.LawBookFile`'s comment says the law book is "installed by jazari update". It isn't yet: that's the last task of `docs/superpowers/plans/2026-11-02-law-book.md`.
9. `lawbook/build.py`: `_split_unit` always closes a split code block with ```` ``` ````, even when it opened with `~~~`. `_read` would raise `ValueError` (from `os.path.commonpath`) for a snippet path on another drive. The pinned Microsoft sources hit neither.
10. `eval/recall.py` builds its filler conversation from the repo's own C#, so the "deep" conversation changes with every commit. Recall scores from different commits aren't strictly comparable.
11. `.superpowers/` is in `.gitignore`, but one file under it is still tracked (committed before the rule).

### Checked and sound

- **Auth**: keys stored only as SHA-256, constant-time comparison, atomic key-file writes, gateway bound to 127.0.0.1.
- **Process launching**: `StartDetached` passes every argument through environment variables, so `&`, `^` and `%` in paths can't inject into cmd. `cmd.exe` and `git.exe` come from full paths, never the current folder.
- **git hardening**: fsmonitor, signature checks, clean/smudge/process filters, external diff and textconv are all switched off when Jazari runs git in an untrusted repo, in both the gateway and the CLI.
- **`/edit`**: writes only after a yes, refuses paths outside the project or through links and junctions, keeps the file's encoding and BOM, refuses non-UTF text, and `/undo` refuses if the file changed since.
- **Terminal**: every path that prints model, server or file text goes through `ConsoleText.Sanitize` (C0/C1 controls, ESC and bidi overrides removed), and `/copy` copies exactly what was shown.
- **RequestQueue**: priorities, superseding, cancellation while waiting and shutdown are all handled without leaking slots.
- **Updater**: atomic folder swaps with rollback, a gateway and app built together, a kept copy of the updater that only a parsing one replaces, and the model's checksum verified.
- **Tests**: the waits are polls with generous bounds, not bare sleeps; no test can pass by asserting nothing.

## Verification after the fixes

- `dotnet test`: 564 passed (562 plus the 2 new tests, both seen failing first).
- `python -m unittest discover -s lawbook -p "test_*.py"`: 33 passed (the command CI now runs).
- `eval`: unchanged, apart from the in-progress test file noted above.
