# Jazari: accurate answers and a real C#/.NET partner

Status: design, for discussion with Eni-M before anything is built. Date: 2026-11-02.
"Eni-M:" marks what Eni-M said. Everything else is Claude's call, because Eni-M handed over the decisions and asked to discuss them on this plan.

## Why

Eni-M wants a local C#/.NET partner they can trust on their T480. Today it's unreliable: on 10 test questions the current model got 4 right, 2 partly right and 4 wrong (see Findings).

Eni-M, what a good session looks like:
- "I ask it for DI, and it answers it properly." Then it offers the next step, like "do you wish to know how to properly implement it?"
- It knows when to mention `/read`: "if you are in a real project and are confused about DI, use `/read` and I'll find a proper example and explain it to you."
- "If I'm having a bug, it reads the logs of the errors, and either asks me to fix it (with instructions) or it fixes it itself", showing what's removed and added in each file.
- Up to about 30 seconds for a question, more for a hard one. Find "a sweet spot of speed and accuracy, but biasing towards accuracy".
- Fine-tune on our data and compress without losing too much accuracy.

## Who it's for

Eni-M, 2026-11-02: the idea is one model optimized to work well on any PC, delivering satisfactory results, free and open source, so others use it and keep working on it. The first users are working .NET developers.

What that means for choices below (Claude's reading):
- They know what DI is. They value a fix for a real bug, answers about their own code, and modern .NET facts with a source they can check.
- One wrong confident answer loses them, so "say you don't know" and the published score matter.
- A free, private, offline tool that's good at everyday C#/.NET is the promise, not cloud quality.
- `/fix` and project search come before the law book.
- A later step, not in this plan: reaching them where they work, in Visual Studio, Rider or VS Code.

## Goals

1. Measure accuracy with a test set, so every later change is judged by a score.
2. Pick the base model from that score. Eni-M asked why only a 3B when there's 16 GB of RAM, which lifts the 2026-10-31 "no new models" rule.
3. Use the fastest device mix this laptop really has.
4. Add the behaviours above: next-step offers, the `/read` hint, and the bug flow.
5. Add a law book: searchable .NET docs, with the source shown.
6. Fine-tune on C#/.NET data, then re-quantize. Keep each only if the score says so.
7. Leave the laptop usable while the model answers. Eni-M: use each component at "around 40%-60%", so "I can work freerly and without interruption".

## Non-goals

- A cloud model or any paid service.
- Matching cloud quality. A small local model will still be wrong sometimes; the point is to be wrong less, and to show sources.
- Autocomplete.
- Other languages beyond what C#/.NET work needs.

## Anti-goals: failure even if it technically works

- Inventing things: file paths, links, API names, accounts.
- Changing a file without showing the change and getting a yes.
- Employer code in training data or the test set. Personal Eni-M repos only.
- A smaller or faster model that scores lower on the test set.
- A test question leaking into training data, the law book's tuning or the quantization text.
- Running installs, updates or the real Jazari app from inside Claude Desktop (`docs/OPTIMIZATION-LOG.md`, "Traps"). Benchmarks with `llama-bench` or a temporary `llama-server` on another port are fine.

## Constraints

- ThinkPad T480: i7-8650U (4 cores, 15 W), one 16 GB DDR4-2400 stick (single channel), Intel UHD 620, NVIDIA MX150 2 GB.
- llama.cpp and GGUF. Jazari's gateway, CLI and installer already depend on them.
- Free training only, on Colab's free T4 GPU: sessions of up to about 12 hours, which can end early, so training saves checkpoints often. Google publishes no weekly limit.
- Time per question: usually at most 30 s, measured from Enter to the end of the answer. Hard questions may take longer, up to about 2 minutes, with visible progress.

## Findings, measured 2026-11-02 on this laptop

Runs were short, the machine was warm, and the first CPU run looks throttled, so treat small differences as noise. The grading was Claude's, by reading the answers. Direction, not proof.

### Why not a much bigger model

RAM isn't the limit; speed is. Writing speed follows memory bandwidth, and one stick means single channel. Research estimates an 8B dense model at about 2.4 tok/s here, or about 70 tokens in 30 seconds: too little for a useful answer. A second stick is the one hardware change expected to help (6.2 to about 9 tok/s by estimate, $40 to $120). That's Eni-M's decision; nothing here depends on it.

### Using the CPU and both GPUs

| Setup, Granite 4.1 3B unless stated | Reading tok/s | Writing tok/s |
|---|---|---|
| CPU, 8 threads | 27.3 | 5.2 |
| **CPU + MX150, 18 layers on the MX150** | **56.0** | **6.9** |
| CPU + MX150 + UHD 620 | 10.9 | 3.5 |
| CPU, Granite 4.2 3B | 27.6 | 4.8 |
| CPU, Qwen3 4B | 26.0 | 5.3 |
| **CPU, Granite 4.0-H Tiny (7B weights, about 1B used per token)** | **45.0** | **9.1** |

- All three together is the worst. The UHD 620 shares memory and power with the CPU, so it competes with it. Drop it.
- The saved tuning on this PC is CPU only (36.9 / 5.7), though CPU + MX150 did better today. Find out why, and fix it if it's a tuner problem.
- H-Tiny with some layers on the MX150 hasn't been tried.

### Half power, measured because Eni-M asked for 40-60% per component

| Setup | Reading tok/s | Writing tok/s |
|---|---|---|
| 3B, CPU, 4 threads (about 50% in Task Manager) | 25.4 | 6.1 |
| **3B, CPU 4 threads + MX150 18 layers** | **59.5** | **6.6** |
| H-Tiny, CPU, 8 threads | 35.0 | 6.4 |
| **H-Tiny, CPU, 4 threads** | **36.0** | **10.1** |
| H-Tiny, CPU 4 threads + MX150 12 layers | 35.1 | 8.3 |

- Half the threads wrote faster than all of them: this CPU has 4 real cores, and the other 4 threads compete for the same memory. So half power is both faster and kinder to other work.
- Jazari will cap the CPU at half its threads and try low priority, so the model gives way when Eni-M is working.
- More hardware use doesn't make the model smarter by itself. Speed only buys room for more text or a stronger model in the same 30 seconds.
- The disk can't compute; it only holds the model. The UHD 620 stays out, since it slows the rest down. The MX150 is used only where it helps, which is reading on the 3B.

### Answer quality, 10 questions per model, one run

| | Granite 4.1 3B, CPU + MX150 | Granite 4.0-H Tiny, CPU |
|---|---|---|
| Right / partly / wrong | 4 / 2 / 4 | 5 / 2 / 3 |
| Average time per question | 14 s | 17 s |
| Longest | 18 s | 27 s |
| Average answer length | 71 tokens | 108 tokens |
| Tool picked when needed (of 2) | 2 | 2 |
| No tool when not needed (of 2) | 0: it even searched for "hello" | 2 |

- Both got the same three wrong: EF Core raw SQL parameters, a 404 from a minimal API, and whether a record is a value type. Those are facts a law book can supply; style training can't.
- H-Tiny matches Eni-M's picture of using fewer links per step: a mixture-of-experts model uses a small part of its weights each token.
- H-Tiny's risks: about twice the RAM (3.94 GiB file). It may not reuse the prompt cache in long conversations the way the 3B does, which could make later turns slow. It failed the earlier fill-in-the-middle test, which matters only for autocomplete.

### Asking the model to offer next steps backfired

Told to mention `/read <path>` when relevant, the 3B invented a path in almost every answer (`/read examples/dependency-injection.cs`, `/read models/chatgpt/README.md`). H-Tiny mostly ignored the instruction. So the hint must come from Jazari's own code, not the model.

## The compression list Eni-M brought

| Technique | Decision | Why |
|---|---|---|
| float16, int8 weights | No | The model is Q4_K_M, about 5.6 bits a weight. These are bigger and slower |
| int16 activations, LiteRT, TensorFlow toolkit | No | Different toolchain; Jazari runs GGUF on llama.cpp |
| Dynamic range quantization | Already effectively so | Q4 weights, float activations |
| Pruning weights to zero, clustering | No | Dense CPU maths still multiplies zeros; 4-bit already shares values |
| Quantization-aware training | Later, only below 4-bit | Not needed at Q4 |
| Architecture search | No | Needs training at the scale of building a new model |
| Knowledge distillation | Yes | A bigger teacher writes training data |
| LoRA, mixed precision, gradient checkpointing and accumulation | Yes | How fine-tuning fits Colab's free T4 |
| Active learning | Yes | Jazari's wrong answers become training data |
| Domain imatrix quantization (added) | Yes | Tunes quantization to C#/.NET text for CPU time only |
| Going up in bits, like Q5 or Q6 (added) | Test it | Eni-M biases towards accuracy, if the time allows |
| Dropping whole layers | Last, optional | The one shrink with real speed gain; costs accuracy |

Eni-M's idea of a smaller network visited more times: a model already passes through every layer once per token, and a smaller one knows less per pass. The version that works repeats at the answer level: draft, check, fix. It only fits where a real check exists, so it's used on `/edit` and `/fix`, with `dotnet build` as the check.

## The work, in order

Each phase ends with a score or a working feature, and can stop there.

### 1. Test set and scoreboard

- 40 to 50 C#/.NET questions: concepts, API facts, code, and security. Include the three both models failed.
- Each has what a right answer must contain and must not contain, checked automatically, with answers saved for reading by hand. Code answers compile where that's practical.
- A script runs the set against a model and settings through a temporary `llama-server`, using Jazari's real prompt, and reports score, time per question and tokens.
- Also project scenarios, each with its own limit: a question after `/read` of about 2,000 tokens, a follow-up after that, `/edit`, and one `/review` part. Plain questions: at most 30 s. First answer after `/read`: at most about 2 minutes. A follow-up after `/read`: at most 30 s when `/read` already paid for the reading.
- Kept in the repo. Never used for training.

### 2. Choose the base model and devices

- Run: 4.1 3B on CPU + MX150, H-Tiny on CPU, H-Tiny with MX150 layers, 4.2 3B and Qwen3 4B. All are already in `spike/models`.
- Also run a 6-turn conversation to see whether later turns slow down.
- Pick the highest score that meets the limits above. A model that re-reads everything on each follow-up after `/read` fails, because it makes the main project workflow slow; `/edit` and `/review` must also still work on it. Then ship it through `scripts/jazari-manifest.json`, and make tuning cover it. Tuning never splits layers across two GPUs; the UHD 620 finding stays specific to that chip, so a stronger integrated GPU can still win alone.
- The T480 is the test machine (Eni-M, 2026-10-31): if it runs well here, it should run acceptably on a developer's laptop. Other PCs tune for themselves. A second, smaller model for PCs under about 12 GB of RAM, chosen at install, comes after the first release, since working developers mostly have 16 GB or more (Claude's guess, to check against feedback).
- Default prediction: H-Tiny if the `/read` follow-up test passes, otherwise the 3B on CPU + MX150.

### 3. Conversation behaviours

- **The `/read` hint (written by Jazari, never by the model):** inside a git project, with nothing read yet, after a conceptual answer, one muted line: "In a real project? `/read <path>` and I'll use your code as the example." Shown at most once per session, and never about a path Jazari hasn't checked.
- **The next step:** after an answer, Jazari may show one suggested follow-up the user can accept with one key. Whether the model writes it well enough gets tried first; if not, skip it or use fixed follow-ups per topic.
- **`/fix [error text or log path]`:**
  1. Jazari finds file and line references in the error by its own parsing (.NET stack traces, compiler errors).
  2. It reads only those lines, plus a little around them.
  3. The model explains the cause and proposes the change.
  4. The user chooses: instructions only, or apply. Apply shows the diff and asks, as `/edit` does, but sends the lines around the error rather than the whole file.
  5. One file per `/fix` at first. Errors spanning several files get instructions instead, and a multi-file version waits until single-file works.
  6. In a .NET project, Jazari offers `dotnet build` after an applied change, and the question says this runs the project's own build code. A new error goes back to the model at most twice, and every retry needs a new yes.
  7. `/undo` restores the file as it was before the `/fix`, including all retries. If the build still fails, the changes stay and Jazari says so and mentions `/undo`.
- **Pasted errors:** a pasted stack trace is offered as `/fix`.
- Parsing and reading in code is deliberate: it's exact, costs no tokens to decide, and works on a weak model.

### 3b. Interface fixes (Eni-M, 2026-11-02, next up)

Eni-M: the interface is "meh"; "everytime I type, the thing glitches/refreshes"; "when I give it a question, the typing bar disappears"; "there is no queue message"; and it'd be "cool to wait longer (meanwhile the output is being made) then shown typing fast and smooth". Canvas check first, with Eni-M looking at real screens.

- No flicker: redraw only what changed, inside a synchronized update, never erase and rewrite the panel per key.
- The panel stays visible while an answer is written, and the user can keep typing. Questions typed meanwhile queue, with a visible queue message, and are sent in order.
- Smooth text: tokens are held briefly, then shown at a fast steady pace. The hold has to stay short at 5-10 tok/s, or it adds to the wait.

### 3c. Freeing GPU memory at tune (Eni-M, 2026-11-02)

Eni-M asked for tune to close background GPU hogs. Claude's call: tune shows what's using GPU memory and how much would be freed, and asks. It never closes anything itself, because a browser or video editor may hold unsaved work. This only matters on setups that use a GPU; today's winner runs on the CPU.

### 4. Law book

- Source: Microsoft's public `dotnet/docs`, `dotnet/AspNetCore.Docs` and `dotnet/EntityFramework.Docs` repos, pinned to a commit. Confirm the repo names and licences before shipping.
- Chunked by heading into an SQLite FTS5 index, which the gateway's SQLite should already be able to do. No second model.
- For each question, search; if a passage matches well enough, send at most about 300 tokens of the best passages inside the question. About 8 s of reading on CPU, less with the MX150.
- Show a `source` row in the gutter naming the doc, so answers can be checked. If nothing matches, send nothing.
- Installed by the updater; keep the download small. Keep it only if the score rises.

### 4b. Looking things up on the web (Eni-M, 2026-11-02)

Eni-M: "if I'm asking something the book doesn't have, the model searches for it, answers me, and adds that information on the book."

Claude's design:
- It uses only the search text and trusted documentation sites to start: Microsoft Learn and the official .NET docs. Jazari's code does the fetching, not the model.
- The law book stores the page's own text with its address and date, never the model's summary, so a wrong answer can't poison the book.
- Web text is data, never instructions, so a page can't tell Jazari to do anything.
- Private code stays private. Only a short question goes out, and never text from `/read`, `/fix` or the user's files; the user sees what's sent.
- It's opt-in, and offline stays the default promise. The system prompt's "you can't reach the internet" changes with it.
- It runs up front or between answers, as fetching and re-reading add time.

### 5. Read-only project tools (gated)

- `search` and `read_file`, so "where is DI set up in this project?" works without `/read`. Native llama-server tool calling, one call per turn, at most about 3 per question, and each call shown.
- Only if the chosen model picks tools correctly on a larger test: H-Tiny got 4 of 4 and the 3B 2 of 4, on very few tries.
- No shell tool. All paths go through the existing project and secret checks.

### 6. Fine-tune

- Eni-M, 2026-11-02: private repo code may go to Colab, and only Colab, for training. Never Kaggle or any other service. This is the one exception to "nothing private leaves the laptop". The private C# repos in scope are EduCore, WareFlow and LearningRoadmap (the 2026-10-30 design's list).
- Data: Qwen2.5-Coder-14B on Colab writes questions and answers seeded from the allowed sources; only code that compiles is kept. Corrected wrong answers are added: those from the test set, and those Eni-M reports. Eni-M reviews any corrections from real conversations before use, and nothing is collected automatically, because logged prompts can hold employer code. About 5% general data, to limit forgetting.
- **The public model trains on public data only:** Microsoft's docs and open-source C# projects whose licences allow it, because a model can memorise its training data. Private repos only go into an optional private version for Eni-M's own laptop, built and installed from a local file, never published. The updater needs a local-file path alongside `modelUrl` for that.
- The public model is published on Hugging Face under the base model's licence terms.
- QLoRA with Unsloth, fp16, GGUF export. Granite 4.0-H has an Unsloth notebook; Granite 4.1 isn't confirmed. Test this first.
- Expect style and conventions rather than new facts, per `reports/Advanced training methods feasibility.md`.
- Ship only if the test set score rises and nothing general gets noticeably worse.

### 7. Quantization, then optional layer dropping

- Build an imatrix from C#/.NET text outside the test set. Compare Q4_K_M, Q5_K_M and Q6_K on score and time; pick the best score that keeps the time limit.
- Layer dropping only if speed is still the problem and the score holds. Likely never.

## Decided by Claude, for Eni-M to check

- Test set first, law book before training.
- The UHD 620 is out; CPU + MX150 stays a candidate.
- H-Tiny is the favourite, but the score decides.
- Hints and bug-file lookup live in code, not in the model.
- A plain answer gets no draft-and-fix loop: there's nothing real to check it against, and it would burn the time limit.
- Tools come after the law book and only if tested.
- `/fix` is single-file at first, with one undo covering it and its retries. A failed build keeps the changes and says so.
- Project work counts in choosing the model, with the limits in phase 1.
- The T480 is the test machine for choosing the model. One model for now, with a smaller one for low-RAM PCs after the first release.
- The CPU is capped at half its threads on every PC, even where more threads would be slightly faster, because Eni-M wants to keep working.
- Wrong answers for training are never collected automatically.
- Public model: public data only. Private version: Eni-M's laptop only.

## Before an open-source release (a later plan)

- Scan the repo and its history for secrets, then make it public with a licence. Check that the licences of Granite, the teacher model, the training data and the law book allow redistribution.
- **Automatic model choice (Eni-M, 2026-11-02):** `jazari tune` picks the most suitable model for the PC, then tunes it (threads, GPU layers, context, priority). Claude's design: a catalog of models and quality levels, each already optimized and scored by us. Tune rules out what doesn't fit the RAM, measures speed on the PC, and applies the same rule as `pick`: the best published score that meets the time limits. Each model is at most one download, and it asks before downloading. This replaces the separate low-RAM model.
- **The choice at tune (Eni-M, 2026-11-02):** tune asks which the user wants: Jazari's own model, fine-tuned on .NET data, or an open-source general model suited to their PC, possibly bigger. Both get the same add-ons: law book, commands and PC tuning. Claude's design: each option shows its test score, expected time per answer and download size for that PC, and the recommended one is the default. `jazari model` changes it later. Only models we've scored and whose licences allow it are offered; `/edit`, `/review` and `/fix` must work on each.
- Fine-tuning and quantization aren't done on the user's PC: they need a GPU, hours and training data, and they'd give every user the same result. We do them once per catalog entry and publish the result.
- Install from a public source, with no sign-in. Windows first; Linux and Mac after.
- Publish the test set and its score, so people can see how good it is.
- Harder test questions a working developer would ask, added before the release score is published.

## Needed from Eni-M

- Before phase 6: a Google account for Colab, and starting the training sessions there.
- At any time: whether to buy the second RAM stick.
- Trying the results: Claude can't run the real Jazari from Claude Desktop, so each shipped phase needs `jazari update` from Eni-M's own terminal.

## Left to the builder

File layout, script language, exact thresholds for passage matching and for "conceptual answer", index schema, and how the score is reported.
