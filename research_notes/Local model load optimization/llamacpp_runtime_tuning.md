# llama.cpp runtime tuning for 1.5B–4B models on a 4C/8T 15 W laptop CPU (i5/i7-8250U/8550U, AVX2, 16 GB DDR4-2400 dual channel), Windows, ~b11982 (late 2026)

Context: measured baselines on the target laptop (from the assignment, CPU-only, 8 threads): Granite 4.1 3B Q4_K_M pp512 39 t/s / tg64 6.0 t/s; Qwen3-4B Q4_K_M pp512 30 / tg 4.8; Qwen2.5-Coder-1.5B Q8_0 pp512 42 / tg 7.5.

Note on flag names: the current server README (master, checked 2026-10-29) has changed several flags since 2024–2025 guides. `--mlock` / `--no-mmap` are folded into `-lm/--load-mode`, `--draft-max/--draft-min` were **removed** in favour of `--spec-draft-n-max/-min`, and `--lookup` style n-gram decoding became `--spec-type ngram-*`. Older blog posts and Reddit threads use the old names. Check `llama-server --help` on the installed build, because flags in the ~b11982 build may differ slightly from master.

## Threads: -t vs -tb, 4 vs 8 threads, --prio, --cpu-mask/--cpu-strict, --poll

### Takeaway
Use `-t 4 -tb 8` as the starting point. Token generation is memory-bandwidth-bound and usually peaks at the physical core count, and 8 threads can be much worse: one paper measured 7.10 t/s at 4 threads against 4.14 t/s at 8 on a 4C/8T laptop. Prompt processing is compute-bound and may gain from all 8 hyperthreads. Leave `--prio` at 0 or lower and try `--poll 0` to cut spin-waiting and heat. Measure both settings with llama-bench before keeping them.

### Cited Findings
- Current flags: `-t, --threads N` (generation threads, default -1 = auto); `-tb, --threads-batch N` (threads for batch and prompt processing, defaults to the `--threads` value); `--prio N` (-1 low to 3 realtime, default 0); `--prio-batch N` (0–3); `-C, --cpu-mask M` (hex affinity mask); `--cpu-strict <0|1>` (default 0); `--poll <0...100>` (polling level while waiting, 0 = no polling, default 50). — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- On "a four-core, eight-thread processor" (likely an Intel i5 10th–12th gen laptop, Q4_K_M per the paper's general setup; the paper doesn't name the exact CPU), llama.cpp generation measured 6.51 t/s at 2 threads, **7.10 at 4**, 6.72 at 6 and **4.14 at 8**. Matching threads to physical cores raised throughput 71%. — [Aletheia, arXiv 2607.24814v3, §VI-H](https://arxiv.org/html/2607.24814v3)
- Decode (token generation) is bound by memory bandwidth and prefill is compute-bound. Generation usually saturates around the physical core count and then plateaus or slightly regresses. — [llama.cpp performance-tuning guide (third-party mirror of docs)](https://www.mintlify.com/ggml-org/llama.cpp/advanced/performance-tuning); [notebook-dg9 performance tuning notes](https://notebook-dg9.pages.dev/kb/ai/llama-cpp/performance-tuning/)
- Older data (2023, Ryzen): the number and frequency of cores set prompt-processing speed, while cache and RAM speed set generation speed. — [r/LocalLLaMA extensive llama.cpp CPU benchmark, Jun 2023](https://redlib.ssps.io/r/LocalLLaMA/comments/14ilo0t/extensive_llamacpp_benchmark_more_speed_on_cpu_7b)
- Counter-example: one user on a 20C/40T Xeon got the best speed at 38 threads, so the physical-core rule is not universal. — [same search, Reddit anecdote](https://redlib.ssps.io/r/LocalLLaMA/comments/14ilo0t/extensive_llamacpp_benchmark_more_speed_on_cpu_7b)
- llama.cpp worker threads spin-wait, "eating 100% CPU even if they don't have work to do". That discussion recommends fewer threads when all layers are offloaded to a GPU. — [llama.cpp discussion #3210](https://github.com/ggerganov/llama.cpp/discussions/3210)
- An anecdote reports that CUDA keeps one thread busy-polling for kernel completion, while Vulkan used much less CPU at the same throughput. — [r/LocalLLaMA snapshot, Apr 2026](https://reddit.sentinel-team.org/posts/1saizts/snapshots/2026-04-03T21%3A20%3A24.375333Z)
- A user on a low-power laptop ran inference under `nice -n 19` so it would yield to interactive apps. — [same snapshot](https://reddit.sentinel-team.org/posts/1saizts/snapshots/2026-04-03T21%3A20%3A24.375333Z)

### Inferences
- **The user's baselines (tg at 8 threads) are probably below the hardware's best.** The 8250U/8550U is 4C/8T and its hyperthreads compete for the same execution units and memory bandwidth. Run `llama-bench -t 2,3,4,6,8 -p 512 -n 64` first. Based on the Aletheia data, 4 threads could plausibly raise tg by 10–70% (an estimate, not measured on this CPU).
- Back-of-envelope (estimate): Granite 3B Q4_K_M weighs about 2 GB, so 6 t/s ≈ 12 GB/s of effective weight streaming. Dual-channel DDR4-2400 peaks at 38.4 GB/s theoretical, and real single-socket laptop numbers are usually 20–28 GB/s. tg therefore has headroom if threading or contention is the limit.
- For heat and power on a 15 W part, fewer active threads means less package power and less thermal throttling of all-core turbo. With `-t 4` the CPU may also hold a higher sustained clock. Prefill is short and bursty, so `-tb 8` (or `-tb 4` when running cool and quiet matters most) is a reasonable split.
- `--poll 0` should cut the busy-spin of idle worker threads between ops and tokens. That should lower heat, at a possible small latency cost. **This is unmeasured; I found no benchmark.** The default of 50 favours latency.
- `--prio 2/3` raises priority on Windows. It doesn't make the CPU faster and can starve the editor or UI, so for a laptop that's also running an IDE, keep `--prio 0` or use `--prio -1`.
- `--cpu-mask`/`--cpu-strict 1` could pin one thread per physical core (on Windows, logical CPUs 0,2,4,6 are usually the first SMT sibling of each core, mask `0x55`). This is untested on this CPU and the gain is unproven. The Windows scheduler normally spreads 4 threads across physical cores anyway.

### Gaps
- No published benchmark of 8250U/8550U thread scaling on current builds.
- No measured data on `--poll 0` power, heat or latency, or on `--cpu-strict` on Windows.

## Batch/ubatch (-b, -ub), flash attention (-fa), KV-cache quantization (-ctk/-ctv), context size (-c)

### Takeaway
Defaults are `-b 2048 -ub 512 -fa auto`. On CPU, `-ub 512` is a sensible default. Smaller (`-ub 128/256`) lowers peak compute-buffer RAM, larger rarely helps a 4-core CPU. Keep the KV cache at f16 unless RAM is tight, because q8_0 KV costs CPU prefill speed (11–19% slower in one test) and only saves memory. Size `-c` to what you actually use (8–16k), not the model's maximum: the default `-c 0` loads the model's full trained context (often 32k–128k+) and allocates KV for all of it.

### Cited Findings
- `-b, --batch-size N`: logical max batch, default 2048. `-ub, --ubatch-size N`: physical max batch, default 512. `-fa, --flash-attn [on|off|auto]`: default `auto`. `-ctk/-ctv`: default f16; allowed f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1. — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-c, --ctx-size`: default 0 = loaded from the model. `-fit, --fit [on|off]` (default on) "adjust[s] unset arguments to fit in device memory". — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- One CPU-only tuning test (2 vCPU / 2 GB VM): q8_0 KV cache made prompt processing about **11–19% slower** at short context, because dequant overhead outweighed the bandwidth saving. Flash attention **helped** on CPU, and its outputs were bit-identical with FA on or off. — [edge-cpu-gguf-tuner (clawhub)](https://clawhub.ai/orionshaowswmw/edge-cpu-gguf-tuner) (single small-hardware test; indicative only)
- Flash attention mainly helps memory use and long-context prefill, with minimal difference below about 2048 tokens. — [performance-tuning notes](https://notebook-dg9.pages.dev/kb/ai/llama-cpp/performance-tuning/)
- Quantized KV plus FA can silently fall back to CPU attention on some GPU builds, making prefill 25–45x slower on CUDA. That's a GPU build issue, not the CPU tradeoff, but it's relevant if Vulkan offload is later enabled with `-ctk q8_0`. — [llama.cpp issue #24485](https://github.com/ggml-org/llama.cpp/issues/24485)
- A quantized V cache has historically required flash attention in llama.cpp. — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) (FA is `auto` by default, so this is normally satisfied)

### Inferences
- KV size estimate for Qwen3-4B (36 layers, 8 KV heads, head_dim 128, from the model config, not verified in this session): about **144 KiB/token at f16**, so 8k ctx ≈ 1.1 GiB, 32k ≈ 4.5 GiB and 40k ≈ 5.6 GiB. q8_0 roughly halves that (about 0.6 GiB at 8k). Granite 4.x is a hybrid Mamba/attention design, so its KV cost per token should be much smaller (estimate). With 16 GB total, `-c 8192`–`16384` at f16 is affordable for 1.5–4B models. Reach for `-ctk q8_0 -ctv q8_0` only above about 16k ctx or when running two models.
- `-ctk q4_0` saves more RAM but costs more quality and more dequant compute. It isn't recommended for code work.
- `-b` above `-ub` only matters for multi-sequence or very long prompts. For a single user on CPU, `-b 512 -ub 512` (or `-b 2048 -ub 512`, the default) is fine. Try `-ub 256` and `-ub 1024` in llama-bench (`-ub 256,512,1024`): CPU GEMM tiles typically saturate by 256–512.
- FA on CPU: keep `-fa auto` or `-fa on`. The cost is near zero and it helps long prompts and memory.

### Gaps
- No 8250U-class measurement of `-ub` sweeps, FA on/off or KV-quant speed on current builds.
- Exact Granite 4.1 3B KV/state memory per token not found.

## Weight formats: Q4_0 (online repack) vs Q4_K_M vs IQ4_XS/IQ4_NL; --no-mmap/--mlock on Windows

### Takeaway
On an AVX2 CPU, Q4_0 is generally the fastest 4-bit format, especially for prompt processing, because llama.cpp repacks it on the fly into interleaved layouts (enabled by default with `--repack`). Q4_K_M has better quality per bit. IQ4_XS and IQ4_NL are smaller or higher-quality but slower on CPU. Use `-lm/--load-mode` (auto/none/mmap/mlock/dio): `--mlock` and `--no-mmap` now live there.

### Cited Findings
- `--repack, -nr, --no-repack`: weight repacking, enabled by default. — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- For Q4_0, llama.cpp automatically repacks the weights on the fly when the hardware benefits. This replaced the Q4_0_4_4/4_8/8_8 formats, which newer builds can no longer load. Performance is similar to the old formats, possibly slightly lower because it uses intrinsics instead of assembly. — [bartowski model card notes](https://huggingface.co/bartowski/huihui-ai_Qwen3-14B-abliterated-GGUF)
- An ARM PR found perplexity identical with and without repacking. — [llama.cpp PR #17241](https://github.com/ggml-org/llama.cpp/pull/17241)
- Mainline AVX2 prompt processing (llama-bench `-p 512 -n 0`, 16 threads, 8B model, 2024): Q4_0 ≈ 104 t/s vs IQ4_XS ≈ 69 t/s. **Old data (July 2024), predates the current repack.** — [ik_llama.cpp wiki, July 2024 PP comparison](https://github-users.yhajoa.workers.dev/ikawrakow/ik_llama.cpp/wiki/July-2024:-Prompt-processing-perfomance-comparison)
- In the ik_llama.cpp fork (not mainline), Q4_0 PP-512 rose from 155.6 to 300.9 t/s (1.93x) on a Ryzen 7950X with its Q8_0_R8 repacking. — [ik_llama.cpp PR #534](https://github-users.yhajoa.workers.dev/ikawrakow/ik_llama.cpp/pull/534)
- When IQ4_XS was introduced, its author noted slower CPU generation: on a Ryzen 7950X TG-128 fell to 14.5 t/s from 15.8 (vs the comparison quant). — [IQ4_XS commit #5747](https://cdn04132025.gitlink.org.cn/replica/llama.cpp/commit/0becb22ac05b6542bd9d5f2235691aa1d3d4d307). Conflicting: IQ4_XS was reported slightly faster in tg but slower in pp than Q4_K_M on Llama-3.1-8B. — [Kaitchup via reader](https://hn.jakechampion.name/reader/https://kaitchup.substack.com/p/choosing-a-gguf-model-k-quants-i)
- `-lm, --load-mode MODE` (default auto): `auto` = mmap unless a device doesn't support it; `none`; `mmap` (if disabled: "slower load but may reduce pageouts if not using mlock"); `mlock` ("force system to keep model in RAM rather than swapping or compressing"); `dio` (DirectIO if available). — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- For speed on this CPU, test **Q4_0** (with an imatrix if available) against Q4_K_M. Expect a clear pp gain and a small tg gain (tg is bandwidth-bound, and the two files are similar in size). Q4_0 has a measurable quality loss versus Q4_K_M, which matters more for 1.5–4B models than for 8B+. Running Q4_0 for prefill-heavy code-context work and Q4_K_M or Q5_K_M where quality matters is a reasonable split. **No 8250U-specific numbers found.**
- On the user's machine Q8_0 for a 1.5B model (≈1.6 GB) gave tg 7.5. A Q4_0 or Q4_K_M 1.5B (≈1 GB) should give noticeably higher tg (estimate ~1.4–1.6x from the bandwidth ratio), at some quality cost.
- Unverified: repacked tensors must be rewritten into a separate CPU buffer, so the mmap page-sharing benefit is probably lost for those tensors. Q4_0 RAM use with mmap may therefore look like `--no-mmap`. Check with Task Manager (committed memory).
- On Windows with 16 GB, `-lm mlock` prevents the model from being paged or compressed out when an IDE or browser is busy, which avoids first-token stalls after idle. Locking large regions on Windows may need the "Lock pages in memory" privilege or a larger working set, and changing that privilege is a system security setting the user must do themselves. `-lm none` (the old `--no-mmap`) gives slower load but more predictable residency. For 1–3 GB models either is cheap.

### Gaps
- No current (2026) mainline AVX2 benchmark comparing repacked Q4_0, Q4_K_M, IQ4_XS and IQ4_NL on the same machine. Recommend running `llama-bench -m a.gguf,b.gguf,c.gguf -t 4 -p 512 -n 64`.
- Whether IQ4_NL is repacked on AVX2 in current builds was not confirmed.
- Windows-specific mlock behaviour in current builds was not confirmed.

## Prompt reuse: cache_prompt, --cache-reuse, --cache-ram, slot save/restore, --keep, context shift, checkpoints

### Takeaway
Prompt caching (`--cache-prompt`) is on by default, so a stable prefix such as the system prompt plus file context is reused automatically when only the tail changes. That's the single biggest prefill saving: at ~30–40 t/s pp, a 2,000-token prefix costs about 50–65 s cold and near zero cached. Add `--cache-reuse 256` so chunks that moved (edited text in the middle) can be KV-shifted instead of recomputed. Since October 2025 there is also a host-RAM prompt cache (`--cache-ram`, default 8192 MiB) that keeps several previous prompts and hot-swaps them into the slot. Cap it (for example `-cram 2048`) on a 16 GB laptop. Save and restore long fixed contexts to disk with `--slot-save-path`.

### Cited Findings
- `--cache-prompt` / `--no-cache-prompt`: prompt caching, default enabled. The request field `cache_prompt` means "Re-use KV cache from a previous request if possible", with the caveat that enabling it "can cause nondeterministic results". — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `--cache-reuse N`: "min chunk size to attempt reusing from the cache via KV shifting, requires prompt caching to be enabled (default: 0)" (0 = disabled). Per request: `n_cache_reuse`. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-cram, --cache-ram N`: max host prompt-cache size in MiB, default 8192, -1 = no limit, 0 = disable. `--cache-idle-slots`: "save idle slots to the prompt cache on new task". — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- PR #16391 (merged 9 Oct 2025) added the host-memory prompt cache. It acts as "extra slots", checks prefix similarity and hot-swaps a cached state into the context when that reduces processing. It lets one slot serve multiple conversations without evicting the cache, and the author says it "significantly improves the experience" with the llama.vscode agent. It is incompatible with multimodal (mtmd). It raised default context checkpoints, and `LLAMA_SERVER_SLOTS_DEBUG=1` gives a verbose `/slots`. No benchmark numbers were given. Third-party reports mention high RAM use at the default cap. — [llama.cpp PR #16391](https://github.com/ggml-org/llama.cpp/pull/16391)
- `--slot-save-path PATH`: path to save the slot KV cache (default disabled). This enables the `/slots/{id}?action=save|restore` endpoints. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `--keep N`: tokens of the prompt to keep when context is exceeded (default 0, -1 = all). `--context-shift` / `--no-context-shift`: default **disabled**. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-ctxcp, --ctx-checkpoints N`: max context checkpoints per slot, default 32. `-cms, --checkpoint-min-step`: min spacing between checkpoints, default 8192 tokens. Checkpoints are what allow cache reuse on SWA and recurrent/hybrid models, whose state can't simply be truncated. `--swa-full` uses a full-size SWA cache (default false). — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- KV shifting is implemented with `llama_kv_cache_seq_add` (formerly `seq_shift`). The context checks whether shifting is supported and disables it with a warning when it isn't. — [commit #5691](https://cdn04132025.gitlink.org.cn/replica/llama.cpp/commit/bf08e00643fd529f748f0a858fd79f3061e3fa18); [commit #12181](https://cdn04132025.gitlink.org.cn/replica/llama.cpp/commit/e0dbec0bc6cd4b6230cda7a6ed1e9dac08d1600b)
- Research system (not llama.cpp): EFIM cut FIM prefill overhead about 40% versus naive FIM with KV reuse, which shows that edit-in-the-middle prompts defeat simple prefix caching. — [EFIM, arXiv 2505.21889](https://arxiv.org/pdf/2505.21889)

### Inferences
- **The practical rule:** put the stable content first (system prompt, then tool definitions, then file context) and the volatile content (the user's latest question or edit region) last. Any change early in the prompt invalidates everything after it (with `--cache-reuse` partially rescuing shifted chunks).
- `--cache-reuse` relies on KV shifting, which isn't possible for recurrent/hybrid state. Granite 4.x (hybrid Mamba) and other SSM hybrids probably get only checkpoint-based or exact-prefix reuse. Set `-cms` lower (for example 1024–2048) so checkpoints exist inside typical 2–8k prompts, because the default 8192 is coarse for short contexts (estimate; untested).
- Use `-np 1` (one slot) on this laptop. Multiple slots split context and RAM, and the CPU can't serve concurrent requests usefully. The host cache (`-cram`) covers switching between conversations.
- Slot save/restore suits a fixed large preamble (for example a repo summary): compute it once, save it, and restore it on startup. The file size is about the KV size (estimate: Qwen3-4B 4k tokens ≈ 0.56 GiB at f16).
- Leave `--context-shift` off for code tasks (silently dropping the middle damages correctness). Use `--keep` only if it's turned on.

### Gaps
- No measured TTFT numbers for cache hits vs misses on CPU in current builds. The expected saving follows directly from the pp rates above (computed, not measured).
- Exact behaviour of `--cache-reuse` with hybrid models (Granite 4.x) wasn't confirmed in sources.

## Speculative decoding on CPU: draft models, n-gram/lookup (`--spec-type`), usefulness on 4 cores for code editing

### Takeaway
On a bandwidth-bound 4-core CPU, a separate draft model (for example 0.5B for a 3–4B target) competes for the same memory bandwidth and cores. Its benefit is marginal or negative unless acceptance is high, and no CPU-specific win was found in sources. **N-gram self-speculation costs nothing extra in RAM or compute and is the recommended thing to try for code editing.** That means `--spec-type ngram-mod` (~16 MB, shared pool) or `ngram-simple`, both of which the docs explicitly cite for rewriting source code. It helps only when the output repeats text already in the context.

### Cited Findings
- Current `--spec-type` values: `none, draft-simple, draft-eagle3, draft-mtp, draft-dflash, draft-dspark, ngram-simple, ngram-map-k, ngram-map-k4v, ngram-mod, ngram-cache` (comma-separated, default none). The draft model is set with `-md/--spec-draft-model`. `--spec-draft-n-max` (default 3), `--spec-draft-n-min` (default 0), `--spec-draft-p-min` (default 0.00), `--spec-draft-p-split` (0.10). The old `--draft-max/--draft-min` were **removed**. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- **ngram-simple**: drafts the tokens that followed the last earlier occurrence of the current n-gram, "simplest self-speculative approach with minimal overhead". Example: `--spec-type ngram-simple --spec-draft-n-max 64`. Defaults: size-n 12, size-m 48, min-hits 1. The docs name the use case: "rewriting of source code by a LLM". — [docs/speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- **ngram-mod**: hashed n-gram pool shared across all slots, "Lightweight (~16 MB)", constant memory. Example: `--spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 48 --spec-ngram-mod-n-max 64`. "MoEs require long drafts". For dense models you "can reduce" n-min and n-max. Use cases: iterating over a block of text or code, reasoning models repeating their thinking, summarization. — [docs/speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- **draft-eagle3**: a one-layer head that reads target hidden states, with higher acceptance than a standalone draft model of the same size. Example given for Qwen3-4B: `llama-server -m Qwen3-4B.gguf -md Qwen3-4B-eagle3.gguf --spec-type draft-eagle3` (converted from `AngelSlim/Qwen3-4B_eagle3`). **draft-mtp** uses the model's own MTP heads (only for models that ship them). — [docs/speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- Example acceptance stats in the docs (illustrative, not benchmarks): 0.576 (171/297) and 0.703 (90/128). — [docs/speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- GPU anecdote (May 2026, RTX 5060 Ti, Qwen3.6-35B-A3B): only ngram-mod won, with 35–90% acceptance depending on repetitiveness and the best gains on tool calls and JSON. — [Reddit snapshot](https://reddit.sentinel-team.org/posts/1t8t0dx/snapshots/2026-05-15T22%3A59%3A01.155307Z)
- A GPU-cluster test of ngram-simple and ngram-mod on Gemma 4 26B gave mixed results ("didn't help"), and the first repeated-prompt test looked "too good to be true". — [dev.to](https://dev.to/defilan/i-tested-speculative-decoding-on-my-home-gpu-cluster-heres-why-it-didnt-help-3ej6)
- N-gram drafting is free on prefill, but acceptance collapses on non-repetitive output. — [MTP vs others (HF space)](https://victor-mtp-on-hf-endpoints.static.hf.space/index.html); [dev.to: speculative checkpointing pays off only on repetitive text](https://dev.to/simon_paxton/speculative-checkpointing-pays-off-only-on-repetitive-text-1j3g)

### Inferences
- Why draft models are weak here: the verification batch (target model, n+1 tokens) on CPU is cheaper per token than sequential decode only if the CPU has spare compute while waiting on bandwidth. A 4-core 15 W chip has little spare compute, and the 0.5B draft also streams ~0.4–0.5 GB per drafted token. Expect at best ~1.0–1.3x for code with a 0.5B Qwen2.5 draft paired with a same-family target. Draft and target must share a tokenizer, so Granite has no small same-family draft in this setup (estimate; no CPU benchmark found). Also, more work per token means more heat.
- For "edit this file and return it" tasks the output largely copies the input, so `ngram-simple` / `ngram-mod` acceptance should be high and tg could rise well above the base 5–7 t/s (unmeasured on this CPU). For free-form chat expect about 1.0x.
- Lower draft lengths for dense small models on CPU, because a rejected 64-token draft wastes a whole verify batch. Start with `--spec-ngram-mod-n-min 8 --spec-ngram-mod-n-max 16` (or `--spec-draft-n-max 16` for ngram-simple) and tune (estimate).
- Eagle3 for Qwen3-4B is the only option with a documented small-head draft for a target in the user's set. Its CPU payoff is unknown.

### Gaps
- No CPU-only (AVX2 laptop) speculative-decoding benchmark found for any type.
- No measured acceptance rates for code-edit workloads with 1.5–4B models.

## Newer 2025–2026 llama.cpp features relevant to low-end CPUs

### Takeaway
The changes that matter for this laptop are the host-RAM prompt cache, context checkpoints for hybrid and SWA models, n-gram speculative types, idle sleep, router mode, and auto-fit. Together they make a single long-running `llama-server` cheap at idle and fast on repeated contexts.

### Cited Findings
- `--sleep-idle-seconds N`: server sleeps after N seconds idle (default -1 = disabled). — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Router mode: `--models-dir PATH` (directory of models for the router server) and `--models-max N` (max models loaded at once, default 4, 0 = unlimited). — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-fit, --fit on|off` (default on): adjusts unset args to fit device memory. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-kvu, --kv-unified`: single KV buffer shared across sequences (default on when slots are auto). `-np, --parallel N` (default -1 auto). `--threads-http N`. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Host prompt cache `--cache-ram` (Oct 2025), context checkpoints `-ctxcp`/`-cms`, and `--spec-type ngram-*` / `draft-eagle3` / `draft-mtp` are covered in the sections above. — [PR #16391](https://github.com/ggml-org/llama.cpp/pull/16391); [docs/speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- `-lm, --load-mode` with `dio` (DirectIO) replaced the separate mmap/mlock flags. — [server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- **Suggested starting command (to be validated with llama-bench / timing on the actual laptop; values are estimates):**
  ```
  llama-server -m model-Q4_0.gguf -ngl 0 -t 4 -tb 8 --poll 0 --prio 0 ^
    -c 8192 -b 2048 -ub 512 -fa on -ctk f16 -ctv f16 ^
    -np 1 --cache-reuse 256 -cram 2048 -cms 1024 --slot-save-path C:\llm\slots ^
    --spec-type ngram-mod --spec-ngram-mod-n-min 8 --spec-ngram-mod-n-max 16 ^
    --sleep-idle-seconds 300
  ```
  For "coolest/quietest" use `-t 3 -tb 4`. For maximum prefill use `-tb 8 -ub 512`. Use `-ctk q8_0 -ctv q8_0` only when `-c` is above 16k.
- `--sleep-idle-seconds` frees RAM and stops background CPU when idle. The cost is a reload on the next request: a 1–3 GB model loads in a few seconds from SSD (estimate).
- `-fit on` may change unset args on its own, so pin explicit `-c`, `-ub` and so on to keep benchmarks reproducible.
- The Vulkan build on this iGPU (UHD 620): the CPU-only `-ngl 0` baseline is what was measured. The iGPU shares the same DDR4 bandwidth and 15 W budget, so offloading is unlikely to raise tg and may add heat (out of scope here; flagged for the GPU researcher).

### Gaps
- Release notes between ~b11982 and master were not checked flag by flag. Confirm with `llama-server --help`.
- No source quantified `--sleep-idle-seconds` power savings or reload latency.
