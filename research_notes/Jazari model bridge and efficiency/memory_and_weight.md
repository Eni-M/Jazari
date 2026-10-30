# Cutting memory, disk and CPU weight from Jazari's local llama.cpp setup (without lowering coding quality)

Scope: Granite 4.1 3B Q4_K_M on llama.cpp b11982 (CPU only), i7-8650U, 16 GB single-channel DDR4-2400, Windows 11, plus the .NET 10 gateway and CLI. Research date 2026-10-30. Nothing was run or downloaded.

Tags: **[source]** primary doc or code; **[third-party]** someone else's measurement, other hardware; **[estimate]** arithmetic or inference; **[repo]** read from this repo. Earlier notes (`research_notes/Whole laptop Jazari optimization/inference_speed.md`, `reports/Local model load optimization.md`) are not repeated. Where they settle a point, it is only referenced.

Current launch line, from `src/MyOwnLLM.Cli/ServerLauncher.cs:36-37` [repo]:
`-m <model> -dev none -t 6 --poll 0 -c 16384 --cache-ram 2048 -np 1 --cache-reuse 256 --host 127.0.0.1 --port 8080`

## Granite 4.1 3B architecture: the hybrid premise is wrong, and what that means for the KV cache

### Takeaway
Granite 4.1 3B is a **dense transformer, not a hybrid Mamba-2 model**. Hybrid Mamba was Granite 4.0 "H". So the KV cache grows linearly with context: **80 KiB per token at f16, which is 80 MiB per 1k tokens and 1.25 GiB at the current `-c 16384`**. `--cache-reuse`, context shift and partial-prefix reuse all work as they would on any dense model. No recurrent-state caveats apply.

### Cited Findings
- The config.json reports `model_type: granite`, 40 hidden layers, hidden size 2560, 40 attention heads, **8 KV heads**, head_dim 64 (2560/40), max_position_embeddings 131072, vocab 100352, tied embeddings, and no Mamba or `layer_types` fields — [HF config.json](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/config.json)
- IBM describes the 4.1 models as decoder-only dense transformers (GQA, RoPE, SwiGLU, RMSNorm, shared input/output embeddings). Coverage calls 4.1 a return to dense design after the hybrid Mamba/MoE 4.0 generation. Release is given as 29 Apr 2026 — [IBM HF blog](https://huggingface.co/blog/ibm-granite/granite-4-1); [NYU Shanghai RITS summary](https://rits.shanghai.nyu.edu/ai/ibm-releases-granite-4-1-dense-8b-matches-prior-32b-moe-flagship/)
- The earlier notes found that llama-bench reports the model as `granite`, not `granitehybrid`, and that the Granite 4.0 Nano configs say `granitemoehybrid` while 4.1 says `granite` — see inference_speed.md (speculative section)

### Inferences
- KV bytes per token = 2 (K and V) × 40 layers × 8 KV heads × 64 × bytes per element [estimate, from the config]:

| KV type | Bytes/elem | Per token | Per 1k tokens | `-c 4096` | `-c 6144` | `-c 8192` | `-c 16384` |
|---|---|---|---|---|---|---|---|
| f16 | 2 | 80 KiB | 80 MiB | 320 MiB | 480 MiB | 640 MiB | 1,280 MiB |
| q8_0 | 34/32 | 42.5 KiB | 42.5 MiB | 170 MiB | 255 MiB | 340 MiB | 680 MiB |
| q4_0 | 18/32 | 22.5 KiB | 22.5 MiB | 90 MiB | 135 MiB | 180 MiB | 360 MiB |

- llama.cpp allocates the KV buffer for the full `-c` at load time. So `-c 16384` with f16 reserves about 1.25 GiB even though prompts stay at 2–5k. Going to `-c 8192` frees about 640 MiB with no quality effect, as long as prompt plus answer still fit [estimate].
- Because the model is dense, the earlier report's caveat ("KV shifting does not work on hybrid Mamba models") does not apply. `--cache-reuse 256` is valid. Context checkpoints (`-ctxcp`, `-cms`) exist mainly for SWA/recurrent models and should be idle here [estimate, consistent with inference_speed.md].
- **The 2.8 tok/s at 5k prompt tokens is too slow to be explained by bandwidth alone.** Weights about 2.1 GB plus 5k × 80 KiB ≈ 0.4 GB of KV gives about 2.5 GB read per token. Bandwidth scaling alone predicts about 6 × 2.1/2.5 ≈ 5 tok/s, not 2.8 [estimate]. The rest is likely attention compute or a non-optimal attention path at depth. That makes the flash-attention test below the most promising single experiment for long prompts.

### Gaps
- The actual KV and compute buffer sizes from the b11982 load log were not read; no `llama-server.log` was found on disk. The load log prints `llama_kv_cache: size = … MiB`, which confirms the table in one run.

## KV cache quantization and flash attention on CPU

### Takeaway
**Keep K/V at f16 and shrink `-c` instead.** At 2–5k prompts, q8_0 KV saves only about 100–200 MiB of the cache that is actually used. The only CPU measurement found shows it slows prompt processing by 11–19%. Greedy code output also diverges from f16 noticeably at q8_0, and badly at q4_0. **q4_0 KV is ruled out by the quality floor.** Flash attention is the one KV-side flag worth a benchmark here, because tg collapses with depth. I found no published CPU-only `-fa` measurement, so this is untested.

### Cited Findings
- b11982/master flags: `-ctk/-ctv` accept `f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1`, with default f16. `-fa` takes `on|off|auto`, with default `auto` — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)
- CPU: q8_0 KV lowered prompt processing by about 11–19% and generation by about 1–2%. The test box was tiny (2 vCPU, 2 GB, build 1a064ab). The guide recommends quantizing KV "only when RAM-starved" — [edge-cpu-gguf-tuner](https://clawhub.ai/orionshaowswmw/edge-cpu-gguf-tuner) [third-party, weak]
- A100, Qwen3-8B Q4_K_M, b11390 (Oct 2026), measured:
  - Perplexity vs f16 over 4K–32K: q8_0 −0.0 to +0.1%; q4_0 +0.5 to +1.1%.
  - Cache at 32K: f16 4.50 GiB, q8_0 2.39, q4_0 1.27.
  - Decode at 64K depth: q8_0 56% of f16; at 8K depth, 83%.
  - Server throughput (32K prompt): q8_0 cost 9% at 128 output tokens and 22% at 1,024.
  - Source: [sotaaz guide](https://sotaaz.com/post/llamacpp-kv-cache-quantization-guide-en) [third-party, GPU]
- InventiveHQ Lab (2026-06-26), Qwen2.5-Coder-7B Q4_K_M, 8K ctx, greedy, 12 prompts including code:
  - Output text similarity to f16 (difflib): q8_0 81.6%, **q4_0 8.3%**.
  - RTX 5060 Ti: f16 81.8, q8_0 76.4, q4_0 80.3 tok/s, saving only 208 and 320 MB at 8K.
  - M3 Max: 54.1 / 50.0 / 31.2 tok/s.
  - No pass-rate (HumanEval) or KLD was measured.
  - Source: [InventiveHQ KV benchmark](https://inventivehq.com/blog/kv-cache-quantization-quality-benchmark) [third-party; similarity measures divergence, not correctness]
- Flash attention:
  - RTX 5060 Ti and M3 Max, Qwen2.5-Coder-7B: `-fa` on vs off was within noise (for example 81.3 vs 82.1 tok/s at 4K, identical VRAM). No CPU-only results — [InventiveHQ FA](https://inventivehq.com/blog/flash-attention-llama-cpp-benchmark)
  - A Vulkan regression where FA "auto" cut tg from about 5.8 to 2.5 tok/s, restored by disabling FA — [issue #27137](https://github.com/ggml-org/llama.cpp/issues/27137). Backend-specific, but it shows "auto" can pick wrong.
  - On CUDA, quantized KV with FA silently falls back to CPU attention unless the build has all FA quant kernels — [issue #24485](https://github.com/ggml-org/llama.cpp/issues/24485). Not relevant with `-dev none`.

### Inferences
- Memory saved by q8_0 at the **used** depth (5k tokens): 400 → 212 MiB, about 190 MiB. That is small next to 16 GB, and it costs measurable prefill speed on CPU. Not worth it [estimate].
- **q4_0 KV fails the quality floor.** It had 8.3% similarity to f16 output on a code model and +0.5–1.1% perplexity [third-party].
- q8_0 K with f16 V, or the reverse, was not measured on CPU anywhere I found. Skip it.
- FA test (free, reversible): `llama-bench -m granite-4.1-3b-Q4_K_M.gguf -dev none -t 4 -fa 0,1 -p 512 -n 64 -d 0,2048,4096 -r 3`. `-d` is llama-bench's depth option in 2025+ builds; confirm with `--help`. If `-fa 1` keeps tg at 4096 depth well above 2.8 tok/s, pin `-fa on` in ServerLauncher rather than relying on `auto` [estimate].

### Gaps
- No CPU-only, AVX2 `-fa on` vs `off` benchmark at depth was found from any source.
- What `-fa auto` resolves to on the CPU backend in b11982 was not read in source. The load log prints the decision.
- No code pass-rate (HumanEval/MBPP) study of q8_0 KV was found. Perplexity says near-lossless, while greedy-text similarity says outputs drift (81.6%). These two do not directly conflict, because drift is not error, but neither measures correctness.

## Right-sizing context, `--cache-ram`, `--cache-reuse`, context shift

### Takeaway
With prompts at 2–5k tokens, **`-c 8192` is the right size**. It covers a 5k prompt plus a 512–1,024-token answer with headroom, and it frees about 640 MiB of KV RAM. **`--cache-ram` is not pre-allocated speed. It is a bounded RAM store of earlier prompts' KV state** that can be swapped back into the single slot. At 80 KiB/token, one 5k-token prompt state is about 400 MiB, so 2048 MiB holds about 5 such states. 1024 MiB (2–3 states) is a reasonable trim if RAM is tight.

### Cited Findings
- `--cache-ram/-cram`: "set the maximum cache size in MiB (default: 8192 … -1 - no limit, 0 - disable)". `--cache-reuse`: "min chunk size to attempt reusing from the cache via KV shifting, requires prompt caching". `--context-shift` is **disabled by default**. `--cache-idle-slots` saves idle slots to the prompt cache and requires cache-ram. `--sleep-idle-seconds` defaults to -1 (disabled) — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)
- PR #16391 (ggerganov, merged 9 Oct 2025): "automatic memory offloading to host memory". The cache acts as "extra slots" in regular RAM. The server checks prefix similarity and swaps a cached prompt back in when that saves work. It is bounded by `--cache-ram` bytes and by a token limit that defaults to the context size. It lets "a single server slot" be used "without trashing the prompt cache". The author says it should work with dense, MoE, SWA and SSM models. No benchmark figures are given — [PR #16391](https://github.com/ggml-org/llama.cpp/pull/16391)
- The earlier report recommended capping `-cram` at about 2 GiB on 16 GB, and set `--sleep-idle-seconds 900` as an estimate — [reports/Local model load optimization.md]

### Inferences
- Sizing rule [estimate]: `-c` ≥ largest prompt + max answer tokens + about 10%. For 5k + 1k, that is about 6.6k, so `-c 8192`. If the gateway caps prompts at 4k (it currently uses chars/4 to budget), `-c 6144` saves another 160 MiB.
- `--cache-ram`: the PR does not say whether memory is reserved up front. The "maximum cache size" wording and the swap-in design suggest it grows as prompts are stored [estimate]. Its value is in restoring a *different* earlier prompt, for example switching between two conversations or between the review and chat prefixes. With one conversation at a time, a small value (1024) loses little. Measure by watching the log's cache-update lines and `/slots`.
- `--context-shift` (off by default) would only matter if generation overruns `-c`. For code, a silent shift that drops early context is a quality risk, so keep it off and return the 400 "exceeds context" error the gateway already handles (`ILlamaClient.cs:15`) [repo + estimate].
- `--sleep-idle-seconds` (unloads the model when idle) trades RAM for a cold-start cost. It is worth enabling if the laptop often idles with Jazari open [estimate; cold start not measured here].

### Gaps
- Whether `--cache-ram` memory is committed lazily was not confirmed in source.
- The gateway's real maximum prompt and answer sizes were not measured in tokens.

## Weight quantization on AVX2: Q4_K_M vs Q4_0 vs IQ4_XS/IQ4_NL vs Q5/Q6

### Takeaway
**Stay on Q4_K_M as the quality floor.** Q4_0 is only about 5% smaller (1.99 vs 2.1 GB). Uploaders call it legacy and lower quality, and its edge is mainly prompt speed through full repacking, with no current AVX2 measurement on this class of chip. IQ4_XS is about 10% smaller with K-quant-like quality, but I-quants are "slower than their K-quant equivalent" on CPU. IQ4_NL is the only I-quant that gets AVX2 repack. Q5_K_M and Q6_K are heavier and slower on a bandwidth-bound machine. Pick them only if Q4_K_M visibly fails on C# tasks.

### Cited Findings
- AVX2 repack targets: Q4_0, Q4_K and IQ4_NL get 8x8 repack; Q5_K, Q6_K and Q8_0 have no AVX2 repack path. The local Q4_K_M is about 27% Q6_K by size — see inference_speed.md (quantization section, citing repack.cpp and local GGUF headers)
- Granite 4.1 3B sizes: Q4_0 1.99 GB, Q4_K_M 2.1, Q5_K_M 2.44, Q3_K_M 1.73 — [Unsloth GGUF tree](https://huggingface.co/unsloth/granite-4.1-3b-GGUF/tree/main) (via inference_speed.md)
- Bartowski's Qwen2.5-Coder-3B sizes give the relative scale: IQ4_XS 1.74, Q4_0 1.83, Q4_K_M 1.93, Q5_K_M 2.22, Q6_K 2.54 GB. The page says I-quants "will be slower than their K-quant equivalent" on CPU, and calls Q4_0 "Legacy format, generally not worth using over similarly sized formats". Recommended quants are Q4_K_M (default), IQ4_XS, Q5_K_M and Q6_K — [bartowski Qwen2.5-Coder-3B GGUF](https://huggingface.co/bartowski/Qwen2.5-Coder-3B-Instruct-GGUF)
- An older bartowski table (EPYC 7702, AVX2, about b4222, late 2024), Qwen2 3B:
  - Q4_0 tg128 about 39 t/s; Q4_K_M tg128 about 18.8, flagged as noisy, while its tg512 was about 90% of Q4_0.
  - Q4_K_M pp512 was 147% of Q4_0.
  - Q4_0_X_X files were dropped at b4282 in favour of online repack of plain Q4_0.
  - Source: search summary of bartowski cards (exact page not opened) — [bartowski Llama-3.3-70B card, quant notes](https://huggingface.co/bartowski/Llama-3.3-70B-Instruct-GGUF) [third-party, old build, contradictory and noisy]
- llama.cpp quantize README table (Llama-3.1-8B, hardware unspecified): Q4_K_S 76.71, Q4_K_M 71.93, Q5_K_M 67.23 tok/s. It also warns that requantizing from an already-quantized source "can severely reduce quality" — via inference_speed.md

### Inferences
- Bandwidth ceiling on tg, from file size alone [estimate]:
  - Q4_0 about +5%
  - IQ4_XS about +10%, minus I-quant compute overhead on prefill
  - Q5_K_M −14%
  - Q6_K about −20%
- A disk saving under 0.2 GB is not a reason to switch.
- **Quality ranking at about 4 bits** (general uploader consensus, not measured on Granite or C#): IQ4_XS ≈ Q4_K_M > Q4_0. Q4_0 therefore fails a strict "no quality loss" bar unless your own C# review prompts show no difference [estimate].
- The cleanest file experiment is **Q4_K_M vs IQ4_XS vs Q4_0** with `llama-bench -m a,b,c -dev none -t 4 -p 512 -n 64 -r 5`, plus your fixed C# prompt set at temperature 0.2. Download only the ones that are quality-safe. Always use a quant made from BF16, never requantize the Q4_K_M.

### Gaps
- No 2025–2026 Q4_0 vs Q4_K_M vs IQ4_XS benchmark on AVX2 with current repack was found. The only table is late 2024 and internally noisy.
- No code-task (HumanEval/MBPP/C#) comparison of these quants for Granite 4.1 3B was found.
- No IQ4_XS or IQ4_NL GGUF size for Granite 4.1 3B was confirmed; the Unsloth tree listing was not re-fetched.

## Loading: mmap, mlock, warmup, large pages, standby memory

### Takeaway
Already settled in inference_speed.md: on b11982 the old `--mlock`/`--no-mmap` flags are gone in favour of `-lm auto|none|mmap|mlock|mmap+mlock|dio`, and large pages are not implemented. For RAM accounting, mmap'd weights live in the file cache and show as standby or shared memory, which Windows can evict and re-read under pressure. `-lm mlock` pins them at the cost of other apps. Leave `-lm auto` and the pagefile system-managed.

### Cited Findings
- `-lm` values on master: `mlock` "force system to keep model in RAM rather than swapping or compressing"; `mmap` "if mmap disabled, slower load but may reduce pageouts if not using mlock". `--no-warmup` skips the empty warmup run (warmup enabled by default) — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)
- b11982 has no `SeLockMemoryPrivilege` strings, so no large pages, and keeps `VirtualLock` — inference_speed.md (threads section) [measured there]
- Windows memory priority: `SetProcessInformation(ProcessMemoryPriority)` lowers the priority of pages a process adds to its working set, and lower-priority pages are trimmed first — [MS Learn SetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)

### Inferences
- With repack on (the default), the Q4_K tensors are copied into repacked CPU buffers, so most weight bytes end up in private memory rather than the mmap file cache. Disk-backed eviction then applies only to the non-repacked parts, such as the Q6_K tensors [estimate from the repack design; check the "CPU_REPACK buffer size" line in the load log].
- `--no-warmup` saves one small empty run at start (sub-second for a 3B model). Its only cost is a slower first real request, so it is a neutral change [estimate].
- Windows memory compression and the pagefile hurt only if the laptop is near its commit limit. At about 3 GB total for llama-server (weights + 640 MiB KV at `-c 8192` + compute buffers + ≤2 GiB cache-ram used), 16 GB leaves room [estimate]. Prefer cutting `-c` and `-cram` over `-lm mlock`.

### Gaps
- Cold-start time for b11982 with this model was not measured.
- Whether the repacked buffers replace the mmap pages (freeing file cache) or duplicate them was not confirmed in source.

## Keeping the laptop usable: priority, EcoQoS, threads, power

### Takeaway
The usable-laptop levers, in order:
1. **Use fewer threads than logical cores.** Generation is bandwidth-bound, and `-t 4` matched 6–8 threads in the measured sweep.
2. **Lower the scheduling priority** with `--prio -1` (low) or the BELOW_NORMAL priority class, so the IDE wins CPU contention.
3. **Do not use EcoQoS (Efficiency mode)** for interactive answers, because Windows then picks the most efficient (lowest) frequency.

Lowering priority can lead Windows to infer a lower QoS. That is the one documented risk of the priority lever, so measure tok/s with `--prio -1` on AC.

### Cited Findings
- `--prio`: "set process/thread priority : low(-1), normal(0), medium(1), high(2), realtime(3) (default: 0)". `--poll`, `--cpu-strict` and `-C` are also documented — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)
- EcoQoS: with `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` set, "the system will try to increase power efficiency through strategies such as reducing CPU frequency". It "should not be used for performance critical or foreground user experiences" — [MS Learn SetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
- QoS classification:
  - Window-owning processes and their descendants get High (in focus), Medium (visible) or Low (minimized or occluded).
  - Everything else is classified heuristically, and "threads running with reduced thread priority can imply a lower QoS level".
  - Source: [MS Learn Quality of Service](https://learn.microsoft.com/windows/win32/procthread/quality-of-service)
- Microsoft's own inference guidance: use EcoQoS only for "background inference that doesn't contribute to the foreground user experience", preferably per thread — [MS Learn Windows ML efficiency tips](https://learn.microsoft.com/windows/ai/new-windows-ml/efficiency-tips)
- BELOW_NORMAL_PRIORITY_CLASS sits above IDLE and below NORMAL. HIGH can "use nearly all available CPU time" — [MS Learn SetPriorityClass](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)
- Measured here earlier: tg is flat at 5.5–6.3 tok/s from 3 to 8 threads; pp is about 15% higher at 6–8 threads than at 4 — reports/Local model load optimization.md. `--poll` is a no-op on this OpenMP build, and `KMP_BLOCKTIME` (set to 200 ms if unset) is the real spin control — inference_speed.md

### Inferences
- Concrete change for ServerLauncher: `-t 4 -tb 6` (or `-tb 8`) and `--prio -1`, and drop `--poll 0` (harmless but a no-op). Set `KMP_BLOCKTIME=0` in the child process environment through `ProcessStartInfo.Environment`, so idle OpenMP workers stop spinning for 200 ms after each token batch [estimate; untested here].
  - This leaves 2–4 logical CPUs for the IDE during generation, at a cost of about 0–5% tg.
  - Prefill drops about 15% if `-tb` is also lowered.
- llama-server is started detached by the CLI and owns no window, so Windows QoS is heuristic. Lowering priority may move it to a lower QoS and lower clocks [source-based inference]. Compare `--prio 0` with `--prio -1` on tg and on IDE responsiveness. If tg drops, keep normal priority and rely on fewer threads.
- EcoQoS is defensible only for background jobs such as idle pre-warming of a prefix, and only if Jazari ever separates those into another process [estimate].
- Power mode and thermal levers (Best performance on AC, turbo cap, ThrottleStop limit reasons) are covered in both earlier notes and are not repeated here.

### Gaps
- No measurement exists of `--prio -1` vs `0` tok/s on Windows 11 for a windowless llama-server, or of `KMP_BLOCKTIME=0` on this CPU.
- Whether llama.cpp's `--prio -1` sets the process class, thread priority or both on Windows was not read in source.

## Slimming the build and the install

### Takeaway
A native build is not worth it for speed: the official zip is already Clang 20.1.8 with a runtime-selected Haswell backend, and expected gains are under 3% (inference_speed.md). For disk, the official CPU zip's non-matching `ggml-cpu-*.dll` variants can be deleted, keeping `ggml-cpu-haswell.dll` (the one this AVX2 CPU loads), `ggml-base.dll`, `ggml.dll`, `llama.dll`, `libomp.dll` and the server exe. Any GPU backend DLLs are dead weight under `-dev none`.

### Cited Findings
- b11982 ships Haswell through Sapphire Rapids CPU backends plus `libomp.dll`, built with Clang 20.1.8, and `CPU_REPACK` is in `ggml-cpu-haswell.dll` — inference_speed.md [measured there]
- On an unstated CPU, pp512 ran 48.1 (MSVC) vs 50.2 (clang-cl) vs 55.7 (clang + Ninja), with tg128 14.8 vs 14.95 — [PR #10668](https://github.com/ggml-org/llama.cpp/pull/10668) (via inference_speed.md)
- BLAS "doesn't affect the generation performance" — [build.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/build.md) (via inference_speed.md)

### Inferences
- Which DLLs to keep [estimate]:
  - Delete: `ggml-cpu-alderlake/icelake/skylakex/sapphirerapids/cannonlake/zen4/x64/sse42…`
  - Keep: `ggml-cpu-haswell.dll`
  - Verify first by renaming rather than deleting, then checking the load log's "loaded CPU backend from …ggml-cpu-haswell.dll" line.
  - The disk saved is tens of MB, which is trivial next to the 2.1 GB model. RAM is unchanged, because only one variant loads.
- If a Vulkan or CUDA zip is in use for Granite, a CPU-only zip avoids loading GPU driver DLLs and their memory under `-dev none` [estimate]. The MX150 coder instance from the earlier report still needs its own GPU build.

### Gaps
- The exact file list of the installed b11982 folder was not located on disk (no `llama-server.exe` found under C:\MyOwnLLM).

## Trimming the .NET gateway and CLI

### Takeaway
The gateway uses `Microsoft.NET.Sdk.Web` on net10.0, so **Server GC is on by default, with DATAS** (on by default since .NET 9). DATAS starts with one heap and grows only as needed, which already avoids most of the old per-core heap bloat. For a single-user localhost service, set `<ServerGarbageCollection>false</ServerGarbageCollection>` (Workstation GC) and optionally `System.GC.ConserveMemory`. Native AOT gives the biggest startup and working-set cut. The gateway's dependencies (Microsoft.Data.Sqlite, HybridCache, no EF) make AOT plausible but unverified.

### Cited Findings
- Projects: Gateway is `Microsoft.NET.Sdk.Web`, net10.0, using `Microsoft.Data.Sqlite` 10.0.14 and `Microsoft.Extensions.Caching.Hybrid`. The CLI uses PrettyPrompt and TextCopy. No GC, AOT or ReadyToRun properties are set, and the gateway uses `WebApplication.CreateBuilder` [repo]
- DATAS "was introduced as an opt-in feature in .NET 8 and is enabled by default in .NET 9". It starts with one heap and grows or shrinks the heap count. Plain Server GC "treats the process as the dominant one on the machine" and "can grow the heap aggressively" — [MS Learn DATAS](https://learn.microsoft.com/dotnet/standard/garbage-collection/datas)
- "If memory utilization is high and CPU usage is relatively low, the Workstation GC might be more performant" — [MS Learn ASP.NET Core memory](https://learn.microsoft.com/aspnet/core/performance/memory?view=aspnetcore-10.0)
- GC knobs:
  - `System.GC.ConserveMemory` / `DOTNET_GCConserveMemory` 0–9: "more frequent garbage collections" for a smaller heap.
  - `System.GC.HeapHardLimit` / `DOTNET_GCHeapHardLimit`: a hard cap; the env var takes hex, e.g. 0xC800000 = 200 MiB.
  - `System.GC.RetainVM`.
  - Source: [MS Learn GC config](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector)
- Native AOT for ASP.NET Core: single executable, "reduced startup time", "reduced memory demand", and lower app size, memory and startup than trimmed or untrimmed JIT apps in Microsoft's template benchmark. `WebApplication.CreateSlimBuilder()` minimizes features — [MS Learn ASP.NET Core Native AOT](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0); [gRPC AOT page](https://learn.microsoft.com/aspnet/core/grpc/native-aot?view=aspnetcore-10.0)
- The AOT trade-off: no JIT means no runtime re-optimization, and one .NET 10 user saw a long JSON workload run 3.5× slower under AOT. Microsoft's answer says AOT wins on startup, not necessarily on steady-state throughput — [MS Q&A](https://learn.microsoft.com/answers/a/12838846)

### Inferences
- Lowest-risk gateway change: add `<ServerGarbageCollection>false</ServerGarbageCollection>` to `MyOwnLLM.Gateway.csproj` [estimate: tens of MB working-set saving, unmeasured]. `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` also removes the background GC thread. A hard limit (for example 256 MiB) is a safety net, not a saving.
- The gateway mostly waits on llama-server I/O and streams small JSON, so steady-state JIT throughput hardly matters. AOT's startup and memory wins dominate. It needs `CreateSlimBuilder`, source-generated `System.Text.Json` contexts, and an AOT-warning-free build. Microsoft.Data.Sqlite ships a native e_sqlite3 library; AOT compatibility was not confirmed [estimate].
- CLI: ReadyToRun (`<PublishReadyToRun>true</PublishReadyToRun>`) is the low-effort startup gain. Full AOT depends on PrettyPrompt and TextCopy being trim-safe, which is unverified.
- Measure with `dotnet-counters` or Task Manager private bytes before and after each change.

### Gaps
- No measured numbers for this gateway's working set under Server+DATAS vs Workstation GC.
- The exact numbers in Microsoft's AOT chart were not readable (image only).

## Prompt weight: system prompt, template overhead, tokens per char

### Takeaway
Prompt tokens are the dominant CPU cost here: about 30 s per 1k uncached tokens, per the earlier report. Every trimmed system-prompt token is saved on each cache miss. **I found no published measurement of characters per token for C# under Granite's tokenizer.** The gateway's chars/4 heuristic (`PromptBuilder.cs:48`) should be replaced or calibrated by calling llama-server's own tokenizer, which the code comment already proposes.

### Cited Findings
- `PromptBuilder.cs:48`: "chars/4 heuristic; switch to llama-server /tokenize if limits get tight" [repo]
- Granite 4.1 uses a 100352-entry vocab of `gpt2`/`dbrx` BPE type, and the earlier notes found it token-identical to the Granite 4.0 lists — [config](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/config.json); inference_speed.md. The ~100k size suggests a cl100k-like BPE [estimate].
- The "≈4 chars per token" rule is described as a heuristic rule of thumb, with tiktoken cl100k used for ground truth. A secondary site claims code with long identifiers is miscounted by 5–10%, with no C# or cl100k specifics — search result summaries ([cel-brief docs](https://docs.rs/cel-brief/0.2.0/cel_brief/tokenizer/); [appcrib toklen](https://appcrib.pages.dev/toklen/learn/)) [weak]
- Prompt caching reuses the longest common prefix; order prompts from stable to volatile — reports/Local model load optimization.md

### Inferences
- Measure locally at zero cost: POST representative C# files to `http://127.0.0.1:8080/tokenize` and divide characters by tokens. Do the same for `/apply-template` on an empty chat, which shows the chat-template overhead in tokens. Indentation runs, `{`/`}` lines and long PascalCase identifiers make C# ratios differ from prose, in a direction unknown without measuring [estimate].
- Cheap prompt-weight cuts [estimate]:
  - Strip trailing whitespace and collapse indentation of included code where whitespace does not carry meaning. Note that `/edit` matching relies on exact lines, so do this only for read-only context.
  - Keep the system prompt short and byte-stable.
  - Send Roslyn signature skeletons instead of whole files (earlier report).

### Gaps
- No measured chars-per-token for C# under the Granite/cl100k-style tokenizer.
- Granite 4.1 chat-template token overhead per message was not found.

## Summary table for the report writer (all [estimate] unless marked)

| Change | Exact setting | RAM / disk / CPU effect | Quality risk | Evidence |
|---|---|---|---|---|
| Cut context | `-c 8192` (or 6144 if prompts capped at 4k) | −640 MiB (−800 MiB) KV RAM | None if prompt + answer fit | Config arithmetic [source] |
| Keep KV f16 | no `-ctk/-ctv` | — | Avoids q8 drift / q4 collapse | Third-party GPU + one CPU test |
| Pin flash attention after test | `-fa on` if llama-bench `-d 4096` shows gain | Possibly large tg gain at 5k depth | None (exact math) | No CPU data; test |
| Trim prompt cache | `--cache-ram 1024`–`2048` | Bounded ≤1–2 GiB (grows with use) | None | PR #16391 |
| Fewer threads | `-t 4 -tb 6` | 2+ logical CPUs free; tg ~same | None | Measured sweep (earlier report) |
| Lower priority | `--prio -1` | IDE responsive | None; possible tg loss via QoS | MS docs; untested |
| Stop OpenMP spin | `KMP_BLOCKTIME=0` env for child | Less idle CPU, cooler | None | Source reading; untested |
| Drop no-op flag | remove `--poll 0` | — | — | Source reading |
| Weight file | stay Q4_K_M; test IQ4_XS | IQ4_XS −10% size | Low; test on C# prompts | Uploader guidance |
| Delete unused CPU variant DLLs | keep `ggml-cpu-haswell.dll` | Tens of MB disk | None | Load log check |
| Workstation GC for gateway | `<ServerGarbageCollection>false</ServerGarbageCollection>` | Lower gateway RAM | None | MS docs |
| AOT gateway / R2R CLI | `PublishAot` / `PublishReadyToRun` | Faster start, less RAM | None (build effort) | MS docs |
| Calibrate token budget | `/tokenize` instead of chars/4 | Fewer context overflows | None | — |
