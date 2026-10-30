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

## Research so far (full notes in `research_notes/Whole laptop Jazari optimization/`)

- **Biggest speed gain is a second RAM stick:** estimated 6.2 to about 9 tok/s, roughly $40 to $120. Paid, so optional.
- **Speculative decoding:** the draft model must have Granite's exact tokenizer. Qwen can't be one. Granite 4.0 350M, 1B or H-Tiny are candidates; Granite 4.2 can't use them.
- **`--poll` probably does nothing on this OpenMP build.** The real spin control is `KMP_BLOCKTIME`. To test.
- **Models to try:** Granite 4.2 3B for chat, Mellum2.1 12B-A2.5B for overnight batch. Neither is tested here.
- **Whole-project review pattern:** tools find, code proves, the model explains. Batch small files into 6k to 10k token units, use grammar-forced JSON with a required quote checked by string match, and resume from a JSONL file keyed on content hash. Small models are weak at this, so their verdict sorts findings and never deletes them.

## Status

- [x] Research: speed, models, whole-project review
- [ ] Research: file tools and clean output
- [ ] Laptop measurements (speed test running)
- [ ] Whole-project review feature
- [ ] Clean code output and copy
- [ ] File tools for the model
