# Small local models and CPU inference options for Jazari (as of 2026-11-02)

Scope note: the earlier reports already cover Granite 4.1 3B/8B, Qwen3-4B-Instruct-2507, Qwen3.5-4B/9B, Gemma 4 E2B/E4B, Mellum2.1, Phi-4-mini, Qwen2.5-Coder, GSQ-RCO small Qwen GGUFs, AVX2 repacking, speculative decoding on hybrids, KV quantization and the iGPU. These notes add only what is new or changed. **Research limits:** huggingface.co, liquid.ai and gitee.com did not resolve from this session (outside the allowed network domains), so no model card was read directly. Every vendor number below comes through a search snippet, a mirror or an aggregator and is marked that way. The session ran about 20 search and fetch calls.

Hardware reminder for the writer: every throughput figure below comes from **different hardware** unless it says "T480". The T480 has single-channel RAM (about 13 GB/s effective was measured), so desktop and Apple figures overstate what it will reach.

## Q1. Small open models (≤4B active), mid-2025 to Nov 2026, that could beat Granite 4.0-H Tiny on quality per second

### Takeaway
No release found up to November 2026 clearly beats H-Tiny on quality per second on a single-channel 4-core CPU. IBM's own 2026 line (Granite 4.1) went dense, so no "H-Tiny successor" exists. The only direct competitor in H-Tiny's class (about 8B total, about 1–1.5B active, hybrid, llama.cpp-native) is **Liquid AI's LFM2-8B-A1B (Oct 2025) and LFM2.5-8B-A1B (May 2026)**. Its published coding scores are not higher than H-Tiny's, and its licence is revenue-capped. Every 2026 model with markedly stronger coding scores (Qwen3.6-35B-A3B, Mellum2.1 12B-A2.5B, LFM2.5-24B-A2B, Qwen3.5-4B thinking) costs 2–3x more bytes per token or tokens per answer, or needs more than 8 GB of RAM.

### Cited Findings
**IBM Granite**
- Granite 4.1 (released 2026-04-29) has 3B, 8B and 30B **dense** decoder-only models. IBM moved away from the 4.0 hybrid Mamba/Transformer MoE design, citing predictable token latency and stable costs. Apache 2.0. — [NYU RITS](https://rits.shanghai.nyu.edu/ai/ibm-releases-granite-4-1-dense-8b-matches-prior-32b-moe-flagship/); [ai-beat](https://ai-beat.github.io/news/2026/05/granite-41-small-dense/); [dsebastien.net](https://www.dsebastien.net/tag/news/page/8/)
- No "Granite 4.1 Tiny" or other hybrid MoE successor to H-Tiny turned up in search, so H-Tiny (2025) is still IBM's newest small hybrid MoE. — same sources (absence of evidence, secondary blogs)
- Granite 4.0 Nano 1B: an aggregator lists LiveCodeBench at about 4.7%, with an unclear table layout, so it is unconfirmed and far below what Jazari needs. — [llmbase.ai](https://llmbase.ai/compare/granite-4-0-nano-1b,minicpm-v4-6-1-3b/)
- Granite 4.0 Micro (3B) scored 65.85% pass@1 (JS) and 68.90% (Java) on HumanEvalX in a third-party IBM-community evaluation. No C# was tested. — [llm-pt-ibm blog](https://llm-pt-ibm.github.io/en/posts/post_humanevalx)

**Liquid AI LFM2 / LFM2.5 (hybrid conv+attention), the closest architectural peer to H-Tiny**
- LFM2-8B-A1B (Oct 2025): about 8.3B total, about 1.5B active, 32K context. Model card (via search snippet): **HumanEval+ 69.51%, LiveCodeBench v6 21.04%**. Licence: **LFM Open License v1.0**. It works with llama.cpp. Liquid's throughput chart uses llama.cpp Q4_0 on a Galaxy S24 Ultra and an AMD Ryzen HX370, values not retrieved. — [Liquid AI blog (JP mirror)](https://www.liquid.ai/ja/blog/lfm2-8b-a1b-an-efficient-on-device-mixture-of-experts); [ONNX community card](https://huggingface.co/onnx-community/LFM2-8B-A1B-ONNX); [OpenRouter](https://openrouter.ai/models/liquid/lfm2-8b-a1b)
- The about 12–13 tok/s laptop figures for LFM2-8B-A1B (Ryzen AI 9 HX370, M3 Air) are **formula estimates, ±20%**, not measurements. — [llmrun.dev](https://llmrun.dev/model/liquidai-lfm2-8b-a1b)
- LFM2.5-8B-A1B released **2026-05-28** (one source gives a different day): 8.3B total / about 1.5B active, context raised 32K → 128K, reportedly trained on 38T tokens. Aggregator scores: BFCL v4 about 49.7, IFBench about 55.6–56.5, GPQA about 51.3, AIME25 about 42.5. **No HumanEval/LCB figure found.** — [lavx.hu](https://news.lavx.hu/article/lfm2-5-8b-a1b-a-more-capable-on-device-mixture-of-experts-model); [thedailypac](https://thedailypac.blogspot.com/2026/05/liquid-ai-releases-lfm25-8b-a1b-on.html); [ai-watch](https://ai-watch.pages.dev/en/post/liquid-ai-lfm2-5-8b-a1b-38t-tokens-non-transformer-2026-en); [BenchLM](https://www.benchlm.ai/models/lfm2-5-8b-a1b) (BenchLM mislabels some benchmarks)
- LFM2.5-1.2B Instruct: under 1 GB of RAM, **239 tok/s decode on "AMD CPU"** (vendor, hardware unspecified). Recommended sampling: temp 0.1, top_k 50, top_p 0.1, rep. penalty 1.05. Unsloth gives a llama.cpp run guide and fine-tune notebook. — [Unsloth LFM2.5 guide](https://www.unsloth.ai/docs/models/tutorials/lfm2.5)
- LFM2.5-2.6B: 220 tok/s on an M5 Max and 113 tok/s on a Ryzen AI Max+ 395 (CPU), vendor claims via a third-party article. Day-one llama.cpp/ONNX support. — [ecosistemastartup](https://ecosistemastartup.com/?p=94894)
- LFM2.5-24B-A2B (2026-10-13): 24B total / about 2–2.45B active, about 13 GB at 4-bit, revenue-capped licence. Reviewed as a "fast local agent" model, *not* a strong coder next to Qwen3.6-35B-A3B. — [Developer's Digest](https://www.developersdigest.tech/blog/lfm2-5-24b-a2b-vs-qwen3-6-35b-a3b-local-agents-2026); [morphllm list](https://www.morphllm.com/best-ollama-models)
- An earlier Jazari report already noted that LFM2's licence terms vary by company size. — `reports/Advanced training methods feasibility.md` (repo)

**Qwen 3.5 / 3.6**
- Qwen3.6-35B-A3B was released 2026-04-16 and Qwen3.6-27B on 2026-04-23. The 35B-A3B reports LiveCodeBench 80.4 (vendor). — [CSDN ranking, May 2026](https://gitcode.csdn.net/69ff36a90a2f6a37c5a8c614.html); [DEV Community](https://dev.to/techlatestnet/qwen36-35b-a3b-the-most-practical-open-source-ai-model-yet-16hf)
- A small Qwen3.6 "4B" is mentioned only anecdotally ("not good for my needs") on Hacker News. No model card or LCB numbers were found, and no Qwen3.6 2B was found. — [HN via brianlovin](https://brianlovin.com/hn/47960507)
- CPU reality for 3B-active MoEs (different hardware): on a Haswell i7 with 32 GB, Qwen3.5-35B-A3B Q4_K_M ran at about 4 tok/s and a 28B-A3B REAP variant at 7.5 tok/s. On an i9-10900 with 64 GB (llama.cpp b8149), Qwen3-30B-A3B-2507 ran at 12–13 tok/s against **5.6 tok/s for Qwen3.5-35B-A3B** at the same quant. Commenters blamed unoptimized Gated DeltaNet in llama.cpp (Feb 2026). — [r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1rei65v/snapshots/2026-02-25T19%3A22%3A50.330313Z)
- Any 30B-A3B needs about 17–20 GB at Q4, which does not fit 16 GB next to an IDE without disk offload (see OpenVINO under Q2). — inferred from file sizes in [Developer's Digest](https://www.developersdigest.tech/blog/lfm2-5-24b-a2b-vs-qwen3-6-35b-a3b-local-agents-2026) (13 GB for 24B at 4-bit)

**Other 2026 MoEs (all too big for the T480)**
- Mellum2.1 Thinking (JetBrains, 2026-10-08): 12B / 2.5B active, Apache 2.0, Q4_K_M GGUF 8.1 GB. Mellum 2 base: 64 experts, 8 active. — [morphllm](https://www.morphllm.com/best-ollama-models); [Mellum2 tech report arXiv 2605.31268](https://arxiv.org/pdf/2605.31268) (already on the earlier watch list)
- Kolibri 1 (Aleph Alpha, Oct 2026): 78B / 3.5B active, Apache 2.0, about 43 GB at 4-bit. — [morphllm](https://www.morphllm.com/best-ollama-models)

**Gemma 4 small**
- No CPU-only x86 tok/s figure for Gemma 4 E2B/E4B was found. On a Raspberry Pi 5, E2B ran at 8–12 tok/s and E4B dropped to 3–5 tok/s from swapping. One guide says E2B needs about 9 GB of system RAM on CPU. In one informal test, E2B failed complex code while E4B produced working React apps. — [dev.to Pi test](https://dev.to/alanwest/gemma-4-runs-on-a-raspberry-pi-i-tested-it-56c5); [kartit.net MacBook test](https://www.kartit.net/blog/gemma4-local-benchmark)

**SmolLM3 / Ministral 3 3B**
- No sourced HumanEval/LCB numbers were retrieved. BenchLM shows "coming soon" for Ministral 3 3B. SmolLM3-3B is now an officially enabled OpenVINO model (see Q2). — [BenchLM](https://benchlm.ai/compare/granite-4-0-1b-vs-ministral-3-3b); [OpenVINO notes](https://docs.openvino.ai/nightly/about-openvino/release-notes-openvino.html)

### Inferences
- **LFM2.5-8B-A1B is the only like-for-like test candidate.** It is a hybrid MoE with a similar total size (8.3B vs 7B, so a Q4 file of about 5 GB, similar RAM), about 1.5x H-Tiny's active parameters (so likely about 7–9 tok/s on the T480 against H-Tiny's 10, an estimate from bytes per token), and 128K context. It has llama.cpp and Unsloth support. Its predecessor's coding scores (HumanEval+ 69.5, LCB v6 21.0) do not clearly beat H-Tiny's vendor HumanEval 83 (different metric, so only a rough comparison). It is worth one run of the 45-question set only if the licence is acceptable. The LFM Open License is revenue-capped, which matters if Jazari is ever shipped commercially. Its hybrid cache in llama.cpp has its own bug history (#16491 in earlier notes).
- No small model released through November 2026 publishes MultiPL-E **C#** numbers in the sources found. Jazari's 45-question set remains the only C#-specific discriminator.
- On this bandwidth-bound laptop, "quality per second" favours ~1B-active MoEs. Every model with clearly better coding scores is a 2.5–3.5B-active MoE (2.5–3.5x bytes per token, so about 3–4 tok/s here, estimated) or needs 8–43 GB. The structural winner remains the H-Tiny class.
- Granite 4.1's switch to dense weakens a "stay on IBM hybrids" roadmap. Future IBM improvements will come at 3B-dense speed (6.2 tok/s measured on the T480 for 4.1 3B).

### Gaps
- Model cards (Hugging Face, Liquid AI) could not be opened, so LFM2.5-8B-A1B's coding scores, exact licence threshold and GGUF quant list are unverified.
- No CPU tok/s measured on any 4-core 8th-gen Intel laptop for LFM2/LFM2.5-8B-A1B, Gemma 4 E2B or SmolLM3.
- Whether a Qwen3.6 small dense model (e.g. 4B) officially exists, and its scores, are unconfirmed.
- No source found for MultiPL-E C# on any 2025–2026 small model.
- DeepSeek or other small dedicated coders (≤4B active) from 2025–2026: none surfaced in search.

## Q2. CPU runtime features since mid-2025 that matter for a ~1B-active MoE on AVX2

### Takeaway
The headline llama.cpp feature, native MTP speculative decoding (merged 2026-05-16), needs models trained with MTP heads (Qwen3.6, some Gemma 4). Granite 4.0-H has none, and all published gains are on GPUs. ik_llama.cpp's CPU prompt-processing gains are real but model-dependent (+35–40% on Qwen3-Coder, −20–30% on Qwen3.5). OpenVINO 2026.3 added native MoE kernels and MoE disk offload, but no Granite/Mamba support was found. bitnet.cpp got a 1.15–2.1x kernel speed-up in Jan 2026. None of these is a free win for H-Tiny. The one worth a cheap test is ik_llama.cpp for prompt speed, if it supports the GraniteMoeHybrid architecture.

### Cited Findings
- **llama.cpp MTP:** PR #22673 "llama + spec: MTP Support" merged **2026-05-16**, used via `--spec-type draft-mtp`. It requires a model with built-in MTP heads (from pre-training or fine-tuning), so ordinary GGUFs gain nothing. Reported speed-ups are GPU-only: 1.71x for Qwen3.6-27B on an RTX 3090, 1.5–1.8x generally, about 1.4x on Gemma 4 26B. Mixed effects on prompt-processing latency were reported. The PR TODO lists "Improve ngram compatibility with mtp", and the author mentions experimental MTP+ngram for non-CUDA systems. — [PR #22673](https://github.com/ggml-org/llama.cpp/pull/22673); [cloudmagazin](https://www.cloudmagazin.com/en/2026/05/27/llama-cpp-mtp-support-27b-modelle-1-7x-schneller-consumer/); [mer.vin guide](https://mer.vin/2026/05/run-qwen-3-6-mtp-in-llama-cpp-faster-local-inference-with-built-in-speculative-decoding/?amp=1); [aiweekly](https://aiweekly.co/alerts/llamacpp-adds-multi-token-prediction-for-faster-local-ai)
- Earlier repo notes already established that Granite has no MTP heads and that speculation on Granite's hybrid runs in slow full-checkpoint mode. — `reports/Local inference sources for Jazari.md`, `reports/Jazari CLI and model enhancements.md` (repo)
- **ik_llama.cpp:** the README claims "much faster CPU prompt processing for all non-interleaved quants" and faster CPU PP for trellis quants and MoE models (maintainer claim). A community comparison (Mar 2026, single user) found ik_llama **about 35–40% faster PP on Qwen3-Coder**, but **llama.cpp about 20–30% faster on Qwen3.5-35B**, with Qwen3.5-9B close. ik_llama ships `llama-sweep-bench`. — [ik_llama.cpp fork README](https://github.com/fenril22/ik_llama.cpp); [r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1ruew2g/snapshots/2026-03-16T20%3A46%3A16.316967Z); [ik_llama discussion #1291](https://github.com/ikawrakow/ik_llama.cpp/discussions/1291)
- For GPU+CPU hybrid setups, PP time is dominated by copying MoE tensors to the GPU, so ik_llama's PP advantage is small there. This does not apply to pure CPU. — [discussion via search](https://github.com/ikawrakow/ik_llama.cpp/discussions/1291); [Doctor-Shotgun MoE offload guide](https://huggingface.co/blog/Doctor-Shotgun/llamacpp-moe-offload-guide)
- **OpenVINO 2026.3 (2026-08-04):** added GroupedMatMul for native MoE execution, **MoE weight offloading to disk** (lets Qwen3-30B-A3B run on 16 GB devices), CPU BF16 precision optimizations for INT4/INT8 weight-compressed LLMs, and Adaptive R-KV. Newly enabled models include SmolLM3-3B, LFM2-1.2B, LFM2.5-1.2B, MiniCPM5-1B and Qwen3-8B with EAGLE-3. **No Granite or Mamba entries were found.** OpenVINO 2025.4 had first MoE optimizations (XMX GPUs) and Qwen3-30B-A3B. — [OpenVINO release notes (nightly)](https://docs.openvino.ai/nightly/about-openvino/release-notes-openvino.html); [GitHub 2026.3.0](https://github.com/openvinotoolkit/openvino/releases/tag/2026.3.0); [edge-ai-vision](https://www.edge-ai-vision.com/?p=59176); [Phoronix 2026.0](https://phoronix.com/news/intel-openvino-2026.0-released)
- Hybrid architectures' CPU kernels lag: Qwen3.5's Gated DeltaNet ran at less than half Qwen3-30B-A3B's speed on the same CPU in Feb 2026. — [r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1rei65v/snapshots/2026-02-25T19%3A22%3A50.330313Z)

### Inferences
- For H-Tiny specifically, the realistic runtime levers are still the earlier ones: prompt-cache hit rate, `-tb`, an all-Q4 file for AVX2 repacking, and a newer llama.cpp build (re-run the eval). MTP becomes relevant only if Jazari switches to an MTP-trained model, and then only if CPU gains exist. No CPU evidence has been published, and earlier MoE analysis says verification multiplies expert reads.
- ik_llama.cpp is the one runtime with a plausible prompt-speed gain on CPU, but it must first be confirmed to load GraniteMoeHybrid (Mamba-2) GGUFs. It is a source build on Windows (VS Build Tools), consistent with the earlier "optional evening experiment" verdict.
- OpenVINO's MoE disk offload is the first documented way to run a 30B-A3B coder within 16 GB, but disk-backed experts would make prompt reading far slower than the ≤30 s median allows. Not viable on the T480, by estimate.

### Gaps
- Not verified: whether ik_llama.cpp supports Granite 4.0-H (Mamba-2 hybrid MoE), and its PP speed on AVX2 for that architecture.
- No CPU-only MTP benchmark exists in any source found.
- No post-mid-2025 llama.cpp change specifically for Mamba-2 CPU scan speed was found in this pass. Earlier notes found only GPU kernels.
- New quant types (trellis/IQ_KT in ik_llama.cpp) have no quality-per-second measurement on a ~1B-active MoE on AVX2 in the sources found.
- ONNX Runtime GenAI and MLC: no 2026 CPU-relevant changes surfaced in this pass. Not searched in depth.

## Q3. Ternary / 1.58-bit and other very-low-bit options in 2026

### Takeaway
No new official BitNet model appeared in 2025–2026 beyond BitNet b1.58 2B4T (April 2025, MIT). The 2026 change was kernels only. At about 15–20 tok/s on x86 (community, thin methodology), a 2B ternary model would be faster than H-Tiny, but no source shows it is competitive at code, and it has no C# evidence. Not a candidate. The practical "very-low-bit" path remains GSQ-refined Q2/Q3 GGUFs of Qwen (already in the earlier report) or IBM's own Q3 H-Tiny files.

### Cited Findings
- BitNet b1.58 2B4T: 2B parameters, 4T training tokens, MIT licence, released April 2025 (pre-2025-H2). No new Microsoft BitNet model was found in 2026. — [wavect.io analysis](https://wavect.io/de/blog/bitnet-cpu-llm-ram-requirements/); [emelia.io](https://emelia.io/de/hub/bitnet-1bit-llm-cpu-inference)
- January 2026 bitnet.cpp CPU update: parallel kernels with configurable tiling and embedding quantization, giving an extra **1.15–2.1x** on top of earlier gains. The x86 TL2 kernel uses AVX2 intrinsics. The official x86 speed-up of 2.37–6.17x is **against llama.cpp FP16**, not against Q4 GGUF. — [repo-explainer](https://repo-explainer.pages.dev/bitnet); [wavect.io](https://wavect.io/de/blog/bitnet-cpu-llm-ram-requirements/) (the Oct 2026 analysis also notes the 82.2% energy figure comes from a 7B comparison, not 2B4T)
- Community: 14.8 tok/s for BitNet 2B on bitnet.cpp on a 16-thread x86 PC, and 19.3 tok/s with the poster's own AVX2 kernels (self-reported, Mar 2026). A fork README states 15–25 tok/s "varies by CPU". — [r/LocalLLaMA snapshot](https://reddit.sentinel-team.org/posts/1s34zsg/snapshots/2026-03-27T22%3A19%3A49.793866Z); [Code4me2/BitNet fork](https://github.com/Code4me2/BitNet)
- Earlier Jazari notes: reame's ternary engine is ARM NEON-only, and calibrating quantization on one's own traffic tied at Q3_K_M and won only at Q2_K. — `reports/Local inference sources for Jazari.md` (repo)

### Inferences
- A 16-thread x86 desktop at about 15 tok/s suggests a 4-core T480 might get roughly 8–15 tok/s for BitNet 2B (estimate). That is not clearly faster than H-Tiny's 10 tok/s, at a much lower capability level.
- Ternary is not a 2026 path for Jazari unless a ternary model with published code scores appears.

### Gaps
- No HumanEval/LCB/C# scores for BitNet 2B4T were retrieved in this pass.
- No ternary or 2-bit MoE with code benchmarks was found.

## Q4. Cheap adaptation: LoRA/QLoRA on .NET material, serving as GGUF+LoRA, and evidence vs retrieval

### Takeaway
Fine-tuning H-Tiny itself is cheap and documented: Unsloth's free Colab notebook covers Granite-4.0, and Micro and Tiny "fit comfortably" on a 15 GB T4. The research evidence says domain tuning **complements but does not replace** retrieval for API facts. Retrieval supplies volatile API content, and tuning improves how the model uses it. So a LoRA should train "answer from the law-book passages, cite, refuse when absent" behaviour, not memorize .NET APIs.

### Cited Findings
- Unsloth offers a free Colab notebook for fine-tuning Granite-4.0 (support-agent example). It describes H-Tiny as 7B total / 1B active and says Granite-4.0 Micro and Tiny fit on a 15 GB T4. Unsloth also publishes Granite GGUFs runnable in llama.cpp. — [Unsloth Granite 4.0 guide](https://unsloth.ai/docs/models/tutorials/ibm-granite-4.0)
- An LFM2.5 run-and-fine-tune guide also exists in Unsloth's docs. — [Unsloth LFM2.5](https://www.unsloth.ai/docs/models/tutorials/lfm2.5)
- "Diagnosing Knowledge Gaps in LLM Tool Use" (June 2026, about 1.9K tasks, 4 base models): fine-tuning does **not** replace retrieval once external knowledge is removed. Retrieval supplies volatile API content, and tuning improves how it is integrated. Date and details not verified from full text. — [papers.cool 2606.03657](https://papers.cool/arxiv/2606.03657)
- ReCode (arXiv 2506.20495, 2025): SFT partly fixes outdated-API knowledge but is costly and causes forgetting under frequent updates. Rule-based RL fine-tuning let Qwen2.5-Coder-7B-Instruct beat a 32B code model on CodeUpdateArena, with less damage to general coding than SFT. — [arXiv 2506.20495v4](https://arxiv.org/pdf/2506.20495v4)
- "When LLMs Lag Behind" (arXiv 2604.09515, 2026): across 270 real API updates in 8 Python libraries, when retrieved updated specs conflict with memorized APIs it is unpredictable whether the model follows context or stale knowledge. — [arXiv 2604.09515](https://arxiv.org/html/2604.09515)
- CodeUpdateArena (arXiv 2407.06249, **2024, pre-2025**) is the base benchmark: fine-tuning on update docstrings vs retrieval. — [arXiv 2407.06249](https://arxiv.org/pdf/2407.06249)
- Practitioner claim (weak): a fine-tuned Llama 3.1 8B hit 84% on internal-doc queries against 56% for RAG with GPT-4o. Single team, no public data. — [dev.to](https://dev.to/johalputt/we-ditched-rag-20-for-fine-tuned-llama-31-50-better-accuracy-for-internal-docs-aci)
- Vendor framing: Unsloth recommends combining fine-tuning with RAG and says "RAG is always better" claims come from poor fine-tunes (commercial interest). — [Unsloth FAQ](https://unsloth.ai/docs/get-started/fine-tuning-for-beginners/faq-+-is-fine-tuning-right-for-me)
- Earlier repo note: llama.cpp has `--lora` for runtime adapters, but adapter quality on a refined/quantized base is untested. — `reports/Local inference sources for Jazari.md` (repo)

### Inferences
- The best-supported recipe for Jazari is retrieval-aware SFT, RAFT-style (train on law-book passages + question → grounded answer or refusal). Jazari's law book already provides the retrieval half. A QLoRA on H-Tiny is feasible on a free T4 per Unsloth.
- Serving: merging the LoRA into the weights and re-quantizing to Q4 (all-Q4 for repacking) is the low-risk path. A runtime `--lora` on a hybrid MoE GGUF is unverified and may disable repacked kernels or add per-token cost (unmeasured).
- RL-style fine-tuning (ReCode) beat SFT for API updates at 7B, but it needs more compute than a free T4 session reliably offers (inference, unverified).

### Gaps
- No source confirms that llama.cpp's runtime LoRA works on GraniteMoeHybrid (expert and Mamba tensors), or what it costs in speed on CPU.
- No published study measures domain LoRA vs retrieval on a ≤4B model for C#/.NET specifically.
- Unsloth's export path (merged GGUF vs adapter GGUF) for Granite-4.0-H was not confirmed from the notebook itself.

## Q5. Low-cost decoding-side quality tricks: grammar constraints, self-consistency, think modes, value vs time on CPU

### Takeaway
No 2025–2026 source found measures these on a CPU laptop. From the numbers already in hand, think modes and self-consistency are the expensive ones. At about 10 tok/s, a 1,000-token think trace adds about 100 s, which breaks the ≤30 s median. Self-consistency with k samples multiplies generation time by about k unless the prompt cache is shared. Grammar constraints stay the cheapest proven lever and are already in use for `/edit` and `/review`.

### Cited Findings
- Qwen3.5-4B/9B's high LCB v6 scores (55.8 / 65.6) come from **thinking mode, on by default**. Turning it off keeps latency acceptable at an unknown score loss. — `reports/Local coding model selection.md`, citing [Qwen3.5-9B card](https://huggingface.co/Qwen/Qwen3.5-9B) (repo, earlier research)
- LFM2.5-24B-A2B and Mellum2.1 are reasoning/"thinking" models, so their headline scores assume long traces. — [Developer's Digest](https://www.developersdigest.tech/blog/lfm2-5-24b-a2b-vs-qwen3-6-35b-a3b-local-agents-2026); [morphllm](https://www.morphllm.com/best-ollama-models)
- Liquid recommends near-greedy sampling for LFM2.5 (temp 0.1, top_p 0.1). — [Unsloth LFM2.5 guide](https://www.unsloth.ai/docs/models/tutorials/lfm2.5)
- Earlier repo notes: grammar-forced JSON is already the right base for `/edit` and `/review`. The weak spots are missing `maxLength` and the quote check, plus a reame result where a `label: value` format beat prose in accuracy (14/20 → 18/20 on a 7B-A1B MoE) and speed (40.5 s → 6.2 s). — `reports/Jazari CLI and model enhancements.md`, `reports/Local inference sources for Jazari.md` (repo)

### Inferences
- Time budget at the T480's measured 10 tok/s: the ≤30 s median allows about 300 generated tokens after prompt reading, so think mode only fits for short, capped traces (e.g. a thinking budget of 150–200 tokens). No small model in the sources documents a measured gain at such budgets.
- Cheap verification beats sampling more: a compile check, an API-name check against the law book, or a scanner pre-pass uses CPU time outside the model. The earlier reports already recommend these.
- Self-consistency could make sense only for short-answer classification (e.g. "is this finding real?" yes/no at about 5 tokens × k), where k=3–5 costs seconds, not minutes, with a shared cached prefix.

### Gaps
- No published CPU-laptop measurement of think-mode gain vs time for any ≤4B-active model in 2025–2026.
- No measurement of grammar-constrained decoding overhead in recent llama.cpp builds on CPU.
- No source on self-consistency gains for small models on code Q&A (vs math), 2025–2026.
