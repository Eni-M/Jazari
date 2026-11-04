# Line-by-line review — 2026-11-03

Every tracked code file, read in full: 130 files, 16,549 lines. Not code, so not in the ledger: `research_notes/`, `reports/`, `docs/` (prose; checked only where code makes a claim about them), `spike/review-test/` (a deliberately buggy fixture), `eval/results/`.

**Read today, pass 1** means read in full during the first review (`reports/Whole project review 2026-11-03.md`), plus every line changed since. **Pass 2** means read in full in this one. Finding numbers refer to the list after the ledger; "—" means read and nothing to change.

## Ledger

### Gateway (`src/MyOwnLLM.Gateway`)

| File | Lines | Read | Findings |
|---|---:|---|---|
| Program.cs | 52 | pass 1 | — |
| GatewayOptions.cs | 18 | pass 1 | — |
| appsettings.json | 9 | pass 2 | R4 |
| MyOwnLLM.Gateway.csproj | 17 | pass 2 | — (packages current) |
| Auth/ApiKeyFilter.cs | 12 | pass 1 | — |
| Auth/ApiKeyStore.cs | 52 | pass 1 | — |
| Auth/LocalOnly.cs | 15 | pass 1 (written today) | — |
| Endpoints/ChatEndpoints.cs | 242 | pass 1 | — |
| LawBook/LawBook.cs | 136 | pass 1 | — |
| Llama/ILlamaClient.cs | 33 | pass 1 | — |
| Llama/LlamaClient.cs | 140 | pass 1 | — |
| Llama/ModelLimits.cs | 20 | pass 1 | — |
| Prompt/ConversationSearch.cs | 83 | pass 1 | — |
| Prompt/PromptBuilder.cs | 88 | pass 1 | — |
| Queue/QueueWorker.cs | 6 | pass 1 | — |
| Queue/RequestQueue.cs | 127 | pass 1 | — |
| State/ConversationNotes.cs | 22 | pass 1 | — |
| State/GitSummary.cs | 68 | pass 1 | — |
| State/ProjectStateStore.cs | 34 | pass 1 | — |
| State/StateNoteUpdater.cs | 140 | pass 1 | — |
| Storage/InteractionLog.cs | 35 | pass 1 | — |
| Storage/LocalDb.cs | 29 | pass 1 | — |

### CLI (`src/MyOwnLLM.Cli`)

| File | Lines | Read | Findings |
|---|---:|---|---|
| Program.cs | 201 | pass 1 | — |
| CliArgs.cs | 79 | pass 1 | — |
| CodeBlocks.cs | 28 | pass 1 | — |
| Completions.cs | 49 | pass 1 | — |
| ConsoleText.cs | 32 | pass 1 | — |
| DeviceTuner.cs | 220 | pass 1 | — |
| Doctor.cs | 67 | pass 1 (written today) | — |
| FileEdit.cs | 154 | pass 1 | — |
| GatewayClient.cs | 147 | pass 1 | — |
| InputLoop.cs | 138 | pass 1 | — |
| JazariConfig.cs | 26 | pass 1 | F6 |
| KeyProvisioner.cs | 30 | pass 1 | — |
| MyOwnLLM.Cli.csproj | 19 | pass 2 | U1 |
| PipedInput.cs | 19 | pass 1 | — |
| Processes.cs | 63 | pass 1 | — |
| ProjectFiles.cs | 101 | pass 1 | — |
| ProjectReview.cs | 217 | pass 1 | — |
| ServerLauncher.cs | 140 | pass 1 | — |
| Session.cs | 712 | pass 1 | — |
| Settings.cs | 11 | pass 1 | — |
| SseChunk.cs | 42 | pass 1 | — |
| Tuning.cs | 67 | pass 1 | — |
| UpdateCheck.cs | 39 | pass 1 | — |
| Welcome.cs | 120 | pass 1 (lines 1–95), pass 2 (96–120) | F4 |
| Terminal/Blocks.cs | 143 | pass 2 | — |
| Terminal/ColorMode.cs | 28 | pass 2 | F1 |
| Terminal/Layout.cs | 28 | pass 2 | — |
| Terminal/Markdown.cs | 228 | pass 2 | R2 |
| Terminal/MarkdownStream.cs | 150 | pass 1, pass 2 | F3 |
| Terminal/PromptEditor.cs | 678 | pass 2 | F5, R3 |
| Terminal/Screen.cs | 430 | pass 2 | R3 |
| Terminal/Styled.cs | 76 | pass 2 | — |
| Terminal/Terminal.cs | 61 | pass 2 | — |
| Terminal/TextPacer.cs | 103 | pass 1 | — |
| Terminal/TextWidth.cs | 174 | pass 1 | — |
| Terminal/Theme.cs | 170 | pass 2 | F2 |
| Terminal/ThinkingIndicator.cs | 59 | pass 2 | — |
| Terminal/Ui.cs | 297 | pass 2 | F2 |

### Launcher, scripts, CI

| File | Lines | Read | Findings |
|---|---:|---|---|
| src/Jazari.Launcher/LaunchPlan.cs | 51 | pass 1 | — |
| src/Jazari.Launcher/Program.cs | 42 | pass 1 | — |
| src/Jazari.Launcher/Jazari.Launcher.csproj | 16 | pass 2 | — |
| MyOwnLLM.slnx | 10 | pass 1 | — |
| scripts/install-jazari.ps1 | 143 | pass 1 | — |
| scripts/update-jazari.ps1 | 243 | pass 1 | — |
| scripts/start-llama.ps1 | 11 | pass 1 | — |
| scripts/jazari-manifest.json | 8 | pass 1 | U4 |
| .github/workflows/ci.yml | 75 | pass 1 | U3 |
| .github/dependabot.yml | 8 | pass 1 (written today) | — |

### Python

| File | Lines | Read | Findings |
|---|---:|---|---|
| eval/compare.py | 112 | pass 1 | — |
| eval/gateway.py | 100 | pass 1 | — |
| eval/recall.py | 216 | pass 1 | — |
| eval/run_eval.py | 271 | pass 1 | — |
| eval/score.py | 40 | pass 1 | — |
| eval/questions.json | 51 | pass 2 (structure only: it's the held-out test set) | — |
| eval/test_questions.py | 29 | pass 2 | — |
| eval/test_recall.py | 133 | pass 1 | — |
| eval/test_score.py | 76 | pass 2 | — |
| eval/test_run_eval.py | 261 | pass 2 (committed part) | your uncommitted work: left alone |
| lawbook/build.py | 434 | pass 1 | R5 |
| lawbook/test_build.py | 179 | pass 2 | — |
| lawbook/try_dev.py | 92 | pass 2 | F8 |
| lawbook/sources.json | 23 | pass 2 | — |
| lawbook/dev_questions.json | 42 | pass 2 | — |
| spike/fim_test.py | 60 | pass 2 | — (a throwaway spike) |

### Tests (`tests/MyOwnLLM.Gateway.Tests`)

| File | Lines | Read | Findings |
|---|---:|---|---|
| ApiKeyStoreTests.cs | 49 | pass 2 | — |
| CanvasSnapshotTests.cs | 150 | pass 2 | — |
| CaptureReplayTests.cs | 38 | pass 2 | — |
| CliArgsTests.cs | 54 | pass 2 | — |
| CliCompletenessTests.cs | 212 | pass 2 | — |
| ConsoleTextTests.cs | 32 | pass 2 | — |
| ConversationSearchTests.cs | 117 | pass 2 | — |
| DeviceTunerTests.cs | 238 | pass 2 | — |
| DoctorTests.cs | 67 | pass 1 (written today) | — |
| EndpointTests.cs | 547 | pass 2 | — |
| GatewayClientTests.cs | 123 | pass 2 | — |
| Golden/*.txt (4 files) | 117 | pass 2 | F2 |
| InputLoopTests.cs | 148 | pass 2 | — |
| InteractionLogTests.cs | 62 | pass 2 | — |
| InteractiveTests.cs | 78 | pass 2 | F5 |
| JazariConfigTests.cs | 60 | pass 2 | F6 |
| KeyProvisionerTests.cs | 72 | pass 2 | — |
| LaunchPlanTests.cs | 89 | pass 2 | — |
| LawBookTests.cs | 189 | pass 2 | — |
| LlamaClientTests.cs | 156 | pass 2 | — |
| MarkdownStreamTests.cs | 266 | pass 2 | F3, F9 |
| ModelLimitsTests.cs | 42 | pass 2 | — |
| MyOwnLLM.Gateway.Tests.csproj | 29 | pass 2 | U2 |
| OpenAiClientTests.cs | 120 | pass 1 (written today) | — |
| PipedInputTests.cs | 27 | pass 2 | — |
| PlainGoldenTests.cs | 118 | pass 2 | F2 |
| ProcessRunnerTests.cs | 83 | pass 2 | — |
| ProjectStateTests.cs | 92 | pass 2 | — |
| ProjectToolsTests.cs | 426 | pass 2 | — |
| PromptBuilderTests.cs | 224 | pass 2 | — |
| PromptEditorTests.cs | 436 | pass 2 | — |
| RequestQueueTests.cs | 165 | pass 2 | — |
| ReviewFixTests.cs | 219 | pass 2 | — |
| ScreenTests.cs | 298 | pass 2 | — |
| ServerLauncherTests.cs | 113 | pass 2 | — |
| SessionTests.cs | 419 | pass 2 | — |
| SettingsTests.cs | 38 | pass 2 | — |
| SseChunkTests.cs | 64 | pass 2 | — |
| StateNoteUpdaterTests.cs | 193 | pass 2 | F7 |
| TerminalThemeTests.cs | 78 | pass 2 | — |
| TextPacerTests.cs | 123 | pass 2 | — |
| TextWidthTests.cs | 91 | pass 2 | — |
| ThinkingIndicatorTests.cs | 84 | pass 2 | — |
| UiTests.cs | 256 | pass 2 | F2 |
| UpdateCheckTests.cs | 117 | pass 2 | — |
| VtScreen.cs | 310 | pass 2 | — |
| WelcomeTests.cs | 70 | pass 2 | F4 |

## Findings, fixed

Each has a test that fails without the fix, unless it says otherwise.

- **F1. A doc comment of mine sat on the wrong method** (`Terminal/ColorMode.cs`). Adding `IsPlain` today left `DrawsEdges`'s summary above it: `IsPlain` had two and `DrawsEdges` none. Moved back. No test: it changes no behaviour.
- **F2. Dead interface code** (`Terminal/Ui.cs`, `Terminal/Theme.cs`). `Ui.TakeOverScreen` (an empty method "kept for callers"), `Ui.ToolLines` and `Ui.ActionRequired` were called only by tests. `ActionRequired` offered "[a] Always allow", which nothing handled: it was drawn for a tool loop that doesn't exist. `Theme.Bold`, `Theme.Band` and `Ui.Output` had no callers at all. All deleted, with the 3 tests that only exercised them and their 7 lines in the `ui-components` golden file. The control-character test now uses components that ship.
- **F3. Dead parameters** (`Terminal/MarkdownStream.cs`). The writer constructor took `screenRows` and `visibleRows` and never read them, a leftover from before the screen took over layout. Two tests passed values that changed nothing. Parameters removed, and the two tests merged into one that checks what's still true: a finished answer is never redrawn to add its time.
- **F4. A folder that starts like the home folder was shown as home** (`Welcome.cs`). `C:\Users\enim2\api` showed as `~2/api`: the prefix was compared without a separator. Test: `A_folder_whose_name_starts_like_home_is_not_home`.
- **F5. The input history file grew forever** (`Terminal/PromptEditor.cs`). Every question sent adds a line; only the last 500 are used, but all were read at every start. Once the file holds more than 1,000 lines, it's cut back to the newest 500. Test: `The_history_file_is_cut_back_once_it_doubles`.
- **F6. An unreadable config crashed jazari at start** (`JazariConfig.cs`). `Load` caught only bad JSON; a file held open by another process (an update writing it) or one without read access threw `IOException` straight out of `Program`. It now loads as no config, which already says what to do. Test: `A_file_that_cant_be_read_loads_as_null`.
- **F7. A test that couldn't fail** (`StateNoteUpdaterTests.Full_queue_does_not_throw`). The code it covered runs in a fire-and-forget task, so an exception there was never seen, and it asserted nothing. Its comment promised "a dropped update must not block later ones". It's replaced by `An_update_turned_away_by_a_full_queue_is_queued_by_the_next_exchange`, which fills the queue, frees it, and checks that the next update carries the turned-away exchanges too.
- **F8. The law book dev check started `dotnet run` per question** (`lawbook/try_dev.py`). That re-evaluates the project 35 times. It now runs the DLL that `main()` has just built, as `eval/gateway.py` already does. Checked by compiling the script; it needs the law book file to run.
- **F9. The one build warning** (`MarkdownStreamTests.cs`, xUnit2029). The build now has none.

## Research and upgrades

- **U1. Roslyn** 5.0.0 → 5.9.0 (`MyOwnLLM.Cli.csproj`).
- **U2. Test packages**: OpenAI 2.13.0 → 2.14.0, Microsoft.NET.Test.Sdk 17.14.1 → 18.11.0, coverlet.collector 6.0.4 → 10.1.0, xunit.runner.visualstudio 3.1.4 → 4.0.1. All 611 tests run under them. xunit itself stays on 2.9.3: v3 is a separate package and a migration, not an upgrade.
- **U3. CI actions** were 2 to 3 major versions behind: checkout v4 → v7, setup-dotnet v4 → v6, setup-python v5 → v7, upload-artifact v4 → v7. Dependabot (added today) keeps them current from now on. Not run here; the next push shows them working.
- **U4. llama.cpp: kept at b11982, on purpose.** b12069 is 87 builds newer (2026-11-03). None of them touch Granite, Mamba, hybrid models or the CPU path Jazari uses; they're Metal, SYCL, WebGPU, UI and multi-sequence changes. An upgrade changes the tuning fingerprint (every PC re-tunes) and needs an `eval/` run on the T480, for no gain anyone has identified. Look again when a release names hybrid-model or Vulkan fixes that matter here.
- The gateway's packages (Microsoft.Data.Sqlite 10.0.14, Microsoft.Extensions.Caching.Hybrid 10.11.0) and TextCopy 6.2.1 are current. .NET 10 is the current LTS.

## Considered, not changed

- **R1. Measured nothing worth it** in: Theme.Encode measuring each span's width more than once, LINQ in the per-frame paths, and `Screen` re-rendering live blocks every frame. They're all per-frame on a 30–50-row window, far under a millisecond.
- **R2. `AnswerBlock.Inline`** allocates a string per character and lays the open line out again on every token, so a very long paragraph costs O(n²) while it streams. At about 10 tokens a second and paragraphs of a few hundred characters, it's invisible. Revisit only if a profile shows it.
- **R3. Idle wake-ups**: the prompt polls for keys every 33 ms and the screen checks the window size every 33 ms when nothing happens. Console has no async key or resize event on Windows, and longer intervals would make typing and resizing feel slower. About 60 wake-ups a second costs well under 0.1% of a core.
- **R4. `AllowedHosts: "*"`** in `appsettings.json`. ASP.NET Core's own host filtering could do part of what `LocalOnly` does, but it doesn't check `Origin`, and `LocalOnly` already rejects both. Left as is.
- **R5. `lawbook/build.py` edge cases** from the first review (a split `~~~` block closed with backticks; a snippet path on another drive). The pinned sources hit neither.

## Verification

- `dotnet build -c Release`: 0 warnings, 0 errors.
- `dotnet test`: 611 passed. 612 before this pass; 4 tests went with dead code or were merged, 3 were added, and 1 was rewritten so it can fail.
- `lawbook` Python tests: 33 passed. `eval` Python tests: 65 passed, without your uncommitted `eval/test_run_eval.py`, which still has its own import error and wasn't touched.
- The `ui-components` golden file changed by exactly the 7 lines of the deleted components; the other golden files are unchanged.
