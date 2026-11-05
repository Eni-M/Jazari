# `/fix` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `/fix` builds the project, reads the compiler's errors, proposes one change to one file, writes it only on yes, then rebuilds to show whether it worked.

**Architecture:** Plain code runs the build, parses the errors and picks the file, in the Agentless style: the model never chooses what to do, it only writes the edit. The edit goes through the same propose, check, show, confirm and undo flow as `/edit`, extracted from `Session.EditAsync`. The rebuild decides the outcome, not the model.

**Tech Stack:** .NET 10, xUnit, the existing `ProjectCheck` and `ProjectTrust`, `dotnet build` as a child process, Python for the benchmark driver.

**Spec:** `docs/superpowers/specs/2026-11-04-what-next.md`, section D1. It is four lines, so the decisions below complete it.

## Decisions this plan makes

Claude's calls, for Eni-M to check:

1. **One file per run.** `/fix` takes the first error's file and sends up to 5 of that file's errors. If errors remain it says so, and `/fix` again does the next round. Every write gets its own yes.
2. **Compile errors only, no tests.** D1's step 4 (re-running tests in a Job Object) is left out: running tests runs the user's code and needs that sandbox built first. Add it after this ships.
3. **No restore.** `--no-restore` keeps the build offline and stops a restore changing packages behind the trust check. A project that was never restored gets a message saying to run `dotnet restore`.
4. **A `.csproj`, not a solution.** Argument, or the single `.csproj` in the project root or one level below. `ProjectTrust` follows project references from a `.csproj`, but has no idea what's in a solution. More than one: the message lists them and asks the user to name one.
5. **Fast model.** `/deep /fix` isn't in this plan, because `/deep` is off without a smart model.
6. **The ship rule in Task 6 is new.** 7 of 12 seeded errors fixed, none left worse, median under 2 minutes. If it fails, the command stays out of `/help` and the README.

## Global Constraints

- Fully local and free: no cloud model, no paid service, nothing sent out. `DOTNET_CLI_TELEMETRY_OPTOUT=1` is set for the build.
- A build runs the project's scripts: ask through `MayLoadProject` (`ProjectTrust`) before the first build, every time trust doesn't hold.
- Never write without `Confirm` returning true. Without a confirm function nothing is written.
- `dotnet.exe` comes from absolute `PATH` entries only, never the folder Jazari runs in, like `git.exe` in `UpdateCheck.cs:31`.
- Only files inside the project are edited (`InsideProject`), and only `.cs` files.
- Build from a Claude session with `--disable-build-servers`. Never start the real Jazari app, `jazari update` or the installer from one (`docs/OPTIMIZATION-LOG.md`, "Traps").
- General-CRM and other company repositories are never used for evaluation.
- Windows PowerShell 5.1 and Windows only at run time, as everywhere else.

## Review Focus

- An error in a generated or outside file (`obj/`, a NuGet package, a path outside the project): never offered for editing; the next editable file's errors are used, and if there's none the errors are shown and nothing is asked. Test in Task 4.
- A path with spaces, parentheses or non-ASCII letters, and the same error printed once per target framework: parsed whole and counted once. Tests in Task 1.
- A build that fails with no compiler error at all (`MSB`, `NU`, `NETSDK1004`, SDK missing): shows the build's last lines and asks the model nothing. Test in Task 4.
- Stop or timeout during a build or rebuild: the process tree is killed and the user is told what state the file is in. A written change stays written and `/undo` still works. Test in Task 4.
- A change that makes the build worse, or a file changed on disk since the build: worse offers undo; a changed file is read fresh by the edit flow, so its `search` text can't apply to stale content. Test in Task 4.

---

### Task 1: Read the compiler's errors

**Files:**
- Create: `src/MyOwnLLM.Cli/BuildErrors.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/BuildErrorsTests.cs`

**Interfaces:**
- Produces: `public sealed record BuildError(string File, int Line, string Code, string Message)` and `public static IReadOnlyList<BuildError> BuildErrors.Parse(string output)`.
- Produces: `public static IReadOnlyList<BuildError> BuildErrors.New(IReadOnlyList<BuildError> before, IReadOnlyList<BuildError> after)`, compared by file, code and message, without lines, since an edit moves the lines below it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Reads_a_compiler_error() {
    var e = BuildErrors.Parse(@"C:\src\My App (2)\Café.cs(12,5): error CS0103: The name 'x' does not exist in the current context [C:\src\App.csproj]")[0];
    Assert.Equal(@"C:\src\My App (2)\Café.cs", e.File);
    Assert.Equal(12, e.Line);
    Assert.Equal("CS0103", e.Code);
    Assert.Equal("The name 'x' does not exist in the current context", e.Message);
}
[Fact] public void Warnings_and_other_lines_are_not_errors() =>
    Assert.Empty(BuildErrors.Parse("C:\\a.cs(1,1): warning CS0168: unused\nBuild FAILED.\nerror MSB1009: Project file does not exist."));
[Fact] public void The_same_error_for_two_target_frameworks_counts_once() =>
    Assert.Single(BuildErrors.Parse("C:\\a.cs(1,1): error CS1: m [C:\\A.csproj::TargetFramework=net8.0]\nC:\\a.cs(1,1): error CS1: m [C:\\A.csproj::TargetFramework=net10.0]"));
[Fact] public void A_moved_error_is_not_new() {
    var before = BuildErrors.Parse("C:\\a.cs(1,1): error CS1: m");
    Assert.Empty(BuildErrors.New(before, BuildErrors.Parse("C:\\a.cs(9,1): error CS1: m")));
    Assert.Single(BuildErrors.New(before, BuildErrors.Parse("C:\\a.cs(9,1): error CS2: other")));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --disable-build-servers --filter FullyQualifiedName~BuildErrorsTests`
Expected: a build error, `BuildErrors` doesn't exist.

- [ ] **Step 3: Implement `BuildErrors`**

One line at a time with `^(?<file>.+?)\((?<line>\d+),\d+\): error (?<code>[A-Z]+\d+): (?<message>.*?)(?: \[[^\]]*\])?\s*$`, `RegexOptions.Multiline`, generated regex. Only `CS` codes count. Distinct by file (ignoring case), line, code and message.

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(fix): read the compiler's errors from a build"`

### Task 2: Run the build safely

**Files:**
- Modify: `src/MyOwnLLM.Cli/Processes.cs`, `src/MyOwnLLM.Cli/UpdateCheck.cs:31-39`
- Create: `src/MyOwnLLM.Cli/ProjectBuild.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/ProjectBuildTests.cs`, `tests/MyOwnLLM.Gateway.Tests/ProcessRunnerTests.cs`

**Interfaces:**
- Produces: on `IProcessRunner`, a default interface method `Task<(int ExitCode, string Output)> RunInAsync(string exe, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken ct)`, so the three fake runners in the tests keep compiling. `global.json` is found from the working directory, which is why the build can't use `RunAsync`, which runs in the program's own folder.
- Produces: `public static string? UpdateCheck.FromAbsolutePath(string exe)`, the method at `UpdateCheck.cs:31` taking the program's name.
- Produces: `public sealed record BuildResult(bool Succeeded, IReadOnlyList<BuildError> Errors, string Output)` and `public static Task<BuildResult?> ProjectBuild.RunAsync(string projectFile, IProcessRunner runner, CancellationToken ct, TimeSpan? timeout = null)`. Null when `dotnet.exe` can't be found. `timeout` defaults to `TimeoutSeconds`.
- Produces: `public const int ProjectBuild.TimeoutSeconds = 300`.

- [ ] **Step 1: Write the failing tests**

With a fake runner recording its call: `The_build_runs_offline_and_in_the_projects_folder` asserts the args are exactly `["build", projectFile, "--no-restore", "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false", "-p:UseSharedCompilation=false"]`, the working directory is the project's folder and the environment holds `DOTNET_CLI_TELEMETRY_OPTOUT=1`. `A_failed_build_returns_its_errors`: exit 1 with one error line gives `Succeeded == false` and one error. `A_passing_build_has_no_errors`. `A_build_that_runs_too_long_is_stopped`: a runner that waits on its token, with the timeout passed in shorter, returns a failed result without errors and doesn't throw. In `ProcessRunnerTests`, `RunInAsync_runs_in_the_folder_it_was_given`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~ProjectBuildTests|FullyQualifiedName~ProcessRunnerTests"`
Expected: a build error.

- [ ] **Step 3: Implement `RunInAsync`, `FromAbsolutePath` and `ProjectBuild.RunAsync`**

Share the body of `ProcessRunner.RunAsync`. MSBuild writes errors to standard output, and standard error is still drained, or a chatty build blocks. A linked `CancellationTokenSource` applies the timeout. The caller's own cancel still throws, so Stop reaches the session. Only the timeout turns into a failed result.

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(fix): run dotnet build offline, in the project's folder, with a time limit"`

### Task 3: Share the edit flow

**Files:**
- Modify: `src/MyOwnLLM.Cli/Session.cs:674-763`
- Test: `tests/MyOwnLLM.Gateway.Tests/SessionTests.cs`

**Interfaces:**
- Produces: `async Task<bool> ProposeEditAsync(string path, string file, string request, string? extraContext, bool checkProject, bool deep, CancellationToken ct)` in `Session`. It holds everything `EditAsync` does after its argument checks, from reading the file to setting `_undo`. True only when the change was written. `extraContext`, when set, follows the file in the context given to `CompleteAsync`, so it's wrapped as data like the file. The project check that ran for `deep` now runs for `checkProject`.
- `EditAsync` keeps its argument checks and calls it with `checkProject: deep, extraContext: null`.

- [ ] **Step 1: Run the existing edit tests as the baseline**

Run: `dotnet test --disable-build-servers --filter FullyQualifiedName~SessionTests`
Expected: PASS. This task changes no behaviour, so they are its test.

- [ ] **Step 2: Add one failing test for the new input**

`Extra_context_reaches_the_model_after_the_file`: calling through a new `internal` wrapper, `FakeGateway` records the context and it ends with the extra text, after the `--- file ---` block.

- [ ] **Step 3: Run it to see it fail**

Same command. Expected: a build error.

- [ ] **Step 4: Extract `ProposeEditAsync`**

Moved, not rewritten. The one retry rule and its comment stay as they are.

- [ ] **Step 5: Run all session tests**

Same command. Expected: PASS.

- [ ] **Step 6: Commit**

`git commit -m "refactor(edit): the propose, check, confirm and write flow can be reused"`

### Task 4: The `/fix` command

**Files:**
- Modify: `src/MyOwnLLM.Cli/Session.cs` (command table at `:18-28`, switch at `:129-147`, groups at `:849`)
- Create: `src/MyOwnLLM.Cli/FixTarget.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/FixTests.cs`, `tests/MyOwnLLM.Gateway.Tests/FixTargetTests.cs`

**Interfaces:**
- Consumes: `ProjectBuild.RunAsync`, `BuildErrors`, `ProposeEditAsync`, `MayLoadProject`, `Undo`.
- Produces: `public static (string? Project, string? Problem) FixTarget.Find(string projectRoot, string? argument)`, per decision 4.
- Produces: `public static IReadOnlyList<BuildError> FixTarget.Editable(IReadOnlyList<BuildError> errors, string projectRoot)`: the first editable file's errors, at most `MaxErrors = 5`. Editable means `.cs`, inside the root, and with no `obj` or `bin` folder in its path.
- Produces: on `Session`, `public Func<string, CancellationToken, Task<BuildResult?>> Build { get; init; }`, defaulting to `ProjectBuild.RunAsync` with a real `ProcessRunner`, as `ProjectErrors` is at `Session.cs:660`.
- `/fix [project.csproj]` joins `Session.PathCommands`, so paths complete.

The request sent to the model, exact:

```
Fix these compiler errors in the file above. Change only what they need, and don't remove code to hide an error.

{each error as: line {Line}: {Code} {Message}}
```

What the user sees, exact:

| Situation | Message |
|---|---|
| Build passes | `The build passes. Nothing to fix.` |
| Fails, no compiler errors | `The build failed, but not on a compiler error, so there's nothing to fix in the code:`, then the last 15 lines of output |
| `NETSDK1004` in the output | `This project hasn't been restored. Run dotnet restore, then /fix again.` |
| Errors, none editable | `The errors are in generated or outside files, so Jazari won't change them:`, then up to 5 |
| After yes, build passes | `Fixed: the build passes.` |
| After yes, fewer errors, none new | `{n} error(s) left. /fix again for the next one.` |
| After yes, no fewer, or new ones | Asks `That didn't help: {n} error(s) now, {m} before. Undo the change?`; on yes runs `Undo` |
| Said no at the trust question | `Nothing was built.` |
| `dotnet.exe` not found | `.NET SDK not found on PATH.` |

- [ ] **Step 1: Write the failing `FixTargetTests`**

`A_named_project_is_used`, `The_only_project_is_found`, `One_level_down_is_found`, `Two_projects_list_both_and_ask` (`Project` null and `Problem` naming both), `A_path_outside_the_project_is_refused`, `Generated_and_outside_files_are_not_editable`, `Only_the_first_files_errors_and_at_most_five`.

- [ ] **Step 2: Write the failing `FixTests`**

Using the fake gateway from `SessionTests`, a temp project folder and a queued fake `Build`, `Confirm` recording its prompts:

- `A_passing_build_asks_the_model_nothing`
- `A_failed_build_with_no_compiler_error_asks_the_model_nothing`
- `An_untrusted_project_is_not_built_when_the_user_says_no`
- `The_model_gets_the_errors_and_the_change_is_written_on_yes`, with the rebuild passing and `Fixed: the build passes.` shown
- `Saying_no_writes_nothing_and_does_not_rebuild`
- `Fewer_errors_says_how_many_are_left`
- `A_worse_build_offers_undo_and_undo_restores_the_file`
- `Only_generated_errors_ask_the_model_nothing`
- `Stopping_the_rebuild_keeps_the_write_and_undo_still_works`
- `A_file_changed_since_the_build_is_read_fresh`

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test --disable-build-servers --filter "FullyQualifiedName~FixTests|FullyQualifiedName~FixTargetTests"`
Expected: a build error.

- [ ] **Step 4: Implement `FixTarget` and `FixAsync`**

Order: find the target; `MayLoadProject`; `Build`, showing `Building {project}...`; the table above; `Editable`; `ProposeEditAsync(checkProject: true, deep: false)`; on true, `Build` again, and compare with `BuildErrors.New`. `MayLoadProject`'s wording says "Checking this edit loads the project": give it the question as a parameter, so `/fix` can say `/fix builds the project, which runs its build scripts. Only say yes for code you trust. Build it?`.

- [ ] **Step 5: Run them to see them pass, then everything**

Run the Step 3 command, then `dotnet test --disable-build-servers`. Expected: PASS.

- [ ] **Step 6: Commit**

`git commit -m "feat(fix): /fix builds, proposes one change, and rebuilds to prove it"`

### Task 5: What the compiler error means

**Files:**
- Modify: `src/MyOwnLLM.Cli/Session.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/FixTests.cs`

**Interfaces:**
- Consumes: `IGatewayClient.DocsAsync`, `ProjectLookup.For(project, question, alreadyRead)` (returns `LookedUp?`, at most `MaxChars` 8000), and `extraContext` from Task 3.
- Produces: `async Task<string?> FixContextAsync(IReadOnlyList<BuildError> errors, string file, CancellationToken ct)`.

The model's weak spot is a member it can't see. `CS1061` on `Order` is only fixable if it can read `Order`. So the context holds the code the error messages name, and the law book's page for the error code. The law book's search finds `CS0103` by name already.

- [ ] **Step 1: Write the failing tests**

- `The_code_an_error_names_goes_with_the_request`: a project holding `class Order`, an error mentioning `'Order'`; the context holds the class.
- `The_docs_page_for_the_error_goes_with_the_request`: the fake `DocsAsync` returns a passage, and the context holds it.
- `No_law_book_still_works`: `Available` false, and the change is still proposed.
- `Lookup_never_repeats_the_file_being_fixed`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --disable-build-servers --filter FullyQualifiedName~FixTests`
Expected: FAIL on the four new tests.

- [ ] **Step 3: Implement `FixContextAsync`**

`DocsAsync` with the first error's code alone; `ProjectLookup.For` with the error messages joined, passing the file being fixed as `alreadyRead`. A gateway failure on docs leaves the docs out and carries on.

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(fix): the model sees the code an error names and the docs for its code"`

### Task 6: Measure it, and decide

**Files:**
- Create: `eval/fix/Seed/` (a small library project with passing code, committed), `eval/fix/seeds.json`, `eval/fix/Driver/` (a console project referencing `MyOwnLLM.Cli`), `eval/fix_bench.py`
- Test: `eval/test_fix_bench.py`
- Modify: `docs/OPTIMIZATION-LOG.md`

**Interfaces:**
- Consumes: `launch`, `gateway_env`, `add_key`, `wait_for_gateway`, `stop` from `eval/gateway.py`, and `Session` built the way `SessionTests` builds it.
- `seeds.json`: a list of `{"id", "file", "search", "replace", "code"}`. Each seed turns the passing project into a failing one with exactly one compile error.

The 12 seeds, one each: `CS0103` (typo in a local), `CS1061` (member of a project class spelled wrong), `CS0246` (missing `using`), `CS0029` (wrong type assigned), `CS0161` (a path without `return`), `CS1503` (wrong argument type), `CS7036` (missing argument), `CS0535` (interface member not implemented), `CS8618` set as an error, `CS1002` (missing semicolon), `CS0266` (needs a cast), `CS4014` set as an error.

- [ ] **Step 1: Write the failing test**

`test_every_seed_breaks_with_its_code` runs only `dotnet build`: the clean project passes, and each seed applied alone fails with exactly its `code`.

- [ ] **Step 2: Run it to see it fail**

Run: `py -I eval/test_fix_bench.py`
Expected: FAIL, no seeds yet.

- [ ] **Step 3: Write the seed project and seeds, and run the test to pass**

Same command. Expected: PASS.

- [ ] **Step 4: Write the driver and `fix_bench.py`**

For each seed: fresh copy in a temp folder, `dotnet restore`, apply the seed, and start a temporary gateway and `llama-server` on ports 5099 and 8099 with their own data folder. The driver runs the real `/fix` with `Confirm` always yes. The bench builds the copy itself afterwards. It records `fixed` (build passes), `worse` (more errors than before), `applied`, `seconds`, and saves `eval/results/fix/<label>.json`. It never uses the real Jazari install.

- [ ] **Step 5: Run the benchmark with the shipped model**

Run: `py -I eval/fix_bench.py --label tiny --model C:/MyOwnLLM/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama C:/MyOwnLLM/spike/bin/llama-server.exe --threads 4`
Expected: a table of the 12 seeds. Then check nothing is left listening: `netstat -ano | Select-String ":8099 |:5099 " | Select-String LISTENING` prints nothing.

- [ ] **Step 6: Decide**

Ships if **at least 7 of 12 are fixed, none is left worse, and the median is under 2 minutes**. Otherwise stop and bring the numbers and failed answers to Eni-M. Don't tune the prompt against these seeds and call the same seeds proof: new seeds decide after any tuning.

- [ ] **Step 7: Record it and commit**

Add the numbers to `docs/OPTIMIZATION-LOG.md`, then `git commit -m "eval(fix): a benchmark of seeded compile errors, and its result"`.

### Task 7: Tell people about it

Only if Task 6 passes.

**Files:**
- Modify: `README.md` (command table), `docs/phase1-runbook.md`, `src/MyOwnLLM.Cli/Session.cs:18-21` (help row)
- Test: `tests/MyOwnLLM.Gateway.Tests/SessionTests.cs`

- [ ] **Step 1: Write the failing test**

`Help_lists_fix`: `/help` output holds `/fix`.

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --disable-build-servers --filter FullyQualifiedName~Help_lists_fix`
Expected: FAIL.

- [ ] **Step 3: Add the help row and the README row**

Help: `("/fix [project]", "Fix a compile error, then rebuild to check")`. README: `` | `/fix [project]` | Builds, proposes one change for the first file with errors, writes it on yes, then rebuilds to show whether it worked. Runs the project's build scripts, so it asks first. | ``.

- [ ] **Step 4: Run everything**

Run: `dotnet test --disable-build-servers`
Expected: PASS.

- [ ] **Step 5: Commit, and ask Eni-M to try it**

`git commit -m "docs(fix): /fix in the help and the README"`. Then Eni-M tries it from their own terminal on their own project, since it can't be run from Claude Desktop.
