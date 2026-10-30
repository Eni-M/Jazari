# Maximizing llama.cpp inference speed on this ThinkPad T480 (late 2026)

Evidence tags used throughout:
- **[measured here]**: read or checked on this laptop on 2026-10-30 (WMI, `--help`, DLL strings, GGUF headers). No new tok/s benchmarks were run, because another Granite `llama-server` sweep held the CPU at 100% the whole time and a bench would have disturbed it and been invalid.
- **[source]**: read in llama.cpp source at commit 8a199ad31 (b11982) or the master docs.
- **[third-party]**: someone else's measurement on other hardware.
- **[estimate]**: arithmetic or inference, not yet tested here.

Not repeated from `reports/Local model load optimization.md`: thread sweep, device split, queueing, prefix-cache prompt layout, Efficiency mode, Defender and indexing, f16 KV.

## Which changes are worth making, in order?

### Takeaway
A second SODIMM is by far the biggest single gain for generation (about +50%, paid). The biggest free gains are n-gram speculation for edit-style output and a tokenizer-compatible Granite draft on the MX150. Most flag-level tuning is worth under 5%, and three things the earlier report relied on need correcting.

### Cited Findings
- This laptop is a T480 (20L6S7K10A) with one 16 GB stick in ChannelA-DIMM0, two memory devices reported, 32 GiB max; DIMM1 is empty, and the stick is a Ramaxel RMSA3300ME78HBF-2666 running at 2400 [measured here].
- Other machine state: BIOS N24ET71W 1.46 (2022-09-29); Balanced power plan; Memory Integrity (HVCI) running; MX150 PCI ID DEV_1D10 [measured here].
- llama-server b11982 is "built with Clang 20.1.8" and ships Haswell through Sapphire Rapids CPU backends plus `libomp.dll` [measured here].

### Inferences
Ranked list. Gains are [estimate] unless a section below shows a measured figure.

| # | Technique | Exact setting | Expected gain | Evidence | Cost | Test |
|---|---|---|---|---|---|---|
| 1 | Second SODIMM, dual channel | Add 16 GB in DIMM1 | tg +40–55%, pp about 0–2% | Third-party, 4C/8T DDR4-2133, four 3B-class Q4_K_M runs | **Paid**, about $40–120 | Same `llama-bench` before and after |
| 2 | N-gram speculation, edit-style output | `--spec-type ngram-mod --spec-ngram-mod-n-match 16 --spec-ngram-mod-n-min 8 --spec-ngram-mod-n-max 32` | 1.3–2× on rewrite-the-file output, about 1.0× on prose | Third-party (GPU), no draft cost | Free | Same edit prompt with and without; read acceptance in the log |
| 3 | Granite draft on the MX150 | See speculative section | Code 1.3–1.9×, review prose 1.0–1.3× | Estimate from third-party CPU run | Free | SPEED-Bench or fixed prompts |
| 4 | Full repack quant | Q4_0 (download) or self-made Q4_K_S | pp +5–25%, tg +5% | Weak; never measured on this CPU | Free | `llama-bench -m a,b` |
| 5 | Repaste with PTM7950, dust, rear lift | Physical | Holds about 10% pp at stake (measured swing in the earlier report) | Forum, other CPU | About $8 | 20-minute loop, log PL and throttle flags |
| 6 | Fix a low thermal offset if present | ThrottleStop PROCHOT Offset | Large only if this unit has the bug | Forum | Free | Read offset in ThrottleStop |
| 7 | Undervolt if FIVR unlocked | ThrottleStop, about −75 mV | Small on tg, some on pp | Forum; Memory Integrity is on here | Free, security trade | FIVR window |
| 8 | Lower-bit quant | Q3_K_M, 1.73 GB | tg up to +21%, quality cost | Bandwidth arithmetic only | Free | Bench plus C# prompts |
| 9 | Physical-core pinning | `-t 4 -C 55 --cpu-strict 1` | 0–5%, more repeatable | Unmeasured | Free | `llama-bench` |
| 10 | OpenMP spin control | `$env:KMP_BLOCKTIME=0` | 0–5%, possibly cooler | Unmeasured | Free | `llama-bench` with HWiNFO |
| 11 | CUDA 12.4 for the MX150 | `llama-b…-bin-win-cuda-12.4-x64.zip` | Coder pp up to 2×; tg mixed | Third-party Pascal | Free | Earlier report's experiment 3 |

Corrections to the earlier report:
1. **`--poll` is a no-op on this build.** See the threads section. The 5.51 to 6.34 tok/s difference it credited to `--poll 0` is probably thermal or run-to-run variation.
2. **Experiment 6 uses a flag that doesn't affect `ngram-simple`.** Its draft length comes from `--spec-ngram-simple-size-m`, not `--spec-draft-n-max`.
3. **"A smaller file is the next knob" is smaller than it sounds.** Q4_0 is only about 5% smaller than Q4_K_M on Hugging Face.

### Gaps
- No tok/s number in these notes was measured on this laptop in this session. Every gain above needs a `llama-bench` run when the CPU is free.

## Speculative decoding: flags, device split, draft model, speedups

### Takeaway
All the flags exist on b11982, except `--spec-replace`, which is gone. A draft must share Granite's exact tokenizer, so Qwen drafts are rejected, and Granite 4.0 models pass. The CPU main with MX150 draft split is supported by the flags, but it hasn't been run here and I found no published number for it.

### Cited Findings
- `--spec-type` on b11982 accepts `none, draft-simple, draft-eagle3, draft-mtp, draft-dflash, draft-dspark, ngram-simple, ngram-map-k, ngram-map-k4v, ngram-mod, ngram-cache` [measured here]; the types are described in [speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md).
- Confirmed in b11982 `--help` [measured here]:
  - `-md` / `--model-draft` / `--spec-draft-model`
  - `-ngld` / `--spec-draft-ngl`
  - `-devd` / `--spec-draft-device`
  - `-td`, `-tbd`, `--spec-draft-n-max` (default 3), `--spec-draft-n-min`
  - `--spec-draft-p-min` (default 0.00), `--spec-draft-p-split`, `--spec-draft-n-ubatch`, `-ctkd`, `-ctvd`
  - `--spec-default`, which enables `ngram-mod` per [speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)
- **Removed or absent on b11982** [measured here]:
  - `--draft-max`, `--draft-min` and `--spec-ngram-size-n/m` are listed as "the argument has been removed".
  - `--spec-replace` and `--spec-use-checkpoints` do not appear in help.
  - The checkpoint PR's squash message said "avoid --spec-use-checkpoints argument" ([PR #19493](https://github.com/ggml-org/llama.cpp/pull/19493)).
- N-gram defaults: `ngram-mod` n-match 24, n-min 48, n-max 64; `ngram-simple` and `ngram-map-k` N 12, M 48, min-hits 1 ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)). In `ngram-mod`, a draft shorter than n-min is discarded, and `ngram-simple` takes its n_max from `size_m` ([common/speculative.cpp @8a199ad31](https://raw.githubusercontent.com/ggml-org/llama.cpp/8a199ad31/common/speculative.cpp)).
- Draft compatibility: vocab type, BOS/EOS flags and IDs must match, sizes may differ by at most 128, and token text must match from ID 5 onward. A failure throws, and there is no detokenize/retokenize path in the code read ([common/speculative.cpp @8a199ad31](https://raw.githubusercontent.com/ggml-org/llama.cpp/8a199ad31/common/speculative.cpp)). Only part of that file was read, so this covers `draft-simple`.
- Granite 4.1 3B and Granite 4.0 H-Tiny GGUFs: both `gpt2`/`dbrx`, BOS and EOS 100257, 100352 tokens, 0 text differences from ID 5 [measured here]. Against the local Qwen3 GGUF: size differs by 51,584 tokens, so it fails the check [measured here].
- `granite-4.0-350m`, `granite-4.0-1b` and `granite-4.1-3b` configs all report vocab 100352 and BOS/EOS 100257 ([350m](https://huggingface.co/ibm-granite/granite-4.0-350m/raw/main/config.json), [1b](https://huggingface.co/ibm-granite/granite-4.0-1b/raw/main/config.json), [4.1-3b](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/config.json)). Both Nano configs say `granitemoehybrid`, while 4.1 says `granite`.
- CPU-only, Qwen2.5-Coder 3B target: 12.9 to 22.1 tok/s overall (1.72×). By task: math 2.03×, code 1.96×, reasoning 1.74×, summarization 1.69×, chat 1.01×. The CPU model isn't named ([InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu)). Its CPU draft model is "not specified"; its GPU runs used Qwen2.5-Coder 0.5B Q8_0.
- Same article, Pascal GTX 1080 Ti, 7B target: best case 1.16× on math. RTX 5060 Ti 7B on CUDA: 0.27×, so a fast target can lose badly ([InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu)).
- Draft on a second device (two GPUs, 123B main, 3B draft): Python coding acceptance 0.813 and over 50% faster; natural language acceptance 0.3–0.5 and 0–20% faster ([discussion #25718](https://github.com/ggml-org/llama.cpp/discussions/25718)). That setup needs an unmerged `draft-remote`.
- In PR #19493, one tester saw over 30% faster on code and about 10% slower on normal text ([PR #19493](https://github.com/ggml-org/llama.cpp/pull/19493)).
- The MLSys 2026 study of speculative decoding in vLLM covers n-gram, EAGLE, draft-model and MTP variants ([arXiv 2601.11580](https://www.arxiv.org/abs/2601.11580)). A summary reports n-gram strongest on code-editing workloads such as InstructCoder. I could not open the PDF, so no figures are cited.
- EAGLE-3, DFlash and DSpark drafts are trained for a specific target; examples are all Qwen3-4B (`AngelSlim/Qwen3-4B_eagle3`, `z-lab/Qwen3-4B-DFlash`, `deepseek-ai/dspark_qwen3_4b_block7`) ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).
- Granite 4.0 Nano comes in 350M and 1B sizes with hybrid and non-hybrid variants ([Unsloth](https://unsloth.ai/docs/models/tutorials-how-to-fine-tune-and-run-llms/ibm-granite-4.0)), and Granite 4.1 itself comes only in 3B, 8B and 30B ([llmreference](https://www.llmreference.com/model-family/granite-4.1)).

### Inferences
- **Upper bound [estimate].** Verifying a batch is much cheaper per token than generating one at a time, as pp about 30 versus tg about 6 tok/s shows. So the theoretical ceiling here is near 30 tok/s (5×), reached only with perfect drafts and a free draft.
- **Draft speed is the real limit [estimate].** Scaling the measured 12 tok/s (1.76 GB coder on MX150) by file size:

| Draft on MX150 | Likely size | Draft tok/s | Notes |
|---|---|---|---|
| Granite 4.0 350M Q4_K_M | about 0.25 GB | about 80 | Best candidate |
| Granite 4.0 1B Q4_K_M | about 1 GB | about 20 | Barely faster than needed; fills most VRAM |
| Granite 4.1 3B `UD-Q2_K_XL` | 1.41 GB | about 15 | Same model, so highest acceptance; a real long shot |

- Example cycle for the Q2 option: 4 drafted tokens at 270 ms plus a 5-token verify at about 200 ms, at about 85% acceptance, gives about 9 tok/s (1.5×). That rests on the assumed verify speed, which `llama-batched-bench` can measure.
- **Why the MX150 helps.** The draft gets its own memory bus, so it doesn't compete for the single DDR4 channel the way a CPU draft would. The cost is heat, because CPU and GPU share a two-heatpipe radiator.
- **Suggested command**, not yet run:

```
llama-server -m granite-4.1-3b-Q4_K_M.gguf -dev none -t 4 -tb 8 -c 8192 -np 1 ^
  -md granite-4.0-350m-Q4_K_M.gguf --spec-type draft-simple ^
  -devd Vulkan1 -ngld 99 --spec-draft-n-max 6 --spec-draft-n-min 2 --spec-draft-p-min 0.75
```

- **Risk: `-dev none` may interfere.** It may stop `-devd Vulkan1` from working. If so, drop it and use `-ngl 0 --no-op-offload --no-host` on the main model.
- **Qwen2.5-Coder cannot draft for Granite**, so your existing coder model can't double as the draft. To use Qwen drafts, the main model would have to change to a Qwen family.
- **For review prose, expect little.** Use the draft or n-gram only for code and edits, since acceptance falls on free text. Per-request toggling isn't confirmed; two server configs may be needed.
- **Newer option.** The local Qwen3-4B could try `--spec-type draft-eagle3` with a trained draft. It is a 2.33 GB main model, so it starts slower than Granite, and no CPU data exists.

### Gaps
- No published measurement of CPU main plus GPU draft, on any hardware.
- `-dev none` plus `-devd Vulkan1` is unconfirmed on b11982; try it first.
- Granite Nano acceptance against Granite 4.1 is unmeasured. The Nano GGUF's architecture tag (`granite` or `granitehybrid`) and token list were not checked directly. A hybrid draft may need checkpoints.
- No exact-file review-prose test exists; "chat" and "summarization" (1.01× to 1.69× on CPU) bracket it.

## Quantization: Q4_0, Q4_K_M, Q3_K_M, Q5 on AVX2

### Takeaway
On AVX2, only Q4_0, Q4_K and IQ4_NL tensors get repacked, so Q5, Q6 and Q8 tensors never do. Your Q4_K_M has 27% of its weights in Q6_K, which isn't repacked, so a fully Q4 file could raise prompt speed. Nobody has published this comparison for this CPU.

### Cited Findings
- AVX2 repack targets: Q4_0 to `q4_0_8x8_q8_0`, Q4_K to `q4_K_8x8_q8_K`, IQ4_NL to `iq4_nl_8x8_q8_0`, all requiring `ne[1] % 8 == 0`. Q8_0, Q5_K and Q6_K have no AVX2 branch ([repack.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/ggml/src/ggml-cpu/repack.cpp)).
- Local Granite 4.1 3B Q4_K_M, by estimated size: Q4_K 1,519 MB, all repack-eligible, and Q6_K 576 MB, about 27% and not repacked [measured here, from GGUF tensor headers].
- `CPU_REPACK` is present in `ggml-cpu-haswell.dll` [measured here].
- Buffer priority is "ACCEL -> GPU host -> CPU extra -> CPU", with GPU host skipped under `no_host` ([llama-model.cpp @8a199ad31](https://raw.githubusercontent.com/ggml-org/llama.cpp/8a199ad31/src/llama-model.cpp)).
- Hugging Face sizes for Granite 4.1 3B: `UD-Q2_K_XL` 1.41 GB, Q3_K_M 1.73, Q4_0 1.99, Q4_K_M 2.1, Q5_K_M 2.44, BF16 6.81 ([Unsloth GGUF](https://huggingface.co/unsloth/granite-4.1-3b-GGUF/tree/main)).
- llama.cpp's table for Llama-3.1-8B, hardware unspecified: Q3_K_M 71.68, Q4_K_S 76.71, Q4_K_M 71.93, Q5_K_M 67.23, Q8_0 50.93 tok/s ([quantize README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/quantize/README.md)). The same page warns requantizing "can severely reduce quality" compared with a 16- or 32-bit source.
- Older tinyBLAS work said Q4_0 and Q8_0 "should go ~40% faster on CPU" for prompt evaluation ([sgemm commit](https://app.semanticdiff.com/gh/ggml-org/llama.cpp/commit/8cc91dc63c0df397d644a581b2cbeea74eb51ae0)). That predates the Q4_K 8x8 repack, so the gap is likely smaller now.
- llamafile on a Threadripper 7995WX favored Q4_K_M over Q4_0, but with llamafile-only K-quant kernels ([llamafile 0.8.5](https://github.com/Mozilla-Ocho/llamafile/releases/tag/0.8.5)).
- Quality: one blog reports Q3_K_M losing about 4–11% overall ([bestllmfor](https://bestllmfor.com/guides/quantization-q4-vs-q5-vs-q8-quality/)). This is a weak, secondhand source.

### Inferences
- If generation is purely bandwidth-limited, tg scales inversely with bytes [estimate, upper bound]:

| Quant | Size | Max tg change | Repack | Verdict |
|---|---|---|---|---|
| Q5_K_M | 2.44 GB | −14% | Mostly none | Skip for speed |
| Q4_K_M | 2.1 GB | baseline | 73% | Current |
| Q4_0 | 1.99 GB | +5% | Full | Try first, free download |
| Q4_K_S | about 2.0 GB | +4% | Near full | Build yourself |
| Q3_K_M | 1.73 GB | +21% | None for Q3 | Try only if quality holds |
| `UD-Q2_K_XL` | 1.41 GB | +49% | Little | Too lossy to be the main model; a possible draft |

- The README table disagrees with the Q3 row (no Q3 gain), so treat +21% as a ceiling.
- **`-dev none` also unlocks repack [estimate from source order].** Without it, a Vulkan GPU host buffer ranks ahead of CPU extra buffers, so CPU-layer tensors may skip repack. This could be a second reason `-ngl 0` without `-dev none` underperforms. `--no-host` is the alternative.
- Self-made Q4_K_S, from the BF16 source: `llama-quantize granite-4.1-3b-BF16.gguf granite-4.1-3b-Q4_K_S.gguf Q4_K_S`.
- Test: `llama-bench -m Q4_K_M.gguf,Q4_0.gguf -dev none -t 4 -p 512 -n 64 -r 5`, then `--repack 0,1` to measure what repack gives. Judge quality on your C# review prompts.

### Gaps
- No direct Q4_0 versus Q4_K_M versus Q3_K_M benchmark on AVX2 with current repack.
- The `llama-roofline` page that reportedly compares Q4_0 and Q4_K_M failed to load.
- Which tensors are Q6_K wasn't broken down.

## Custom builds: Clang, BLAS, OpenVINO, CUDA

### Takeaway
The official prebuilt already has the Clang gain, so a native build has little left to offer. BLAS doesn't change generation. OpenVINO has a free prebuilt, but no evidence for a UHD 620 or 8th-gen CPU.

### Cited Findings
- b11982 prebuilt: Clang 20.1.8, with a `ggml-cpu-haswell.dll` backend [measured here].
- On an unstated CPU, an early-2025 test with Llama 2 7B Q4_0 on 6 threads gave pp512 48.1 (MSVC), 50.2 (clang-cl), 55.7 (clang with Ninja), and tg128 14.8 versus 14.95 ([PR #10668](https://github.com/ggml-org/llama.cpp/pull/10668)).
- BLAS "may lead to some performance improvements in prompt processing using batch sizes higher than 32", and "Using BLAS doesn't affect the generation performance" ([build.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/build.md)).
- Current releases include `win-cuda-12.4`, `win-cuda-13.4`, `win-vulkan`, `win-sycl` and `win-openvino-2026.4.1` x64 zips, and the latest tag was b12014 on 30 Oct 2026 ([releases](https://github.com/ggml-org/llama.cpp/releases)).
- OpenVINO backend: "validated specifically on AI PCs such as the Intel Core Ultra Series 1 and Series 2". Q4_0 and Q4_K_M are supported, only a subset of GGML ops is covered, and stateful `llama-server` supports one chat session ([OPENVINO.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/backend/OPENVINO.md)).
- OpenVINO's requirements list "Intel UHD Graphics" generically ([docs](https://docs.openvino.ai/systemrequirements)).
- An Arc B70 test: OpenVINO pp about 3846 versus SYCL 845 t/s, but tg 40.9 versus 73.9 ([piefed](https://piefed.jeena.net/c/localllama@sh.itjust.works/p/372929/intel-b70-llama-cpp-sycl-vs-llama-cpp-openvino-vs-llm-scaler)). This is a single community run on a discrete card.
- GTX 1080 Ti (Pascal) 7B baseline: CUDA 50.5 versus Vulkan 57.3 tok/s, so CUDA wasn't faster on generation there ([InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu)).

### Inferences
- Native build: expect under 3% [estimate]. Not worth the setup unless you want to try `-DGGML_NATIVE=ON` with the `x64-windows-llvm-release` preset.
- OpenBLAS or MKL: skip. Only prompt speed could move, and I found no benchmark showing a gain.
- OpenVINO: a 10-minute test at most, on the CPU device. On the UHD 620 it can't escape the same single DDR4 channel, where Vulkan gave 12.5 / 4.1. Check it loads the `granite` architecture.
- CUDA 12.4: keep to the coder or draft on the MX150, and compare VRAM first. Your MX150 is DEV_1D10, the 25 W variant by the earlier report's source.

### Gaps
- No OpenBLAS or MKL versus native sgemm numbers found.
- No OpenVINO results on any UHD or 8th-gen device.
- LTO or PGO gains: nothing found.

## Threads, batches, memory and cache flags on b11982

### Takeaway
`--poll` does nothing on this build because it's OpenMP-based, and the real spin control is `KMP_BLOCKTIME`. `--mlock` and `--no-mmap` no longer exist on b11982; use `-lm`. Large pages aren't implemented.

### Cited Findings
- `threadpool->poll` is consumed only inside an `#ifndef GGML_USE_OPENMP` block ([ggml-cpu.c @8a199ad31](https://raw.githubusercontent.com/ggml-org/llama.cpp/8a199ad31/ggml/src/ggml-cpu/ggml-cpu.c)). `ggml-cpu-haswell.dll` references `libomp.dll` and `__kmpc_fork_call` [measured here].
- With OpenMP, priority and CPU mask are applied per thread, and `ggml_cpu_init` sets `KMP_BLOCKTIME` to 200 if unset ([ggml-cpu.c @8a199ad31](https://raw.githubusercontent.com/ggml-org/llama.cpp/8a199ad31/ggml/src/ggml-cpu/ggml-cpu.c)). The string is in the Haswell DLL [measured here].
- Load mode on b11982: `-lm auto|none|mmap|mlock|mmap+mlock|dio`, default auto. No `--mlock`, `--no-mmap` or `--direct-io` in help [measured here].
- `--repack` is on by default, and `-nr` / `--no-repack` turn it off on the server; `llama-bench` takes `--repack <0|1>` [measured here].
- Defaults on b11982: `-ctxcp` 32, `-cms` 8192, `-cram` 8192, `--cache-reuse` 0, `--cache-idle-slots` on [measured here].
- No `SeLockMemoryPrivilege` or `AdjustTokenPrivileges` strings in `llama.dll`, `ggml-base.dll` or the Haswell DLL; `VirtualLock` is in `llama.dll` [measured here].
- One 2026 test (RTX 5070 Ti laptop, large MoE) found llama.cpp and ik_llama.cpp equal, its maintainer calling the work "100% memory bandwidth bound" ([ik_llama #1291](https://github.com/ikawrakow/ik_llama.cpp/discussions/1291)).
- `--poll` still appears in help with default 50, so the flag parses but has no effect here [measured here; effect is from the source reading above].

### Inferences
- Drop `--poll 0` from your scripts or leave it as harmless. Rerun the 4-thread test with and without it, with cooldowns, to confirm.
- Spin test: `$env:KMP_BLOCKTIME=0` versus unset, same `llama-bench`, logging package watts.
- Pinning: logical CPUs 0, 2, 4, 6 should be one per core on Windows, so `-C 55 --cpu-strict 1` for `-t 4` [estimate; confirm the topology]. `llama-bench` has `-C`; I didn't check whether it also takes `--cpu-strict`.
- `-lm mlock` protects against pageouts, not slowness. With only 3.4 GB free at check time [measured here], it could cost other apps. Test when switching apps causes tg dips.
- Large pages: not available without patching, and no gain has been measured. Skip it.
- `-cms 8192` with `-c 8192` means checkpoints are rarely made. That's fine for a dense model, which can roll back partially.

### Gaps
- Effect of `KMP_BLOCKTIME` and of pinning on this CPU: no published numbers.
- `ggml_barrier` spin behaviour wasn't read, so the size of the spin cost is unknown.
- Whether repack happens at `-ngl 0` without `-dev none` needs a load-log check.

## Dual-channel RAM: gain, T480 slots, price

### Takeaway
The T480 has two slots and yours has one free. Measured gains on a similar 4C/8T chip are 52–58% for generation at 4 threads, and essentially nothing for prompts. Prices are unsettled and only roughly known.

### Cited Findings
- T480: two slots, 32 GB max, and a review unit with one module and one slot free ([Notebookcheck](https://www.notebookcheck.net/Lenovo-ThinkPad-T480-i7-8550U-MX150-FHD-Laptop-Review.293682.0.html)). Your machine reports the same [measured here].
- i7-6700T, 4C/8T, 35 W, DDR4-2133, llama.cpp b10326 CPU, Q4_K_M, 4 threads, single then dual channel ([InsiderLLM](https://insiderllm.com/guides/dual-channel-ram-local-llm-cpu-inference/)):

| Model | Single tok/s | Dual tok/s | Gain |
|---|---|---|---|
| Gemma 3 1B | 18.20 | 27.75 | +52.5% |
| SmolLM3 3B | 7.72 | 12.22 | +58.4% |
| Llama 3.2 3B | 7.36 | 11.59 | +57.5% |
| Gemma 3 4B | 5.89 | 9.15 | +55.4% |

- Same test: average gain 19.5% at 2 threads, 44.8% at 3, 55.9% at 4; prompt gains +0.4 to +1.8% ([InsiderLLM](https://insiderllm.com/guides/dual-channel-ram-local-llm-cpu-inference/)). A search summary flagged a labelling conflict between the HTML and PDF versions, so I used the fetched HTML.
- On single channel, 2 to 4 threads added only about 3–5% on 3–4B models; on dual channel, 31–38% ([InsiderLLM](https://insiderllm.com/guides/dual-channel-ram-local-llm-cpu-inference/)).
- Prices, mostly undated: CT16G4SFRA266 16 GB about $40–120 in the US, €45–138 in Europe, £165 from one UK listing ([search of listings](https://www.ebay.de/p/5070294073), [rampricesusa $119.99](https://rampricesusa.com/products/B08C4VKYFG), [PriceSpy UK](https://pricespy.co.uk/s/crucial-ddr4-2666mhz-2x4gb/)).
- A TweakTown headline says DDR4 prices rose over 50% in Q3 2026 ([TweakTown](https://www.tweaktown.com/news/112541/even-ddr4-ram-is-not-safe-from-the-rampocalypse-prices-rose-over-50-percent-in-q3-2026/index.html?type=News)); used prices tend to lag retail ([PC Build Advisor](https://www.pcbuildadvisor.com/?p=3327)).

### Inferences
- Estimated tg here: 6.2 to about 9–9.5 tok/s, a bit less if the 15–25 W limit bites [estimate]. With the extra bandwidth, more threads would start to help, unlike now.
- Current read rate: 2.09 GB × 6.2 tok/s is about 13 GB/s of a 19 GB/s theoretical channel [estimate], consistent with being near the wall.
- Buy a 16 GB DDR4 SODIMM rated 2400 or 2666. It runs at 2400. A matching size keeps all memory dual-channel; 8 GB would leave the upper part single-channel (Intel Flex Memory, from background knowledge, not sourced here).
- It may also help the UHD 620, but your numbers there are low enough that it shouldn't change the device plan.

### Gaps
- No T480-specific or i7-8650U before-and-after measurement exists.
- No live, dated used price was found; check eBay sold listings on the day.
- Lenovo's PSREF PDF couldn't be read, so 32 GB rests on Notebookcheck and WMI.

## Windows and T480 tuning

### Takeaway
The T480 can hold 25 W for a while if thermals allow, so the cooling path and the power limits matter more than Windows settings. Undervolting has two blockers here: Memory Integrity is on, and whether BIOS 1.46 locks voltage is unknown.

### Cited Findings
- T480 i7-8550U + MX150 in a 30-minute Cinebench loop: 29 W for 28 s, then 25 W, falling to 15 W at its thermal limit, with about 5% lost after the first run. On battery, multi-core fell 19% ([Notebookcheck](https://www.notebookcheck.net/Lenovo-ThinkPad-T480-i7-8550U-MX150-FHD-Laptop-Review.293682.0.html)).
- The review also saw drops of up to 40% under extended load, apparently fixed by a BIOS update ([Notebookcheck](https://www.notebookcheck.net/Lenovo-ThinkPad-T480-i7-8550U-MX150-FHD-Laptop-Review.293682.0.html)).
- `throttled` on Linux uses 15 W normal, 25 W up, 10 W down for the i7-8650U, with config values of 44 W / 28 s and a 97 °C trip ([throttled](https://github.com/abn/throttled)). This is a tool's config, not Lenovo's spec.
- Some Lenovo laptops reportedly set a thermal offset of 35, throttling at about 65 °C; the suggested fix is PROCHOT Offset 3 in ThrottleStop, and one user reports raised limits still couldn't sustain more than 20 W ([TechPowerUp](https://www.techpowerup.com/forums/goto/post?id=4574853)). Search-level forum evidence.
- A Yoga X1 Gen3 owner said Lenovo removed that performance-control feature from Vantage ([TechPowerUp](https://techpowerup.com/forums/goto/post?id=4397797)). This is search-snippet evidence for another model.
- The MX150 T480 has a two-heatpipe radiator; a UMA i5-8350U unit with the smaller radiator hit 96 °C and throttled. Fitting the larger radiator (about $26) stopped throttling, and PTM7950 (about $8) gained a further couple of degrees ([Habr](https://habr.com/ru/amp/publications/737244)).
- Undervolt status conflicts:
  - One forum user read Lenovo's changelogs and found CVE-2019-11157 never addressed ([ComputerBase](https://www.computerbase.de/forum/threads/undervolting-intel-skylake-im-notebook-thinkpad.2248709/)).
  - A T480s user there reports `undervolt` working on Linux ([same thread](https://www.computerbase.de/forum/threads/undervolting-intel-skylake-im-notebook-thinkpad.2248709/)).
  - T480 users report −100 to −125 mV on i5-8350U ([TechPowerUp](https://techpowerup.com/forums/goto/post?id=4397305), search snippet only; page returned 403).
  - The earlier report says most post-2019 BIOSes lock it ([SecurityWeek](https://www.securityweek.com/plundervolt-attack-uses-voltage-steal-data-intel-chips/)).
- ThrottleStop can't reach voltage control unless VBS is off, and that means turning off Memory Integrity; WSL2 and Virtual Machine Platform conflict too ([TechPowerUp](https://techpowerup.com/forums/goto/post?id=5062273)). A locked FIVR blocks it regardless ([TechPowerUp](https://techpowerup.com/forums/goto/post?id=5057754)).
- HVCI is running here, with `VirtualizationBasedSecurityStatus` 2 and `SecurityServicesRunning` {2} [measured here].

### Inferences
- First step: open ThrottleStop's FIVR window read-only. If it says "Locked", stop. If it's not locked, undervolting still needs Memory Integrity off, which is a real security trade and your decision.
- Generation gains little from clocks, so the benefit is steadier prompt speed and less heat [estimate].
- Check ThrottleStop's Limit Reasons during a 20-minute Granite run, to see whether PL1, PROCHOT or EDP is the cap. That one check decides which of these fixes matters.
- Stay on AC, because of the 19% battery loss. Use the Best performance slider rather than Balanced [estimate of a small gain].
- If your unit's fan or paste is original, PTM7950 is the cheapest repair (about $8, paid). Your MX150 unit probably already has the larger radiator.
- Background load: at check time, free RAM was 3.4 GB [measured here, while another server ran]. Anything hitting memory competes with the single channel, so close heavy apps before timing.

### Gaps
- The TechPowerUp links above came from search summaries and the pages weren't opened, so the exact post each link points to is unverified.
- Nothing confirms whether BIOS 1.46 locks FIVR on a T480.
- No official Lenovo PL1/PL2 values, thresholds, or current Vantage mode behaviour for this T480 were found.
- No tok/s measurement of any cooling or power change exists on this CPU.

## Anything newer, including other CPU runtimes

### Takeaway
I found no runtime with credible evidence of beating llama.cpp on AVX2 for small GGUF-style models. Newer work inside llama.cpp, such as trained drafts and SPEED-Bench, looks more useful.

### Cited Findings
- b12014 (30 Oct 2026) is 32 builds past your b11982, and its listed highlights are not CPU speedups ([releases](https://github.com/ggml-org/llama.cpp/releases)).
- `tools/server/bench/speed-bench` measures throughput and acceptance against a running `llama-server` and compares baseline to speculative ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).
- `--spec-synth-rates` and `--spec-synth-len` simulate acceptance for benchmarking; their output isn't valid model output ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).
- ik_llama.cpp beat llama.cpp on a 32-core Broadwell Xeon AVX2 (about 5.05 versus 2.70 t/s) with `-rtr 1`, but against old build 4404 ([ik_llama #164](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)).
- A planned llama.cpp versus OpenVINO GenAI benchmark exists as a proposal, not results ([ravi9 issue #166](https://github.com/ravi9/llama.cpp/issues/166)).
- `--spec-type draft-mtp` uses multi-token heads built into the main model, and `--backend-sampling` runs supported samplers on the backend ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).

### Inferences
- `--spec-synth-len` could give a cheap, model-independent ceiling for this CPU: it shows what tok/s is reachable at each acceptance level, before you download a draft.
- Test `-rtr`-style gains only if the Q4_0 test shows repack matters. Bandwidth still limits tg, so ik_llama.cpp is low priority.
- Upgrading from b11982 to a newer build is low risk but isn't expected to change speed.

### Gaps
- No credible 2026 head-to-head of MNN, OpenVINO GenAI, or ONNX Runtime GenAI versus llama.cpp on an AVX2 laptop. Searches returned nothing usable.
- Granite 4.1 support in those runtimes was not checked.
