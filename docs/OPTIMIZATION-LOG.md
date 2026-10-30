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
- [ ] Measure and build the safe wins on branch `jazari-optimize`, tests passing, one commit each
- [ ] List what waits for Eni-M under "Decisions waiting"

**Found 2026-10-30, session 2: this session runs on a different PC from the one measured above.** All measurements in this file come from the T480 (user `enim`). This PC (user `techcom`) is a Lenovo 20X3 with an **i5-1135G7** (Tiger Lake, 4 cores, 8 threads, AVX-512 and VNNI), **32 GB DDR4-2667 in two channels** (about 42 GB/s theoretical, twice the T480), Intel Iris Xe (96 EU, shared memory), 377 GB free on C:, Balanced power plan. Jazari, llama-server and the model are not installed here. The research report targets the T480. Its hardware advice (MX150, single channel, quant choice) does not carry over; its bridge and usability advice does. On this PC, answers should be roughly twice as fast (bandwidth), Iris Xe is a real option through llama.cpp Vulkan/SYCL or OpenVINO, and the AVX-512 path in llama.cpp applies.

### Decisions waiting for Eni-M

- **Which PC hosts Jazari:** the T480 or this i5-1135G7 PC? Measuring here needs llama.cpp and the model downloaded into `spike/` (git-ignored, not an install), which needs Eni-M's OK

- Merge `jazari-optimize` into `main` (see "Next")
- Paid RAM upgrade (see "The hardware answer")

## Rules that still apply (from the design doc, `docs/superpowers/specs/2026-10-30-myownllm-design.md`)

- **General-CRM and other company repositories are never used for data, evaluation or tuning.** Test only on Eni-M's own code (EduCore, EduCoreFront, WareFlow, LearningRoadmap, this repo).
- Nothing private leaves the laptop. Web research must not quote private code.
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

- **Claude Desktop redirects `AppData`.** Anything run from a Claude Desktop session writes `%APPDATA%` and `%LOCALAPPDATA%` into `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache`. To read the real files from there, use `\\localhost\C$\Users\enim\AppData\...`.
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
