# `jazari update` — design

Date: 2026-10-30 · Owner: Eni-M · Status: draft for approval
Builds on: `2026-10-30-jazari-cli-design.md` (installer, config, auto-start).

## Intent

One command that makes this machine's Jazari the **latest Jazari**: whatever is on GitHub `main`, complete and working. It gets the latest files, runs a health check over everything Jazari needs, and repairs what's missing or broken — replacing or deleting-and-re-adding files. After the first install, the installer is never needed again on that machine.

It must be **dynamic**: it doesn't matter which folder you run it from, where your code clones live, or which PC it is.

## Goals

1. `jazari update` = sync with GitHub `main` → health check → repair → reopen Jazari.
2. On every interactive launch, a quiet check tells you when a newer version exists: `update available, run jazari update`.
3. Works on any Windows PC with the repo access, from any directory.

## Non-goals

- Updating automatically without being asked.
- Touching any folder where you write code.
- Following branches other than `main`, or unpushed work.
- Linux/macOS (Windows only, like the installer).

## Anti-goals (failure even if it "works")

- Losing anyone's uncommitted or unpushed work.
- Leaving Jazari broken half-way: a failed update must leave the previous working version in place.
- Making startup slower or noisier: the launch check never blocks, never errors, and says nothing when offline or up to date.

## Decided by Eni-M

- GitHub (`Eni-M/MyOwnLLM`, private) is the source of truth; "latest" means its `main`.
- `jazari update` adds missing files, health-checks, and repairs (replace, or delete and re-add).
- On launch: show `update available, run jazari update` when GitHub has something newer.
- Nothing tied to a particular directory.

## Design (Claude's calls, Eni-M to check)

### A private source copy

Jazari keeps its own clone of the repo at `<InstallDir>\source`. Nobody edits it, so repair can be blunt: make it match `origin/main` exactly (`fetch`, `reset --hard origin/main`, `clean` of untracked non-ignored files). Your own working clones are never involved.

The config (`%APPDATA%\Jazari\config.json`) gains:
- `repoUrl` — the GitHub address, taken by the installer from the clone it was run from (`git remote get-url origin`);
- `installDir` (exists) and `sourceDir` (`<InstallDir>\source`);
- `installedCommit` — the `main` commit the current build was made from.

`home` (the clone the installer ran from) stops being used for anything at run time.

### The installer becomes a one-time bootstrap

`install-jazari.ps1` (run once per machine, from any clone): clones `repoUrl` into `<InstallDir>\source` (or reuses it), then hands over to the same update routine below. If the clone it was run from already has the model or llama.cpp, it copies them into `<InstallDir>` to avoid a 2 GB re-download.

### What `jazari update` does

1. **Hand-off through a launcher.** A running program can't replace its own files, so the `jazari` command becomes a tiny **launcher** (the .NET global tool) that runs the real Jazari from `<InstallDir>\app\jazari.exe` in the same console, passing arguments through and ignoring Ctrl+C itself (the app handles it). The launcher rarely changes. On `jazari update` the launcher stops the background servers, runs the updater (`<InstallDir>\source\scripts\update-jazari.ps1`) in the same window **and waits for it**, then starts the app again, so the shell prompt never interleaves. If other Jazari windows are open, the updater lists them and asks `Close them and continue? [y/n]`; "n" stops the update with nothing changed. After the sync (step 2) the updater re-runs itself from the freshly synced copy, so updater changes apply in the same update.
2. **Sync source.** `git fetch` + `reset --hard origin/main` + `clean -fd` in `<InstallDir>\source`. Failure (offline, credentials) → stop with a clear message; nothing else changes.
3. **Health check and repair,** each printed as one line (`✓ … OK`, `! … repaired`, `ERROR …`):

| Check | Healthy when | Repair |
|---|---|---|
| Source | matches `origin/main` | (done in step 2) |
| Model file | exists and its SHA-256 matches the pinned value in a repo manifest | download to a temp file, verify SHA-256, then replace |
| llama.cpp | `llama-server.exe --version` runs and reports the pinned release | download and unpack to a temp folder, verify `--version`, then replace |
| Gateway build | built from the current source commit (marker file) | rebuild |
| `jazari` command | built from the current source commit | rebuild and reinstall the tool |
| Config | valid JSON, every path exists | rewrite |
| API key | the gateway accepts `JAZARI_KEY` | new behaviour in jazari: a 401 at startup creates and saves a new key, as on first run |

   Pinned values (model SHA-256 and URL, llama.cpp release) live in a manifest file in the repo (`scripts/jazari-manifest.json`), so they update with the code.
4. **Safe swap, per component.** Every repaired component (model, llama.cpp, gateway, app) is built or downloaded into a temporary location and replaces the old one only after its check passes. A failure keeps that component's previous version, the update continues with the rest, and it ends with `ERROR` naming what failed. `installedCommit` is only updated when everything succeeded.
5. **Finish.** Records `installedCommit`, prints `Jazari is up to date (<short commit>)`, and starts `jazari` again in the same window.

### Update notice on launch

After the welcome header, the interactive session runs `git ls-remote <repoUrl> refs/heads/main` in the background (5 s timeout). If the commit differs from `installedCommit`, it prints one muted line: `update available, run jazari update`. Offline, slow, or no `repoUrl` → nothing is shown. One-off commands don't check.

## What's left to the builder

- Exact PowerShell structure of the updater and how it shares code with the installer.
- Marker-file format, temp-folder naming, the clean-up of old builds.
- Launcher details: argument pass-through, exit codes, how it finds `<InstallDir>\app` from the config.
- `GIT_TERMINAL_PROMPT=0` for the launch check, PowerShell host and execution policy for the updater, `--version` parsing, and updating the "run install-jazari.ps1 again" messages to say `jazari update`.
- Tests: the C# side (command parsing, the hand-off, the launch notice comparison and its silence when offline) is unit-tested; the PowerShell updater gets a manual checklist run.
