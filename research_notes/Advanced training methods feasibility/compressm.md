# CompreSSM and SSM state compression: feasibility for a small, CPU-served coding model

Scope: what CompreSSM is, related SSM-compression work, whether any of it helps make a small coding model lighter. Constraints: training on free Colab/Kaggle T4 16 GB; inference on a laptop CPU through llama.cpp. Researched 2026-10-29.

## 1. What is CompreSSM (paper, authors, mechanism, supported SSM types, in-training vs post-hoc)?

### Takeaway
CompreSSM comes from "The Curious Case of In-Training Compression of State Space Models" (Chahine, Nazari, Rus, Rusch; MIT CSAIL and ELLIS/MPI Tübingen; arXiv 2510.02823, Oct 2025; ICLR 2026). It uses control-theory balanced truncation: it computes Hankel singular values (HSVs) of each SSM block early in training and cuts the state dimensions that carry little HSV energy. It is meant for training from scratch, mainly on LTI diagonal SSMs (LRU, and in principle S5). Mamba-1 is covered only by an approximate appendix case study. Mamba-2 is listed as future work. The paper is not a post-hoc tool for pretrained LLMs. The name is ambiguous: an unrelated 2024 paper on "Compressive Selective State Space Models" (Bhat) exists.

### Cited Findings
- The paper is arXiv 2510.02823 (v3 dated 15 Jan 2026). Its authors are Makram Chahine (MIT CSAIL), Philipp Nazari (ELLIS Tübingen / MPI-IS / ETH), Daniela Rus (MIT CSAIL) and T. Konstantin Rusch (ELLIS Tübingen / MPI-IS). — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- The paper was accepted at ICLR 2026. — [ICLR 2026 proceedings](https://proceedings.iclr.cc/paper_files/paper/2026/hash/b57ec011a4a1c0c97fdc7ccebb672db4-Abstract-Conference.html)
- Mechanism: for each SSM block, the method computes the controllability and observability Gramians, then the HSVs. It keeps the smallest rank whose top HSVs hold a fraction τ of the total energy, then truncates the balanced realization. Truncation happens at snapshots early in training (for example during LR warm-up). The authors argue that the HSV ordering stays stable during training, but they support this empirically, not with a proof. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Supported types: the experiments use LRU. A closed-form Lyapunov solution covers diagonal SSMs (LRU, S5). For selective models, the method averages the input-dependent dynamics into an LTI surrogate. Mamba-2 and Gated DeltaNet are explicitly future work. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Mamba case study (Appendix E.3): original Mamba (S6), IMDB classification only, starting at d_state 128 with τ=0.001, ending at about 12–14 states on average. It trains about 4× faster than the 128-state baseline, about the same as a fixed 16-state model. The study reports no language-modeling or perplexity results. — [arXiv HTML v3, appendix](https://arxiv.org/html/2510.02823v3)
- Mamba-specific caveats from the authors: the link between state size and performance is weak (single-input single-output channels). The HSV spectrum is heavy-tailed, so randomly dropping states is competitive with balanced truncation. Shared projections couple the channels. Real speedups need rank-aware kernels. — [arXiv HTML v3, appendix](https://arxiv.org/html/2510.02823v3)
- The paper starts from a large model and shrinks it during training. It cites a separate post-hoc balanced-truncation method for S4 as related work. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Name ambiguity: an aggregator also lists "Compressive Selective State Space Models" (Bhat, 2024) under a similar name, which is a different method (gating and rate-distortion). — [Emergent Mind topic](https://www.emergentmind.com/topics/compressm); [Emergent Mind algorithm page](https://www.emergentmind.com/topics/compressm-algorithm)

### Inferences
- For "make my pretrained coding model lighter", CompreSSM as published is the wrong tool. It is a recipe for training from scratch, its theory is LTI, and it has never been run on Mamba-2 or on an LLM.

### Gaps
- The Mamba appendix gave no accuracy numbers in the text I could read, so it is unclear whether compressed Mamba matched baseline accuracy on IMDB.

## 2. Reported compression ratios and quality retention; language-model and LLM-scale hybrid results?

### Takeaway
CompreSSM reports results on small LRU models on LRA-style tasks only. Example: CIFAR10 went from 384 to about 93 states (about 4× smaller state) with accuracy falling from 86.5% to 85.7%, and training was about 1.5× faster. It reports no language-modeling results and no LLM or hybrid results. The post-hoc follow-up work (GHOST, and the HARP/balanced-subspace paper) does report Mamba-2 language-model perplexity at 130M–2.7B, plus Zamba2 hybrids. None of it covers Granite 4.0-H.

### Cited Findings
- CompreSSM, CIFAR10: unreduced (384 states) 86.5%. CompreSSM at about 93 states: 85.7%. A model trained directly at that size: 81.8%. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- CompreSSM, sMNIST: unreduced (256 states) 97.3%. About 191 states: 97.2%. About 27.6 states: 96.9%, against 96.0% for a model trained at that size. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Training speedup on CIFAR10 at about 93 states: about 1.5× over the full model (training at the small size directly: about 1.6×). — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Limitations: no gain on AAN or Pathfinder, overfitting on IMDB, and the "pragmatic" variant varies a lot (sMNIST final size 121.3 ± 89.8). Tasks: sMNIST, CIFAR10, ListOps, IMDB, AAN, Pathfinder; no language modeling. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- GHOST (Menezes and Kyrillidis, Rice; arXiv 2602.11408, Feb 2026): post-hoc structured pruning of Mamba-2's state dimension N. It scores states by controllability energy × observability energy, an approximation of balanced truncation, using forward passes only and 128 WikiText-2 calibration samples, with no fine-tuning. Results: Mamba2-1.3B at 50% state reduction goes from 13.17 to 14.23 WikiText-2 perplexity (16.16 at 70%, 25.07 at 90%). The 130M model goes from 25.85 to 29.06. LAMBADA drops sharply (65.55 → 51.76). Hybrid Zamba2-1.2B: 11.15 → 11.80. Calibration takes about 15 GB VRAM and about 8 minutes for 1.3B. — [GHOST arXiv](https://arxiv.org/html/2602.11408)
- GHOST measures no real speedup or memory saving. It uses masking ("soft pruning"), and physical removal or sparse kernels are left to future work. — [GHOST arXiv](https://arxiv.org/html/2602.11408)
- The authors' blog says GHOST was accepted to ICML 2026 (author claim, not independently verified). — [Kyrillidis blog](https://akyrillidis.github.io/2026/05/08/ghost-mamba2-blog.html)
- "Balanced State Subspaces for Mamba-2 State Compression" (Shang Tingyu, Cao Wenfei; arXiv 2610.36617, submitted 27 Oct 2026, a 2-day-old preprint, not peer-reviewed): a post-training balanced truncation that uses the full Gramians. The authors say the rotation is exact because Mamba-2 has a scalar-identity transition. HARP allocates rank per layer from the Hankel spectrum and protects retrieval-sensitive layers. Calibration is a single forward pass. At half the state dimension, perplexity rises by 0.3 on average (1.8%), against 1.6 (9.3%) for GHOST. Tested on Mamba-2 130M–2.7B. The abstract does not say whether hybrids were tested. No code link was found. — [arXiv 2610.36617 abstract](https://arxiv.org/abs/2610.36617)
- "On Pruning State-Space LLMs" (EMNLP 2025): Mamba-2-2.7B is the most sensitive of the models tested to state pruning, degrading severely even at moderate ratios. Other SSM and hybrid models hold up better at 50%. — [arXiv 2502.18886](https://arxiv.org/pdf/2502.18886); [ACL Anthology](https://aclanthology.org/2025.emnlp-main.950.pdf)
- Mamba-Shedder: Mamba-2 tolerates removal of whole SSM modules fairly well. This is a different granularity from state-dimension pruning. — [arXiv 2501.17088](https://arxiv.org/pdf/2501.17088)
- Unstructured weight pruning of Mamba (EMNLP 2025) keeps quality better than structured pruning, but it does not shrink the recurrent state. — [arXiv 2505.08299](https://arxiv.org/pdf/2505.08299)

### Inferences
- Realistic expectation for post-hoc state truncation on a roughly 3B hybrid: about 50% state reduction for a small perplexity cost (GHOST: +1 point; HARP claim: +0.3). Retrieval-style tasks degrade first (LAMBADA). Code completion leans on exact recall of identifiers, so it is probably on the sensitive side. This is not verified.
- No one has published results on Granite 4.0-H, Falcon-H1, or Nemotron-H. Applying these methods there would be original, unvalidated work.

### Gaps
- No code-benchmark results (HumanEval or similar) exist for any SSM state-compressed model.
- HARP's hybrid results and code release are unknown (I read the abstract only).

## 3. Open-source code? Does it work with Mamba-2 layers in Hugging Face models?

### Takeaway
CompreSSM code is public, but it is JAX, MIT-licensed and LRU-only. The README says the Mamba experiments live in a separate "CompreSSMamba" repo, which I could not locate. Nothing targets Mamba-2 or Hugging Face transformers models. GHOST's code is on an anonymous mirror and targets pure Mamba-2. HARP has no code link.

### Cited Findings
- github.com/camail-official/compressm: official ICLR 2026 code, JAX ≥0.4, MIT license, LRU experiments, about 34 stars and 40 commits. The README points to a separate CompreSSMamba repo for Mamba. No Hugging Face model integration or Mamba-2 support is mentioned. — [GitHub repo](https://github.com/camail-official/compressm)
- A search for a "CompreSSMamba" repo found nothing. The author's project page exists. — [Makram Chahine project page](https://makramchahine.github.io/projects/compressm.html)
- GHOST code: anonymous.4open.science/r/mamba2_ghost-7BCB/. — [GHOST arXiv](https://arxiv.org/html/2602.11408)
- The arXiv page for the HARP / balanced-subspace paper lists no code. — [arXiv 2610.36617](https://arxiv.org/abs/2610.36617)

### Inferences
- Using any of this on Granite 4.0-H in Hugging Face would mean porting it yourself: hooking GraniteMoeHybrid's Mamba-2 mixer, collecting B/C/x statistics, rotating and truncating the B/C slices of in_proj and the conv1d channels, then rewriting `mamba_d_state` in the config. That is feasible on a T4 for a 3B model in bf16/fp16 calibration (GHOST needed about 15 GB for 1.3B, so 3B may be tight). It is a research engineering project, not a library call.
- Mamba-2 has a scalar-times-identity A per head. That makes a uniform rotate-and-truncate of the state basis exact in structure (the HARP authors' claim). The result is a standard Mamba-2 layer with a smaller d_state, which keeps the model loadable by HF and llama.cpp if every layer uses the same rank. HARP's per-layer ranks would break the uniform `ssm.state_size` assumption (my inference about llama.cpp's GGUF hyperparameter, not verified in code).

### Gaps
- I did not verify whether llama.cpp accepts per-layer d_state, or whether a non-standard d_state (for example 64) hits a kernel assumption in ssm_scan.

## 4. Does reducing SSM state dimension cut CPU inference cost in llama.cpp for hybrids? How much of a hybrid's compute and memory is in the SSM?

### Takeaway
For Granite 4.0-H Micro, the d_state-dependent weights are well under 1% of parameters, so halving d_state barely shrinks the GGUF file. The recurrent state is a fixed roughly 75 MB per sequence (fp32), and the scan adds about 1–2% of per-token FLOPs. On a bandwidth-bound laptop CPU, reading and writing that state could be a few percent to about 8% of decode memory traffic. Halving d_state might therefore give low-single-digit decode speedups and save about 38 MB of RAM. MLP and projection matmuls dominate. File size and speed come almost entirely from quantization, so state compression is a marginal lever. (These are my own calculations from the config; I found no benchmark.)

### Cited Findings
- Granite 4.0-H Micro config: hidden 2048; 40 layers (36 Mamba-2, 4 attention at layers 6/16/26/36); mamba_d_state 128; mamba_n_heads 64; mamba_d_head 64; expand 2; n_groups 1; intermediate (MLP) 8192; 32 attention heads with 8 KV heads; vocab 100352. — [HF config.json](https://huggingface.co/ibm-granite/granite-4.0-h-micro/raw/main/config.json)
- llama.cpp parallelized the Mamba-2 SSM_SCAN kernel over d_state on Metal (GPU only): about 3× faster prefill and about 15% faster decode on a Granite 4 preview. This shows the scan was a real bottleneck before that optimization. — [llama.cpp commit via semanticdiff](https://app.semanticdiff.com/gh/ggerganov/llama.cpp/commit/793c0d7f46384001738c337d7afa46b45ae32745)
- A llama.cpp PR (#16982) on Granite 4.0-H covers the SSD formulation and sub-ubatch batching changes for prefill performance (GPU-offload benchmarks). — [PR #16982 mirror](https://mygithub.libinneed.workers.dev/ggml-org/llama.cpp/pull/16982)
- IBM notes that users may need non-hybrid variants where Mamba-2 support is not yet optimized (llama.cpp was cited). — [IBM Granite docs](https://www.ibm.com/granite/docs/models/granite)

### Inferences (my arithmetic from the config above; not measured)
- Parameters per Mamba layer: in_proj 2048×(2·4096 + 2·128 + 64) ≈ 17.4M, of which the B/C part tied to d_state is 2048×256 ≈ 0.52M. out_proj ≈ 8.4M. Gated MLP ≈ 3·2048·8192 ≈ 50M. So d_state-tied weights are about 0.7% of a layer. Halving d_state removes about 9M of about 3B parameters (about 0.3%).
- Recurrent state per Mamba layer: 64·64·128 = 524,288 values, which is 2 MB in fp32. Across 36 layers that is about 75 MB per sequence, constant in context length. The KV cache for the 4 attention layers is 4·8·64·2 values per token, about 8 KB/token in f16, so about 256 MB at 32K context. The KV cache, not the SSM state, dominates memory at long context.
- Decode FLOPs: the state update and readout are about 4 ops per state element, so about 2M per layer, against about 150M of matmul FLOPs per layer. That is about 1–2%.
- Decode bandwidth: Q4 weights are about 1.7–1.9 GB read per token, against about 150 MB of fp32 state read and written. That puts the state at up to about 8% of traffic, less if it stays in L2/L3 cache. Halving d_state gives at most a few percent faster decode.
- Prefill on CPU: the scan runs sequentially through the tokens, so a smaller d_state helps prefill roughly in proportion to the scan's share. That share is unknown for CPU and could be larger than the FLOP share if the CPU scan is poorly vectorized.
- None of this applies to dense Granite 4.1 3B or to LFM2, which have no SSM state.

### Gaps
- I found no CPU-specific llama.cpp profile that shows the ssm_scan share of time for Granite 4.0-H. Measure it with `llama-bench` plus perf, or with GGML op timing, before investing.
- I did not confirm which version of the ggml CPU ssm_scan kernel is current.

## 5. Candidate base models: architectures, sizes, code benchmarks, licenses, llama.cpp support

### Takeaway
Granite 4.1 3B is a pure dense transformer with no SSM layers, so CompreSSM-style methods do not apply to it. The relevant SSM hybrids are Granite 4.0-H Micro (3B dense hybrid) and H-Tiny (7B MoE, 1B active). Both are Apache 2.0, both are supported in llama.cpp, and their code scores roughly match the dense Granite 4.0 Micro. LFM2 is a convolution/attention hybrid, not an SSM. Falcon-H1 and Nemotron-H are Mamba-2 hybrids, but they come with license caveats or support caveats.

### Cited Findings
- Granite 4.1 3B: dense decoder-only transformer with GQA, RoPE, SwiGLU, RMSNorm and tied embeddings. Embedding size 2560, 40 layers, 40 heads (head size 64), 8 KV heads, MLP 8192. The family is 3B/8B/30B, all dense. — [HF blog: Granite 4.1 LLMs, How They're Built](https://huggingface.co/blog/ibm-granite/granite-4-1)
- Some third-party pages list the 4.1 3B at about 3.4B parameters and about 131K context. The family-wide 512K claim is a separate statement. — [llmrun.dev](https://llmrun.dev/model/ibm-granite-granite-4-1-3b)
- Granite 4.0 code scores (pass@1, IBM model card):

  | Model | HumanEval | HE+ | MBPP | MBPP+ | CRUXEval-O | BigCodeBench |
  |---|---|---|---|---|---|---|
  | 4.0-H Micro (3B dense hybrid) | 81 | 75 | 73 | 64 | 41.25 | 37.9 |
  | 4.0-H Tiny (7B/1B active MoE hybrid) | 83 | 76 | 80 | 69 | 39.63 | 41.06 |
  | 4.0 Micro (3B dense transformer) | 80 | 72 | 72 | 64 | 41.5 | 39.21 |
  | 4.0-H Small (32B/9B MoE hybrid) | 88 | 83 | 84 | 71 | 50.25 | 46.23 |

  H-Micro and H-Tiny use 4 attention layers + 36 Mamba-2 layers with NoPE. H-Micro is Apache 2.0. — [HF model card granite-4.0-h-micro](https://huggingface.co/ibm-granite/granite-4.0-h-micro)
- Granite 4.0-H Small uses about a 9:1 Mamba-2:attention mix. — [It's FOSS](https://itsfoss.com/news/ibm-unveils-granite-4/)
- LFM2 (Liquid AI): gated short convolutions plus a few GQA blocks. The 1.2B model has 10 convolution and 6 GQA layers. Liquid's architecture search found that short convolutions match or beat SSMs and linear attention when a few GQA blocks are present. llama.cpp support has existed since July 2025. — [LFM2 tech report arXiv 2511.23404](https://arxiv.org/abs/2511.23404); [InsiderLLM guide](https://insiderllm.com/guides/liquidai-lfm2-local-setup-guide/)
- LFM2 license: commercial terms vary by company size (LFM Open License); exact terms not verified. — [bestllmfor.com guide](https://bestllmfor.com/guide/lfm2-liquid-ai-edge/)
- Falcon-H1 is a parallel hybrid: attention and Mamba-2 heads run side by side in each mixer block. Its license is described inconsistently ("based on Apache 2.0" versus "Falcon LLM license"). The blog gives no code scores and no d_state. — [Falcon-H1 blog](https://falcon-lm.github.io/blog/falcon-h1/)
- Falcon-H1-Tiny model cards state that llama.cpp and ollama work under the Falcon-LLM License. — [HF Falcon-H1-Tiny-R-0.6B](https://huggingface.co/tiiuae/Falcon-H1-Tiny-R-0.6B-pre-GRPO)
- Nemotron-H / Nemotron-Nano-9B-v2: NemotronH architecture support was added to llama.cpp in PR #15507 (the first version was reported to give invalid output). A later PR #21664 fixed config parsing. — [commit e8d99dd0](https://app.semanticdiff.com/gh/ggml-org/llama.cpp/commit/e8d99dd0b67f2ecc1e45fca8074a3a18c3e036d2); [PR #21664](https://app.semanticdiff.com/gh/ggml-org/llama.cpp/pull/21664/overview)
- A user in a llama.cpp issue called Nemotron-Nano-9B-v2 a very good small coding model (anecdotal, not a benchmark). — [llama.cpp issue #16450](https://github.com/ggml-org/llama.cpp/issues/16450)

### Inferences
- Of the hybrid options, Granite 4.0-H Micro/Tiny is the cleanest fit: Apache 2.0, good small-model code scores, and native llama.cpp support. H-Tiny (1B active) is likely faster on CPU than any 3B dense model at better MBPP, but its full 7B of weights must sit in RAM (about 4–4.5 GB at Q4).
- Nemotron-Nano-9B-v2 is too large for a comfortable laptop-CPU and T4-finetune budget. Zamba2 (1.2B/2.7B) is the only hybrid with published state-pruning results (GHOST), but it is not known for code.

### Gaps
- I did not retrieve code benchmarks for Falcon-H1 (0.5B–7B), LFM2 or Zamba2, or confirm their licenses from the model cards.
- I did not verify whether Granite 4.1 has an "-H" hybrid variant. Every source found describes 4.1 as dense only.

## 6. Verdict: use, approximate, or skip? Should it change the base model?

### Takeaway
Skip CompreSSM itself. It trains from scratch, ships JAX LRU code, has no Mamba-2 support and no LLM results, and the user's base model, Granite 4.1 3B, has no SSM layers. If the user moves to Granite 4.0-H, the post-hoc follow-ups (GHOST, HARP) could in principle halve d_state at a small perplexity cost. Even so, the expected CPU gain is a few percent of decode speed and about 38 MB of RAM, far less than quantization or picking an MoE such as H-Tiny gives. Do not switch base models because of CompreSSM.

### Cited Findings
- CompreSSM targets training from scratch and has been validated only on LRU on LRA-style tasks. Mamba-2 is future work. — [arXiv HTML v3](https://arxiv.org/html/2510.02823v3)
- Granite 4.1 3B is dense with no SSM. — [HF blog Granite 4.1](https://huggingface.co/blog/ibm-granite/granite-4-1)
- The best post-hoc Mamba-2 state compression gives about 50% state reduction for +0.3 to +1.0 perplexity on pure Mamba-2. GHOST reported no wall-clock speedups. — [GHOST](https://arxiv.org/html/2602.11408); [arXiv 2610.36617](https://arxiv.org/abs/2610.36617)
- Granite 4.0-H Micro and 4.0 Micro (dense) have near-identical code scores. — [HF model card](https://huggingface.co/ibm-granite/granite-4.0-h-micro)

### Inferences
- Recommendation by base model:
  - Granite 4.1 3B (dense): not applicable. Use quantization (Q4_K_M/Q5_K_M), distillation or LoRA instead.
  - Granite 4.0-H Micro/Tiny: optional, low-priority experiment. GHOST-style output-aware truncation to d_state 64 uniformly, then convert to GGUF, then measure with llama-bench on the laptop. Expect a small gain and some retrieval or code-recall risk. Re-check HumanEval/MBPP afterwards. Calibration fits roughly on a T4 for 3B. H-Tiny's MoE in HF may be awkward to calibrate.
  - Choosing a hybrid base for CPU reasons is a valid argument on its own terms (constant-size state, small KV cache from only 4 attention layers, H-Tiny's 1B active parameters). It is not a reason to adopt CompreSSM.
- "Approximate" option for the from-scratch case: if the user ever trains a tiny SSM from scratch, CompreSSM's recipe (start wide, truncate by HSVs during warm-up) is cheap to adopt. That is out of scope for fine-tuning a pretrained 3B coding model.

### Gaps
- No empirical CPU measurement exists for a d_state-truncated Granite 4.0-H in llama.cpp. The cost-benefit above rests on arithmetic, not benchmarks.
