# `jazari update` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `jazari update` syncs Jazari's private source copy with GitHub `main`, health-checks and repairs every component, and reopens Jazari in the same window; launches show `update available, run jazari update` when GitHub is ahead.

**Architecture:** The `jazari` command becomes a tiny launcher (.NET global tool, new project `src/Jazari.Launcher`) that runs the real app from `<InstallDir>\app\jazari.exe` or, for `update`, runs `<InstallDir>\source\scripts\update-jazari.ps1` and waits. The updater (PowerShell) does sync → checks → per-component safe swaps. `install-jazari.ps1` becomes a one-time bootstrap that clones the private source copy, installs the launcher, then runs the updater.

**Tech Stack:** .NET 10, PowerShell 5.1, git, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-jazari-update-design.md`

## Global Constraints

- Source of truth: GitHub `main` of the repo the installer was run from (`git remote get-url origin`).
- Private source copy: `<InstallDir>\source`; app: `<InstallDir>\app\jazari.exe`; gateway: `<InstallDir>\gateway`; downloads: `<InstallDir>\llama`, `<InstallDir>\models`.
- Never read from or write to any other clone at run time (`home` in the config is no longer used by the app).
- Pinned values live in `scripts/jazari-manifest.json`: `{"llamaRelease":"b11982","llamaZip":"llama-b11982-bin-win-vulkan-x64.zip","modelFile":"granite-4.1-3b-Q4_K_M.gguf","modelUrl":"https://huggingface.co/ibm-granite/granite-4.1-3b-GGUF/resolve/main/granite-4.1-3b-Q4_K_M.gguf","modelSha256":"662b0626cd58f443baea23559b469df6576a81d349649c59413b36a9fb32eb29"}`.
- Every replacement is prepared in a temp location and swapped in only after its check passes; a failed component keeps its previous version; `installedCommit` is written only when every step succeeded.
- Launch notice text exactly: `update available, run jazari update` (muted). Silent when up to date, offline, or `repoUrl` missing. Git runs with `GIT_TERMINAL_PROMPT=0`.
- Updater output lines: `✓ <thing> OK`, `! <thing> repaired`, `ERROR <thing>: <reason>`, final `Jazari is up to date (<7-char commit>)`.
- Windows only.

## Review Focus

1. **Offline or no GitHub access during `jazari update`** → `ERROR source: could not reach GitHub (…)`, nothing else changes, Jazari reopens on the old version. (Task 5 manual check)
2. **Download interrupted (Ctrl+C or network drop) during a model/llama repair** → the old file stays usable; the next update cleans the temp file and retries. (Task 5 manual check)
3. **`jazari` run when `<InstallDir>\app\jazari.exe` is missing** → launcher prints `Jazari isn't fully installed. Run: jazari update` and exits 1, no exception. (Task 3)
4. **Launch check when git is missing, offline or slow** → no notice, no delay at startup, no error. (Task 2)
5. **Ctrl+C inside a session started by the launcher** → handled by the app (stops the answer); the launcher stays alive and doesn't orphan the app. (Task 3)

---

### Task 1: Config fields, manifest, and rejected-key repair

**Files:**
- Modify: `src/MyOwnLLM.Cli/JazariConfig.cs`, `src/MyOwnLLM.Cli/KeyProvisioner.cs`, `src/MyOwnLLM.Cli/Program.cs`
- Create: `scripts/jazari-manifest.json` (values from Global Constraints)
- Test: `tests/MyOwnLLM.Gateway.Tests/JazariConfigTests.cs`, `tests/MyOwnLLM.Gateway.Tests/KeyProvisionerTests.cs`

**Interfaces:**
- Produces: `public sealed record JazariConfig(string Home, string GatewayExe, string LlamaServer, string Model, string? RepoUrl = null, string? InstallDir = null, string? SourceDir = null, string? InstalledCommit = null)` (JSON names camelCase: `repoUrl`, `installDir`, `sourceDir`, `installedCommit`).
- Produces: `KeyProvisioner.EnsureKeyAsync(string? currentKey, JazariConfig? config, CancellationToken ct, bool rejected = false)` — when `rejected` is true it creates and saves a new key even if `currentKey` is set.

- [ ] **Step 1: Write failing tests**
  - `Old_config_without_new_fields_still_loads`: JSON with only the four original fields → loads, new fields null.
  - `New_fields_roundtrip`.
  - `Rejected_key_is_replaced`: `EnsureKeyAsync("mollm_old", Config, ct, rejected: true)` → runs `add-key`, returns and saves the new key.
- [ ] **Step 2: Run** `dotnet test --filter "JazariConfigTests|KeyProvisionerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `Program.cs`, after `ServerLauncher.EnsureRunningAsync` returns `KeyRejected` and a config exists: call `EnsureKeyAsync(key, config, ct, rejected: true)`, set the new bearer header, continue.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(cli): config fields for updates; replace a rejected key automatically`.

### Task 2: Launch notice

**Files:**
- Create: `src/MyOwnLLM.Cli/UpdateCheck.cs`
- Modify: `src/MyOwnLLM.Cli/InputLoop.cs`, `src/MyOwnLLM.Cli/Program.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/UpdateCheckTests.cs`, `tests/MyOwnLLM.Gateway.Tests/InputLoopTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `JazariConfig` (Task 1).
- Produces: `public static class UpdateCheck { public static Task<bool> IsUpdateAvailableAsync(IProcessRunner runner, JazariConfig? config, TimeSpan timeout, CancellationToken ct); }` — runs `git ls-remote <RepoUrl> refs/heads/main`; true only when exit code 0, the first token is a 40-hex commit, `InstalledCommit` is set, and they differ. Any exception, timeout, or missing value → false.
- Produces: `InputLoop.RunAsync(..., Task<bool>? updateAvailable = null)` — before each prompt, if the task has completed with `true` and the notice hasn't been shown, print the muted line `update available, run jazari update` once. Never awaits an unfinished task.
- `Program.cs` sets `GIT_TERMINAL_PROMPT=0` for the process and starts the check (5 s timeout) right after loading the config, interactive session only.

- [ ] **Step 1: Write failing tests**
  - `Newer_commit_means_update`: runner returns `"<40 hex A>\trefs/heads/main"`, config `InstalledCommit` = 40 hex B → true.
  - `Same_commit_means_no_update` → false.
  - `Git_failure_or_garbage_means_no_update`: exit 128; output `"fatal: ..."`; runner throws → all false.
  - `Missing_repo_or_commit_means_no_update` (null config, null `RepoUrl`, null `InstalledCommit`) → false, runner never called.
  - `Slow_git_times_out_as_no_update`: runner waits forever; timeout 50 ms → false within 1 s.
  - `InputLoopTests.Update_notice_shows_once_before_a_prompt`: completed `Task.FromResult(true)`, two scripted inputs → output contains the notice exactly once.
  - `InputLoopTests.Unfinished_check_never_blocks`: `new TaskCompletionSource<bool>().Task` → loop finishes its scripted inputs, no notice.
- [ ] **Step 2: Run** `dotnet test --filter "UpdateCheckTests|InputLoopTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(cli): tell the user when GitHub has a newer Jazari`.

### Task 3: The launcher

**Files:**
- Create: `src/Jazari.Launcher/Jazari.Launcher.csproj` (net10.0, `AssemblyName` `jazari`, `PackAsTool` true, `ToolCommandName` `jazari`, `PackageId` `Jazari`, `Version` `0.3.0`, no package references), `src/Jazari.Launcher/Program.cs`, `src/Jazari.Launcher/LaunchPlan.cs`; add to `MyOwnLLM.slnx`
- Modify: `src/MyOwnLLM.Cli/MyOwnLLM.Cli.csproj` (remove `PackAsTool`, `ToolCommandName`, `PackageId`; keep `AssemblyName` `jazari`)
- Test: `tests/MyOwnLLM.Gateway.Tests/LaunchPlanTests.cs` (reference the launcher project)

**Interfaces:**
- Produces: `public sealed record LaunchPlan(string Kind, string Exe, string[] Args, string? Message)` with `Kind` ∈ `run` | `update` | `fail`; `public static LaunchPlan LaunchPlan.Decide(string[] args, string? configJson, Func<string, bool> fileExists)` — reads `installDir` and `sourceDir` from the config JSON.
  - `update` → `Exe` = `powershell.exe`, `Args` = `["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "<sourceDir>\\scripts\\update-jazari.ps1"]`.
  - anything else → `run` with `Exe` = `<installDir>\\app\\jazari.exe`, `Args` = the given args.
  - config missing/unreadable → `fail`, `Message` = `Jazari isn't installed on this PC yet. Run scripts\\install-jazari.ps1 from your MyOwnLLM clone.`
  - app or updater file missing → `fail`, `Message` = `Jazari isn't fully installed. Run: jazari update` (for a missing updater: `… Run scripts\\install-jazari.ps1 again.`).
- `Program.cs`: executes the plan as a child process with inherited console (`UseShellExecute = false`, no redirection), sets `Console.CancelKeyPress` to `e.Cancel = true` while the child runs, returns the child's exit code. After a successful `update` (exit 0) it runs the app with no arguments (reopen). `fail` prints `Message` and returns 1.

- [ ] **Step 1: Write failing tests** for `Decide`: run with args passed through; update paths; missing config; missing app; missing updater; unreadable JSON.
- [ ] **Step 2: Run** `dotnet test --filter LaunchPlanTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the launcher.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat: jazari launcher runs the app and the updater`.

### Task 4: The updater script

**Files:**
- Create: `scripts/update-jazari.ps1`
- Modify: `src/MyOwnLLM.Cli/ServerLauncher.cs`, `src/MyOwnLLM.Cli/Welcome.cs`, `src/MyOwnLLM.Cli/Program.cs` (messages: "run jazari update" instead of "run install-jazari.ps1 again"), matching tests

**Interfaces:**
- Consumes: config (Task 1), manifest, launcher paths (Task 3).
- Behaviour, in order (each step prints one checklist line):
  1. `param([switch]$Reexec)`. Read config; require `installDir`, `sourceDir`, `repoUrl`, else `ERROR config: …` and exit 1.
  2. Not `-Reexec`: if any `jazari.exe` process runs from `<installDir>\app`, list them and ask `Close them and continue? [y/n]`; `n` → exit 1 unchanged; `y` → stop them. Stop `llama-server` and `MyOwnLLM.Gateway`.
  3. Not `-Reexec`: `git -C <sourceDir> fetch origin main`, `reset --hard origin/main`, `clean -fd` (`$env:GIT_TERMINAL_PROMPT=0`). Failure → `ERROR source: could not reach GitHub (<git message>)`, exit 1. Then re-run itself from the synced copy with `-Reexec` and exit with its code.
  4. Model: SHA-256 of `<installDir>\models\<modelFile>` vs manifest. Bad/missing → download to `<file>.download`, verify, then replace; leftover `.download` files are deleted first.
  5. llama.cpp: `<installDir>\llama\llama-server.exe --version` output contains `llamaRelease`. Bad/missing → download zip to temp, unpack to `<installDir>\llama.new`, verify, swap folders.
  6. Gateway and app: `build-commit.txt` in each folder vs `git -C <sourceDir> rev-parse HEAD`. Different/missing → `dotnet publish` into `<folder>.new`, write the marker, swap.
  7. Config: rewrite with current paths; `installedCommit` = HEAD only if steps 4–6 all succeeded.
  8. Final line, exit 0 (or 1 if any `ERROR`).
- Messages in C#: `ServerLauncher` missing-files hint → `Run: jazari update`; `Welcome` GatewayNotRunning hint first line → `Jazari starts its servers itself; if it can't, run: jazari update`.

- [ ] **Step 1: Update the message tests** (`Missing_files_are_reported_right_away_without_starting_anything` expects `jazari update`; `Problems_come_with_next_steps` GatewayNotRunning expects `jazari update`). Run `dotnet test`. Expected: FAIL on those.
- [ ] **Step 2: Implement** the messages and `scripts/update-jazari.ps1`.
- [ ] **Step 3: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 4: Commit** `feat: update-jazari.ps1 syncs, checks and repairs Jazari`.

### Task 5: Bootstrap installer, docs, end-to-end check

**Files:**
- Modify: `scripts/install-jazari.ps1`, `README.md`, `docs/phase1-runbook.md`

**Interfaces:**
- Installer: refuse while jazari runs (as now); `repoUrl` = `git -C <its clone> remote get-url origin`; clone into `<installDir>\source` (or `fetch` + `reset --hard origin/main` when it exists); copy `spike\models\<modelFile>` and `spike\bin` from its own clone into `<installDir>\models` / `<installDir>\llama` when present and missing there; pack and install the **launcher** (global tool, or `--tool-path <installDir>\bin` + PATH for a custom folder, as now); write the initial config (with `repoUrl`, `installDir`, `sourceDir`); run `<installDir>\source\scripts\update-jazari.ps1 -Reexec`.

- [ ] **Step 1:** Rewrite the installer.
- [ ] **Step 2: Manual check on this PC** (close jazari first): run the installer → checklist ends `Jazari is up to date (…)`; `jazari` opens; `jazari update` → all `✓`, reopens Jazari in the same window.
- [ ] **Step 3: Repair check:** delete `<installDir>\gateway\MyOwnLLM.Gateway.exe` → `jazari` says to run `jazari update` → `jazari update` prints `! gateway repaired`.
- [ ] **Step 4: Review Focus checks:** with the network adapter off (or a wrong `repoUrl` in the config), `jazari update` prints `ERROR source: could not reach GitHub …` and Jazari still opens; launch notice stays silent. Push a commit, run `jazari`: the notice appears.
- [ ] **Step 5:** Docs: install once with the installer, then `jazari update`; `-InstallDir` unchanged.
- [ ] **Step 6: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 7: Commit** `feat: install once, then jazari update`.
