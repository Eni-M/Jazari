# Alternative runtimes, backends and device use for a 1B–8B coding model on the T480 (i7-8650U, single-channel DDR4, UHD 620, MX150 2 GB)

Research date: 2026-10-30. Nothing was run, downloaded or installed. These notes build on `research_notes/Whole laptop Jazari optimization/inference_speed.md` and avoid repeating it: thread flags, `--poll` doing nothing, `KMP_BLOCKTIME`, repack rules, dual-channel RAM, speculative flags and T480 thermals are all covered there.

Tags:
- **[measured, third-party]**: someone else's number on other hardware.
- **[measured here earlier]**: the user's own numbers from the brief or the earlier notes.
- **[background]**: my general knowledge, not checked against a source in this session. Treat as unverified.
- **[estimate]**: arithmetic.

Baseline [measured here earlier]: Granite 4.1 3B Q4_K_M (2.1 GB), CPU only, prompt 28.8 tok/s, generation 6.2 tok/s.
- UHD 620 Vulkan full offload: 12.5 / 4.1.
- MX150 with 24 layers: 39.8 / 6.1.
- A 1.76 GB coder model fully on the MX150 earlier gave about 12 tok/s generation (earlier notes, `inference_speed.md`).

## Ranked list: what to actually test on this laptop

### Takeaway
The biggest free gain is probably not a new runtime. It is using the MX150 for **all** of a slightly smaller model (full offload of a ~1.7 GB Q3/IQ3 file on the CUDA 12.4 build), or using it as a **prompt-only accelerator** via op-offload. ik_llama.cpp is the only alternative CPU engine with credible evidence of faster prompt processing on AVX2. Everything else is either a llama.cpp wrapper, unmeasured on this class of hardware, or limited by the same single DDR4 channel.

### Cited Findings
- The MX150 already ran a 1.76 GB model at about 12 tok/s generation, about 2× the CPU's 6.2 ([earlier notes, local file](file:///C:/MyOwnLLM/research_notes/Whole%20laptop%20Jazari%20optimization/inference_speed.md)). Granite 4.1 3B Q3_K_M is 1.73 GB and `UD-Q2_K_XL` is 1.41 GB ([Unsloth GGUF](https://huggingface.co/unsloth/granite-4.1-3b-GGUF/tree/main)).
- [measured, third-party] An i7-8550U + MX150 2 GB laptop (Asus UX430U, Linux) ran Bonsai-8B 1-bit fully on the MX150 with a PrismML llama.cpp CUDA fork ([r/LocalLLaMA via snapshot](https://reddit.sentinel-team.org/posts/1sbnf8y/snapshots/2026-04-03T21%3A20%3A24.375333Z), about Sept 2025 by post age).
  - Results: pp 52 tok/s, tg 8–9 tok/s at `-ub 512 -c 5632 -ctk q8_0 -ctv q8_0`.
  - The GPU hit about 80 °C and throttled, costing "30–40%" of TG.

### Inferences
Ranked by expected value. Every number below is an [estimate] until benchmarked.

| # | What | Why | Expected | Effort |
|---|---|---|---|---|
| 1 | **Full offload of a ≤1.75 GB Granite quant to the MX150, CUDA 12.4 build** | The GDDR5 bus is separate from the single DDR4 channel, and the earlier 12 tok/s run shows the card delivers about 20 GB/s effective | tg about 10–12 (vs 6.2); pp about 50; quality cost of Q3 | 30 min |
| 2 | **MX150 as prompt accelerator** (CUDA build, model weights stay in RAM, `-ngl 0` or partial, op-offload on, large `-ub`) | Prompt processing is compute-bound, and the CPU manages only 28.8 tok/s | pp 1.3–1.8×, tg unchanged | 30 min |
| 3 | **ik_llama.cpp** built from source (no official Windows binaries), `-rtr` / `_R4` | Credible 2–3.6× pp on AVX2 K-quants (Zen4, Dec 2024); tg gains are smaller and bandwidth-capped here | pp 1.3–2×?, tg 0–10% | 1–2 h build |
| 4 | **OpenVINO GenAI** (INT4 export) on CPU, then on the UHD 620 | The only route that uses Intel's own iGPU kernels; no Kaby Lake data exists | Unknown; tg capped by the same DDR4 | 1–2 h |
| 5 | 1-bit or ternary 8B on the MX150 (Bonsai-8B) | A third-party run on the same GPU gives tg 8–9 for an 8B | Bigger model at tg ≈ current; coding quality unknown | 30 min, fork build |
| — | Skip: Ollama, KoboldCpp, LM Studio, llamafile, ORT-GenAI DirectML, MLC-LLM, bitnet.cpp, T-MAC | Wrappers, unmaintained paths, or no coding-grade model | — | — |

Commands are below. All are untested on this machine, and flags marked [background] should be checked against `--help` first.

**1. MX150 full offload (CUDA).** Download `llama-b<N>-bin-win-cuda-12.4-x64.zip` plus `cudart-llama-bin-win-cuda-12.4-x64.zip` from the [releases page](https://github.com/ggml-org/llama.cpp/releases). CUDA 13 dropped Pascal ([bestllmfor, secondary](https://bestllmfor.com/guide/llm-gtx-1080-ti/)), so the 12.4 zip is the one that matters.

```
llama-bench -m granite-4.1-3b-Q3_K_M.gguf -dev CUDA0 -ngl 99 -fa 1 -ctk q8_0 -ctv q8_0 -p 512 -n 128 -r 3
llama-server -m granite-4.1-3b-Q3_K_M.gguf -dev CUDA0 -ngl 99 -fa on -c 8192 -ctk q8_0 -ctv q8_0 -np 1 --cache-reuse 256
```

- If it runs out of memory, drop `-c` to 4096. Next try `UD-Q2_K_XL` (1.41 GB) or IQ3_XXS.
- Watch the GPU temperature, given the 30–40% throttle seen on the UX430U.
- Use the Windows display adapter on the iGPU so the MX150's 2 GB stays free [background].

**2. Prompt-only accelerator.**

```
llama-bench -m granite-4.1-3b-Q4_K_M.gguf -dev CUDA0 -ngl 0 -ub 512,1024,2048 -b 2048 -p 2048 -n 64 -t 4
llama-server -m granite-4.1-3b-Q4_K_M.gguf -dev CUDA0 -ngl 0 -ub 2048 -b 2048 -t 4 -c 16384 -np 1 --cache-reuse 256
```

- Compare against `--no-op-offload` and against `-dev none`.
- Also try `-ngl 20` together with large `-ub`.
- Set `$env:GGML_OP_OFFLOAD_MIN_BATCH` only if the mainline env var exists on your build (see the MX150 section).

**3. ik_llama.cpp.**

```
git clone https://github.com/ikawrakow/ik_llama.cpp
cmake -B build -DGGML_NATIVE=ON        (add -DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=61 for the MX150)
cmake --build build --config Release -j 8
build\bin\Release\llama-bench -m granite-4.1-3b-Q4_K_M.gguf -t 4 -rtr 1 -p 512 -n 128
```

- Run the same `llama-bench` on mainline with `-dev none -t 4` for the comparison.
- First confirm ik_llama loads the `granite` architecture of 4.1.

**4. OpenVINO GenAI.** Install with `pip install openvino-genai optimum[openvino]`, then export:

```
optimum-cli export openvino -m ibm-granite/granite-4.1-3b --weight-format int4 --sym --group-size 128 granite41-3b-int4-ov
```

- The `--sym --group-size 128` recipe is from an INT4 model card ([HF example](https://huggingface.co/Irfanuruchi/Llama-3.2-1B-Computer-Engineering-OpenVINO-INT4)).
- Run with `openvino_genai.LLMPipeline(path, "CPU")`, then the same with `"GPU"`.
- Alternatively, use llama.cpp's own `win-openvino` zip (earlier notes).

### Gaps
- None of the gains above are measured on this laptop.
- Whether Granite 4.1 Q3_K_M plus a usable KV cache fits in 2 GB depends on Granite 4.1's KV head layout, which wasn't checked.
- Coding quality of Q3 and Q2 Granite against Q4_K_M is unmeasured.

## ik_llama.cpp: is it measurably faster than mainline on a 4-core AVX2 laptop?

### Takeaway
On AVX2, ik_llama.cpp's clear win is prompt processing, about 2–3.6× on K-quants in the maintainer's Dec 2024 test against a llama.cpp build from that time. Generation gains were 1.1–1.7× there, but that test used only 2 threads on dual-channel DDR5. On a single-channel bandwidth wall, expect little tg gain.

There are no official Windows binaries, the maintainer has no Windows machine, and nobody has published a 4-core AVX2 laptop comparison. It is worth testing for prompt speed only.

### Cited Findings
- [measured, third-party, Dec 2024] LLaMA-3.1-8B on a Ryzen 7950X with the AVX2 code path, against llama.cpp build 4384, ik with `-rtr 1`; PP-512 uses 16 threads, TG-128 uses 2 threads ([ik_llama #164](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)):

| Quant | PP llama.cpp | PP ik | TG llama.cpp | TG ik |
|---|---|---|---|---|
| Q4_0 | 153.5 | 273.5 | 11.23 | 12.92 |
| Q4_K_S | 108.4 | 269.6 | 9.57 | 13.48 |
| Q6_K | 82.6 | 259.2 | 7.62 | 10.05 |
| IQ4_XS | 74.4 | 269.9 | 8.15 | 13.58 |

- On M2 Max, Q4_0 was nearly even: PP 114.6 vs 122.5, TG 23.9 vs 23.4 ([same](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)).
- `-rtr 1` repacks to row-interleaved `_R4`/`_R8` at load time. The maintainer recommends it over requantizing to `_R4`, because requantizing changed the quant mix ([same](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)).
- Windows: the maintainer "has no Windows machine", and the project "may or may not work" on Windows, older GPUs or AMD GPUs. It is aimed at users who build from source ([same](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)).
- Community reports from about July 2025 ([r/LocalLLaMA "Ik_llama vs llamacpp", snapshot](https://reddit.sentinel-team.org/posts/1rsyo23/snapshots/2026-03-16T20%3A46%3A16.316967Z)):
  - A user running CPU-only on "older Intel machines with AVX2 only" says it "definitely outperforms mainline for CPU-only", with no numbers.
  - Other users report Llama 3.2 3B crashes and quant-dependent output problems with Qwen2.5-Coder.
  - Mixed results on GPU: RTX 2060 prefill 400→440, but generation 16 vs 18 slower on ik.
- A small Xeon E-2176G test (AVX2, 4-core cgroup) found the advantage "mainly from prompt evaluation", with generation "marginally slower" (search summary of [deemwar engine bake-off](https://deemwar-products.github.io/llama-cpu-benchmarks/article-engines)). The page returned 404 when fetched, so this is unverified.
- A 2026 test found both engines equal on a bandwidth-bound workload ([ik_llama #1291](https://github.com/ikawrakow/ik_llama.cpp/discussions/1291), cited in the earlier notes).

### Inferences
- The Dec 2024 PP gap overstates today's gap. Mainline has since added Q4_K 8×8 AVX2 repack (earlier notes), which is part of what `-rtr` did. A realistic pp gain on this CPU might be 1.2–2× [estimate].
- Prompt speed is this setup's worst pain point: a 5k prompt takes about 3 minutes at 28.8 tok/s. So even a 1.3× pp gain matters, even if tg stays the same.
- Risks: Granite 4.1 (`granite` arch) support in ik is unverified, and stability on small dense models was reported as shaky. You need Visual Studio Build Tools and CMake, which are free.
- ik also has its own quant types (IQ4_KS, IQ4_K and others) [background], but these need ik-only GGUFs, which would lock you into the fork.

### Gaps
- No ik vs mainline numbers on any 4-core, 15 W AVX2 laptop, or on single-channel memory.
- No 2026 rerun against a recent mainline build on AVX2 was found.
- Whether ik_llama.cpp ships any Windows release zips couldn't be confirmed; the search found none.

## OpenVINO / OpenVINO GenAI on the i7-8650U and UHD 620

### Takeaway
Officially supported hardware, but there is zero published LLM data for Kaby Lake or UHD 620. The UHD 620 shares the same single DDR4 channel, so OpenVINO can't push generation above the CPU's bandwidth ceiling. At best it could beat llama.cpp Vulkan's poor UHD 620 prompt speed (12.5 tok/s). Worth one evening as an experiment, not a plan.

### Cited Findings
- Current docs list "6th - 14th generation Intel Core processors" and "Intel UHD Graphics" for the GPU plugin, which needs the Intel graphics driver ([OpenVINO system requirements](https://docs.openvino.ai/system_requirements), via search summary; the full page was too large to read). An older Intel page lists 6th–13th gen ([Intel](https://www.intel.com/content/www/us/en/developer/tools/openvino-toolkit/system-requirements.html)).
- llama.cpp's OpenVINO backend (prebuilt `win-openvino-2026.4.1` zip) is "validated specifically on AI PCs such as the Intel Core Ultra Series 1 and Series 2". It supports Q4_0 and Q4_K_M and covers only a subset of ops ([OPENVINO.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/backend/OPENVINO.md), per the earlier notes).
- Intel's benchmark page measures 2nd-token throughput with 1024 input and 128 output tokens, and doesn't list UHD 620 ([OpenVINO benchmarks](https://docs.openvino.ai/benchmarks)).
- OpenBenchmarking's OpenVINO GenAI profile (Phi-3-mini int4, TinyLlama) has results only for Alder Lake and later Intel parts, with no Kaby Lake entry ([OpenBenchmarking](https://openbenchmarking.org/test/pts/openvino-genai)).
- Intel's guidance recommends OpenVINO GenAI over plain Optimum-Intel for a smaller footprint and better optimization options ([Intel community blog](https://community.intel.com/t5/Blogs/Tech-Innovation/Artificial-Intelligence-AI/Efficient-AI-Inference-on-CPUs-with-OpenVINO/post/1749501)).
- An Arc B70 discrete-GPU run gave OpenVINO pp about 3846 vs SYCL 845, but tg 40.9 vs 73.9 ([piefed](https://piefed.jeena.net/c/localllama@sh.itjust.works/p/372929/intel-b70-llama-cpp-sycl-vs-llama-cpp-openvino-vs-llm-scaler), earlier notes). OpenVINO tends to win prefill and lose decode.

### Inferences
- UHD 620 (Gen9.5, 24 EUs): roughly 0.4 TFLOPS FP32 / 0.8 FP16 [background]. That is below the MX150 and around the CPU's AVX2 peak. Any OpenVINO pp gain on the iGPU would be modest. tg is capped near the CPU's 6 tok/s, because both read the same 12–13 GB/s.
- On the CPU device, OpenVINO INT4 has no evidence of beating llama.cpp's repacked Q4 kernels on AVX2. It could plausibly be similar or slower [estimate].
- A realistic use of the UHD 620 is not speed. It could run a separate small job, such as an embedding model, without taking CPU cores, but it still competes for memory bandwidth.
- Granite 4.1 export: the model uses the standard `granite` transformers architecture, so `optimum-cli` export is likely to work [estimate]. Check before investing time.

### Gaps
- No tok/s for any LLM on UHD 620 or Gen9 graphics with OpenVINO, from any year.
- Whether OpenVINO 2026.x still supports Gen9 graphics for GenAI LLM pipelines, as opposed to only classic inference, was not confirmed.
- Granite 4.1 isn't confirmed in optimum-intel's supported list.

## ONNX Runtime GenAI, MLC-LLM, llamafile, Ollama, LM Studio, KoboldCpp

### Takeaway
None offers a credible advantage on this hardware.
- **Ollama, KoboldCpp and LM Studio** wrap llama.cpp (ggml), so at best they match it while adding overhead and lagging versions. LM Studio is also closed-source, which breaks the FOSS constraint.
- **llamafile** has its own tinyBLAS kernels, but no current evidence of beating mainline on AVX2.
- **ONNX Runtime GenAI's DirectML path** is in sustained engineering, with new work moved to Windows ML, which targets NPUs and newer hardware.
- **MLC-LLM** (Vulkan) has no data for these GPUs.

### Cited Findings
- DirectML is in "sustained engineering"; new feature development moved to Windows ML. An ONNX Runtime docs commit repositions WinML as the preferred Windows path ([Microsoft Learn DirectML](https://learn.microsoft.com/lb-lu/windows/ai/directml/dml-min-app), [ORT commit #25901](https://app.semanticdiff.com/gh/microsoft/onnxruntime/commit/d6123fb9a4805b31ab060edc764b7a070abcc56c)).
- Windows ML accelerates "on NPUs, GPUs, and CPUs through optional execution providers" and has a `Microsoft.ML.OnnxRuntimeGenAI.WinML` package ([microsoft/WindowsML](https://github.com/microsoft/windowsml)).
- llamafile showed K-quant gains with llamafile-only kernels on a Threadripper in an older release ([llamafile 0.8.5](https://github.com/Mozilla-Ocho/llamafile/releases/tag/0.8.5), older, 2024).

### Inferences
- [background] Ollama and KoboldCpp embed llama.cpp/ggml. LM Studio uses llama.cpp runtimes on Windows but is proprietary. These would only help with packaging, not speed.
- ORT-GenAI on CPU with an int4 ONNX Granite export is possible in principle, but no evidence suggests it beats llama.cpp. DirectML on UHD 620 or MX150 would hit the same memory and compute limits as Vulkan, so it's not worth testing.
- MLC-LLM needs its own model compilation, and no Pascal-mobile or Gen9 results were found. Skip.

### Gaps
- No 2025–2026 head-to-head of ORT-GenAI CPU, MLC-LLM or llamafile against current llama.cpp on any AVX2 laptop.
- No check of whether Ollama's bundled llama.cpp is current enough for Granite 4.1.

## llama.cpp backends on the MX150: CUDA vs Vulkan, partial offload, prompt-only offload, using all devices

### Takeaway
Use the CUDA 12.4 build, because Pascal is dropped in CUDA 13. There are two high-value configurations:
- **(a) Fit the whole model on the MX150 with a smaller quant.** Separate memory bus, about 2× generation.
- **(b) Keep weights in RAM and let op-offload send prompt batches (≥32 tokens) to the GPU.** Faster prompts, unchanged generation.

Partial layer splits gave nothing on generation in the earlier test (6.1 vs 6.2). Combining CPU, MX150 and UHD 620 for one model is not worth it. Use the second device for a draft model or a second job instead.

### Cited Findings
- llama.cpp offloads ops on host-resident weights to the GPU when the batch is at least a minimum size, default 32. Since mainline PR #18535 this can be overridden with `GGML_OP_OFFLOAD_MIN_BATCH`, per an ik_llama PR describing the mainline change ([ik_llama PR #1214](https://mygithub.libinneed.workers.dev/ikawrakow/ik_llama.cpp/pull/1214)). The mainline change itself was not verified.
- `--op-offload` / `--no-op-offload` ("offload host tensor operations to device", default true) is a documented server flag ([llama-server man page](https://manpages.debian.org/unstable/llama.cpp-tools/llama-server.1.en.html)).
- Pascal is dropped in CUDA 13, but CUDA 12.x builds for sm_61 still exist ([bestllmfor, secondary](https://bestllmfor.com/guide/llm-gtx-1080-ti/); [community sm_61 builds](https://github.com/Yanichar/llama.cpp-cuda/releases/tag/b10531)).
- [measured, third-party] On a GTX 1080 Ti (Pascal, desktop) with a 7B model, CUDA gave 50.5 tok/s and Vulkan 57.3, so CUDA was not faster on generation ([InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu), in the earlier notes).
- [measured, third-party] MX150 + i7-8550U, full GPU, CUDA fork: pp 52, tg 8–9, with thermal throttling of 30–40% ([r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1sbnf8y/snapshots/2026-04-03T21%3A20%3A24.375333Z)).
- [measured here earlier] Vulkan with 24 layers on the MX150 gave pp 39.8 and tg 6.1. A 1.76 GB model fully on the MX150 gave tg about 12.

### Inferences
- **Why partial splits don't help tg [estimate].** Per token, the CPU part and the GPU part run one after the other, not in parallel. With 2.1 GB in RAM at about 13 GB/s, and the GPU share at about 20 GB/s effective, the saving is small and sync overhead eats it. That matches 6.1 vs 6.2.
  - Full offload removes the DDR4 read entirely, which is why the 1.76 GB model reached 12 tok/s.
  - So the best "2 GB VRAM" use is a quant that fits, not layer juggling.
- **Implied GPU prompt speed [estimate].** From the 24-layer pp 39.8 (assuming 40 layers, unverified), the GPU share alone corresponds to about 50 tok/s for the whole model, consistent with the Bonsai 52.
  - So a prompt-only accelerator could take 28.8 to roughly 40–50.
  - Costs: PCIe transfers (the MX150 link is typically PCIe 3.0 x4, about 3.9 GB/s [background]; 2.1 GB per ubatch is about 0.55 s) and dequant.
  - Use `-ub 2048` to amortize the transfer: per 2048 tokens, about 0.55 s of transfer against about 40 s of GPU compute.
- **CUDA vs Vulkan on the MX150 [background].** CUDA has DP4A-based integer MMQ kernels for compute capability 6.1, which likely favor CUDA for prompt processing on Pascal. Vulkan won tg on the 1080 Ti. Test both on the full-offload model.
- **KV only on GPU, or `-ot` tricks.** With a dense 3B model, attention and KV are a small share of bytes at short context, so moving only the KV cache to the MX150 adds PCIe sync per token for little saving [estimate]. `--n-cpu-moe` applies only to MoE models (e.g. Granite 4.0 H-Tiny), not Granite 4.1 3B.
  - If an MoE model is ever used, `-ngl 99 --n-cpu-moe <N>` keeps dense and attention tensors on the MX150 and experts in RAM [background]. That is the one case where a 2 GB card helps a larger model.
- **All three devices together.** Not useful for one model [estimate]: the UHD 620 is slower than the CPU on this model (12.5/4.1 vs 28.8/6.2) and shares its memory. The useful split is by job:
  - MX150: main model (full offload of a ≤1.7 GB quant), or the speculative draft (earlier notes).
  - CPU: the other model.
  - UHD 620: idle, or the desktop only.
- **Thermals.** The T480 shares one heatpipe radiator between CPU and MX150 (earlier notes), and the UX430U MX150 lost 30–40% to heat. Running both at full load will slow both.

### Gaps
- No MX150 `llama-bench` result with a mainline CUDA build was found, only the PrismML fork's 1-bit run.
- Whether `GGML_OP_OFFLOAD_MIN_BATCH` exists in b11982 wasn't checked. Run `strings ggml-base.dll | findstr OFFLOAD` or read the source.
- Granite 4.1 3B layer count and KV size per token weren't checked, so the 2 GB fit at a given `-c` is uncertain.
- No measurement of PCIe link width on this T480's MX150.

## Thread, affinity and power tuning on a 4C/8T 15 W chip

### Takeaway
Already covered in the earlier notes. Nothing new and credible was found that changes it:
- Test `-t 4` vs 6 vs 8 for generation and `-tb 8` for the prompt batch.
- Pin with `-C 0x55 --cpu-strict 1`.
- Set `KMP_BLOCKTIME`. `--poll` is a no-op in OpenMP builds.
- Use AC power with Best performance.

On single-channel memory, thread count barely matters for generation.

### Cited Findings
- On single-channel DDR4, going from 2 to 4 threads added only about 3–5% on 3–4B models. On dual channel it added 31–38% (i7-6700T, llama.cpp b10326) ([InsiderLLM](https://insiderllm.com/guides/dual-channel-ram-local-llm-cpu-inference/), in the earlier notes).
- On battery, multi-core performance fell 19% on a T480 ([Notebookcheck](https://www.notebookcheck.net/Lenovo-ThinkPad-T480-i7-8550U-MX150-FHD-Laptop-Review.293682.0.html)).

### Inferences
- Use fewer threads for generation (4) and more for prompt batches (`-tb 8`). Fewer active cores leave more of the 15–25 W for clocks during prefill [estimate].
- `--numa` is irrelevant on a single-socket laptop [background].
- `--prio 2` may help when the desktop is busy, but not otherwise [estimate].

### Gaps
- No i7-8650U-specific measurements of `-t`/`-tb`/affinity/prio were found.

## Newer techniques: BitNet/bitnet.cpp, T-MAC, 1-bit models, Q4_0 repack, speculative variants

### Takeaway
- **Ternary and 1-bit models** are the one genuinely new lever for bandwidth-bound inference. A 1-bit 8B (Bonsai) ran at tg 8–9 on the same MX150, but its coding quality is unproven.
- **bitnet.cpp** only runs BitNet-trained models. The official BitNet 2B4T trails Qwen2.5 1.5B on HumanEval+.
- **T-MAC** explicitly doesn't promise x86 speedups, especially for 4-bit generation.
- Q4_0 repack and speculative decoding are covered in the earlier notes.

### Cited Findings
- BitNet b1.58 2B4T ([Wavect, secondary](https://wavect.io/blog/bitnet-cpu-llm-ram-requirements/)):
  - Reported at about 29 ms per output token (about 34 tok/s) on an i7-13800H with 8 threads and 128 tokens, from the 2B4T report as relayed by Wavect.
  - Ahead of Qwen2.5 1.5B on GSM8K and IFEval, behind on MMLU and HumanEval+.
  - The "5–7 tok/s for 100B on a laptop" claim comes from a synthetic 100B configuration, not a released model.
- T-MAC ([T-MAC repo](https://github.com/lessw2020/T-MAC) mirror; [Microsoft Research](https://www.microsoft.com/en-us/research/?p=1112340)):
  - Up to 4× throughput over llama.cpp on low-bit models, mostly measured on ARM.
  - The repo warns its tests "do not encompass all x86 platforms, particularly older generations", and that significant speedup isn't guaranteed, "especially for 4-bit token generation".
  - Its GEMM kernels were integrated into llama.cpp for prefill only.
- Bonsai-8B 1-bit on MX150 2 GB: pp 52, tg 8–9, PrismML fork build ([r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1sbnf8y/snapshots/2026-04-03T21%3A20%3A24.375333Z)). A commenter suggested a Q4 8B on CPU could match it without throttling.

### Inferences
- For a coding assistant, BitNet 2B4T is likely weaker than Granite 4.1 3B or Qwen2.5-Coder 3B, so bitnet.cpp is low priority [estimate].
- T-MAC on Kaby Lake: skip.
- A 1-bit 8B coder at tg 8–9 on the MX150 would be a capacity upgrade (8B at current speed), if a 1-bit coding-capable model exists and mainline supports its format. Unverified.

### Gaps
- No bitnet.cpp or T-MAC measurement on any 8th-gen U-series CPU.
- Whether mainline llama.cpp (b11982) runs Bonsai's 1-bit format without the PrismML fork wasn't checked.
- No 1-bit or ternary coding-specialist model with published HumanEval-class scores was found.
