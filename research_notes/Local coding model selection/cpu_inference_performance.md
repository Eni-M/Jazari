# CPU Inference Performance of Small LLMs (llama.cpp / Ollama GGUF) on an 8th-gen Intel laptop + MX150 2 GB

Target box: i5/i7-8250U/8550U (4C/8T, 15 W, AVX2, no AVX-512), 16 GB DDR4-2400 dual channel, NVIDIA MX150 2 GB (Pascal, sm_61), Windows. Research date: 2026-10-29. Several data points are 2024-2025 vintage and are flagged as such.

## Q1. Typical tokens/sec and RAM footprint for 0.5B / 1.5B / 3B / 7-8B dense and small MoE at Q4_K_M / Q5 / Q8

### Takeaway
Generation on this CPU is memory-bandwidth bound (37.5 GB/s theoretical ceiling). The only direct measurement found for this CPU class is ~5.6-6.4 tok/s generation and ~13-16 tok/s prompt processing for a 7B Q4_0. Prompt processing (not generation) is the real pain point for coding: a 4k-token prompt into a 7B takes roughly 4-5 minutes on CPU. Qwen3-30B-A3B-class MoE does not fit comfortably at Q4_K_M (18.6 GB file > 16 GB RAM); only ~Q2/IQ3 quants (11-13 GB) technically fit.

### Cited Findings
- i5-8250U spec: DDR4-2400 / LPDDR3-2133, 2 memory channels, max memory bandwidth 37.5 GB/s, 15 W TDP (cTDP 10-25 W), 4 cores / 8 threads, AVX2 (no AVX-512 listed) — [Intel ARK](https://www.intel.com/content/www/us/en/products/sku/124967/intel-core-i58250u-processor-6m-cache-up-to-3-40-ghz/specifications.html)
- Measured, Lenovo Yoga 730 with i7-8550U, 16 GB RAM, llama.cpp CPU (AVX2) build ~b6878 (late 2025), llama 7B Q4_0 (3.56 GiB): 4 threads pp512 13.40 tok/s, tg128 5.64 tok/s; 8 threads pp512 16.29 tok/s, tg128 6.37 tok/s — [mgroeber.de benchmark](https://www.mgroeber.de/benchmarking-llamacpp-on-legacy-hardware-2.html)
- Same laptop with a GTX 1050 (Pascal, CUDA, full offload): pp512 207 tok/s, tg128 16.6 tok/s; Intel UHD 620 via OpenCL was slower than CPU (pp512 9.5, tg 4.0) — [mgroeber.de](https://www.mgroeber.de/benchmarking-llamacpp-on-legacy-hardware-2.html)
- Rule of thumb: generation tok/s ceiling ≈ memory bandwidth (GB/s) ÷ bytes read per token (≈ dense model file size); measurements above ~50% of that ceiling indicate you are bus-limited — [donweb blog (Spanish)](https://blog.donweb.com/llama-cpp-rendimiento-ancho-banda-memoria/); each token requires reading the full dense weights — [specandstack](https://specandstack.substack.com/p/bottleneck-wasnt-ram); [llama.cpp issue #1437](https://github.com/ggml-org/llama.cpp/issues/1437)
- Quant-type matters on CPU beyond file size (Ryzen 7950X, LLaMA-3.1-8B, Dec 2024, older llama.cpp build 4384): llama.cpp pp512 Q4_0 153.5 vs Q4_K_S 108.4 vs IQ4_XS 74.4 vs Q8_0 147.9 tok/s; tg128 (2 threads) Q4_0 11.2, Q4_K_S 9.6, IQ4_XS 8.2, Q8_0 5.0, Q6_K 7.6 tok/s. ik_llama.cpp fork was much faster on pp (~250-270 tok/s across most quants) — [ik_llama.cpp discussion #164](https://github.com/ikawrakow/ik_llama.cpp/discussions/164) (2024 data, flag as older)
- Qwen2.5-Coder-3B at Q4_K_M needs ~2 GB of memory — [SynConfRoute paper, arXiv 2605.04894](https://arxiv.org/pdf/2605.04894)
- CPU-only Qwen2.5-Coder 3B baseline of 12.9 tok/s reported in one speculative-decoding benchmark (CPU model/quant not clearly stated) — [InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu); a separate project reports 8.37 tok/s for Qwen2.5-3B on unspecified CPU hardware — [edge-cpu-inference](https://github.com/nandan2003/edge-cpu-inference) (both weak, hardware unclear)
- Qwen3-Coder-30B-A3B GGUF sizes: UD-IQ2_XXS 10.3 GB, Q2_K 11.3 GB, UD-Q2_K_XL 11.8 GB, UD-IQ3_XXS 12.8 GB, Q3_K_S 13.3 GB, UD-Q3_K_XL 13.8 GB, Q3_K_M 14.7 GB, IQ4_XS 16.4 GB, Q4_0 17.4 GB, Q4_K_M 18.6 GB, Q5_K_M 21.7 GB, Q8_0 32.5 GB — [unsloth HF page](https://huggingface.co/unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF)
- Qwen3-30B-A3B CPU-only reports: 12-13 tok/s on i9-10900 desktop DDR4 64 GB (Q4_K_XL); 12-15 tok/s on Ryzen 7950X3D DDR5 (Q6_K); commenters attribute limits to DDR4 bandwidth — [r/LocalLLaMA thread](https://lr.eu.psf.lt/r/LocalLLaMA/comments/1kag4er/qwen330ba3b_runs_at_1215_tokenspersecond_on_cpu/mpmkc0t/?context=3) (April 2025); a model catalog says ~32 GB RAM is needed CPU-only — [bestllmfor](https://bestllmfor.com/catalog/qwen3-30b-a3b/) (rough guide)
- gpt-oss-20b (another small MoE, ~3.6B active): catalog says ~18 GB RAM CPU-only; The Register recommends 24 GB+ system memory and notes DDR4/DDR5 notebooks offer 20-100 GB/s — [bestllmfor](https://bestllmfor.com/catalog/gpt-oss-20b/); [The Register](https://www.theregister.com/2025/08/07/run_openai_gpt_oss_locally)
- Ollama default context is 4096 tokens; RAM scales with OLLAMA_NUM_PARALLEL × context length — [Ollama FAQ](https://docs.ollama.com/faq)

### Inferences
- Bandwidth-ceiling estimates for generation on this laptop (37.5 GB/s theoretical; the 7B Q4_0 result of 6.4 tok/s × 3.8 GB ≈ 24 GB/s effective, i.e. ~65% of peak). Using ~22-26 GB/s effective and approximate GGUF sizes (not individually sourced; sizes are the usual ballpark for Q4_K_M ≈ 0.6 bytes/param incl. embeddings):

  | Model | Q4_K_M file (approx) | RAM incl. 4k ctx (approx) | Gen tok/s (est.) | Prompt tok/s (est.) |
  |---|---|---|---|---|
  | 0.5B | ~0.4 GB | ~0.7-1 GB | 30-50 | 150-300 |
  | 1.5B | ~1.0 GB | ~1.5-2 GB | 15-22 | 60-100 |
  | 3B | ~1.9-2.0 GB (sourced ~2 GB) | ~2.5-3 GB | 9-13 | 30-50 |
  | 7B/8B | ~4.5-5 GB | ~5.5-6.5 GB | 4.5-6 (sourced 5.6-6.4 for Q4_0 7B) | 12-16 (sourced 13-16) |
  | Qwen3-30B-A3B @ IQ3_XXS/Q2_K_XL | 11.8-12.8 GB | ~13-14.5 GB | ~6-10 if it stays in RAM | ~10-20 |

  Q5_K_M ≈ 1.15-1.2× the Q4_K_M size → ~15% slower generation; Q8_0 ≈ 1.8-1.9× → roughly half the Q4 generation speed. Thermal throttling at 15 W will pull sustained numbers below short llama-bench runs.
- Prompt processing is compute-bound and on this CPU is only ~2-3× generation speed for a 7B. For coding assistants that send large file contexts, 7B on CPU is impractical for interactive use (4,000 tokens / ~15 tok/s ≈ 4.5 min). 1.5B-3B is the realistic interactive tier; 7B is "batch/patient" tier.
- The 30B-A3B MoE at Q2/IQ3 leaves only ~2-4 GB for Windows + IDE + browser; expect paging (mmap) and much worse speed under real use. Treat it as not viable on 16 GB; gpt-oss-20b is similarly borderline.

### Gaps
- No direct measured benchmarks found for 0.5B/1.5B/3B on 8th-gen U-series; table values for those are bandwidth-derived estimates.
- No measured Q4_K_M vs Q5/Q8 numbers on this exact CPU.
- No CPU-only measurement of Qwen3-30B-A3B or gpt-oss-20b on a 16 GB DDR4-2400 laptop.
- Windows-specific overhead vs Linux was not measured in any source found.

## Q2. Is partial GPU offload to a 2 GB MX150 (Pascal, sm_61) worth it, and is it still supported?

### Takeaway
Support exists but is shrinking: as of Aug-Sep 2026 llama.cpp's Windows CUDA 12.4 build (not the CUDA 13.x builds) and Ollama's CUDA v12 runner still include sm_61; CUDA 13 drops Pascal entirely. With 2 GB VRAM it can hold a whole 1.5B Q4 (or a 3B Q4 with a short context) and meaningfully speed up prompt processing, but it throttles thermally; for 7B it's only a handful of layers and gains are marginal.

### Cited Findings
- CUDA 13 drops Maxwell, Pascal and Volta (Turing 7.5 is the floor). llama.cpp's Windows `cuda-12.4` build is the one for Pascal; `cuda-13.3` is not; CMake adds `61-virtual` only for toolkit < 13. Release b10472 (17 Aug 2026) cited — [localaimaster, Sept 20 2026](https://localaimaster.com/blog/pascal-gpus-after-cuda-13) (secondary source; not verified against llama.cpp repo)
- Ollama ships CUDA v12 and v13 runners side by side; v12 builds sm_60/61, v13 starts at 75; compute 5.0-6.2 GPUs need driver 570+; Ollama v0.32.14 (15 Aug 2026) cited — [localaimaster](https://localaimaster.com/blog/pascal-gpus-after-cuda-13)
- Ollama and llama.cpp both ship Vulkan backends as an alternative; no Pascal Vulkan vs CUDA speed comparison was found — [localaimaster](https://localaimaster.com/blog/pascal-gpus-after-cuda-13)
- NVIDIA announced CUDA dropping Maxwell/Pascal/Volta in the next major toolkit — [Tom's Hardware](https://www.tomshardware.com/pc-components/gpus/nvidia-to-drop-cuda-support-for-maxwell-pascal-and-volta-gpus-with-the-next-major-toolkit-release); PyTorch 2.14 is the last release with Pascal wheels — [PyTorch dev-discuss](https://dev-discuss.pytorch.org/t/notice-cuda-12-6-wheels-will-no-longer-be-published-from-pytorch-2-15-drops-maxwell-pascal-volta/3432.md)
- Older llama.cpp issue documents flash-attention failures on Pascal — [llama.cpp issue #7055](https://github.com/ggml-org/llama.cpp/issues/7055) (2024)
- Anecdote: MX150 2 GB with a CUDA-built llama.cpp fork reached ~52 tok/s prompt processing, used q8_0 KV cache with `-fit` off, max usable context 5632 tokens; GPU hit ~80 °C and lost 30-40% to thermal throttling (model/quant unstated) — [r/LocalLLaMA snapshot, Apr 2026](https://reddit.sentinel-team.org/posts/1sbnf8y/snapshots/2026-04-03T21%3A20%3A24.375333Z)
- Comparable Pascal reference: GTX 1050 full offload of 7B Q4_0 gave pp512 207 / tg 16.6 tok/s vs CPU 16 / 6.4 — [mgroeber.de](https://www.mgroeber.de/benchmarking-llamacpp-on-legacy-hardware-2.html)
- llama-server: `-ngl` default "auto"; `--fit` default on ("adjust unset arguments to fit in device memory"); `--cpu-moe` / `--n-cpu-moe N` keep MoE expert weights on CPU; `-ot` overrides tensor placement — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- MX150 has ~48 GB/s GDDR5 (not sourced here) vs ~25 GB/s effective CPU; the main win is prompt processing, which is compute-bound and where even a weak Pascal GPU beats a 15 W 4-core CPU by several times (cf. GTX 1050 at ~13× CPU pp). Best use: fully offload a 1.5B (or 3B Q4 with ≤4k ctx) autocomplete model, or put only the KV cache/attention + a few layers of a 7B there.
- With partial offload of a 7B (~25-30% of layers), generation gain is bounded by the CPU-resident share (Amdahl), so expect maybe +10-30% tg and a larger pp improvement; on Windows also watch the shared CPU/GPU 15-25 W power budget (both chips heat the same chassis).
- Practical guidance: pin the llama.cpp `cuda-12.4` Windows build (or Vulkan build) and don't auto-update to CUDA-13-only releases; with Ollama make sure the v12 runner is selected and driver ≥ 570.

### Gaps
- No primary-source (llama.cpp repo/release notes) confirmation of the cuda-12.4 Windows artifact continuing beyond Aug 2026.
- No controlled MX150 benchmark with known model/quant, and no Vulkan vs CUDA comparison on Pascal.

## Q3. Which compute-saving techniques are practical?

### Takeaway
Highest value on this box: (1) pick the smallest model that's good enough (3B Q4_K_M), (2) cap context (2-8k) and keep prompt caching on, (3) Q8_0 KV cache, (4) test Q4_0 (CPU repacking) vs Q4_K_M, (5) threads = 4 for generation, 8 for batch. Speculative decoding on CPU is unreliable with a 0.5B draft; the ngram/prompt-lookup variant is worth a try for code edits.

### Cited Findings
- Q4_0 weights are repacked at load time into CPU-friendly interleaved layouts (replacing Q4_0_4_4/4_8/8_8 files, which stopped working from build b4282); loading gets slower, inference faster — [bartowski HF notes](https://huggingface.co/bartowski/Kwaipilot_KwaiCoder-AutoThink-preview-GGUF); [llama.cpp PR #17241 (search result)](https://github.com/ggml-org/llama.cpp/pull/17241)
- On an AVX2 CPU (Dec 2024 data), Q4_0 was the fastest 4-bit type in mainline llama.cpp for both pp (153 vs 108 for Q4_K_S, 74 for IQ4_XS) and tg (11.2 vs 9.6 vs 8.2); i-quants (IQ2/IQ3) were 3-5× slower on pp in mainline — [ik_llama.cpp #164](https://github.com/ikawrakow/ik_llama.cpp/discussions/164)
- Threads: on the i7-8550U, 8 threads vs 4 gave +22% pp and +13% tg for 7B Q4_0 — [mgroeber.de](https://www.mgroeber.de/benchmarking-llamacpp-on-legacy-hardware-2.html); llama-server has separate `--threads` and `--threads-batch` — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- KV cache quant: `-ctk/-ctv` accept f16 (default), q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1; `-fa` default auto — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md). In Ollama `OLLAMA_KV_CACHE_TYPE` q8_0 ≈ half f16 memory with very little precision loss; q4_0 ≈ quarter with small-to-medium loss that grows with context; requires flash attention; global setting — [Ollama FAQ](https://docs.ollama.com/faq)
- Context: Ollama default num_ctx 4096, override via `OLLAMA_CONTEXT_LENGTH` or `num_ctx`; keep `OLLAMA_NUM_PARALLEL=1` because RAM scales with parallel × context; `keep_alive` default 5 min (negative = keep loaded) — [Ollama FAQ](https://docs.ollama.com/faq)
- Prompt caching: llama-server `--cache-prompt` enabled by default; `--cache-reuse N` reuses KV chunks via shifting (default 0 = off) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Speculative decoding: current flags are `--spec-draft-model`, `--spec-draft-n-max/min`, `--spec-type`, plus ngram-based speculation (`--spec-ngram-mod-n-min` default 48, `-n-max` default 64) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Speculative decoding results: one CPU-only test reports Qwen2.5-Coder 3B baseline 12.9 tok/s with 2.03× speedup on math — [InventiveHQ](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu); a 0.5B draft is "barely coherent" so acceptance is low, and some users saw no improvement — [r/LocalLLaMA Nov 2024](https://redlib.hackliberty.org/r/LocalLLaMA/comments/1gzm93o/speculative_decoding_just_landed_in_llamacpps/lz0dy5n/?context=3); Qwen2.5 14B + 0.5B draft +38% on Apple hardware — [insiderllm](https://insiderllm.com/guides/speculative-decoding-explained/); GPU 3090 qwen-coder-32B 1.47× — [r/LocalLLaMA](https://redlib.hackliberty.org/r/LocalLLaMA/comments/1gzm93o/speculative_decoding_just_landed_in_llamacpps)
- MoE on limited RAM/VRAM: `--cpu-moe`, `--n-cpu-moe N`, `-ot` keep experts on CPU while attention/dense parts go to GPU — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Model loading: `-lm/--load-mode` with `mlock` to prevent swapping; default mmap — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- On a bandwidth-bound 4-core CPU, speculative decoding with a separate draft model competes for the same memory bus and cores; for a 7B target with a 0.5B/1.5B draft, expect 0-40% gains depending on acceptance (code with repetitive structure accepts better). Running the draft on the MX150 (`-ngld all`) and target on CPU is the configuration most likely to pay off. Ngram/prompt-lookup speculation costs almost nothing and suits "rewrite this file with edits" workloads.
- KV-cache size for small models at 4-8k context is a few hundred MB; q8_0 KV mainly helps when squeezing a model + KV into the 2 GB MX150 (as the anecdote did) rather than system RAM.
- Prompt caching (`cache_prompt` + stable system prompt/file prefix) is the single biggest win for repeated coding queries, since pp is the bottleneck on this CPU.
- Use `ollama run <model> --verbose` or `llama-bench -p 512 -n 128 -t 4,8` to validate; disable Windows power-saving ("Best performance" power mode, plugged in).

### Gaps
- No AVX2-laptop-specific measurement of Q4_0 repack vs Q4_K_M in 2026 builds; 2024 Ryzen data may not carry over (llama.cpp has improved K-quant CPU kernels since).
- No controlled CPU-only speculative decoding benchmark on 4-core laptops.
- Ollama's exposure of speculative decoding / cache-reuse was not confirmed in sources.

## Q4. Quality loss vs quantization level for small models, especially coding

### Takeaway
For ≥3B coding models, Q4_K_M is essentially lossless on simple completion benchmarks (Qwen2.5-Coder-3B: 64.7% Q4_K_M vs 64.4% fp16); for <3B models use Q8_0; Q2_K is catastrophic. Harder tasks reveal bigger gaps, and some blog benchmarks report ~6% HumanEval loss at Q4_K_M for 4B models.

### Cited Findings
- Quantization study (q2_K, Q4_K_M, q8_0, fp16) on Qwen2.5-Coder 1.5B and 3B: Q4_K_M retains fp16 quality for 3B (64.7% vs 64.4% pass@1 HumanEval-Infilling) at lowest latency; Q2_K yields 0.0% for 3B; authors recommend Q4_K_M for ≥3B and q8_0 for smaller models; sub-1B models are non-viable regardless of quant — [SynConfRoute, arXiv 2605.04894 (May 2026)](https://arxiv.org/pdf/2605.04894)
- Same paper: model family matters more than size — Qwen2.5-Coder-3B (64.7%) beat Qwen2.5-Coder-7B (54.0%) and Qwen3-Coder-30B-A3B (64.2%) on FIM single-line completion; but SAFIM block tasks show a 9.6-point gap to 32B (23.1% vs 32.7%); the 1.5B model is near-zero on SAFIM; Qwen3-Coder-Next (80B-A3B, agentic) scored only 41.1% on FIM — [arXiv 2605.04894](https://arxiv.org/pdf/2605.04894)
- Blog benchmark: Q4_K_M costs 1.8-2.5% on 32B+ but Qwen3 4B Q4_K_M loses 6.4% on HumanEval vs Q8 — [bestllmfor](https://bestllmfor.com/guides/quantization-q4-vs-q5-vs-q8-quality/) (non-peer-reviewed); conflicts in magnitude with the arXiv 3B result
- Output-agreement study: Q4_K_M 56.6% token-level fidelity to Q8_0, with the drop steepest below Q4 — [InventiveHQ](https://inventivehq.com/blog/gguf-quantization-quality-benchmark) (agreement ≠ correctness)
- Ollama KV cache q4_0 has noticeable precision loss at larger contexts; q8_0 very little — [Ollama FAQ](https://docs.ollama.com/faq)

### Inferences
- Recommended: 3B coder at Q4_K_M (or Q4_0 for CPU speed if quality holds in own testing); 1.5B at Q8_0 (~1.6-1.8 GB, still fast); 7B at Q4_K_M only if latency acceptable. Avoid IQ2/Q2 for code. Q5_K_M is a reasonable middle ground for 3-4B if RAM permits and slight speed loss is acceptable.
- Benchmark choice drives conclusions: easy FIM benchmarks hide quant/size losses; evaluate on own repo tasks.

### Gaps
- Exact 1.5B Q4_K_M vs Q8_0 numbers not extracted from the paper text.
- No rigorous study found of i-quants (IQ4_XS) vs K-quants on coding for 1-8B models, nor for newer Qwen3/Qwen3.5-era small coders.
