# Small Open-Weight Coding LLMs: Landscape and Benchmarks (as of late Oct 2026)

Scope: models of about 0.5B–9B dense, or MoE models whose 4-bit weights fit in about 10 GB, for a 16 GB RAM laptop CPU. Unless a line says otherwise, every score comes from the vendor (model card or tech report). Scores from different reports use different harnesses and LiveCodeBench (LCB) windows, so **numbers are not comparable across vendors**. LCB in particular ranges from about 18 (2024 windows, non-reasoning) to about 80 (v6, thinking mode).

## Q1. Which small open-weight models are currently strongest at code?

### Takeaway
On reported numbers, the 2026 generation leads: Qwen3.5-9B / 4B (Feb–Mar 2026, hybrid attention, thinking mode), Gemma 4 E4B, Ministral 3 8B, and IBM Granite 4.1 8B (Apr 2026, dense, FIM). JetBrains Mellum2.1 (Oct 2026, 12B-A2.5B MoE) is a new contender, but only its vendor has reported results so far. Qwen2.5-Coder-7B (2024) is still the best-documented code-specialist base. It has full FIM and repo-level tokens and remains the default for autocomplete.

### Cited Findings
**Qwen2.5-Coder (Sep 2024; sizes 0.5/1.5/3/7/14/32B)**
- 7B-Instruct is 7.61B params (6.53B non-embedding). It is dense, with RoPE, SwiGLU, RMSNorm, QKV bias, 28 layers and GQA (28 query / 4 KV heads). Context is 131,072 with YaRN (32K default in the config). Base model is Qwen2.5-Coder-7B — [HF model card](https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct)

**Qwen3 / Qwen3-2507 (2025)**
- Qwen3-4B-Instruct-2507 is 4.0B (3.6B non-embedding), 36 layers, GQA 32/8, with a native 262,144-token context. It is non-thinking only and Apache-2.0 — [HF model card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)

**Qwen3.5 Small (Feb/Mar 2026; 0.8B/2B/4B/9B, Apache-2.0)**
- Qwen3.5-9B is hybrid: 8 × (3 Gated DeltaNet linear-attention layers + 1 Gated Attention layer), with a vision encoder. Context is 262,144 native and up to about 1.01M with YaRN. Thinking is on by default and can be turned off with `enable_thinking=False`. Base model is Qwen3.5-9B-Base. The card does not say whether the 9B is dense or MoE (FFN blocks only are listed). The Hub metadata says 10B params — [HF model card](https://huggingface.co/Qwen/Qwen3.5-9B)
- The family has four sizes, 0.8B / 2B / 4B / 9B, launched around 2 Mar 2026 — [Gigazine](https://gigazine.net/gsc_news/en/20260303-qwen-3-5-small)

**Google Gemma 4 (2026; Apache 2.0)**
- E2B is 2.3B effective (5.1B with embeddings) with 128K context. E4B is 4.5B effective (8B with embeddings) with 128K context. Both take text, image and audio and use Per-Layer Embeddings. The 12B Unified model is dense, 11.95B, 256K context. The 26B-A4B is MoE: 25.2B total, 3.8B active, 128 experts with 8 active plus 1 shared. The 31B is dense. Attention is hybrid sliding-window plus global. The card says pre-trained and instruction-tuned variants are released — [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4)

**IBM Granite 4.1 (29 Apr 2026; Apache 2.0)**
- The 8B is a dense decoder-only model with GQA, RoPE, SwiGLU, RMSNorm and shared embeddings. The card describes no Mamba or MoE layers. Context is 131,072. Sizes are 3B, 8B and 30B. The instruct model is SFT+RL on top of Granite-4.1-8B-Base. The Hub metadata says 9B params — [HF model card](https://huggingface.co/ibm-granite/granite-4.1-8b)

**Mistral Ministral 3 (Dec 2025; Apache 2.0)**
- The 8B is 8.4B LM + 0.4B vision encoder, with 256K context. Base, Instruct (FP8) and Reasoning checkpoints exist at 3B, 8B and 14B — [HF model card](https://huggingface.co/mistralai/Ministral-3-8B-Instruct-2512)

**ByteDance Seed-Coder-8B (Jun 2025; MIT)**
- 8B, llama-style, 32K context (Reasoning variant 64K). Base, Instruct and Reasoning variants are released — [HF model card](https://huggingface.co/ByteDance-Seed/Seed-Coder-8B-Instruct)

**Microsoft Phi-4-mini-instruct (Feb 2025; MIT)**
- 3.8B dense, GQA, about 200K vocabulary, 128K context — [HF model card](https://huggingface.co/microsoft/Phi-4-mini-instruct)

**JetBrains Mellum2.1 Thinking (8 Oct 2026; Apache 2.0)**
- 12B-total / 2.5B-active MoE. Weights are on Hugging Face (reported path `JetBrains/Mellum2.1-12B-A2.5B-Thinking`) — [NYU Shanghai RITS](https://rits.shanghai.nyu.edu/ai/jetbrains-ships-mellum2-1-a-12b-open-model-for-coding-agents/). One source says "25 billion active", which contradicts this — [KuCoin](https://www.kucoin.com/news/flash/jetbrains-releases-open-source-mellum2-1-outperforms-qwen-in-livecodebench)
- About 6.7 GB at 4-bit, with a Q4_K_M GGUF of about 8.1 GB, aimed at 16–32 GB machines (third-party roundup) — [pinggy.io](https://pinggy.io/blog/best_open_source_self_hosted_llms_for_coding/) / [vdf.ai](https://vdf.ai/blog/best-local-llm-for-coding/)

**Out of the 16 GB envelope (listed for context)**
- Qwen3-Coder-Next is an 80B-total / about 3B-active MoE (512 experts, 10 active + 1 shared) with 256K context, released Feb 2026. It needs about 46 GB RAM at 4-bit — [Unsloth docs](https://unsloth.ai/docs/models/tutorials/qwen3-coder-next.md); [HF GGUF](https://huggingface.co/Qwen/Qwen3-Coder-Next-GGUF)
- Liquid LFM2.5-24B-A2B (Oct 2026, 2.45B active) is about 13 GB at 4-bit. Its LFM Open License has a revenue cap, so it is not a standard permissive license (third-party report) — [vdf.ai](https://vdf.ai/blog/best-local-llm-for-coding/)
- Gemma 4 26B-A4B (25.2B total) is too large at 4-bit for 16 GB once the OS and KV cache are counted — [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4)

### Inferences
- **For a chat/agent assistant:** the strongest candidates by reported scores that fit 16 GB at Q4 are Qwen3.5-9B (about 5.5–6 GB at Q4), Ministral 3 8B, Granite 4.1 8B, Gemma 4 E4B and Mellum2.1 (about 8 GB). Qwen3.5-4B is the lighter fallback.
- **For FIM autocomplete:** Qwen2.5-Coder (1.5B or 3B for speed on a CPU, 7B for quality) is the best-documented choice. Granite 4.1 8B is the newer FIM-capable alternative.
- The scores for thinking-mode 2026 models depend on long reasoning traces, which are slow on a laptop CPU. With thinking off, expect real quality well below the reported LCB numbers.

### Gaps
- No verified official specs for a "Qwen3.8" family. A self-hosting guide names "Qwen3.8-27B" and "Qwen3.8-Flash-Next" ([vdf.ai](https://vdf.ai/blog/best-local-llm-for-coding/)), but I found no small (≤9B) Qwen3.6–3.8 dense model card. Check this before choosing a model.
- Mellum2.1 specs and scores come from news coverage, not the HF card, which I did not fetch.
- Gemma 4 release date is not on the card (page last updated 2026-07-30). The card is marked deprecated in favour of gemma.ai.
- Not researched in detail: SmolLM3, DeepSeek-Coder-V2-Lite (16B total / 2.4B active; Q4 is about 9–10 GB, borderline), Llama 3.x as a primary pick, and Codestral/Devstral (22B/24B, out of the size range).

## Q2. Coding benchmark scores by size

### Takeaway
At about 8B on the older HumanEval/MBPP-style tests, Qwen2.5-Coder-7B-Instruct (HE 88.4), Granite 4.1 8B (HE 85.4 / MBPP 87.3) and Seed-Coder-8B (HE 84.8, best BigCodeBench and LCB of its 2025 cohort) are roughly tied. On the 2026 LCB v6 runs (thinking mode), the reported order is Mellum2.1 (82, self-reported), Qwen3.5-9B (65.6), Ministral 3 8B Reasoning (61.6), Qwen3.5-4B (55.8), Ministral 3 3B (54.8), Gemma 4 E4B (52.0) and Gemma 4 E2B (44.0).

### Cited Findings
**Qwen2.5-Coder Instruct (tech report)** — [arXiv 2409.12186](https://arxiv.org/html/2409.12186v3)

| Model | HE | HE+ | MBPP | MBPP+ | BCB-Inst Full / Hard | LCB | Aider P@1 / P@2 |
|---|---|---|---|---|---|---|---|
| Qwen2.5-Coder-0.5B-Inst | 61.6 | 57.3 | 52.4 | 43.7 | 11.1 / 1.4 | 2.0 | 14.3 / 14.3 |
| Qwen2.5-Coder-1.5B-Inst | 70.7 | 66.5 | 69.2 | 59.4 | 32.5 / 6.8 | 6.1 | 28.6 / 31.6 |
| Qwen2.5-Coder-3B-Inst | 84.1 | 80.5 | 73.6 | 62.4 | 35.8 / 14.2 | 10.8 | 33.8 / 39.1 |
| Qwen2.5-Coder-7B-Inst | 88.4 | 84.1 | 83.5 | 71.7 | 41.0 / 18.2 | 18.2 | 55.6 / 68.4 |
| DeepSeek-Coder-V2-Lite-Inst (2.4B active / 16B) | 81.1 | 75.6 | 82.8 | 70.4 | 36.8 / 16.2 | 16.3 | 44.4 / 52.6 |
| Yi-Coder-9B-Chat | 82.3 | 74.4 | 82.0 | 69.0 | 38.1 / 11.5 | 17.2 | 45.9 / 54.1 |
| DeepSeek-Coder-6.7B-Inst | 74.4 | 71.3 | 74.9 | 65.6 | 35.5 / 10.1 | 15.5 | 37.6 / 44.4 |

The paper's text gives 51.9 for the 7B on Aider Pass@1, but Table 19 gives 55.6. The "Aider" here is the report's own code-editing set, not the current Aider polyglot leaderboard — [arXiv 2409.12186](https://arxiv.org/html/2409.12186v3)

**Qwen2.5-Coder Base (same report)**

| Model | HE | HE+ | MBPP | MBPP+ | BCB Full / Hard |
|---|---|---|---|---|---|
| 0.5B | 28.0 | 23.8 | 52.9 | 47.1 | 16.1 / 4.7 |
| 1.5B | 43.9 | 36.6 | 69.2 | 58.6 | 34.6 / 9.5 |
| 3B | 52.4 | 42.7 | 72.2 | 61.4 | 41.1 / 11.5 |
| 7B | 61.6 | 53.0 | 76.9 | 62.9 | 45.8 / 16.2 |
| StarCoder2-3B | 31.7 | 27.4 | 60.2 | 49.1 | 21.4 / 4.7 |
| StarCoder2-7B | 35.4 | 29.9 | 54.4 | 45.6 | 27.7 / 8.8 |
| DeepSeek-Coder-6.7B-Base | 47.6 | 39.6 | 70.2 | 56.6 | 41.1 / 11.5 |
| DeepSeek-Coder-1.3B-Base | 34.8 | 26.8 | 55.6 | 46.9 | 26.1 / 3.4 |

Source: [arXiv 2409.12186](https://arxiv.org/html/2409.12186v3)

**Granite 4.1 Instruct** — [HF model card](https://huggingface.co/ibm-granite/granite-4.1-8b)

| Size | HE | HE+ | MBPP | MBPP+ | BigCodeBench | CRUXEval-O | MultiPL-E | EvalPlus avg |
|---|---|---|---|---|---|---|---|---|
| 3B | 81.71 | 76.83 | 71.16 | 62.17 | 32.19 | 40.75 | 52.54 | 67.05 |
| 8B | 85.37 | 79.88 | 87.30 | 73.81 | 35.00 | 47.63 | 60.26 | 80.21 |
| 30B | 88.41 | 85.37 | 85.45 | 73.54 | 38.77 | 55.75 | 62.31 | 82.66 |

The card reports no LiveCodeBench score.

**Seed-Coder-8B-Instruct, with its 8B-class comparison** — [HF model card](https://huggingface.co/ByteDance-Seed/Seed-Coder-8B-Instruct)

| Model | HE | MBPP | MHPP | BCB Full / Hard | LCB (2410–2502) |
|---|---|---|---|---|---|
| Seed-Coder-8B-Inst | 84.8 | 85.2 | 36.2 | 53.3 / 26.4 | 24.7 |
| Qwen3-8B | 84.8 | 77.0 | 32.8 | 51.7 / 23.0 | 23.5 |
| Qwen2.5-Coder-7B-Inst | 88.4 | 83.5 | 26.7 | 48.8 / 20.3 | 17.3 |
| OpenCoder-8B-Inst | 83.5 | 79.1 | 30.5 | 50.9 / 18.9 | 17.1 |
| Yi-Coder-9B-Chat | 82.3 | 82.0 | 26.7 | 49.0 / 17.6 | 17.5 |
| Llama-3.1-8B-Inst | 68.3 | 70.1 | 17.1 | 40.5 / 13.5 | 11.5 |
| DeepSeek-Coder-6.7B-Inst | 74.4 | 74.9 | 20.0 | 43.8 / 15.5 | 9.6 |

The BigCodeBench (BCB) numbers here differ from the Qwen2.5-Coder report's numbers for the same models, because each report used a different harness or split.

**Phi-4-mini report (Table 8)** — [arXiv 2503.01743](https://arxiv.org/html/2503.01743)

| Model | HE | HE+ | MBPP (3-shot) | MBPP+ | BCB Compl / Inst | LCB (2024-05..09) |
|---|---|---|---|---|---|---|
| Phi-4-mini 3.8B | 74.4 | 68.3 | 65.3 | 63.8 | 43.0 / 33.8 | 19.9 |
| Qwen2.5-7B (general) | 75.0 | 68.9 | 76.3 | 65.9 | 43.4 / 33.5 | 19.9 |
| Qwen2.5-3B (general) | 72.0 | 64.6 | 65.3 | 60.6 | 33.8 / 25.0 | 14.7 |
| Ministral-8B (2410) | 70.7 | 70.7 | 68.9 | 61.6 | 47.4 / 35.6 | 16.2 |
| Llama-3.1-8B | 66.5 | 57.3 | 69.4 | 11.4 (as printed) | 34.1 / 34.8 | 16.8 |
| Llama-3.2-3B | 62.8 | 51.8 | 67.2 | 52.9 | 25.7 / 18.6 | 9.9 |
| Gemma2-9B | 63.4 | 54.3 | 69.6 | 63.5 | 40.6 / 33.6 | 14.7 |

**Qwen3-4B-Instruct-2507** — [HF model card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)
- LCB v6 (25.02–25.05): 35.1, against 26.4 for the original Qwen3-4B non-thinking.
- MultiPL-E: 76.8.
- Aider-Polyglot: 12.9 (Qwen3-30B-A3B non-thinking scores 24.4).

**2026 models: LiveCodeBench v6**
- Qwen3.5-9B scores 65.6 (OJBench 29.2). Qwen3.5-4B scores 55.8 (OJBench 24.1). The card lists no scores for 0.8B or 2B — [HF Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B). A third-party guide claims 82.7 for the 9B, which conflicts with the card's 65.6 — [codersera](https://codersera.com/blog/qwen-3-5-complete-guide-2026/). Use the model card.
- Gemma 4: E2B 44.0 / Codeforces ELO 633, E4B 52.0 / 940, 12B 72.0 / 1659, 26B-A4B 77.1 / 1718, 31B 80.0 / 2150. Gemma 3 27B scored 29.1 — [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4)
- Ministral 3 Reasoning variants: 3B 54.8 (Qwen3-VL-4B-Thinking 51.3), 8B 61.6 (Qwen3-VL-8B-Thinking 58.0), 14B 64.6. The card has no HumanEval or MBPP scores and no coding scores for the Instruct variant — [HF model card](https://huggingface.co/mistralai/Ministral-3-8B-Instruct-2512)
- Mellum2.1 (self-reported) claims LCB v6 82% against "Qwen's 75.4%" and SWE-bench Verified 47.0% (Mellum2 scored 2.0%). On JetBrains' own tests, Qwen3.5-9B still fixes more bugs and leads on SWE-bench Pro and Terminal-Bench 2.1 — [NYU RITS](https://rits.shanghai.nyu.edu/ai/jetbrains-ships-mellum2-1-a-12b-open-model-for-coding-agents/); [datanorth](https://datanorth.ai/news/jetbrains-mellum2-1-jumps-from-2-to-47-on-bug-fixes)
- Granite 4.1 8B is called the top HumanEval scorer in one 2026 roundup (87.2%, which does not match the card's 85.37) — [labellerr](https://www.labellerr.com/blog/best-small-language-models-under-10b-parameters/)

### Inferences
- HumanEval and MBPP have saturated at about 85–88 for the best 7–9B models and no longer separate them. BigCodeBench-Hard, LCB and Aider-style editing are more useful for telling them apart.
- Among non-thinking 2024–25 models, the best roughly 8B performers on harder tests are Seed-Coder-8B, Qwen3-8B and Qwen2.5-Coder-7B. The 2026 thinking-mode models report much higher LCB, but those results rely on long reasoning chains.
- The cheapest reasonable sizes are Qwen2.5-Coder-3B-Instruct (HE 84.1, Aider 33.8) and Granite 4.1 3B (HE 81.7).

### Gaps
- I did not fetch the independent leaderboards (EvalPlus, the LiveCodeBench site, the BigCodeBench site, the Aider polyglot leaderboard), so I have no independent cross-vendor check of 2026 small models. Aider polyglot numbers for small models are mostly missing; the only one found is Qwen3-4B-2507 at 12.9.
- Qwen3.5-9B, Gemma 4 E4B and Ministral 3 report no HumanEval, MBPP or BigCodeBench scores.
- No official Ministral 3 Instruct coding numbers. Aggregators give conflicting figures (llmbase: 8B LCB 30.3% vs 3B 24.7%; benchlm: HE 23 for the 8B), and both look unreliable — [llmbase](https://llmbase.ai/compare/ministral-3-8b,ministral-3-3b); [benchlm](https://benchlm.ai/models/ministral-3-8b)

## Q3. Which support FIM natively, and which have both base and instruct checkpoints?

### Takeaway
Confirmed native FIM: **Qwen2.5-Coder (all sizes; base and instruct)** and **IBM Granite 4.1**. DeepSeek-Coder and StarCoder2 also support FIM according to their own documentation, but I did not re-verify that here. Not documented for the 2026 general models (Qwen3.5, Gemma 4, Ministral 3, Phi-4-mini). Base and instruct checkpoints exist for Qwen2.5-Coder, Qwen3.5, Gemma 4, Granite 4.1, Ministral 3 (plus Reasoning) and Seed-Coder (plus Reasoning).

### Cited Findings
- Qwen2.5-Coder was trained with FIM at both file level and repo level. Its tokens are `<|fim_prefix|>` (151659), `<|fim_middle|>` (151660), `<|fim_suffix|>` (151661), `<|fim_pad|>`, `<|repo_name|>` (151663) and `<|file_sep|>` (151664). HumanEval-FIM single-line averages: 0.5B 77.7, 1.5B 83.5, 3B 85.7, 7B 86.2, against 84.0 for DeepSeek-Coder-6.7B-Base — [arXiv 2409.12186](https://arxiv.org/html/2409.12186v3)
- Granite 4.1 lists "Fill-In-the-Middle (FIM) code completions" as a capability. A Base checkpoint exists — [HF model card](https://huggingface.co/ibm-granite/granite-4.1-8b)
- Qwen3.5-9B has a Base checkpoint. FIM is not mentioned — [HF model card](https://huggingface.co/Qwen/Qwen3.5-9B)
- Gemma 4 releases pre-trained and instruction-tuned variants. FIM is not mentioned — [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4)
- Ministral 3 has Base, Instruct and Reasoning checkpoints at 3B, 8B and 14B. FIM is not mentioned — [HF model card](https://huggingface.co/mistralai/Ministral-3-8B-Instruct-2512)
- Seed-Coder-8B has Base, Instruct and Reasoning checkpoints. The Instruct card does not mention FIM — [HF model card](https://huggingface.co/ByteDance-Seed/Seed-Coder-8B-Instruct)
- Phi-4-mini-instruct does not mention FIM — [HF model card](https://huggingface.co/microsoft/Phi-4-mini-instruct)
- Qwen3-4B-Instruct-2507 does not mention FIM and has no thinking mode — [HF model card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)

### Inferences
- A two-model setup fits 16 GB: Qwen2.5-Coder-1.5B or 3B Base for FIM autocomplete (about 1–2 GB at Q4) next to a roughly 8–9B chat model. Granite 4.1 8B could cover both roles with one model.

### Gaps
- I did not verify the Seed-Coder-8B-Base FIM format (its tech report likely documents one) or the StarCoder2/DeepSeek-Coder FIM details.
- I found no FIM benchmark for Granite 4.1 or Mellum2.1. The earlier Mellum versions were FIM completion models, but FIM support in 2.1 is not confirmed.

## Q4. Dense vs MoE, active params, and context length

### Takeaway
Nearly every candidate under 9B is dense. The MoE options that fit 16 GB are Mellum2.1 (12B / 2.5B active, about 8 GB at Q4_K_M) and, borderline, DeepSeek-Coder-V2-Lite (16B / 2.4B active). Gemma 4 26B-A4B, LFM2.5-24B-A2B, Qwen3-30B-A3B and Qwen3-Coder-Next are too large. Context ranges from 32K (Seed-Coder) to 128K (Qwen2.5-Coder via YaRN, Phi-4-mini, Gemma 4 E-series, Granite 4.1) to 256K+ (Qwen3-2507, Qwen3.5, Ministral 3).

### Cited Findings
| Model | Type | Params (total / active) | Context | Source |
|---|---|---|---|---|
| Qwen2.5-Coder-7B | Dense | 7.61B | 32K default, 128K with YaRN | [HF](https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct) |
| Qwen3-4B-Instruct-2507 | Dense | 4.0B | 262K | [HF](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507) |
| Qwen3.5-9B / 4B | Hybrid DeltaNet + attention (dense/MoE unclear) | 9B (Hub: 10B) | 262K native, about 1M with YaRN | [HF](https://huggingface.co/Qwen/Qwen3.5-9B) |
| Gemma 4 E4B / E2B | Dense, PLE | 4.5B effective (8B with embeddings) / 2.3B effective (5.1B) | 128K | [Google](https://ai.google.dev/gemma/docs/core/model_card_4) |
| Granite 4.1 8B / 3B | Dense | 8B (Hub: 9B) / 3B | 128K | [HF](https://huggingface.co/ibm-granite/granite-4.1-8b) |
| Ministral 3 8B / 3B | Dense, with vision | 8.4B + 0.4B vision | 256K | [HF](https://huggingface.co/mistralai/Ministral-3-8B-Instruct-2512) |
| Seed-Coder-8B | Dense (llama) | 8B | 32K (Reasoning variant 64K) | [HF](https://huggingface.co/ByteDance-Seed/Seed-Coder-8B-Instruct) |
| Phi-4-mini | Dense | 3.8B | 128K | [HF](https://huggingface.co/microsoft/Phi-4-mini-instruct) |
| Mellum2.1 Thinking | MoE | 12B / 2.5B | not found | [NYU RITS](https://rits.shanghai.nyu.edu/ai/jetbrains-ships-mellum2-1-a-12b-open-model-for-coding-agents/) |
| DeepSeek-Coder-V2-Lite | MoE | 16B / 2.4B | not verified here | [arXiv 2409.12186](https://arxiv.org/html/2409.12186v3) |
| Gemma 4 26B-A4B (too big) | MoE | 25.2B / 3.8B | 256K | [Google](https://ai.google.dev/gemma/docs/core/model_card_4) |
| Qwen3-Coder-Next (too big) | MoE | 80B / about 3B | 256K | [Unsloth](https://unsloth.ai/docs/models/tutorials/qwen3-coder-next.md) |

### Inferences
- On a CPU, decode speed depends mostly on active params and memory bandwidth. A 2.5B-active MoE such as Mellum2.1 should decode noticeably faster than a dense 8–9B model while taking similar RAM, which is attractive if its self-reported scores hold up.
- Qwen3.5's linear-attention layers should keep the KV cache small at long context, but llama.cpp/GGUF support for Gated DeltaNet on CPU needs checking.
- Suggested shortlist: **primary chat/agent model** Qwen3.5-9B (or Granite 4.1 8B if a single model must also do FIM). **Fallback** Qwen2.5-Coder-7B-Instruct, the most documented code specialist with FIM. **Lightweight or autocomplete** Qwen2.5-Coder-1.5B/3B. **Watch** Mellum2.1 until independent results appear.

### Gaps
- I did not find Mellum2.1's context length, or DeepSeek-Coder-V2-Lite's context length in a primary source.
- I did not confirm whether Qwen3.5-9B is dense or MoE.
- I did not measure real Q4 GGUF file sizes or CPU tokens/sec for any of these models.
