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

## Real-task speed test (Granite 4.1 3B, CPU, 1,480-token prompt, 200-token answer)

| Setting | Task | Prompt tok/s | Answer tok/s | Total |
|---|---|---|---|---|
| `-t 6` | edit | 34.2 | 3.63 | 98 s |
| `-t 6` | review | 23.4 | 2.59 | 140 s |
| `-t 4 -tb 8` | edit | 24.5 | 3.11 | 124 s |
| `-t 4 -tb 8` | review | 20.8 | 3.36 | 131 s |
| `ngram-simple` | edit | 21.9 | 2.99 | 134 s |
| `ngram-simple` | review | 23.7 | 3.68 | 117 s |
| `ngram-mod` | edit | 20.9 | 3.52 | 127 s |
| `ngram-mod` | review | 23.3 | 3.69 | 118 s |
| `ngram-map-k` | edit | 25.1 | 3.22 | 121 s |

The later rows were still running or unread when this was written. The MX150 draft row used a Qwen draft model, which research says can't work. Retest with a Granite draft.

What this shows, with caution because heat and other work moved the numbers:

- **Answers are about half the benchmark speed** (3 to 3.7 tok/s, against 6.2). My guess, not yet tested: `llama-bench` measures answers with an almost empty context, and each answer token gets slower as more prompt sits in context. Test this first by measuring answer speed at 0, 1.5k, 6k and 12k tokens of prompt. The result sets the review batch size. If 6k tokens of prompt means about 1 tok/s, batches must be smaller.
- **N-gram speculation didn't help.** Few drafts were accepted (31 of 204 at best).
- `-t 4 -tb 8` wasn't better than `-t 6`.

## Code on this branch

**Written but not yet compiled or tested** (Eni-M asked for it to be pushed before tests run):

- `src/MyOwnLLM.Cli/CodeBlocks.cs`: pulls the exact code out of an answer.
- `/copy [n]` in `Session.cs`, using TextCopy, which PrettyPrompt already brings in. Answers now end with a hint such as `2 code blocks, /copy 1`.
- `MarkdownStream.cs` numbers each block, for example `[1] csharp`.
- Two new tests in `SessionTests.cs`.

**Known to fix first:** `MarkdownStreamTests.Code_fence_keeps_code_and_shows_language` still expects `csharp` and must expect `[1] csharp`. Other fence tests may need the `[n]` line too. If `TextCopy` doesn't compile as a transitive reference, add a direct `PackageReference`.

## Plan for the rest

My decision: explicit commands first, not a free-running tool loop. At about 3.5 tok/s, a model that explores on its own wastes minutes per step, and a command that does the reading itself is predictable. A tool loop can come later (see `agent_tools_and_output.md`).

1. **Fix and test `/copy`**, then commit.
2. **`/read <file or folder>`** in `Session.cs`: reads text files into the next question, up to what the context allows, skipping generated code, data dumps and secrets (`Migrations`, `*.Designer.cs`, `*.g.cs`, `node_modules`, `bin`, `obj`, `dist`, lock files, `*.sql` dumps, `.env`, keys). It says what was left out. Use `git ls-files` when the folder is a repo, finding git as `UpdateCheck.FindGit` does.
3. **`jazari review [path]` and `/review [path]`:**
   - Files in risk order: auth, controllers, payment, upload, SQL, config, middleware first.
   - Batch into units sized from the context-speed test above.
   - Add an optional JSON schema to `ChatRequest` and `LlamaClient.ChatBody` so answers are forced into a findings shape: file, quote, problem, severity, fix.
   - Keep a finding only as "verified" when its quote is really in the file, and take line numbers from that match. Unverified ones stay in the report, marked as such.
   - Resume from a JSONL file in `%LOCALAPPDATA%\MyOwnLLM\reviews`, keyed on content hash and prompt version, so only changed files are re-read.
   - Write a Markdown report and print its path. Never write into the reviewed project.
   - Test on this repo, with a planted bug to confirm it can be found.
4. **`/edit <file> <instruction>`:** the model answers with search/replace blocks. Jazari shows the diff, asks y/n, and writes only after yes. It refuses paths outside the project and works only on files in git, so a change can be undone.
5. **Speed checks to run:** context-size test; Granite 4.0 Tiny or 350M as a draft model on the MX150; `--poll 0` on and off; `KMP_BLOCKTIME=0`; Granite 4.2 3B (2.24 GB) as a possible new chat model, checking FIM first, since Granite 4.2 can't use a Granite 4.1 draft model.
6. **Later:** static tools as a first pass (gitleaks, Opengrep, osv-scanner, NetAnalyzers), and Roslyn for effective auth and data flow.

Paid option to report to Eni-M, not needed by anything above: a second 16 GB DDR4 stick for dual channel.

## Status

- [x] Research: speed, models, whole-project review
- [ ] Research: file tools and clean output (`agent_tools_and_output.md`, check whether it finished)
- [ ] Laptop measurements (first pass done, see above)
- [ ] Clean code output and copy (written, untested)
- [ ] `/read`
- [ ] Whole-project review feature
- [ ] `/edit`
