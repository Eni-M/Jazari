# Local inference sources: what Jazari should use, change and skip

Date: 2026-11-02. Machine: the T480 (i7-8650U, 16 GB single channel). Model: Granite 4.0-H Tiny Q4_K_M. llama.cpp build 11982.

**The biggest win isn't a server flag, a quantization or a bigger model. It's keeping the right few facts next to the question.** Measured here, H-Tiny recalled 12 of 12 facts in a short conversation and 9 of 12 at 12,700 tokens, swapping in similar values from elsewhere in the conversation. A short list of the facts placed right before the question brought it back to 12 of 12, for about 4 seconds of extra reading. Facts lost in a history cut came back as confident wrong answers.

So the answer to "delegate the chat to RAM" is yes, with one change. On a CPU the model's working memory (the KV cache) is already in RAM, and on H-Tiny it's tiny. What's missing is a searchable store of the whole conversation that Jazari's code pages back in, the same design as the law book.

Evidence tags: **[measured]** run on this T480 today; **[theirs]** the source's own measurement, on other hardware; **[estimate]** arithmetic or reasoning; **[repo]** read from this code. Scripts and raw results: `research_notes/Local inference sources/`. One run each, so read the small numbers as direction, not proof.

## What to do, in order

Each item ends in a yes or no from the test set or the recall test. Nothing here is built yet.

| # | Change | Why | Cost | Keep it if |
|---|---|---|---|---|
| 1 | **Conversation memory:** search the conversation with BM25 and put the best-matching old lines (up to about 250 tokens) right before each question | Fixes both the depth errors and the cut errors, see "Context" | Small: one gateway class, no storage | Recall stays at or near 12/12 at depth and after a cut, using real search rather than a hand-made list |
| 2 | **Say when history is cut,** with a `cut` row | Today's cut is silent [repo], and the model then invents answers [measured] | Very small | Always |
| 3 | **Keep the prompt's start stable:** freeze the project note per conversation | A changed note forces a re-read of everything after it. History cuts don't [measured] | Small | Follow-up after a note change stays under 30 s |
| 4 | **Cut memory use:** `-ctxcp 8 --cache-ram 1024` | The server reached 9.9 GB, mostly checkpoints and saved prompts [measured] | One line in `Tuning.ServerArgs` | Retried questions still answer in about 1 s, and memory falls |
| 5 | **Add the recall test to `eval/`** | The current test set never goes deep, so it can't see this problem | Small | Always |
| 6 | **Test ISTA's GSQ files** on the test set: Qwen3 4B Q2_K and Q3_K_M, Qwen3.5 4B Q2_K_XL | Qwen3 4B was the most accurate but too slow. These are smaller and need no training | 5.7 GB download, then hours of eval | Beats H-Tiny's 37/45 inside the time limits |
| 7 | **Law book gating:** decide "nothing relevant" from evidence, not a score threshold | reame measured that similarity scores can't detect absence [theirs]. The spec's "matches well enough" needs this | Part of the law book work | Fewer wrong sources on questions the book can't answer |
| 8 | **Answer check:** flag file paths, API names and links in an answer that aren't in anything Jazari read | Anti-invention, in code. Doesn't catch swapped values | Small | Flags real inventions without much noise |

Later, smaller: an evidence block on every eval run, a Best Performance power plan test, a newer llama.cpp re-run, `--sleep-idle-seconds`, per-tensor quantization. See the tables below.

## Context: the must

### What was measured

Temporary `llama-server` on port 8099 with Jazari's settings (`-t 4 -c 16384 -np 1 --cache-reuse 256 --cache-ram 2048`), this repo's own C# as filler, and invented project facts. Jazari itself wasn't touched.

**Speed barely changes with depth. This is the opposite of the dense 3B.** [measured]

| Tokens in the conversation | Writing tok/s | | Same test, Granite 4.1 3B (earlier log) | Writing tok/s |
|---|---|---|---|---|
| 1,100 | 11.45 | | 111 | 6.75 |
| 6,700 | 10.97 | | 1,420 | 5.45 |
| 14,800 | 10.61 | | 5,244 | 2.82 |

The reason is in the model's header [measured with `gguf_meta.py`]: only 4 of its 40 layers use attention, with 4 cache heads each. The other 36 are Mamba layers with a fixed-size state.

**Cached work is cheap too.** [measured]

| Situation | New tokens read | Reading time |
|---|---|---|
| Short follow-up at 14,900 tokens | 22 | 0.9 s |
| New question, same history, at 6,700 tokens | 18 | 0.8 s |
| History cut at 8,500 tokens, cache reused | 355 | 11.4 s |
| The same cut text read from scratch | 8,561 | 227.9 s |

**Accuracy does change with depth.** Twelve invented facts in six confusable pairs, one per turn. Temperature 0.2. [measured]

| Test | Score | What went wrong |
|---|---|---|
| A. Short conversation, 400 tokens | 12/12 | |
| B. Deep, 12,700 tokens, nothing pinned | 9/12 | "Invoices 2 years" (the audit log value), "payment timeout 20" (login), "frozen on Sundays" (backups) |
| C. Deep, facts pinned as `label: value` before the question | 12/12 | 3.4 s of extra reading |
| D. Deep, same facts pinned as sentences | 12/12 | 4.2 s of extra reading |
| E. After cutting 4 pairs (8,500 tokens, cache reused), facts still present | 6/8 | Same two wrong as G |
| G. Same cut, read from scratch, facts still present | 6/8 | Same two wrong as E |
| F. After the cut, cache reused, facts removed | 1/4 | "Refund retries 5 times", "staging port 6432": confident and wrong |
| H. After the cut, read from scratch, facts removed | 1/4 | Same, except one honest "the code does not provide" |

What this says:

1. **Deep context makes H-Tiny confuse similar things, not lose speed.** Every error is a real value from elsewhere in the conversation. That matches reame's finding: the model isn't blind to the value, it fails to bind it to the question.
2. **A short list near the question fixes it.** Sentences did as well as `label: value` here. reame's format effect came from a 2,137-token page, not a 100-token list, so this doesn't test it.
3. **A larger context wouldn't help quality.** It would be cheap, 128 MiB at 16k and so 256 MiB at 32k, and speed held to 15k. But more raw context is what caused the errors. LangWatch points the same way: across 201 audited steps, what an agent actually drew on stayed at 6,000–8,500 tokens across a 16.6-fold range of window sizes [theirs, one person's fleet on large models].
4. **The silent cut is the worst case.** After it the model answers confidently from whatever is left.
5. **The cheap cut is safe enough.** On this test, E and G scored the same. One caveat: llama.cpp shifts the attention cache but doesn't rewind the Mamba state, so the model isn't in the same state as after a fresh read. F's invented "5 times" against H's honest refusal may be that difference, or just noise. Worth a larger test.

Limits: 12 easy invented facts, filler that has nothing to do with them, one seed, and answers checked by pattern. Questions about real code may behave differently, which is why item 5 comes before trusting this widely.

### The design this points to

Everything in it is done by Jazari's code, in the spirit of the spec's "parsing in code is exact and costs no tokens". The model never writes its own memory.

- **Store:** nothing new. The CLI already keeps the whole conversation in RAM and sends it with every request, including what the prompt has to cut [repo]. This is the "RAM" you asked about. `/clear` empties it. An earlier draft of this report proposed a SQLite FTS5 table; it would add storage and clean-up for no gain until conversations survive a restart.
- **Page in:** before each question, BM25 picks the best lines from your earlier messages outside the last two exchanges, up to about 250 tokens. They go inside the question's own message and are never added to history, so the prefix stays stable. They do get re-read each turn, about 8 s at the full 250, though usually far fewer lines match. Only your own lines are searched, because a model's earlier answers can be wrong. `/read` bodies aren't searched yet: no test covers code. reame's measured BM25 versus embeddings is in the next section.
- **Cut:** no change for now. With the list in place, 12,700 tokens scored 12/12 and speed held, so an earlier cut has no measured benefit. Revisit it if the recall test says otherwise. Where `/read` text sits also stays as it is: it goes before the history, and moving it after would make every follow-up re-read the files, which an earlier draft of this report got wrong. The cost that remains is one re-read of the history when `/read` happens mid-conversation.
- **No model summaries.** The junk project note on 2026-11-02 already showed what a small model's summary does here. LangWatch also found operator corrections rising to 41.9% in the five steps after a compaction, against a 17.7% baseline, and that a median compaction kept 0.8% of the window. It concludes that keeping more of the original beats tuning thresholds [theirs]. Amp is reported to have retired its compaction feature for similar reasons [one blog, not checked].
- **Project rules stay outside the conversation.** The system prompt and frozen note survive every cut, the same reason `CLAUDE.md` exists.
- **Tell the user:** a `cut` row saying what no longer fits. Whether a line like "nothing about this earlier in the conversation" stops the model guessing is left for later: since the search still covers cut turns, it would only matter for things never said, and most questions are about neither.

### Memory: what the server actually holds

All from today's server log [measured]:

| Item | Size | Note |
|---|---|---|
| Attention cache at `-c 16384` | 128 MiB | About 8 KiB a token, one tenth of the 3B's 80 KiB. Matches `DeviceTuner.cs:124` |
| One checkpoint | 55.4 MiB | Almost all Mamba state: 36 layers × 3,072 × 128 × 4 bytes [estimate]. Fixed size, whatever the depth |
| Checkpoints allowed per slot | 32 | So up to 1.77 GiB. One is made about every 512 tokens, and `-cms 1024` didn't change that |
| A saved prompt with 32 checkpoints | 1,873 MiB | Went straight into `--cache-ram` at the cut, and would be evicted or reused only later |
| Whole process, 12,700 tokens | 9,565 MiB, then 9,961 peak | Includes the model file's mapped pages, and about 3.6 GB I can't account for |
| Whole process, `-ctxcp 8`, 6,500 tokens | 7,315–7,464 MiB | Not like for like: a shallower conversation, nothing saved yet |

Only the most recent checkpoint was used for a retried question. With 8 kept, it still took about 1 s. So `-ctxcp 8 --cache-ram 1024` should free over 2 GB [estimate], which matters on a 16 GB laptop that must stay usable. Measure at matching depth before shipping, and find out what the other 3.6 GB is.

### KV cache streaming (the fork you mentioned)

**Not for this PC, or for Jazari now.** It solves a GPU problem, where the cache doesn't fit in VRAM. On a CPU the cache is already in RAM.

- The fork is [Raymond Huang's](https://github.com/RaymondHuang210129/llama.cpp-adaptive-kv-streaming). "Troed" is a user supporting it in the [discussion](https://github.com/ggml-org/llama.cpp/discussions/28216), not an author. Related: [giveen's PR](https://github.com/TheTom/llama-cpp-turboquant/pull/357) and [an AMD fork](https://github.com/katalystnord/llama.cpp-kvstream).
- It supports one CUDA GPU, one request at a time, and only Qwen3.8-27B and Gemma 4 31B are qualified. Reported: 26 tok/s at 128k context against 2.5 on stock with unified memory, on an RTX 5070 Ti [theirs].
- Results depend on PCIe: one user on PCIe 4.0 saw a steep drop near 80k context, and another on PCIe 3.0 reported strong slowdown once streaming started [theirs].
- Not merged upstream as of today. Revisit it only if the catalog ever offers a 27B model on 12–16 GB cards.

## carteakey's guide

[Local LLM Inference Optimization](https://carteakey.dev/blog/local-inference/local-llm-optimization/), updated 2026-10-18, AI-assisted, measured on one RTX 4070 Linux machine. Most of it targets GPU and huge MoE models. Read in full; what touches Jazari:

| Advice | For Jazari | Action |
|---|---|---|
| Check RAM speed first (§6.1) | The T480 runs 2400, the CPU's maximum, but single channel. The second stick stays the biggest hardware win | Decision already waiting |
| Measure the whole loop, with an evidence block for every run (§3, §18) | `eval/` records score and times, but not build, sampling, cache state or memory | **Add the block to `run_eval.py`** |
| Discard warm-up, log system state (§3) | Today's second run read about 15% faster than the first, which is the noise the guide warns of | **Add to `run_eval.py`** |
| Four-step acceptance: cold prompt, follow-up, tool loop, long-context stress (§18) | `eval/` has the first two. No deep test | **Item 5** |
| Don't test with a short prompt (§3) | Exactly how the depth problem went unseen | **Item 5** |
| Windows: High Performance plan instead of Balanced (§7.2) | The T480 is on Balanced. Earlier notes say memory, not clocks, limits writing, so the gain is uncertain | Test once, and report in `tune`. Eni-M's own setting to change |
| Linux runs 15–20% faster (§7.1) | Jazari is Windows first | Note for a later Linux release |
| Threads: real cores only (§14) | Already done: half the logical cores | None |
| `--threads-batch` separate for reading (§14.2) | H-Tiny read 36 at 4 threads and 35 at 8 [measured, earlier] | Skip |
| `--poll` makes no difference (§14.4) | Already `--poll 0` | None |
| `--parallel 1` (§11.3) | Already | None |
| KV cache quantization (§11.2) | Saves almost nothing at 128 MiB. Note that a guide this keen on it still calls `q4_0` emergency-only | Skip, keep f16 |
| Flash attention on (§11.4) | Measured on CPU: reads faster, writes slower. The tuner tests it per PC | None |
| `--fit` instead of fixed layers (§10.4) | `DeviceTuner.LayersThatFit` is hand-written. Build 11982 has `--fit`, which accounts for the cache | Try it on GPU PCs. Could delete code |
| `-ot` to keep routed experts in RAM, the rest on GPU (§10) | H-Tiny on the MX150 by layers lost (8.3 against 10.1 tok/s). Expert placement wasn't tried | Low priority test |
| Expert caching (§10.5, Strata) | One model family, needs a large GPU | Skip |
| MTP draft heads (§19) | Granite has none | Skip for H-Tiny. Note for catalog models |
| `ngram-mod` speculation (§19.4) | Earlier n-gram tests gained nothing on the 3B, and drafting did help rewrites. Untested on H-Tiny, where rollback means restoring the Mamba state | Test on `/edit` only, low priority |
| Keep sampling defaults, no repeat penalty for code (§13) | The server default is already off, and temperature 0 warnings apply to thinking models | None |
| `--mlock` / `--no-mmap` (§15) | **These flags don't exist in build 11982.** It's `-lm` now | Leave default |
| Rebuild from source, keep a known good binary (§8.2, §17.4) | Jazari ships a pinned prebuilt build, which is fine. Newer builds can gain speed with no flag changes [theirs] | Re-run `eval/` on the newest build once |
| llama-swap (§16.3) | Only if a second model runs. `--sleep-idle-seconds` gives its idle unload without it | Try idle sleep for the "laptop usable" goal |
| Localhost binding, logs hold prompts, check model sources (§23) | Bound to 127.0.0.1 with an API key already. `interactions` stores prompts [repo] | Mention logged prompts in the docs |
| QAT and iMatrix: calibrate on your own workload, judge by your workload (§9) | Agrees with spec phase 7 | See GSQ-RCO |

## reame

[swellweb/reame](https://github.com/swellweb/reame), MIT, one maintainer, 114 stars. **Take its ideas and its honesty, not its code or engine.** The `nucleo` engine is ARM NEON and its ternary models need that or a vendor fork, so neither runs fast on our x86 Windows laptops. It also describes itself inconsistently: the GitHub description says rewriting input made it about 6x faster, the README is now about ternary engines, the README claims decode +18–22% while its website says +7–14%, and `NOTICE` says all kernels come from llama.cpp while the README says none do. All results are theirs, on one machine. The fact exam is one Italian page and questions they wrote, and they say so.

| Idea | What they measured | For Jazari | Where |
|---|---|---|---|
| **Format beats model** | Same page: prose 19/20 at 40.5 s, `label: value` 20/20 at 6.2 s. OLMoE 7B-A1B, a model in H-Tiny's class, went 14/20 to 18/20 | Strongest idea here, and it needs testing on code. Try a short facts form of `/read` (types, members, DI registrations, routes) built by code, with bodies only where the question points. Fits our log's lesson from `/commit` | `bench/README.md`, `run_fact_exam.py` |
| **BM25, not embeddings, for facts** | Finding the right block: BM25 80% and 95% (top 1, top 3), dense embeddings 25% and 45% | Supports the spec's FTS5 law book and conversation memory. No embedding model needed | `bench/retrieval.py`, 110 lines, no dependencies |
| **Absence can't be scored** | Similarity medians 0.895 for answerable, 0.879 for traps. Computed checks refused 92–100% of near-theme traps | **Changes the law book plan.** Instead of a threshold: refuse when the question names a value, version or API that no passage holds | `bench/assenza.py`, Italian-specific: reuse the idea, not the lists |
| **Anti-invention check** | Every number or time in an answer must appear in a source. Tested in CI | Item 8, for paths, API names, links and versions. By their own note it can't catch the right value on the wrong question, so it wouldn't catch B's swaps | `bench/grounding.py` |
| Warm while nobody waits | `/v1/warm` | **Jazari already has this**, same name | |
| Prefix snapshots on disk | Same request in about 0.5 s across restarts | Log says skip: little carries over. Conversation resume would change that, at about 96 MB of cache plus state for 12,000 tokens [estimate]. `--slot-save-path` does it | `prefix_cache.hpp` for the design |
| Escalate to a bigger model | Replays on a larger model if the answer is blank or matches a pattern | Possible: H-Tiny first, Qwen3 on "I don't know". Costs a second model in RAM and a long wait. Later | `escalation.cpp` |
| Keep the failures | Every benchmark page lists its losses | Our log already does. Keep it up | |
| Calibrate quantization on your own traffic (`sartoria.sh`) | Tie at Q3_K_M (3.18 against 3.14). A win only at Q2_K (3.91 against 4.09) | **Lowers expectations for spec phase 7.** Tailoring may show nothing at Q4. Worth it mainly if going lower | `scripts/sartoria.sh`, 50 lines around llama-quantize |
| Speculation from past output (Palimpsest) | **Their own A/B shows no gain:** 30.19 against 31.38 tok/s, with 46 of 48 answers differing | Don't copy | `bench/palimpsest-ab.json` |
| Fewer experts while reading | Faster reading, but it corrupted the cached state and collapsed at K=2 | Don't try | README, `bench/README.md` |
| Copy values from data instead of generating | "Hallucinating a price is structurally impossible" | Fits a price list, not coding answers. The spirit lives on in the law book showing its source | |

## GSQ-RCO, a 6B model, the shared GPU, and training from scratch

**What it is.** A way to compress a model that's already trained. GSQ relearns which low-bit value each weight gets. RCO decides how many bits each tensor gets, under a size budget. The output is an ordinary GGUF. It doesn't teach a model anything new. Sources: [GSQ](https://github.com/IST-DASLab/GSQ), [RCO](https://github.com/IST-DASLab/RCO), [paper](https://arxiv.org/abs/2604.18556), [paper](https://arxiv.org/abs/2605.00649), [wiki summary](https://aiwiki.ai/wiki/gsq_rco).

**Can we run it ourselves? Not today.** The README's recipe for an 8B model is one 80 GB H100 or A100, and free Colab gives about 15 GB. Its model list covers Llama, Qwen, Gemma 4 and Kimi, with no Granite and no Mamba hybrid. It has only been tested on NVIDIA Hopper. Whether a 4B would fit a T4 with a reduced setup isn't documented, and I didn't try it.

**The published GSQ-RCO files are too big for this laptop.** The smallest Qwen3.8-27B is 8.4 GB, so about 1.5 tok/s here [estimate]. For a catalog on 12–16 GB GPUs it looks excellent: its 11.8 GB file matched the base on LiveCodeBench v6 and AIME25 [theirs], and carteakey measured the 27B at 36.7 tok/s with 262k context on an RTX 4070 [theirs, not necessarily this quantization].

**The useful find: ISTA already published GSQ-refined small models.** [Collection](https://huggingface.co/collections/ISTA-DASLab/gsq). Standard GGUF that runs on our build, Apache 2.0, no training and no GPU. [Qwen3 4B](https://huggingface.co/ISTA-DASLab/Qwen3-4B-GGUF-GSQ), [Qwen3.5 4B](https://huggingface.co/ISTA-DASLab/Qwen3.5-4B-GGUF-GSQ), [Qwen3 8B](https://huggingface.co/ISTA-DASLab/Qwen3-8B-GGUF-GSQ).

On the T480, writing speed follows bytes read per token: the 3B (2.1 GB) gives 6.2 tok/s and Qwen3 4B (2.5 GB) gives 5.3, so about 13 GB/s [measured]. Scaling from that [estimate]:

| Model | Size | Estimated writing tok/s | Their scores | Worth a test? |
|---|---|---|---|---|
| **Granite 4.0-H Tiny Q4_K_M (today)** | 4.2 GB, about 1.2 GB used per token | **10–12 [measured]** | Our test set 37/45 | Baseline |
| `qwen3-4b-Q2_K` | 1.67 GB | about 8 | No published table | **Yes** |
| `qwen3-4b-Q3_K_M` | 2.08 GB | about 6 | No published table | **Yes** |
| `Qwen3.5-4B-Q2_K_XL` | 1.94 GB | about 7 | MMLU-Pro 70.9 against Unsloth's 66.8, GPQA 67.2 against 56.1 | **Yes** |
| Qwen3 4B Instruct Q4_K_M (tested) | 2.5 GB | 5.3 [measured] | Our test set 41/45, missed the limits | Reference |
| A 6B at 2.5 bits a weight | about 1.9 GB | about 7 | None exists in this collection | No |
| `qwen3-8b-Q2_K` | 3.28 GB | about 4 | 56.28 against 50.03 for Unsloth's Q2_K; the full model scores 65.16 | Only for faster PCs |
| `qwen3-8b-Q3_K_M` | 4.12 GB | about 3 | 61.61 | Only for faster PCs |

Cautions. These come from plain Qwen3-4B, not the Instruct-2507 we scored, so 41/45 doesn't carry over. They are thinking models, so test with thinking off as the eval already can. ISTA's scores are reasoning and multiple-choice benchmarks, not C#. Low-bit formats may not get llama.cpp's fast AVX2 path, so these speeds could be high. Reading time may not improve by the same factor: Qwen3's 153 s after `/read` was mostly reading.

So on your question: **a dense 6B at these bit widths would write at about two thirds of H-Tiny's speed, and nobody has published one.** H-Tiny's advantage is structural, since it touches about 1 of its 7B per token. A cheap related test: IBM publishes [H-Tiny at Q3 levels](https://huggingface.co/ibm-granite/granite-4.0-h-tiny-GGUF/tree/main), which should write faster for less accuracy. Include Q3_K_M with item 6.

**The idea we can copy with our own tools: per-tensor precision.** Build 11982's `llama-quantize` has `--tensor-type` and `--imatrix`. Keep attention, Mamba and router tensors at higher precision, let routed experts go lower, and search with the test set as judge. It's a manual, cruder RCO that fits Granite and runs on a CPU. It needs an F16 or BF16 H-Tiny source, which IBM's GGUF repo may not include and Unsloth's might (check). Do it after fine-tuning, since quantization comes last.

**Order matters.** If Qwen3 4B wins, fine-tuning changes the weights, which throws away GSQ's refinement, and we can't re-run GSQ on free hardware. Options: a LoRA loaded at run time on the refined file (llama.cpp has `--lora`, quality untested), or fine-tune with plain imatrix quantization afterwards. Compare on the test set before committing either way. This could decide which base we "mainly use and perfect", so it belongs before phase 6.

**The shared GPU's "8 GB VRAM" isn't VRAM.** It's Windows lending the UHD 620 up to half of the same single RAM stick, so it shares the CPU's bandwidth and power. Measured: 12.5 reading and 4.1 writing on the 3B, the slowest option here, and worse with all three used together. The Iris Xe on the other PC won because it's a far stronger chip with two RAM channels (222 reading, 14.9 writing).

**Dynamic per PC: mostly built.** `jazari tune` already measures each device and saves the winner with a fingerprint. What's left is the spec's catalog, so tune also picks the model: bandwidth and RAM decide what's possible, the published score picks among those. One addition from today: the speed estimate above could rule out models before any download.

**Training our own model from scratch: no.**

| | Tokens | Compute | Result |
|---|---|---|---|
| Our free budget | about 10^8 a week (earlier report) | Colab or Kaggle T4 | |
| [nanochat](https://hackaday.com/2025/10/20/nanochat-lets-you-build-your-own-hackable-llm), reported run | 38 billion | about 33 h on 8 H100s, about $800 | A much smaller model; a 24-hour run reaches the 40s on MMLU. Figures from mirrors of the project, not checked against the original |
| A useful small model (the 8.9B NCP paper, from our earlier report) | 5.73 trillion | Very large | The class we'd need to match |

At our budget, the nanochat run takes about 7 years and the 8.9B run about 1,100 [estimate]. It would also have to learn English and programming before C#, and it would need a tokenizer, data pipeline and evaluation of its own. It breaks the $0 rule without producing anything better than Apache-licensed models we can download. It's a fine learning project and a bad way to get a useful assistant.

"Our own model" should mean what the spec already says: a chosen base, fine-tuned on .NET data, quantized for this hardware, with the law book, tuning and tools around it. That's where a user would actually notice a difference.

## The rest of what you found

| Source | Takeaway | Action |
|---|---|---|
| The diagram, "Getting started with self-hosted AI" | Six generic steps. Jazari has done 1–4, 5 is planned, 6 is `eval/` | None |
| FastAPI gateway post | Same design as Jazari's: the gateway controls, the model reasons. Its "summarized memory" conflicts with what we measured. Its idea of letting the AI read its own documentation is good | Put Jazari's own docs in the law book, so "what can you do?" has a source. Small |
| Advice for a 5070 (Qwen 3.5 9B, Gemma 4 E4B, unsloth, pi) | A GPU recipe. A 9B dense at 5.3 GB would write about 2.5 tok/s here [estimate]. Gemma 4 E4B wasn't checked | Candidates for the catalog on GPU PCs |
| pi.dev | Its point is a small system prompt, which Jazari already has. Not read beyond this | None |
| "The harness matters most" | Agrees with our log | Keep the gated tool loop as planned |
| Tool list (Ollama, LM Studio, vLLM, Jan, textgen, LocalAI) | Nothing replaces llama.cpp here. Ollama hides the flags used above, vLLM needs a large GPU | None |
| "Parameter optimisation with Ollama" | We don't use Ollama | None |
| Learning links (KV cache, batching, attention) | Background reading, not reviewed | Keep as a reading list |
| Reddit summaries on tuning | All covered above | None |
| AutoSwarm, logging chats to inject lessons | Conflicts with the rule that wrong answers aren't collected automatically | Skip |
| Locally Uncensored, n8n, LangChain, Prem Studio | Other products for other jobs. Fine-tuning stays on Unsloth | Skip |

## Corrections to our own documents

- **`README.md:5`** still says Granite 4.1 3B.
- **"Hybrids can't KV-shift"** in `spike/granite-tiny-vs-3b.md`, `reports/Advanced training methods feasibility.md` and the accuracy spec is wrong on build 11982. The log shows "reusing chunk with size 8531, shifting KV cache". Source comments agree: `get_can_shift` asks only the attention cache ([llama-memory-hybrid-iswa.cpp mirror](https://huggingface.co/OpenTransformer/llama.cpp-prismml/blob/main/src/llama-memory-hybrid-iswa.cpp)).
- **"The checkpoint flags don't matter"** in `reports/Jazari model bridge and efficiency.md` was true for the dense 3B. On H-Tiny, checkpoints are active and large.
- **Spec phase 4,** "if a passage matches well enough": replace with computed checks.
- **Spec phase 7:** expect little from imatrix at Q4 and Q5.

## What I read, and what I didn't

- **carteakey:** the whole page.
- **reame:** README, website, `BENCHMARKS.md`, `bench/README.md`, `CONTRIBUTING.md`, `NOTICE`, the config, the issue list, and the code that matters here: `assenza.py`, and parts of `grounding.py`, `retrieval.py`, `prefix_cache.hpp`, `corpus_index.hpp`, `escalation.cpp`, `prompt_lookup.cpp`, `form_draft.cpp`, `conclave.cpp`, `autoconfig.cpp`, `sartoria.sh` and the A/B results. **Not line by line:** the rest of the repo's roughly 9,600 lines, mainly the C++ engine, server and tests, because they can't run or be reused here. The Italian README was read only in part.
- **GSQ-RCO:** the GSQ README and the Qwen3.8-27B card, each up to a length cut near the end. The start of the RCO README, the RCO abstract, the wiki page, three small model cards. The GSQ paper by search, not cover to cover. The RCO paper not read beyond its abstract.
- **KV streaming:** the discussion in full, the fork's README up to a length cut.
- **Compaction:** the first 12,000 characters of the LangWatch study and of the Vaughan article, so LangWatch's later sections are known only from its abstract.
- **Not fetched:** pi.dev, Unsloth's guides, the learning links, the Reddit threads. Your pasted summaries were all I saw of those.

Sources also used: [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md), [LangWatch study](https://langwatch.ai/research/finding-the-optimal-context-window), [compaction comparison](https://codex.danielvaughan.com/2026/04/14/context-compaction-deep-dive-codex-cli-claude-code-opencode/), [WUSH-KV summary](https://www.opentrain.ai/papers/wush-kv-kv-cache-quantization-with-data-adaptive-transforms--arxiv-2609.38121) (KV quantization, no llama.cpp support found, not useful at 128 MiB), [Ternary Bonsai 8B card](https://huggingface.co/prism-ml/Ternary-Bonsai-8B-gguf) (its Q2_0 is CPU NEON/generic and not in mainline, so no).

## Decisions for Eni-M

1. **Order of work.** My recommendation: items 1–5 first, since they need no downloads and fix what you asked about, then item 6.
2. **Download for item 6:** Qwen3 4B Q2_K (1.67 GB), Q3_K_M (2.08 GB) and Qwen3.5 4B Q2_K_XL (1.94 GB) from ISTA-DASLab, plus H-Tiny Q3_K_M from IBM.
3. **The second RAM stick** is still the one hardware change that speeds up everything above.
