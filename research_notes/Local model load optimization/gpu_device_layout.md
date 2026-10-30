# GPU device layout for llama.cpp on MX150 + UHD 620 + 8th-gen 4C/8T laptop (late 2026)

Context (user measurements, not from the web): Qwen2.5-Coder-1.5B Q8_0 fully on MX150 via Vulkan with `-ub 128`: pp512 166 t/s, tg64 ~12 t/s (high variance); default ubatch failed to allocate compute buffers. Granite 4.1 3B Q4_K_M with 24 layers on MX150: pp 39.8 / tg 6.1 vs CPU 39 / 6.0. Full Granite offload OOM (1.95 GiB weights > ~1.9 GB free VRAM).

Note: there are almost no published llama.cpp numbers for the MX150 or UHD 620 themselves. Most numbers below come from the nearest Pascal cards (GTX 1050 / 1050 Ti) and Intel iGPUs (Iris Xe), and use Llama 2 7B Q4_0, the standard model in llama.cpp's benchmark threads.

## Q1. Vulkan vs CUDA 12.4 on the Pascal MX150: speed, VRAM overhead, support status

### Takeaway
CUDA 13 dropped Pascal and driver branch 580 is NVIDIA's last for Pascal. llama.cpp still shipped a Windows CUDA 12.4 build covering sm_61 as of Aug 2026. On comparable Pascal cards, CUDA gives roughly 2x Vulkan's prompt processing and about the same token generation. For autocomplete, where the cost is mostly prompt processing, the CUDA 12.4 build is worth an A/B test on the MX150.

### Cited Findings
- CUDA 13.x removed support for compute capabilities below Turing (sm_75), so Maxwell, Pascal and Volta are out — [PyTorch dev-discuss notice](https://dev-discuss.pytorch.org/t/notice-cuda-12-6-wheels-will-no-longer-be-published-from-pytorch-2-15-drops-maxwell-pascal-volta/3432); [Tom's Hardware (announcement from CUDA 12.9 notes)](https://www.tomshardware.com/pc-components/gpus/nvidia-to-drop-cuda-support-for-maxwell-pascal-and-volta-gpus-with-the-next-major-toolkit-release)
- llama.cpp's CMake appends arch 61 only when the toolkit is older than 13. Release b10472 (17 Aug 2026) ships a Windows CUDA 12.4 build that covers Pascal; the CUDA 13.x builds do not. The 12.4 build needs both the main zip and the cudart zip. Article dated 20 Sep 2026 — [localaimaster: Pascal GPUs after CUDA 13](https://localaimaster.com/blog/pascal-gpus-after-cuda-13)
- Same source: Ollama v0.32.14 (Aug 2026) ships both CUDA 12 and 13 runners and picks one at load time. GPUs with compute capability 5.0–6.2 need driver 570 or newer — [localaimaster](https://localaimaster.com/blog/pascal-gpus-after-cuda-13)
- NVIDIA said the R580 driver series is the last to support Maxwell, Pascal and Volta. It becomes a legacy branch with occasional fixes. The confirmation was for Linux in July 2025 (older data); outlets said Windows would likely follow under the unified driver but NVIDIA had not confirmed it — [Phoronix](https://www.phoronix.com/news/NVIDIA-580-Linux-Driver-Last-HW); [Tom's Hardware](https://www.tomshardware.com/pc-components/gpu-drivers/nvidia-to-axe-maxwell-pascal-and-volta-gpus-with-end-of-driver-support-580-series-drivers-will-be-the-last-to-support-gtx-900-and-1000-cards)
- CUDA results on Pascal, llama-bench with Llama 2 7B Q4_0 at ngl 99:
  - GTX 1050: pp512 207, tg128 15.2 (with FA: 196 / 16.5)
  - GTX 1050 Ti: pp512 267, tg128 19.1 (with FA: 268 / 20.3)
  - GTX 1060: 417 / 27.8
  - No MX150 entry.
  — [llama.cpp discussion #15013 (CUDA)](https://github.com/ggml-org/llama.cpp/discussions/15013)
- Vulkan results, same model: GTX 1050 Ti pp512 136, tg128 21.0 (with FA: 128 / 20.1); GTX 1070 322 / 41.5; Tesla P40 488 / 59. No MX150 entry and no driver versions listed — [llama.cpp discussion #10879 (Vulkan)](https://github.com/ggml-org/llama.cpp/discussions/10879)
- Over many reports, Vulkan prompt processing is generally lower than CUDA, while tg is about the same. NVIDIA presented cases where Vulkan beats CUDA, but those need newer GPUs with cooperative-matrix support — [Phoronix](https://www.phoronix.com/news/NVIDIA-Vulkan-AI-ML-Success); [NVIDIA dev forum](https://forums.developer.nvidia.com/t/vulkan-as-alternative-backend-for-llama-cpp/363516)
- An OpenBenchmarking result shows a GTX 1050 Ti on Vulkan at about 193 t/s pp (granite-3.0-3b Q8_0, 2048 tokens) — [OpenBenchmarking](https://mail.openbenchmarking.org/result/2601035-NE-20260104549&grr&export=json)

### Inferences
- Comparing the same card (GTX 1050 Ti) across the two threads: CUDA pp is about 2x Vulkan (267 vs 136) and tg is equal or slightly lower (19–20 vs 21). If the MX150 follows the same pattern, the CUDA 12.4 build could raise the measured 166 t/s pp toward about 300 t/s for Coder-1.5B, with tg unchanged. These numbers come from different commits and the CUDA MMQ path may behave differently on a 384-core part, so verify with `llama-bench` on both builds.
- tg on the MX150 is bounded by its 64-bit GDDR5 bus. Clock data from TechPowerUp puts it at roughly 40–48 GB/s depending on variant (see Q5), which is similar to dual-channel DDR4-2400 on the CPU (about 38 GB/s theoretical). This explains why offloading Granite gave no tg gain: the GPU is not faster at the memory-bound part.
- The Q8_0 Coder at 1.76 GiB leaves only about 150 MB for KV cache and compute buffers, which is why the default ubatch failed. Q4_K_M (about 1 GB) should allow the default `-ub 512` (faster pp), a larger context, and about 1.7x the tg bandwidth ceiling. The cost is a small quality drop for a FIM model.
- Plan for staying on CUDA 12.4 and driver R580 for the life of this laptop. Vulkan does not depend on CUDA toolkit support, so it is the fallback if llama.cpp stops shipping cu12.4 builds.

### Gaps
- CUDA vs Vulkan VRAM overhead on Windows (CUDA context and cuBLAS workspace vs Vulkan allocations): no source found. This matters at 2 GB. Measure it with `nvidia-smi` after loading the same model on each build.
- No published MX150 llama.cpp numbers on either backend.
- Whether the latest Windows Game Ready or Studio drivers still support the MX150 in late 2026 (that is, whether R580 is also the Windows cutoff): not confirmed.

## Q2. Is the Intel UHD 620 useful (Vulkan, SYCL, OpenVINO) for prompt processing or as a split device?

### Takeaway
Vulkan on the UHD 620 works and can beat a poorly tuned CPU setup, but it shares the same DDR4 bandwidth and package power as the CPU. It cannot add generation speed while the CPU is busy. SYCL does not support it (11th gen and newer only), and the OpenVINO backend is a preview with no Gen9 validation. Splitting a model across UHD 620 and CPU or MX150 is not worth it here.

### Cited Findings
- On an i5-8250U with UHD 620 (Debian 13, Mesa, Vulkan):
  - Qwen 3.5 2B: 2.32 t/s CPU-only vs 6.07 t/s Vulkan (25/25 layers).
  - Qwen2.5-3B Q4_K_M: 4.54 t/s with 13/37 layers on the iGPU.
  - 8 GB RAM. CPU run flags not stated, so the CPU baseline may be untuned.
  — [ozkanpakdil (Mar 2026)](https://ozkanpakdil.github.io/posts/my_collections/2026/2026-03-22-vulkan-llamacpp-debian-13)
- Iris Xe (i7-1185G7) on Vulkan, Llama 2 7B Q4_0, June 2025 (older data): full offload pp512 106 / tg128 5.9; ngl 0 pp512 73 / tg128 5.2. In an earlier run, ngl 0 raised tg (7.3 to 9.9) but lowered pp. A maintainer said that running only prompt processing on Vulkan is "often a good setting for integrated GPUs" — [llama.cpp discussion #10879](https://github.com/ggml-org/llama.cpp/discussions/10879)
- iGPU pattern on a Ryzen 5600H Vega 7: pp roughly doubled on the iGPU, while tg stayed about equal to CPU because tg is memory-bandwidth bound — [blog: CPU vs iGPU](https://blog-data-d05.pages.dev/posts/2025-03-31_llama-cpp-benchmark--cpu-vs-igpu-93b3cc40ece5)
- The SYCL backend lists supported iGPUs as 11th-gen Core and newer. Intel's verified list covers the iGPUs in 11th–13th gen Core and Arc — [llama.cpp SYCL.md (Debian mirror)](https://sources.debian.org/src/llama.cpp/8064%2Bdfsg-2/docs/backend/SYCL.md); [Intel article](https://www.intel.com/content/www/us/en/developer/articles/technical/run-llms-on-gpus-using-llama-cpp.html)
- The OpenVINO backend for llama.cpp arrived as a preview with OpenVINO 2026.1. Validated models include Qwen2.5-1.5B-Instruct and Llama-3.2-1B. The hardware called out is Wildcat Lake and Arc Pro B70; Gen9 is not mentioned. It is built with `-DGGML_OPENVINO=ON`, and the device is chosen with `GGML_OPENVINO_DEVICE` — [Phoronix](https://phoronix.com/news/OpenVINO-2026.1-Released); [elizaOS RFC](https://github.com/elizaOS/eliza/issues/7633)
- One user's Lunar Lake test had the OpenVINO GPU path abort on init — [elizaOS RFC](https://github.com/elizaOS/eliza/issues/7633)
- `--split-mode` accepts none, layer (default, "pipelined"), row, and tensor (experimental). `--tensor-split` sets per-GPU proportions. `-dev` takes a comma-separated device list, and `none` disables offload — [llama-server manpage](https://manpages.debian.org/unstable/llama.cpp-tools/llama-server.1.en.html)

### Inferences
- The 2.3 to 6 t/s jump in the Debian report is mostly a weak CPU baseline (8 GB RAM, likely untuned threads). The user's own CPU number for a 3B Q4_K_M (6.0 t/s) already matches what the UHD 620 got there. Expect no tg gain from the iGPU on this laptop.
- The UHD 620 sits on the same die as the CPU, so it shares the 15 W package power budget and the DDR4 bus. Running it alongside a CPU-bound instance should slow that instance down (contention), not add throughput. Its only plausible role is prompt processing for a model while the CPU is otherwise idle. Even then, the MX150 at 166+ t/s pp is better.
- A layer split across MX150 and UHD 620 under Vulkan (`-dev Vulkan0,Vulkan1 --tensor-split ...`) is technically possible, but pipelined splitting adds sync overhead, and the iGPU part is limited by RAM bandwidth. It is not recommended for Granite. Instead, keep Granite on the CPU, or reduce it to a smaller quant if a full MX150 fit is ever needed.
- Windows uses Intel's proprietary Vulkan driver rather than Mesa. The Gen9 driver is in legacy or maintenance mode, so results may differ from the Linux report.

### Gaps
- No UHD 620 llama-bench numbers (pp512/tg128) on Windows or Linux.
- No direct measurement of CPU tg slowdown when the iGPU runs concurrently.
- Whether OpenVINO GPU runs at all on Gen9 under the llama.cpp backend: unknown.

## Q3. MX150 for prompt processing / KV / draft model only (weights on CPU); op offload at ngl=0

### Takeaway
With a GPU build and default `--op-offload`, any batch of 32 tokens or more makes llama.cpp copy CPU-resident weights to the GPU and run the batch there, even at `-ngl 0`. On an MX150 over a narrow PCIe link with a 2 GB model, that copy can cancel the gain, which matches the "no gain" result measured for Granite. For the chat model, use `-dev none` (or `--no-op-offload`) so it does not touch the MX150. A 1.5B draft model on the MX150 for Granite is possible in principle, but it competes with the autocomplete instance for the same 2 GB.

### Cited Findings
- Op offload: when a batch is large enough, llama.cpp copies "all of the CPU-assigned weights over to the GPU to process the prompt tokens as a single batch." The default threshold is 32 tokens and can be overridden with the env var `GGML_OP_OFFLOAD_MIN_BATCH`. With low PCIe bandwidth or many CPU-side weights, 32 is probably too low — [HF guide (Doctor-Shotgun)](https://huggingface.co/blog/Doctor-Shotgun/llamacpp-moe-offload-guide)
- Small prompts can finish on the CPU before the weight copy completes; break-even depends on PCIe bandwidth and how many weights are on the CPU — [llama.cpp issue #17026](https://github.com/ggml-org/llama.cpp/issues/17026)
- One user's table showed offload overtaking CPU-only pp at about 16 tokens for an 8B model (fast PCIe). The env var is now read in the CUDA backend — [llama.cpp issue #27425](https://github.com/ggml-org/llama.cpp/issues/27425)
- A bug report says the threshold counts heads times tokens, which can trigger offload even when decoding single tokens. Check its status — [llama.cpp issue #31229](https://github.com/ggml-org/llama.cpp/issues/31229)
- Flags:
  - `--op-offload/--no-op-offload`: "whether to offload host tensor operations to device (default: true)".
  - `--kv-offload/--no-kv-offload`: default enabled.
  - `-devd/--device-draft`: draft devices, defaults to following `--device`.
  - `-ngld/--gpu-layers-draft`: default auto.
  - `-md/--spec-draft-model`, `--spec-draft-n-max` (default 3), `--spec-default`.
  - `--fit`: adjusts unset args to fit device memory, default on.
  — [llama-server manpage](https://manpages.debian.org/unstable/llama.cpp-tools/llama-server.1.en.html)

### Inferences
- The measured Granite results (24 layers on GPU: pp 39.8 vs CPU 39) fit this picture. The CPU-resident remainder is shuttled for pp, the MX150's raw pp is low, and the narrow MX150 PCIe link limits the copy. The link is commonly PCIe 3.0 x4, about 3.9 GB/s, but this was not sourced, so check with GPU-Z. Copying about 2 GB per ubatch at that rate costs on the order of 0.5 s per ubatch.
- Recommended chat command: `-dev none -ngl 0` (or the CPU-only zip). This also keeps the Vulkan/CUDA context from using MX150 VRAM that the autocomplete instance needs. If you do test op offload for Granite pp, raise `GGML_OP_OFFLOAD_MIN_BATCH` (for example to 256) and use the largest `-ub` that fits, so each copy is spread over more tokens.
- KV-on-GPU with weights-on-CPU (`-ngl 0` and KV offload) does not help tg much: attention still pulls K/V over PCIe, and KV for a 3B model at a few thousand tokens is small anyway.
- Speculative decoding with Granite on the CPU and a draft on the MX150 (`-md <small model> -devd Vulkan0 -ngld 99`) needs a draft model with a compatible vocabulary. Granite 4.1 3B would need a Granite-family tiny draft; the Qwen coder cannot draft for Granite. Verification on a bandwidth-bound 4-core CPU gains little when acceptance is modest. Treat it as an experiment, not a default. It also cannot coexist with the 1.5B autocomplete model in 2 GB.

### Gaps
- MX150 PCIe link width on this specific laptop: unverified.
- Whether a Granite 4.x draft-compatible micro model exists: not researched here.
- No benchmark of speculative decoding with a CPU target and a weak dGPU draft.

## Q4. Two llama-server instances concurrently (chat on CPU, autocomplete on MX150)

### Takeaway
This is the best layout: Granite 3B on the CPU (CPU-only, `-dev none`) and Qwen2.5-Coder-1.5B Q4_K_M fully on the MX150. They use different memory buses (DDR4 vs GDDR5), so tg does not compete for bandwidth. The interaction is thermal, plus some CPU time for the GPU instance's host thread.

### Cited Findings
- No source measured two concurrent llama.cpp instances on a CPU+dGPU laptop. I found no reliable sources on this.
- Supporting facts: iGPU and CPU tg are both memory-bandwidth bound — [blog: CPU vs iGPU](https://blog-data-d05.pages.dev/posts/2025-03-31_llama-cpp-benchmark--cpu-vs-igpu-93b3cc40ece5). The MX150 has its own GDDR5 — [TechPowerUp](https://techpowerup.com/242650/nvidia-sneaks-less-powerful-geforce-mx150-variant-into-ultrabooks?cp=3)

### Inferences
- Memory: the instances share only PCIe and a little host RAM. The autocomplete prompt (about 1–2k tokens of FIM context) uploads once per request, and token and logit transfers are tiny. Contention on DDR4 should be negligible.
- CPU: a fully offloaded GPU instance still uses one host thread to submit kernels and sample. Give the chat instance `-t 4` (physical cores) rather than 8, which leaves headroom. HT threads rarely help bandwidth-bound tg.
- Thermal: thin laptops with an MX150 usually share a heatpipe and fan between CPU and dGPU (not sourced for this model). Simultaneous load can push both into throttling, which would explain the high tg variance seen. Autocomplete is bursty (short requests), so sustained overlap only happens when chat generation and typing coincide.
- If both are busy and the CPU throttles, prioritize: cap the autocomplete `n_predict` (for example 64–128), use a short context (`-c 2048–4096`), and set `--cache-reuse` / prompt caching so only new tokens are processed.

### Gaps
- No measurements of CPU tg drop while the MX150 is loaded on a shared-cooling laptop. Recommend a 10-minute A/B (chat tg alone vs while autocomplete loops) with HWiNFO logging of package power and temperatures and GPU clocks.

## Q5. MX150 thermal and power behaviour (25 W vs 10 W variant) and how to limit it

### Takeaway
There are two MX150 variants. On Windows, check the device ID in Device Manager or GPU-Z:
- 1D10: about 1469/1532 MHz, 25 W
- 1D12: 937/1038 MHz core, 1253 MHz memory, about 10 W (8–10 W in some designs), roughly 20–25% slower

`nvidia-smi -pl` on laptop GeForce is unconfirmed and usually locked by the vBIOS/Optimus. A practical cap is an MSI Afterburner voltage/frequency-curve undervolt or frequency cap.

### Cited Findings
- 1D10 runs at about 1469/1532 MHz at 25 W. 1D12 runs at 937 MHz core / 1038 boost / 1253 MHz memory at about 10 W. Notebookcheck measured a 20–25% 3DMark deficit for 1D12. 1D12 was found in 13" ultrabooks (IdeaPad 320S-13, HP Envy 13, ZenBook UX331UA); 1D10 in 14–17" designs. 2018 data, still valid for the hardware — [TechSpot](https://www.techspot.com/news/73846-not-all-nvidia-mx150-gpus-created-equal.html); [TechPowerUp](https://techpowerup.com/242650/nvidia-sneaks-less-powerful-geforce-mx150-variant-into-ultrabooks?cp=3); [Tom's Hardware](https://www.tomshardware.com/news/nvidia-geforce-mx150-two-variants,36724.html)
- A community undervolt guide reports an MX150 running about 168 mV lower, going from 25 W to about 15 W at similar performance. This is one user's numbers. The guide also warns that Afterburner running on Optimus laptops keeps the dGPU awake — [rentry GPU undervolting guide](https://rentry.org/GPUV)
- An unstable voltage point froze an MX150 under stress, and a reboot was needed — [Tom's Hardware forum](https://forums.tomshardware.com/threads/undervolting-gpu-get-back-to-work-after-a-crash-without-a-reboot.3218197/)
- On a 1050 Ti Max-Q, the voltage slider was locked until unlocked in Afterburner settings. A power-limit reading of 1 indicated power throttling — [ComputerBase forum](https://www.computerbase.de/forum/threads/undervolting-1050-ti-max-q-throttling.1806474/)
- A reported MX150 bug left the graphics clock stuck at 427 MHz on some drivers — [NVIDIA dev forum](https://forums.developer.nvidia.com/t/mx150-graphics-clock-suddenly-stuck-at-427mhz-with-drivers-390-87-and-430-26/81357)
- `nvidia-smi -pl` usage comes from desktop mining threads and needs admin. There is no confirmation that it works on laptop GeForce — [hardforum](https://hardforum.com/threads/undervolting-gpu.1954242/)

### Inferences
- The memory clock matters most for tg. 1D12's 1253 MHz GDDR5 (about 5 Gbps effective on 64-bit, so about 40 GB/s) versus about 6 Gbps (about 48 GB/s) for 1D10 is a computed estimate, not from a source. A core undervolt or core clock cap barely hurts tg because tg is memory-bound, so it is a nearly free way to cut heat. pp is compute-bound and will drop with core clock.
- Check what can be changed: `nvidia-smi -q -d POWER,CLOCK,PERFORMANCE` shows the enforced and default power limit and throttle reasons. If "Power Limit" shows N/A or the min equals the max, `-pl` is unavailable. `nvidia-smi -lgc <min>,<max>` (lock GPU clocks) is sometimes allowed even when `-pl` is not; untested on the MX150.
- Alternatives that need no GPU tools:
  - Windows power mode "Best power efficiency" or the OEM quiet profile.
  - Limiting the CPU to a lower max processor state (for example 99% to disable turbo) during chat, which frees shared cooling headroom for the MX150.
- The variance in the measured tg64 (about 12 t/s) is likely boost and thermal oscillation. Benchmark with `-r 5` and log clocks to confirm.

### Gaps
- No source confirming whether `nvidia-smi -pl` or `-lgc` work on the MX150 under Windows 11 with R580-era drivers.
- No sustained-load llama.cpp throttling data for either MX150 variant.

## Q6 (synthesis). Recommended device layout

### Takeaway
1. Chat: Granite 4.1 3B Q4_K_M on the CPU only (`-dev none` or the CPU build, `-t 4`, flash attention on).
2. Autocomplete: Qwen2.5-Coder-1.5B Q4_K_M fully on the MX150 (`-ngl 99`, default or 256 ubatch, `-c 2048–4096`), on the CUDA 12.4 build if A/B shows better pp, otherwise Vulkan.
3. UHD 620: leave it unused.

### Cited Findings
- Same-card comparison shows CUDA pp about 2x Vulkan with equal tg on Pascal — [#15013](https://github.com/ggml-org/llama.cpp/discussions/15013) vs [#10879](https://github.com/ggml-org/llama.cpp/discussions/10879)
- Op offload copies CPU weights for batches of 32 tokens or more — [HF guide](https://huggingface.co/blog/Doctor-Shotgun/llamacpp-moe-offload-guide)
- `-dev none` disables offload — [manpage](https://manpages.debian.org/unstable/llama.cpp-tools/llama-server.1.en.html)

### Inferences
- Why Q4_K_M for autocomplete: it fits with the default compute buffers and KV, raises the tg ceiling, and leaves room for the CUDA context overhead. Q8_0 only fits at `-ub 128` with almost no headroom.
- Why Granite does not go on the MX150: it does not fit fully, and partial offload gave zero gain (measured). Op offload over PCIe cannot beat a CPU that already matches the MX150's memory bandwidth.
- Fallback if VRAM is still tight on CUDA: run autocomplete on Vulkan, which avoids cuBLAS workspace (an unverified assumption), or reduce `-c`.

### Gaps
- Final numbers depend on on-device A/B tests: CUDA 12.4 vs Vulkan with Coder Q4_K_M, and concurrent-load thermal behaviour. No public data exists for this exact hardware.
