# Handoff: `jazari update` work

Read this first, then the plan and the spec. This note says where the work stands and what was decided after the plan was written.

## Where to start

- **Plan (task list, follow it in order):** `docs/superpowers/plans/2026-10-30-jazari-update.md`
- **Spec:** `docs/superpowers/specs/2026-10-30-jazari-update-design.md`
- **Branch:** `claude/tender-babbage-2mmupp` (same as `main` at `7a1cebc`, plus this note)
- **Build and tests:** `dotnet test` from the repo root (.NET 10, xUnit, `tests/MyOwnLLM.Gateway.Tests`). Windows only at run time.
- **Way of working:** test first for every change (write a failing test, see it fail, implement, run the full suite, commit). Keep commit messages in the style of the git log (`feat(cli): ...`, `fix: ...`).

## Status: no code is in the repo yet

An earlier session worked through Tasks 1 to 3 and part of Task 4 and reported 227 passing tests. **None of that work was pushed.** It is not on this branch, `main`, or any other remote branch, and the code it describes (`src/Jazari.Launcher`, `scripts/jazari-manifest.json`, `UpdateCheck.cs`) does not exist here. Start again from **Task 1**. The decisions below came out of that session, so apply them as you go.

| Task | State |
|---|---|
| 1. Config fields, manifest, rejected-key repair | To do |
| 2. Launch notice (`update available, run jazari update`) | To do |
| 3. The launcher (`src/Jazari.Launcher`) | To do, with the change in decision 1 |
| Hardening (decisions 2 to 4) | To do; fold into Tasks 2, 3 and the existing `Processes.cs` |
| 4. Updater script + "run jazari update" messages | To do |
| 5. Bootstrap installer, docs, manual checks on Windows | To do (manual steps need the Windows PC) |

## Decisions made after the plan was written

These override the plan where they differ.

1. **The launcher's assembly is named `Jazari.Launcher`, not `jazari`.** The app (`src/MyOwnLLM.Cli`) already builds as `jazari.dll`. If the launcher also did, the test project, which references both, would get two `jazari.dll` files. In `Jazari.Launcher.csproj` set `AssemblyName` to `Jazari.Launcher` and keep `ToolCommandName` as `jazari`, so the command people type is still `jazari`. Everything else in Task 3 stays as written.

2. **Update check: don't run `git` by bare name** (security review). On Windows, a bare `git` can resolve to a `git.exe` planted in the current folder. In `UpdateCheck`:
   - find `git` by searching the `PATH` directories only, never the current folder; if it isn't found, report no update;
   - set the working directory to Jazari's own folder (the app's base directory), not the user's current folder;
   - accept only `https://` values for `repoUrl`; anything else (`file://`, `ssh`, `ext::`, local paths, values that start with `-`) means no update and git is never run.
   Write tests for each of these first.

3. **Launcher: don't run `powershell.exe` by bare name.** Use the full system path (`Environment.SystemDirectory` + `\WindowsPowerShell\v1.0\powershell.exe`) and set the child process's working directory to Jazari's folder. Change the Task 3 `LaunchPlanTests` to expect the full path. Test first.

4. **Background server start: don't run `cmd.exe` by bare name.** `src/MyOwnLLM.Cli/Processes.cs:39` currently starts `cmd.exe` by name, which runs in the current folder, so a planted `cmd.exe` or `git.exe` there could run. Use the full path (`Environment.SystemDirectory\cmd.exe`) and set the working directory to the folder of the server being started. Test first.

## Not yet decided

- `src/MyOwnLLM.Gateway/State/GitSummary.cs:22` also runs `git` by bare name. The earlier session didn't cover it. It probably needs the same fix as decision 2; check with the owner.
- Task 5 steps 2 to 4 are manual checks that have to run on the owner's Windows PC.
