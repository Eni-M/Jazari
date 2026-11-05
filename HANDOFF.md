# Handoff: `jazari update` work

Read this first, then the plan and the spec.

## Where to start

- **Code branch: `jazari-update-wip`.** All the work is here. Continue on it.
- **Plan (task list, follow it in order):** `docs/superpowers/plans/2026-10-30-jazari-update.md`
- **Spec:** `docs/superpowers/specs/2026-10-30-jazari-update-design.md`
- **Owner's local clone:** `C:\MyOwnLLM` on Windows (where the earlier work was done; it is on `jazari-update-wip`).
- **Build and tests:** `dotnet test` from the repo root (.NET 10, xUnit, `tests/MyOwnLLM.Gateway.Tests`). Windows only at run time. The last full run passed 227 of 227 tests, before the two Task 4 test edits.
- **Way of working:** test first for every change (write a failing test, see it fail, implement, run the full suite, commit). Keep commit messages in the style of the git log (`feat(cli): ...`, `fix: ...`).
- **How to carry out the plan (owner's choice):** build the remaining tasks in one session, then have one strong reviewer check everything at the end, instead of a reviewer per task.

## Status

| Task | State | Commit on `jazari-update-wip` |
|---|---|---|
| 1. Config fields, manifest, rejected-key repair | Done | `3c6b5ab` |
| 2. Launch notice (`update available, run jazari update`) | Done | `8b9f73c` |
| 3. The launcher (`src/Jazari.Launcher`) | Done | `04df573` |
| Hardening (decisions 2 to 4 below) | Done | `fa7a99a` |
| 4. Updater script + "run jazari update" messages | **In progress: Step 1 started** | `cb410da` (WIP) |
| 5. Bootstrap installer, docs, manual checks on Windows | To do (manual steps need the Windows PC) | |
| 6. Final review and fixes (review the whole diff against the spec's Review Focus list, fix what it finds) | To do | |

### Exactly where Task 4 stopped

- **Done (Step 1, first half):** the two message tests now expect `jazari update`:
  - `tests/MyOwnLLM.Gateway.Tests/ServerLauncherTests.cs`: `Missing_files_are_reported_right_away_without_starting_anything`
  - `tests/MyOwnLLM.Gateway.Tests/WelcomeTests.cs`: the `GatewayNotRunning` `InlineData` is now `"jazari update", "start-llama.ps1"`
- **Next:** run `dotnet test` and confirm that exactly these two tests fail. This step hasn't been run yet.
- **Then Step 2:** change the messages in `src/MyOwnLLM.Cli/ServerLauncher.cs` (missing files → `Run: jazari update`) and `src/MyOwnLLM.Cli/Welcome.cs` (first line of the GatewayNotRunning hint → `Jazari starts its servers itself; if it can't, run: jazari update`). Write `scripts/update-jazari.ps1` as the plan describes. Then Steps 3 and 4.

### Clean-up needed on `jazari-update-wip`

- The WIP commit `cb410da` accidentally added IDE files under `.idea/` (JetBrains Rider). Remove them from git (`git rm -r --cached .idea`) and add `.idea/` to `.gitignore`.
- The WIP commit message is a placeholder. When Task 4 is finished, the work can go into a proper commit (`feat: update-jazari.ps1 syncs, checks and repairs Jazari`).

## Decisions made after the plan was written (all already in the code)

These override the plan where they differ.

1. **The launcher's assembly is named `Jazari.Launcher`, not `jazari`.** The app already builds as `jazari.dll`, so a launcher named `jazari` would clash in the test project, which references both. `ToolCommandName` stays `jazari`, so the command people type is unchanged.
2. **The update check never runs `git` by bare name.** `UpdateCheck` finds `git.exe` only in absolute `PATH` entries (it skips `.` and relative entries), runs it from Jazari's own folder, accepts only `https://` repo addresses, and allows only the https protocol (`-c protocol.allow=never -c protocol.https.allow=always`).
3. **The launcher runs PowerShell by its full system path** (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`) and starts from Jazari's folder.
4. **Background servers start through `cmd.exe` by its full system path,** with the working directory set to the server's own folder (`src/MyOwnLLM.Cli/Processes.cs`).

## Other context from the planning chat ("Project Creation" issue chat)

- **The model checksum is verified.** The `modelSha256` in the plan and in `scripts/jazari-manifest.json` came from IBM's Hugging Face repository and matches the owner's local model file.
- **Known, accepted limit:** the launcher can't update itself while it's running an update. Running the installer again updates it.
- **The installer reuses the model already downloaded** (from `spike\models`), so the 2 GB download is skipped.

## Not yet decided

- `src/MyOwnLLM.Gateway/State/GitSummary.cs:22` still runs `git` by bare name. The hardening didn't cover it. It probably needs the same fix as decision 2; check with the owner.
