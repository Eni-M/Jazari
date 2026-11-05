# Jazari optimization log

Read this first if you are continuing this work, human or AI. Newest status at the top of each section.

## The goal (Eni-M, 2026-10-30, in their words where quoted)

"Properly utilize the whole laptop to optimize the usage for the AI model." Find everything, try everything, and if nothing exists, build it.

1. **Use the whole laptop:** get the most speed and usefulness from CPU, both GPUs, RAM and disk.
2. **Whole-project review:** "reading a whole project is what I usually do the most to an AI." It should read every file and report loopholes, better code, security breaches waiting to happen, better practices, protection and business-flow upgrades. This is what Eni-M got from a cloud AI on EduCore and a CRM.
3. **The model can read files and folders and make changes itself.**
4. **Quality in, quality out:** shave off unneeded things and feed the model quality code work.
5. **Clean code output with a copy option.**
6. **Keep track in a file** so other AIs can continue. This is that file.

"This isn't some groundbreaking thing... I just want something VERY useful."

**Scope of item 2 (Eni-M, 2026-10-30):** the deliverable is the capability. Jazari must be able to read and review a project's files. Nobody wants an EduCore review out of this work, so don't produce one. Build and test the feature on this repo. EduCore's size is only a yardstick for how big a project it must handle.

## Session 2, autonomous (started 2026-10-30, Eni-M away, on mobile only)

Eni-M's request: optimize usability and performance, make Jazari easier to use, use the whole PC efficiently, try alternative runtimes, cut what makes the model heavier, and build the bridge between the model and the gateway properly. "I am leaving the decision entirely up to you". Very important decisions wait for Eni-M. Don't use or post Eni-M's own data.

Plan and status (update as each step finishes):

- [x] `/edit` line-trimmed fallback (commit `04d4ac6`, pushed)
- [x] Research, 4 parts, notes in `research_notes/Jazari model bridge and efficiency/`: alternative runtimes and backends, cutting weight and memory, the gateway-to-llama-server bridge, usability
- [x] Report in `reports/Jazari model bridge and efficiency.md`. Its plan (a) is the build list, (b) the measurements, (c) the decisions. Note: Granite 4.1 3B is dense, not hybrid (checked against its config.json); `gateway_bridge.md` is wrong on that point
- [ ] Measure and build the safe wins on branch `jazari-optimize`, tests passing, one commit each. Built so far (unit-tested, not yet tried with a real llama-server):
  - Note updates wait 3 min of quiet and give way to chats (`ace29ae`)
  - Review answer strings capped; workstation GC in the gateway, 22 to 17 MB private (`5a8d7fc`)
  - READING n of total tokens, % and time left while the prompt is read, taskbar progress, tokens read / from cache / tok/s after each answer (`778f146`)
  - `git diff | jazari ask ...` (`27aea6f`)
  - `/read` has the model read the files while the question is typed, through the gateway's new `/v1/warm` (`e09d9f8`)
  - llama-server starts with `--no-webui` (`66a0cb6`)
  - Long history is cut 4 pairs at a time, so the prompt's start stays cached between turns (`735985e`)
  - Per-PC tuning: `jazari tune` and the first start on a new PC measure CPU threads, each GPU and flash attention, and save the winner with a hardware fingerprint (`60adc5e`). The gateway reads the context size from `/props`.
  - **Checked live on this PC (llama-server b11982, Iris Xe, Release gateway):** progress events arrive through the gateway; timings arrive; a follow-up with the same context had 2,431 of 2,447 tokens cached and answered in 2 s instead of 18.4 s; `/v1/warm` then a question: 3,553 of 3,564 cached, 2.1 s instead of 22 s; cancelling stops the server within 2 s; `/props` gives `default_generation_settings.n_ctx`; this repo's C# is 4.46 chars a token, so chars/4 errs about 11% on the safe side and stays.
  - Not done: exact token counts with `/tokenize` (chars/4 measured safe enough), the report's other part (b) items
- [ ] List what waits for Eni-M under "Decisions waiting"

**Found 2026-10-30, session 2: this session runs on a different PC from the one measured above.** All measurements in this file come from the T480 (user `enim`). This PC (user `techcom`) is a Lenovo 20X3 with an **i5-1135G7** (Tiger Lake, 4 cores, 8 threads, AVX-512 and VNNI), **32 GB DDR4-2667 in two channels** (about 42 GB/s theoretical, twice the T480), Intel Iris Xe (96 EU, shared memory), 377 GB free on C:, Balanced power plan. Jazari, llama-server and the model are not installed here. The research report targets the T480. Its hardware advice (MX150, single channel, quant choice) does not carry over; its bridge and usability advice does. On this PC, answers should be roughly twice as fast (bandwidth), Iris Xe is a real option through llama.cpp Vulkan/SYCL or OpenVINO, and the AVX-512 path in llama.cpp applies.

**Eni-M's direction (2026-10-30, session 2):** "make jazari use the optimum on every device, not only this or the T480". So Jazari now measures each PC itself (`jazari tune`, `DeviceTuner.cs`, `Tuning.cs`) instead of using settings hard-coded from one laptop. Eni-M allowed downloading llama.cpp b11982 (CPU and Vulkan zips) and Granite 4.1 3B Q4_K_M into `spike/` for measuring on this PC.

**Found on this PC:**
- Smart App Control is enforcing (`VerifiedAndReputablePolicyState` 1). It blocked `llama-fit-params.exe` but allowed `llama-server`, `llama-bench`, `llama-cli` and `llama-tokenize`. So any tool Jazari runs can be blocked on some PC: the tuner treats a blocked tool as "can't measure" and keeps safe settings.
- First quick look, i5-1135G7, CPU only, 4 threads, 1 run: prompt 74 tok/s, answer 12.9 tok/s (T480: 28.8 / 6.2).
- `jazari tune` result here, 4 minutes: **Iris Xe through Vulkan, flash attention: 224 tok/s reading, 14.7 tok/s writing**, a typical turn about 29 s. CPU at 4 threads was about 74 / 13. At 4,096 tokens of context the GPU leads by more: 116 / 12.4 against the CPU's 35 / 9.7 (6 threads), or 45 / 7.1 with flash attention, which on the CPU speeds reading but slows writing. So measuring at an empty context picks the same winner here.
- Smart App Control also blocked Jazari's own fresh builds twice: `jazari.dll` while it read the CPU name from the registry, and the test DLL while tests wrote fake `.exe` files. With both removed, builds load. Keep code that touches the registry or writes executables out of Jazari, or test that the built DLLs still load on a Smart App Control PC.
- The test config written for `jazari tune` in this session's AppData was deleted afterwards.

**Tuner v2 re-run here (partial GPU layers, flash attention only at the best thread count): 2.4 min instead of about 4.** Same winner, Iris Xe with flash attention, 222 / 14.9 tok/s, typical turn 29 s. The CPU writes slightly faster (16.9 tok/s at 8 threads with flash attention) but reads 2.5x slower (91 tok/s), so it loses on a whole turn.

**Eni-M, 2026-10-31:** the T480 is the main device. No new models: tune Granite 4.1 3B for every device. The 8B download was stopped and deleted, and so was the OpenVINO build (tried first: reads 249 tok/s but writes only 8.0 on Iris Xe, so Vulkan stays). The review fixture is now in `spike/review-test` (3B on Iris Xe: 45 s, found the password and the SQL injection, missed the refund ownership check).

**Session 2, later (2026-10-31):**
- Shorter answers: the new system prompt cut five test answers from 762 to 246 tokens with the same findings (`be29f23`). Answers cut at the token limit now say so.
- Skipped, measured or reasoned: batch sizes (`-ub` 256 to 2048 made no difference on Iris Xe or CPU), and saving the prompt cache to disk (after a restart the CLI starts a fresh conversation, so only the system prompt and note would carry over, and the note changes).
- CLI: `/diff`, `/commit`, `/context`, path completion, `--help`, `--version`. Lesson for prompts: a 3B model given a big diff as background context, or examples, describes the wrong thing. Put the material in the question and keep it compact (`--stat` plus `-U1`).
- Interface refresh (`666fc82`), within the CLI design spec: welcome rows with "Running on" (the tuned device), grouped `/help` with a key column that fits every command (long commands used to overlap their descriptions), and what the next question carries shown on the line above the prompt. Checked by rendering the screens to HTML.
- The clipboard failed again while the PC was locked (`/commit` says so and leaves the text on screen).

### Next, after session 2

- If llama-server doesn't come up with GPU settings (a driver update, for example), fall back to CPU settings and re-tune, instead of timing out. The fingerprint doesn't include the GPU driver version.
- Try the official OpenVINO build of llama.cpp (`llama-b11982-bin-win-openvino-2026.4.1-x64.zip`, 104 MB) on Intel GPUs. Not downloaded: it wasn't in what Eni-M approved.
- On PCs that run the 3B fast (this one: 14.7 tok/s), a bigger model such as Granite 4.1 8B might be worth its speed. That's a quality and download decision for Eni-M.

### Decisions waiting for Eni-M

- ~~Which PC hosts Jazari~~ Answered: every PC, each tuned to itself. Eni-M allowed the downloads and installing here (install from Eni-M's own terminal, see Traps).

- ~~Merge `jazari-optimize` into `main`~~ Done 2026-10-31 (fast-forward to `d5823a1`). Next: Eni-M runs `jazari update` on the T480 from their own terminal; its first start runs `jazari tune`
- Paid RAM upgrade (see "The hardware answer")

## Rules that still apply (from the design doc, `docs/superpowers/specs/2026-10-30-myownllm-design.md`)

- **General-CRM and other company repositories are never used for data, evaluation or tuning.** Test only on Eni-M's own code (EduCore, EduCoreFront, WareFlow, LearningRoadmap, this repo).
- Nothing private leaves the laptop, with one exception: private repo code may go to Colab, and only Colab, for training (Eni-M, 2026-11-02). Web research must not quote private code.
- Budget is $0. Paid options can be reported, but must not be needed.
- The laptop must stay usable while the model runs.
- Quality floor comes before speed: a faster model that gets worse is a failure.
- Install and update only from Eni-M's own terminal, never from inside Claude Desktop (see "Traps").
- Never start the real Jazari app from a Claude Desktop session: it can replace `JAZARI_KEY`.

## Hardware (read from this PC)

| Part | Value |
|---|---|
| Laptop | Lenovo model 20L6S7K10A (a ThinkPad T480, to confirm) |
| CPU | i7-8650U, 4 cores, 8 threads, 15 W |
| RAM | 16 GB DDR4-2666 stick running at 2400, **two slots, one empty, so single channel** (about 19 GB/s), 32 GB max reported |
| GPUs | Intel UHD 620 (shared memory), NVIDIA MX150 2 GB |
| Disk | 239 GB free on C: |
| Power plan | Balanced |

**Correction to earlier reports:** `reports/Local coding model selection.md` and `reports/Local model load optimization.md` assume dual-channel memory (37.5 GB/s). Windows reports one stick in `ChannelA-DIMM0`. The measured 12 to 13 GB/s fits single channel far better.

## Measured so far

| Test (Granite 4.1 3B Q4_K_M, 2 runs, plugged in, Jazari closed) | Prompt tok/s | Answer tok/s |
|---|---|---|
| CPU, `-dev none -t 6 --poll 0` | 28.8 | 6.2 |
| Intel UHD 620, full offload | 12.5 | 4.1 |
| MX150, 24 layers (earlier report) | 39.8 | 6.1 |
| MX150, full offload (earlier report) | out of memory | out of memory |

## Project sizes (rough, 1 token = 4 bytes)

| Project | Code files | Tokens | Reading time at 30 tok/s |
|---|---|---|---|
| EduCore, hand-written C# only | about 300 | ~350k | ~3.2 h |
| EduCore, everything | 705 | ~5.0M | ~46 h, but 15 MB is `seed-dump.sql`, 2.4 MB is generated migrations |
| EduCoreFront | 117 | ~233k | ~2.2 h |

So skipping generated code and data dumps is what makes a whole-project read possible overnight.

General-CRM was measured for size only and is otherwise off limits.

## Traps

- **Claude Desktop redirects `AppData`.** Anything run from a Claude Desktop session writes `%APPDATA%` and `%LOCALAPPDATA%` into `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache`. To read the real files from there, use `\\localhost\C$\Users\enim\AppData\...`; plain `%LOCALAPPDATA%` paths can show a stale private copy instead. It isn't a clean split either: changes can land in the real files, so a git command there can leave a folder half in each.
- **Compiler servers from a Claude session can catch builds from other terminals.** Build with `--disable-build-servers`, or run `dotnet build-server shutdown` when finished.
- **`llama-server --version` prints `build 11982`, not `b11982`.**

## Where things stand (2026-10-30, end of first session)

**Done on branch `jazari-optimize`, not merged to `main`, so not in the installed Jazari yet.** All automated tests pass.

| Command | What it does | Tested with the real model |
|---|---|---|
| `/copy [n]` | Copies a numbered code block, stripped of hidden characters | Not yet: the clipboard said "access denied" while the laptop was locked, cause unconfirmed |
| `/read <path>` | Reads a file or folder for the next questions, 5,000 tokens at most | Yes, one small file |
| `/review [path]`, `jazari review` | Reviews a whole project, resumes after a stop, writes a report | Yes, 6 files with 3 planted bugs |
| `/edit <file> <what>`, `/undo` | Shows the change, writes after yes, can undo | Yes, one rename |

`/edit` falls back to whole-line matching with ends trimmed when the model's search text is off by indentation or trailing spaces, and moves the replacement to the file's indentation. Unit-tested only, not yet with the real model.

Also changed: code lines print without padding so mouse copies stay exact, temperature is 0.2 for code, and the gateway and client timeouts are 30 minutes.

**Not built:** a free-running tool loop where the model picks its own files. Static tools as a first pass. Roslyn. See "Next".

## What the measurements say

All on Granite 4.1 3B Q4_K_M unless stated. Numbers from before 17:16 overlapped with other work and are not used here.

**1. Answer speed falls as the prompt grows.** This is why the benchmark's 6.2 tok/s doesn't hold in real use.

| Prompt tokens | Prompt tok/s | Answer tok/s |
|---|---|---|
| 111 | 27.3 | 6.75 |
| 1,420 | 28.0 | 5.45 |
| 5,244 | 23.8 | 2.82 |

So small parts win. Review parts are about 2,000 tokens, and `/read` stops at 5,000.

**2. Settings tried.**

| Setting | Result | Decision |
|---|---|---|
| `KMP_BLOCKTIME=0` | Prompt about 13% faster at small sizes, answers 20 to 28% slower | No |
| `-t 4` with blocktime 0 | Answers still slower than `-t 6` default | No |
| `ngram-simple`, `ngram-mod`, `ngram-map-k` | Few drafts accepted, no real gain (measured while other work ran) | No |
| Draft model, Granite 4.0 350M on MX150 | Rewriting a file: **5.46 to 7.95 tok/s**, 176 of 177 accepted. Reviewing: **5.47 to 2.67 tok/s**, 134 of 512 accepted | Off. It only helps when the answer repeats the input |
| Draft model, Granite 4.0 1B on MX150 | No gain on rewriting, slower reviewing | No |
| Draft model, Granite 4.0 H-Tiny | Too big for the MX150 | No |
| Intel UHD 620 | 12.5 / 4.1 tok/s | No |

**The current setup was already close to the best this laptop gives without new hardware:** `-t 6`, CPU only.

**3. Granite 4.2 3B Q4_K_M against 4.1, same 6 files with 3 planted bugs (hardcoded password, SQL injection, refund with no authorization).**

| | Granite 4.1 3B | Granite 4.2 3B, thinking off |
|---|---|---|
| Planted bugs found | 3 of 3 | 3 of 3 |
| Time | 4 min 9 s | 6 min 37 s |
| Parts that failed | 0 | 1 of 4 |
| Findings beyond the planted bugs | Mostly weak, for example 2 "no authorization" guesses on an internal class | Mostly weak, including a duplicate |
| Benchmark, prompt / answer tok/s | 28.8 / 6.2 | 35.1 / 6.6 |

Decision: **keep Granite 4.1.** One small test, so this is direction, not proof. Granite 4.2 is worth another look, since its benchmark was faster. Thinking on was not tried.

**Honest quality picture:** the review reliably catches obvious, visible problems, and each finding quotes real code. It also reports weak ones, so read the report as a list to check. It won't do what a cloud AI does on cross-file flows and business logic: it sees one part at a time. Nothing here tests that, and research found even strong cloud models struggle with cross-file authorization.

**Review time:** about 1 minute per part in that test, so an EduCore-sized project (350k tokens, about 175 parts) would take about 3 hours at best, and up to about 7 with heat and more parts. Estimate, not run, as Eni-M asked for no EduCore review.

## The hardware answer

A **second 16 GB DDR4 SODIMM** (the empty slot) is the one change that should really speed answers. Research estimates 6.2 to about 9 tok/s, from a test on similar hardware, and it costs about $40 to $120. It's paid, so it needs Eni-M's decision. Nothing else found comes close. Undervolting is blocked unless Memory Integrity is turned off, a security trade-off, and whether BIOS 1.46 allows it is unknown.

## Next

1. Eni-M decides whether to merge `jazari-optimize` into `main`, then runs `jazari update` from their own terminal.
2. Try `/copy` with the laptop unlocked.
3. `GitSummary.cs:22` still runs `git` by bare name. Eni-M's decision is open.
4. Try review on Granite 4.2 with thinking on, and Mellum2.1 (8 GB) as an overnight model.
5. Add gitleaks, Opengrep and osv-scanner as a first pass, then Roslyn for effective auth. See `repo_review.md`.
6. Add a tool loop if explicit commands feel limiting. `agent_tools_and_output.md` has the design: four small tools, one call per turn, no shell.

Downloaded for testing, in `spike/models` (git-ignored): `granite-4.2-3b-Q4_K_M.gguf` (2.24 GB), `granite-4.0-350m-Q8_0.gguf` (0.38 GB), `granite-4.0-1b-Q4_K_M.gguf` (1.02 GB). Safe to delete.

Research notes: `research_notes/Whole laptop Jazari optimization/`. The research agents' notes are raw material: check a claim here before trusting one there.

## Code review fixes (2026-10-31)

All 32 reported findings fixed; 335 tests pass (27 new, in ReviewFixTests and alongside the code they cover).

- Program execution: StartDetached passes every path and argument through environment variables, so cmd never parses `&`, `^` or `%` in them. /diff, /commit and the gateway's GitSummary empty every filter driver the repo names and turn off fsmonitor and signature checks (verified against a real repo with a planted clean filter).
- Accented folders: X-Project-Path is percent-encoded by the CLI and decoded by the gateway.
- Data: /edit keeps the file's encoding and refuses non-UTF text; /undo writes back the exact bytes, and only if the file still holds what /edit wrote; edits can't go through symlinks or junctions. Diffs leave out secret files.
- Gateway: cut-off or invalid-JSON answers aren't cached and report finish_reason "length"; stream errors are errors; a stopped note update reschedules itself and its prompt has a size budget; a full queue cancels nothing; keys.json is swapped in atomically and recovers from damage.
- Launcher and tuning: only this install's server processes are stopped or watched; a port conflict is reported instead of blamed on tuning; the fallback is for one start only and isn't saved; a failed device listing keeps the saved tuning; the fingerprint now includes the ggml DLLs, so **every PC re-tunes once after this update** (a few minutes on the T480).
- Scripts: gateway and app are swapped together or not at all; the launcher runs an updater copy kept in the install folder, replaced only by one that parses; PATH stays REG_EXPAND_SZ with its %VAR% entries; -InstallDir with a trailing backslash works.

## Install and update tested end to end (2026-10-31)
A full install into a temporary folder on this PC, then `jazari update` the way the launcher runs it: clone, model check, llama.cpp download and check, both builds, config, the `jazari` command and PATH all worked; the update exited 0 and `jazari --help` ran from the installed command. Everything was removed afterwards.

- Found and fixed: the installer called Path.TrimEndingDirectorySeparator, which Windows PowerShell 5.1's .NET Framework doesn't have, so **the installer from the review fixes failed on every PC** before doing anything. It now trims by hand.
- Hardened: the installer's git calls and the updater's download are judged by exit code, so git and curl progress on stderr can't fail a run whose output is redirected to a log (a console run, which is what the launcher does, was never affected).
- `.gitattributes` now checks every file out with CRLF, whatever core.autocrlf says.

## Canvas interface and opt-in tuning (2026-11-02)

- **Tuning only runs when asked.** A start uses the saved settings, or safe CPU settings when nothing was measured. `jazari update` installs the files first, then asks; `jazari tune` measures any time.
- **Mistake to not repeat:** the session ran `jazari update` and the Jazari app from inside Claude Desktop, against the rule below in "Traps". Through the redirected `AppData` it saw a stale copy of the install and reported the real, up-to-date install as 35 commits behind. Its `git reset --hard` then deleted tracked files from the real source copy and recreated them only in the redirected one (`unable to create file ...: File exists` on the first try), which left the real `jazari update` saying its updater was missing. The installer, run from Eni-M's own terminal, repairs that.
- **The interface follows the Jazari CLI Interface canvas**, which replaces the spec's "monochrome, no boxes" with its accent color and backgrounds: a gutter of short labels (time, `copy 1`, `read`, `why`, `fix`, `cut`), the panel around the prompt (command menu above it; file chip, hint, model, device, speed and context meter below it), a `?` panel for /edit, type-ahead while the servers start, and a live tuning block. Piped output is unchanged.
- PrettyPrompt is gone: it can't draw below the input or put the menu above it. `Terminal/PromptEditor.cs` replaces it, and reads the old history file.
- Deliberately not like the canvas: the header is printed once and scrolls away instead of being pinned (the panel is pinned, see "one screen owns the window" below). Text scrolled out of a terminal's scroll margins never reaches its scrollback, so pinning with margins would lose the conversation. The canvas's window background also isn't painted: the terminal's own shows.
- An answer no longer prints its id, tokens and cache use in a real terminal; `/details` has them.

## Made-up answers traced and fixed (2026-11-02)

Answers like "tokens are limited by your subscription plan" and a fake GitHub link had three causes:

- **A junk project note.** The gateway kept a note for the home folder, written by the model from an early "hello" and full of invented tasks, and sent it with every question. Notes now exist only inside a git repository: they're neither sent nor updated elsewhere. The note prompt also forbids inventing anything, and the old row in the database is left alone, unused.
- **The system prompt.** "add only the details" came back as an "Additional Details:" section padded with invented bullets, and later answers copied that shape.
- **No facts about itself.** The model assumed an online service. The prompt now says it runs on this PC, is unlimited and free, and has no account, website or support team.

Tried on Granite 4.1 3B with a temporary llama-server on port 8099, not through Jazari. "As few words as the question needs" gave one-word, often wrong answers; "one to three complete sentences" gave correct ones to the questions tried, at 40-160 tokens. That undoes part of the 68% cut from 2026-10-31, so answers take longer: accuracy comes first. Still wrong: it says Jazari has no GitHub repository, because the prompt doesn't name one.

The panel now sits at the bottom of the window. It's drawn at a fixed position while the conversation scrolls normally, so scrollback still works; scroll margins aren't used.

## Model chosen by the test set (2026-11-02)

`eval/` runs 45 C#/.NET questions plus `/read`, `/edit`, `/review` and a conversation against any model. Granite 4.0-H Tiny on the CPU won under the agreed limits: 37/45, typically 8 s an answer, 47 s for the first answer after `/read`, 12 s for a follow-up. Qwen3 4B scored higher, 41/45, but missed the limits, at 23 s typically and 153 s after `/read`. Compare them again once the law book exists. CPU threads are now capped at half: on the T480 that wrote faster than all 8. Full results: `docs/superpowers/specs/2026-11-02-model-choice-results.md`.

## Context and outside sources (2026-11-02)

Eni-M brought a set of sources, with the must "properly utilize context windows, so the AI doesn't get dumb". Full write-up, with what to use and skip: `reports/Local inference sources for Jazari.md`. Scripts and raw results: `research_notes/Local inference sources/`. Built on `feat/conversation-memory` and pushed to `main` on 2026-11-02, with the memory line of the gate failed and the regression check not run: see the results below.

Measured on H-Tiny with a temporary server, one run each:

- **Speed holds at depth:** 11.45 tok/s writing at 1,100 tokens, 10.61 at 14,800. Only 4 of its 40 layers use attention: 128 MiB of cache at `-c 16384`.
- **Accuracy doesn't:** 12/12 invented facts in a short conversation, 9/12 at 12,700 tokens (swapped similar values), 12/12 with the facts listed right before the question, for about 4 s of reading. Facts removed by a history cut became confident wrong answers.
- **History cuts are cheap on this build:** 11 s with the cache shifted, 228 s from scratch, same score. "Hybrids can't KV-shift" in earlier notes is wrong here.
- **Memory is in the checkpoints:** 55 MiB each, up to 32, and the process reached 9.9 GB. `-ctxcp 8` still answered a retried question in about 1 s.

Recall test through the real gateway, before (`1f72651`) and after (`6de629b`), H-Tiny Q4_K_M on the T480, one run each:

| | Before | After | Gate | Result |
|---|---|---|---|---|
| `short` | 12/12 | 12/12 | 12/12 | Pass |
| `deep` | 9/12 | 12/12 | at least 11/12 | Pass |
| `cut` | 8/12 | 12/12 | at least 10/12 | Pass |
| `median_s` short / deep / cut | 2.05 / 1.95 / 1.8 | 2.25 / 2.9 / 2.25 | at most 30 | Pass |
| `working_set_mib` | 8,767 | 8,711 | at most 7,231 | **Fail**, 56 MiB lower, not 1,536 |
| Regression, 37/45 and limits | 37/45 | 39/45 | at least 37/45 | Pass, see below |

Regression check, run on 2026-11-03 on the same T480, against the saved `tiny-cpu` baseline: 39/45, median 8.5 s, longest 40.5 s, first answer after `/read` 37.3 s (was 46.7), follow-up 8.3 s (was 12.0), `/edit` and `/review` inside their limits. So `-ctxcp 8 --cache-ram 1024` did no harm to quality. It shows no more than that: the saved answers gained 4 questions and lost 1 (`xss-razor`, which missed `Html.Raw`), too few to tell from chance. The faster `/read` and follow-up may also come from the stray `find.exe`, which was using a full core during the baseline and was stopped for this run. The run used `feat/conversation-memory` (`3f70dce`), not `main`. The server flags and the system prompt text are the same on both, compared by diff rather than by a run on `main`. Raw result: `research_notes/Local inference sources/tiny-regress.json`.

Eni-M chose to push to `main` before a 17:00 deadline whatever the result, and will pick it up from `docs/HANDOFF-task-6.md`. `ORD` and `CUS` with ignore-case also match "order" and "customer": one baseline `deep` answer ("prefix CUS-" for order ids) scored right only because of that, so the true baseline `deep` is 8/12. No after answer depends on it. None of the 72 answers echoes the "From earlier" heading. Also new in the after `short` run: 5 of 12 answers add an invented "in an ASP.NET Core application", where the baseline's add nothing like it. Those answers are still right, and the cause isn't known.

Plan (`docs/superpowers/plans/2026-11-02-conversation-memory.md`): search the conversation the CLI already sends, and put the best lines inside the question, no model summaries; say when history is cut; freeze the project note per conversation; fewer checkpoints. ISTA-DASLab publishes GSQ-refined Qwen3 4B and Qwen3.5 4B GGUFs that run on our build, untested here. GSQ itself needs far more GPU than Colab's free tier and doesn't support Granite. Training from scratch is not worth it.

## Interface: a panel that stays (2026-11-02)

The panel stays at the bottom from start to exit. Keys are read the whole time, conversation text is drawn above the panel, and only rows that changed are redrawn, so typing doesn't flicker. Questions sent during an answer wait in a visible queue. Answers flow at a steady pace through `TextPacer`, after holding the first words for a moment (250 ms since the rework below). Ctrl+C stops the answer and drops the queue.

## Interface: one screen owns the window (2026-11-02)

Eni-M asked for the interface "fixed for good": identical to the Jazari CLI Interface canvas, correct on resize, smooth while answers stream. Decisions (Eni-M): inline layout (the conversation stays in the terminal's own scrollback, the panel pinned at the bottom, the header scrolls away), the terminal's own colors (no recoloring of background or cursor), keep the shell's earlier scrollback.

- **Why it was fragile.** Nothing owned the screen: renderers wrote escape codes, and the panel guessed where they landed by counting line breaks, then read the cursor back. An answer's width was fixed when it started, so a resize left stair-stepped duplicates; once the screen was full the whole panel was redrawn on every token; inline styles were lost when a row wrapped; the `/` menu pushed the conversation into the scrollback for good. Character widths were wrong too: every surrogate counted wide, BMP emoji narrow, and the table's CJK-compatibility literal had been normalized to U+8C48, so the Private Use Area (Powerline and Nerd Font glyphs) counted wide.
- **Now.** `TextWidth` measures by grapheme cluster. Every Ui component and every answer is a block that lays itself out at any width (`Terminal/Blocks.cs`, `Terminal/Markdown.cs`), and `Terminal/Screen.cs` draws the window from them. Each frame writes only the rows that changed, placed absolutely and cleared to the end of the line, in one synchronized update and one write; an idle frame writes nothing. The conversation scrolls by real line breaks on the last row, so it lands in the scrollback. Nothing reads the cursor back. A resize clears the window (not the scrollback) and lays everything out again. The menu, queue and a tall prompt grow over the conversation instead of scrolling it away. Answer markers (`code`, **bold**) are hidden as they arrive, so only the last row of the line being written can change. Input drains every waiting key: a paste is one insert, and a line break followed by more keys stays a new line.
- **Geometry from the canvas** (`Terminal/Layout.cs`): gutter labels end at column 8 and text starts at column 10; an 86-column text column; thin box edges for code, diffs, the fix command, the chip and the skip button drawn with one-eighth blocks (▁▔▕▏) in Windows Terminal and VS Code, backgrounds only elsewhere.
- **Deliberately not like the canvas:** the window background and the accent block caret (the terminal's own), rounded corners and 1 px borders (one-eighth blocks instead; no side edges on code and diff boxes, so a mouse copy stays clean), 11–13 px text (color only), the tuning bar inside a session (tuning only runs when asked; the session shows "Starting the model · Ns"), dimming the previous answer while the menu is open, and a pinned header.
- **Checked here, on Linux:** piped output is byte-for-byte unchanged (golden tests); the five artboards drawn at 124×31 compared side by side with the canvas; the real `jazari` run in a pseudo-terminal against a fake gateway, through a question, a streamed answer, a resize to 80×24 and back, the menu and exit, replayed through a terminal emulator with no row ever wrapping on its own.
- **Still to check on Windows** (from Eni-M's own terminal, in Windows Terminal, conhost and the VS Code terminal): start with earlier output on screen and type during startup; a long answer with code, scrolling up while it streams; resize narrower, wider, taller, shorter and maximized during an answer; paste 500 lines; CJK and emoji; the menu with a full screen; `/edit` with y and n; the gateway offline; Ctrl+C, Ctrl+C twice, Ctrl+Break, closing the window, Ctrl+L; `NO_COLOR=1` and 16 colors in conhost.
- **A taller window no longer repeats rows (2026-11-03).** Eni-M's paste showed 8 rows of an answer twice, though the stored answer holds them once. The screen redrew rows from the scrollback when the window grew, assuming the terminal had brought them back. Windows Terminal (`Terminal::UserResize`) keeps its top row and adds blank rows at the bottom, and xterm.js does the same on Windows; only xterm.js elsewhere brings rows back. Now a taller window shows blank rows between the conversation and the panel until new text fills them, and the scrollback keeps the rest. Also fixed: shrinking a window with little text moved the first visible row past the end, so the next rows of a streaming answer never showed. That the paste came from a resize is inferred: the test reproduces the same pattern and a streaming test without a resize shows no repeats, but nobody saw the window change. Checked in the emulator only, so it's on the Windows list below.
- **Resizing redraws at every step** (Eni-M: "redraws for each resize movement, so it's always good"). The size is checked every 8 ms while the window is being resized and every 33 ms otherwise, and each change is drawn at once, without waiting. Windows Terminal reflows its screen the instant it narrows, before any program can redraw, and a row whose text reaches past the new edge wraps and pushes rows into the scrollback. So no row ever has text in the right margin (2–4 columns, the header and panel lines included), and while a resize is in progress everything is laid out 8 columns narrower still; 0.4 s after the last change it's laid out at full width once more. Checked here by dragging the real jazari from 124 to 70 columns one column at a time in a pseudo-terminal, replayed through an emulator that reflows like Windows Terminal: no row reflowed, none wrapped. A jump of more than about 10 columns at once (snapping the window to half the screen) can still make the terminal wrap a few rows into the scrollback; the screen itself is redrawn correctly right after. Ctrl+L redraws the window.


## Review false alarms and follow-ups (2026-11-04)

A review of one EF Core configuration file returned 27 findings, none of them real, and every full part filled the limit of 5. Measured on Granite 4.0-H Tiny on the CPU, 2 seeds each, findings on clean code per run:

| Prompt | Planted bugs found (of 3) | Clean files in the planted part | Clean schema part | 3 parts of a real schema file |
|---|---|---|---|---|
| Old prompt, up to 5 findings | 3, 2 | 1, 1 | 5, 5 | 15, 15 |
| Old prompt, up to 3 | 3, 2 | 0, 1 | 3, 3 | 9, 9 |
| "Empty list is the usual answer", up to 5 | 3, 3 | 0, 0 | 1, 1 | 9, 8 |
| **"Empty list is the usual answer", up to 3 (shipped)** | **3, 3** | **0, 0** | **1, 1** | **8, 6** |
| Asking for a verdict first | 0, 0 | 0, 0 | 0, 0 | 0, 0 |

The verdict field stops everything, real bugs included, so it stayed out. The shipped prompt still reports false alarms on schema code, and only 3 planted bugs measure what it finds: try it on real code with logic before trusting it further. The answer limit went from 500 to 1024 tokens: replies reached 494 and 535.

Follow-ups: history reached the model and it stayed on topic in every replay. The failures came from `/exit` ending the conversation, `/docs` answers not joining it, and the law book searching a follow-up's own words ("more" found a page titled Learn more). `/docs` answers now join the conversation, and a follow-up under three search words with no API name isn't searched. Conversations still don't survive `/exit`.

## `/fix` (2026-11-05)

`/fix` builds, reads the compiler's errors, proposes one change to the first editable file, writes it on yes and rebuilds. Plain code decides everything except the edit. The model also gets the code the errors name, and the law book's page for the error code when a book is installed. Compile errors only: running tests needs a sandbox first.

Measured by `eval/fix_bench.py` on 12 seeded errors in a small project, Granite 4.0-H Tiny on the CPU, no law book. The ship rule, set before the run: at least 7 fixed, none left worse, median under 2 minutes. Other work shared the CPU during the run, so the times are on the slow side.

| Result | Seeds |
|---|---|
| Fixed, 7 | CS0103, CS1061, CS0246, CS0029, CS0161, CS8618, CS4014 |
| The edit couldn't apply, nothing written, 3 | CS7036, CS1002, CS0266: its search text wasn't in the file, or appeared twice |
| Written, no better, undo offered, 2 | CS1503, CS0535 |

Median 36 s, and none left with more errors. That only just passes. Every fix was read: none deleted code to hide an error, though `string?` for CS8618 and `"5"` for CS0029 are the easy way out, so the build passing doesn't mean the change is what the user wanted. A failed CS1503 also changed an unrelated line, which the undo question covers. Twelve seeds in one small project say little about real code. The failed edits point at the next thing to try, a second try when the search text doesn't apply; new seeds would have to judge that, not these.
