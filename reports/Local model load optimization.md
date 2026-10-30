# Send fewer tokens, not smaller pieces

**The best way to ease the load on this laptop is to process fewer tokens. Splitting the same tokens into smaller pieces does not help.** Measured on this machine, Granite 4.1 3B (Q4_K_M, CPU-only) processes prompts at about **30–35 tok/s** and generates at about **5.5–6.3 tok/s**. Each 1,000-token prompt therefore costs about 30 s of full-package CPU work before the first output token. Each output token costs about 0.16 s, which makes output roughly 5–6× more expensive per token than input. The largest wins follow from that. First, keep prompts prefix-stable so llama-server's KV prompt cache skips tokens it has already processed. Second, send C# signatures instead of whole files. Third, cap output hard and ask for diffs rather than whole files. Fourth, keep the two models on separate hardware: Granite on the CPU, and Qwen2.5-Coder-1.5B entirely on the MX150. Your queueing idea is partly right. **A single-slot priority queue with cancellation and "latest wins" for autocomplete helps.** **"Feeding the model small amounts" saves no compute**, because llama-server already batches prompts internally. The one variant that does help is pre-warming stable context while you are idle. **"Delivering queued output in full" saves nothing either.** It only adds about 50 s of blank screen per answer, so stream to the UI and buffer only output that a machine consumes. On threads, this laptop's measurements contradict the published "4 threads beats 8" claim. The setup below uses `-t 4 --poll 0` for generation, which tied 6–8 threads in this run while using half the cores, and `-tb 8` for prompt processing. Several knobs remain unmeasured here, among them Q4_0, CUDA 12.4, turbo-off and n-gram speculation. They are listed at the end as short experiments.

Each number below is tagged by where it came from. **[measured here]** means it was measured on this laptop (llama.cpp b11982 Vulkan build, recorded in `research_notes/Local model load optimization/measured_thread_sweep.md` and in your earlier baselines). **[third-party]** means someone else's measurement on other hardware, with a link. **[estimate]** means arithmetic or inference not yet checked on this machine.

## Measured threading: 6–8 threads win prefill, and spin-wait off ties them on generation

The most important correction comes from your own thread sweep. A 2026 paper measured llama.cpp generation on an unnamed 4C/8T laptop at **7.10 tok/s with 4 threads versus 4.14 tok/s with 8** [third-party] ([Aletheia, arXiv 2607.24814](https://arxiv.org/html/2607.24814v3)). The llama.cpp docs also advise matching the thread count to physical cores [third-party] ([llama.cpp completion README](https://github.com/ggml-org/llama.cpp/blob/master/tools/completion/README.md)). **Neither held for Granite 4.1 3B on this laptop.**

| Granite 4.1 3B Q4_K_M, `-dev none` | pp512 tok/s | tg64 tok/s |
|---|---|---|
| 3 threads | 29.6 ± 4.3 | 5.62 |
| 4 threads | 30.4 ± 1.2 | 5.51 |
| 6 threads | 34.8 ± 0.2 | 6.23 |
| 8 threads | 35.4 ± 0.4 | 6.13 |
| **4 threads, `--poll 0`** | 30.3 ± 0.6 | **6.34** |
| 8 threads, earlier run on a cooler machine | 39 | 6.0 |

All rows are [measured here], with 2 repetitions each. Differences under about 0.3 tok/s in generation are noise.

Three conclusions follow. **Prompt processing scales with threads.** 6–8 threads give about 15% more than 4, which is expected because prefill is compute-bound. **Generation is flat at about 5.5–6.3 tok/s across thread counts.** Turning off spin-waiting (`--poll 0`) lifted the 4-thread run from 5.51 to 6.34 tok/s, which ties or beats 6 and 8 threads with half the cores busy. llama.cpp worker threads otherwise spin-wait, "eating 100% CPU even if they don't have work to do" [third-party] ([llama.cpp discussion #3210](https://github.com/ggerganov/llama.cpp/discussions/3210)), so at `--poll 0` the generation phase should also run cooler [estimate; no power was logged]. **Thermal state alone moved prefill by about 10%** [measured here]: the same 8-thread test gave 39 tok/s on a cool machine and 35 tok/s warm. Cooling therefore matters about as much as any flag.

The recommended split is `-t 4 -tb 8 --poll 0`: four threads for the bandwidth-bound generation phase, and all eight for the short, compute-bound prefill bursts. This exact combination, and `--poll 0` at 6 and 8 threads, has **not** been measured yet. It is the first experiment to run.

Generation is limited by memory bandwidth, not cores. At 6 tok/s over a 1.95 GiB weight file, the CPU streams about **12–13 GB/s** [estimate, from the measured numbers], against roughly **38 GB/s** theoretical for dual-channel DDR4-2400 ([Intel ARK](https://www.intel.com/content/www/us/en/products/sku/124967/intel-core-i58250u-processor-6m-cache-up-to-3-40-ghz/specifications.html)). Each generated token reads the full weights with almost no reuse, so generation saturates long before all cores are busy [third-party] ([llama.cpp #1437](https://github.com/ggml-org/llama.cpp/issues/1437); [InventiveHQ](https://inventivehq.com/blog/cpu-thread-scaling-llm-inference)). The gap between 12 and 38 GB/s is why a smaller weight file (Q4_0, or Q4_K_M instead of Q8_0 for the 1.5B model) is the next knob for generation speed. More threads are not.

## Each model gets its own device: Granite on the CPU, the 1.5B coder on the MX150

Your measurements already settle device placement. **Offloading 24 of Granite's layers to the MX150 gave pp 39.8 / tg 6.1, the same as CPU-only (39 / 6.0) [measured here]**, and full offload ran out of memory because the 1.95 GiB of weights exceed the roughly 1.9 GB of free VRAM. The likely cause is op offload. With a GPU build, any batch of 32 or more tokens makes llama.cpp copy the CPU-resident weights to the GPU to run the batch, even at `-ngl 0` [third-party] ([HF llama.cpp offload guide](https://huggingface.co/blog/Doctor-Shotgun/llamacpp-moe-offload-guide); [llama.cpp issue #17026](https://github.com/ggml-org/llama.cpp/issues/17026)). On a narrow laptop PCIe link that copy cancels the gain. Generation cannot gain either: the MX150's 64-bit GDDR5 delivers roughly 40–48 GB/s [estimate from TechPowerUp clock data](https://techpowerup.com/242650/nvidia-sneaks-less-powerful-geforce-mx150-variant-into-ultrabooks?cp=3), about the same as the CPU's DDR4. So run Granite with **`-dev none`**. That disables offload [third-party] ([llama-server manpage](https://manpages.debian.org/unstable/llama.cpp-tools/llama-server.1.en.html)) and keeps Granite's GPU context out of the VRAM the coder needs.

The MX150 belongs to Qwen2.5-Coder-1.5B. Fully offloaded via Vulkan at Q8_0 with `-ub 128`, it measured **pp512 166 tok/s and tg64 about 12 tok/s, with high variance [measured here]**, against 42 / 7.5 on the CPU [measured here]. That is a 4× gain on the prompt-heavy work autocomplete does. The default ubatch failed to allocate, because a 1.76 GiB Q8_0 file leaves only about 150 MB for KV and compute buffers. **Switching the coder to Q4_K_M (about 1 GB)** should restore the default `-ub 512`, leave room for a 4k context, and raise the generation ceiling about 1.7× [estimate]. The trade is a small FIM quality loss. Your earlier research found Q4_K_M kept fp16 quality on HumanEval-Infilling for Qwen2.5-Coder-3B ([arXiv 2605.04894](https://arxiv.org/pdf/2605.04894)).

The two instances use different memory buses, so they should not slow each other's generation. Their interaction is **heat**. Thin MX150 laptops typically share a heatpipe and fan between CPU and dGPU [estimate; not sourced for your model], and that probably explains the coder's variable tg. Autocomplete is bursty, so sustained overlap happens only while you type during a long chat answer.

**CUDA 12.4 is worth a test on the MX150.** CUDA 13 dropped Pascal, but llama.cpp still shipped a Windows CUDA 12.4 build covering sm_61 as of Aug 2026 [third-party] ([localaimaster](https://localaimaster.com/blog/pascal-gpus-after-cuda-13)). On a GTX 1050 Ti with the same model, CUDA reached **pp512 267 versus 136 on Vulkan, with generation about equal (19–21 tok/s)** [third-party] ([CUDA thread #15013](https://github.com/ggml-org/llama.cpp/discussions/15013); [Vulkan thread #10879](https://github.com/ggml-org/llama.cpp/discussions/10879)). If the MX150 behaves the same way, coder prefill could approach about 300 tok/s [estimate]. The open question is VRAM overhead: the CUDA context and cuBLAS workspace may cost memory that 2 GB cannot spare. Leave **the UHD 620 unused.** It shares the CPU's 15 W budget and DDR4 bus, SYCL supports only 11th-gen and newer iGPUs [third-party] ([llama.cpp SYCL.md](https://sources.debian.org/src/llama.cpp/8064%2Bdfsg-2/docs/backend/SYCL.md)), and an i5-8250U user's UHD 620 result (4.54 tok/s on Qwen2.5-3B Q4_K_M, partial offload) is below your own CPU number [third-party] ([ozkanpakdil](https://ozkanpakdil.github.io/posts/my_collections/2026/2026-03-22-vulkan-llamacpp-debian-13)).

## Your queue idea: the queue helps, small feeds and full delivery don't

At the measured rates, one 1,000-token prompt is about 30 s of prefill and a 300-token answer is about 48 s of generation [estimate from measured rates]. Judge each part of the idea against that.

**Queueing requests helps.** CPU decode is bandwidth-bound, so two concurrent requests each run at about half speed. Total time is the same, but the first answer arrives later. One CPU server with `-np 8` held aggregate throughput flat at about 10–11 tok/s regardless of concurrency [third-party] ([OCI Ampere benchmark](https://quartoblog-52a.pages.dev/blog/tech/benchmark%20llm%20-%20flags)). Ollama defaults to `OLLAMA_NUM_PARALLEL=1` with a FIFO queue for the same reason ([Ollama FAQ](https://docs.ollama.com/faq)). So run llama-server with `-np 1` and keep the queue in your app, where you can do what llama-server cannot.

Priority comes first: interactive chat, then explicit explain/fix, then background pre-warm. Second, **"latest wins" for autocomplete**: a new keystroke cancels the older pending completion instead of queueing behind it. Third, **cancellation all the way down**: pass `HttpContext.RequestAborted` into the `HttpClient` call so that Stop, or a closed tab, ends generation and frees the CPU. Fourth, single-flight deduplication of identical in-flight requests. Fifth, a short bound on the queue (4–8 deep) that fails fast with 429 or 503, because five queued chat requests at 6 tok/s are minutes of backlog. Since the models run on separate devices, **use two independent queues**, one per backend. A single shared lock would make autocomplete wait behind a 50-second chat answer for no reason.

**"Feeding the model small amounts" does not reduce compute.** Every prompt token must pass through every layer once whatever the chunking. llama-server already splits prompts into `-b 2048` / `-ub 512` batches internally [third-party] ([llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)), and smaller batches are, if anything, less efficient per token. Pausing between chunks lowers peak temperature and fan noise. It does not lower energy per request (the same work, plus idle power for longer), and it adds latency one-for-one [estimate]. Fewer threads or a turbo cap are cleaner thermal knobs.

Two versions of "small amounts" do work. The first is **sending fewer tokens**: signatures instead of files, retrieval instead of whole projects, and diffs instead of rewrites. The second is **pre-warming during idle time**. While you type, send the stable context (system prompt, repo map, current file) with `n_predict: 0`, which "evaluates the prompt into the cache" without generating ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)). The real request then pays only for the new tail. llama.vim and llama.vscode do exactly this with a ring buffer of recently edited chunks, primed during inactivity. In one run that left **only 260 of 15,186 context tokens to process** [third-party, Apple M1] ([llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)).

**Delivering queued output in full has zero compute benefit.** The model generates the same tokens whether or not you stream them. Buffering a 300-token answer just means about 48 s of blank screen. Streaming has an indirect compute benefit: you can stop a wrong answer after 5 seconds, and the disconnect stops generation. Buffer only output that a machine consumes, such as a JSON tool call, a patch to apply, or a commit message, and bound it with `n_predict`, a grammar or `json_schema`, and `t_max_predict_ms` (a server-side wall-clock cap) ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)).

## Prefix caching and output caps beat every hardware knob

**Prompt caching is on by default** (`cache_prompt: true`) and reuses the longest common token prefix from the previous request ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md); [PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)). A 1,000-token fixed prefix that hits the cache saves about 30 s on this CPU [estimate from measured pp]. No flag or Windows setting comes close to that. The rule that follows is to **order the prompt from most stable to most volatile**: fixed system prompt, then repo map or project summary, then retrieved snippets in deterministic order, then conversation history, then the current file excerpt, then the question. Never put timestamps or request IDs near the top, because one changed byte invalidates everything after it.

`--cache-reuse 256` also lets chunks that moved position be KV-shifted instead of recomputed. In the PR's example, a second request reused 516 tokens and computed 1 ([PR #9866](https://github.com/ggml-org/llama.cpp/pull/9866)). The host-RAM prompt cache added in October 2025 (`--cache-ram`, default **8,192 MiB**) keeps earlier prompts and swaps them back into the single slot ([PR #16391](https://github.com/ggml-org/llama.cpp/pull/16391)). Cap it at about 2 GiB on a 16 GB laptop.

There is one caveat. KV shifting does not work on hybrid Mamba models, so the architecture of Granite 4.1 3B matters. Your earlier research found that the Granite 4.1 card describes a plain dense decoder ([HF model card](https://huggingface.co/ibm-granite/granite-4.1-8b)), and llama-bench reports the model as `granite`, not `granitehybrid`. That points to dense [estimate; confirm from the load log], in which case `--cache-reuse` should work normally.

Shrink the input with your own stack. Roslyn (`Microsoft.CodeAnalysis.CSharp`) can emit a signature skeleton (namespaces, types, member signatures, bodies replaced by `;`), with the full body included only for the member under discussion. Aider's repo map does the same job in a default **1k-token** budget ([aider docs](https://aider.chat/docs/repomap.html)). For chat retrieval, use BM25 over identifier-split chunks, for example SQLite FTS5. It is cheap on CPU and strong for identifier-heavy C# queries, while an embedding model would compete with the LLM for the same cores [estimate]. **Skip LLMLingua-style prompt compression.** An independent 2026 study found at most an **18% end-to-end speedup**, and only inside a narrow operating window. Outside it, "the compression step dominates and cancels out the gains" [third-party, GPU and M1 hardware] ([summary of arXiv 2604.02985](https://www.emergentmind.com/papers/2604.02985)). On this CPU the compressor would run on the same four cores.

Output is the expensive side, so cap it per request type. Use about 32–64 tokens for autocomplete, with a stop on `\n` (llama.vim budgets about 32 tokens per suggestion ([PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787))), and 256–512 for chat. **Ask for diffs or search/replace blocks rather than whole files.** At 6 tok/s, rewriting a 300-line C# file (about 3,000 tokens) takes about 8 minutes, while a 30-line diff takes about 50 s [estimate]. Grammars are effectively free with the LLGuidance backend, at about 50 µs per token mask [third-party] ([llguidance docs](https://huggingface.co/OpenTransformer/llama.cpp-prismml/blob/main/docs/llguidance.md)). With default GBNF, keep repetitions bounded (`x{0,N}`), because unbounded constructs "may result in extremely slow sampling" ([grammars README](https://huggingface.co/OpenTransformer/llama.cpp-prismml/blob/main/grammars/README.md)).

Response caching adds little. An exact-match cache keyed by SHA-256 of the request plus hashes of the included files is a few lines with .NET's `HybridCache` and is safe at temperature 0. Semantic caching is risky for code. One banking case saw false-positive rates of up to 99% at a 0.7 threshold [third-party] ([InfoQ](https://www.infoq.com/articles/reducing-false-positives-retrieval-augmented-generation/)).

Two memory settings are worth knowing. **Keep the KV cache at f16.** q8_0 KV made CPU prompt processing **11–19% slower** in one small test [third-party] ([edge-cpu-gguf-tuner](https://clawhub.ai/orionshaowswmw/edge-cpu-gguf-tuner)), and an 8k context is cheap for a 3B model. Always set `-c` explicitly, because the default `-c 0` allocates the model's full trained context ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)).

## Windows changes trade peak clocks for steady ones

Generation is bandwidth-bound, so turbo buys little generation speed but a lot of heat. Prefill is compute-bound and will lose some speed without it. Disabling turbo with "Maximum processor state = 99%" cut one user's CPU from 85–90 °C to the 60s [third-party] ([Mirazon](https://www.mirazon.com/laptop-overheating-due-turbo-boost-heres-can/)). The trick is unreliable on newer builds with Speed Shift ([Geeks3D](https://www.geeks3d.com/20170213/how-to)), so "Processor performance boost mode = Disabled" or a "Maximum processor frequency" cap (for example 2,200–2,600 MHz) is the more dependable switch. Nobody has measured tok/s with turbo on versus off on this CPU, so it goes on the experiment list.

**Do not put llama-server in Task Manager's Efficiency mode.** EcoQoS "always selects most efficient CPU frequency", even on AC ([MS Learn: Quality of Service](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)). For the same reason, keep `--prio 0`: raising priority starves the IDE, and lowering it can heuristically drop the process to a lower QoS on battery ([same source](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)).

Undervolting, about **−75 mV core and cache on an 8550U**, was the biggest heat reducer on this generation [third-party] ([TechPowerUp forum](https://techpowerup.com/forums/threads/throttlestoping-a-8550u-some-questions.277011)). Most BIOS updates after December 2019 lock it because of the Plundervolt mitigation ([SecurityWeek](https://www.securityweek.com/plundervolt-attack-uses-voltage-steal-data-intel-chips/)). If ThrottleStop shows "Locked", stop there. BIOS modding risks bricking the machine and removes a security fix. Lowering the power limits (PL1 about 10–12 W, PL2 about 15–20 W) and setting EPP toward efficiency in ThrottleStop or XTU are safe and revert on reboot, but nobody has quantified their effect on this chip [estimate].

On cooling, raising the back of the laptop alone gave **−7 °C**, and a fan pad blowing into the intakes gave **−14.5 °C** in one controlled test [third-party, older laptop] ([Overclockers](https://www.overclockers.com/cooler-master-notepal-ergostand/)). Given the measured 10% thermal swing in prefill, both are worth trying. Cleaning the dust out of a laptop that is 6–8 years old is probably worth more still, but no source measured it.

For the MX150, check its device ID in GPU-Z first. **1D10 is the 25 W variant and 1D12 the roughly 10 W variant, about 20–25% slower** [third-party] ([TechSpot](https://www.techspot.com/news/73846-not-all-nvidia-mx150-gpus-created-equal.html)). One user's Afterburner undervolt took an MX150 from 25 W to about 15 W at similar performance ([rentry guide](https://rentry.org/GPUV)). An unstable voltage point froze another user's machine, so test it carefully. Because generation is memory-bound, capping the core clock costs autocomplete little in generation speed [estimate].

The table lists settings you should change yourself. Each is reversible.

| Setting | Change | Why |
|---|---|---|
| Power mode (AC) | Balanced or Best performance; not Battery saver | Avoids EcoQoS throttling of the server |
| Processor boost mode / max frequency | Experiment: Disabled, or capped at about 2.4 GHz | Large heat cut, small generation cost (benchmark first) |
| Task Manager Efficiency mode | Leave **off** for llama-server | EcoQoS forces the lowest-efficiency clocks even on AC |
| Search indexing | Exclude the models folder | No re-indexing of multi-GB files |
| Defender | Path exclusion for `*.gguf` in the models folder only, or put models on a trusted Dev Drive with performance mode ([MS Learn](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode)) | Load-time scan cost. Never exclude the llama-server process or disable real-time protection |
| Pagefile | Keep system-managed | Disabling it risks commit-limit crashes on 16 GB |
| ThrottleStop/XTU | Optional: PL1 10–12 W, EPP toward efficiency; undervolt only if unlocked | Steady clocks instead of hitting PROCHOT; never BIOS-mod |
| Physical | Raise the rear, use a fan pad if the intakes are on the bottom, clean the fans | 7–14.5 °C (third-party); about 10% prefill at stake (measured here) |

## Recommended setup

Check the exact flag names with `llama-server --help` on b11982 first. Several names changed in 2025–2026, for example `-lm/--load-mode`, `--spec-type`, and `-cram`, and some may differ from master ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)). Find the MX150's Vulkan index with `llama-server --list-devices`.

**Chat: Granite 4.1 3B on the CPU** (port 8080):

```
llama-server -m C:\MyOwnLLM\models\granite-4.1-3b-Q4_K_M.gguf ^
  -dev none -ngl 0 -t 4 -tb 8 --poll 0 --prio 0 ^
  -c 8192 -b 2048 -ub 512 -fa on -ctk f16 -ctv f16 ^
  -np 1 --cache-reuse 256 -cram 2048 --slot-save-path C:\MyOwnLLM\slots ^
  --sleep-idle-seconds 900 --host 127.0.0.1 --port 8080
```

**Autocomplete: Qwen2.5-Coder-1.5B on the MX150** (port 8081):

```
llama-server -m C:\MyOwnLLM\models\qwen2.5-coder-1.5b-Q4_K_M.gguf ^
  -dev Vulkan0 -ngl 99 -t 2 --poll 0 ^
  -c 4096 -ub 512 -fa on -np 1 --cache-reuse 256 -cram 512 ^
  --host 127.0.0.1 --port 8081
```

If you stay on Q8_0 for the coder, use `-ub 128 -c 2048`, the measured working configuration. If `-dev none` is not accepted on the chat line, `--no-op-offload` gives the same protection against op offload. The values `-t 2` for the GPU instance's host threads and `--sleep-idle-seconds 900` are estimates. `--slot-save-path` lets you save a warmed system-prompt and repo-map slot and restore it after a restart.

The app layer is an ASP.NET Core proxy with one `Channel<T>` (or a `PriorityQueue` behind a `SemaphoreSlim(1)`) **per backend**. The chat queue orders interactive above explicit fix/explain, above idle pre-warm with `n_predict: 0`. The autocomplete queue is latest-wins per document, with a 250 ms debounce, `n_predict` 32–64, a stop on `\n`, and `t_max_predict_ms` around 2,000. Both queues propagate cancellation, cap depth at about 4–8 and return 429 beyond that, stream SSE to the UI, and buffer only machine-consumed output, which is capped and grammar-constrained. The prompt assembler builds the stable-to-volatile layout from Roslyn skeletons plus FTS5 retrieval. Chat answers are capped at 512 tokens, edits are requested as diffs, and an exact `HybridCache` is keyed by request hash plus file hashes. For FIM, use `/infill` with `input_extra` rather than chat prompts ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)). Skip llama-swap, LiteLLM, semantic caching and prompt compression.

## Experiments still to run on the laptop

Run each with `-r 5`, log temperatures and package power in HWiNFO, and change one thing at a time.

| # | Experiment | Command sketch | What decides it |
|---|---|---|---|
| 1 | `--poll 0` at 6 and 8 threads, and `-t 4 -tb 8` | `llama-bench -m granite.gguf -dev none -t 4,6,8 --poll 0,50 -p 512 -n 64 -r 5` | Whether 4 threads at poll 0 still ties 6–8 on tg, and package watts per tok/s |
| 2 | Q4_0 vs Q4_K_M (Granite) | `llama-bench -m g-Q4_0.gguf,g-Q4_K_M.gguf -dev none -t 4 -tb 8 -p 512 -n 64` | Q4_0 is repacked for AVX2 and expected to raise pp; check quality on your own C# prompts |
| 3 | CUDA 12.4 vs Vulkan for the coder on the MX150 | Same `llama-bench` on both builds, `-ngl 99`; `nvidia-smi` for VRAM after load | Third-party Pascal data suggests about 2× pp for CUDA; VRAM headroom decides |
| 4 | Coder Q4_K_M vs Q8_0 on the MX150 | `-ub 128,256,512 -c 4096` | Whether Q4_K_M runs the default ubatch and raises tg |
| 5 | Turbo off / frequency cap | Run #1's best config with boost Enabled vs Disabled | tok/s per watt and steady-state temperature |
| 6 | n-gram speculative decoding for edits | `--spec-type ngram-simple --spec-draft-n-max 16` (or `ngram-mod` with `--spec-ngram-mod-n-min 8 --spec-ngram-mod-n-max 16`) on a "return this file with X changed" task | Docs cite source-code rewriting as the use case ([speculative.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)); expect about 1.0× on free-form chat |
| 7 | Concurrent load | Chat generating for 10 min while an autocomplete loop runs | How much CPU tg drops from shared cooling |
| 8 | Cache-hit TTFT and architecture check | Send the same 2k prefix twice; read `n_cache`/timings; check the load log for `granite` vs `granitehybrid` | Confirms prefix and `--cache-reuse` savings on Granite |
| 9 | `-ub 256/512/1024` and `-lm mlock` | `llama-bench -ub 256,512,1024`; watch for tg dips when switching apps | Minor; keep the defaults unless clearly better |

## Conclusion

On this hardware, compute spent equals tokens processed, and nothing in the queue changes that number except avoiding work. The queue earns its place through cancellation, stale-request dropping and idle pre-warming, not through how it slices or holds tokens. The measured sweep also shows that published tuning advice for "a 4C/8T laptop" can be wrong for your exact machine. The 4-versus-8 thread claim failed here, while a flag nobody had benchmarked (`--poll 0`) produced the best generation result. So treat every [estimate] in this report the same way: a hypothesis that one `llama-bench` run settles.

Because prompt processing is the bottleneck and the cache makes repeated tokens nearly free, the most valuable engineering is in your ASP.NET Core prompt assembler. Roslyn skeletons and a strictly stable prefix will save more minutes per day than any combination of threads, quantization and power plans.
