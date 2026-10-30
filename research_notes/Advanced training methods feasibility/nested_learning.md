# Nested Learning (Google Research, Behrouz et al., NeurIPS 2025): Hope, Continuum Memory System, and Feasibility for Small Open Models

Scope: what Nested Learning (NL) is, whether it can be retrofitted onto Granite 4.1 3B / Granite 4.0-H / Qwen2.5-Coder 1.5B/3B for .NET/C#/networking/security specialization and ongoing preference learning, under a free-GPU budget (T4 16 GB, ~30 GPU-h/week) with llama.cpp CPU inference. Researched 2026-10-29.

## 1. Core ideas, in plain language

### Takeaway
Nested Learning is mainly a way of looking at models, plus one proof-of-concept architecture. It treats a network and its optimizer as a stack of learning problems, each updating its own weights at its own speed. The practical artifacts are the "Continuum Memory System" (a chain of MLP blocks that keep updating at different rates, including at inference time), "deep optimizers," and Hope, a self-modifying variant of Titans that uses CMS.

### Cited Findings
- NL treats a model as "a system of interconnected, multi-level learning problems" optimized together. It argues that architecture and optimizer are the same kind of thing: "levels" of optimization, each with its own context flow and update rate. Ranking components by how often their weights change orders them into levels. — [Google Research blog, Nov 7 2025](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/)
- Backprop and attention can both be formalized as associative memories. Backprop maps each data point to its local error, a measure of how "surprising" it was. — [Google Research blog](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/)
- Deep optimizers: momentum and Adam are recast as associative memories that compress gradients. Swapping the dot-product objective for an L2 regression loss gives new momentum variants that the authors claim are more robust to imperfect data. — [Google blog](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/); [arXiv 2512.24695](https://arxiv.org/abs/2512.24695v1)
- Continuum Memory System (CMS): in a standard Transformer, attention is the short-term memory and the FFN is long-term memory that never updates after pretraining. CMS replaces this split with a spectrum of MLP blocks, each updating at a different frequency. — [Google blog](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/); [LessWrong summary](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- How often each level updates: a CMS level ℓ has chunk size C^(ℓ) = max_i C^(i) / f_ℓ and updates its parameters every C^(ℓ) tokens. Fast levels adapt quickly but forget quickly. Slow levels hold persistent knowledge. — [arXiv 2512.24695 HTML, Sec. 7.1](https://arxiv.org/html/2512.24695v1)
- Hope vs Titans: Titans has two levels of parameter updates and first-order in-context learning. Hope is a self-modifying recurrent variant of Titans, which the authors say supports "unbounded levels" of in-context learning, with CMS blocks added for longer context. — [Google blog](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/)
- The full paper, "Nested Learning: The Illusion of Deep Learning Architectures," is by Behrouz, Razaviyayn, Zhong, and Mirrokni. The arXiv version (Dec 31 2025) extends the NeurIPS 2025 version. It lists three contributions: expressive optimizers, a self-modifying learning module, and CMS. — [arXiv 2512.24695](https://arxiv.org/abs/2512.24695v1); [NeurIPS 2025 poster](https://neurips.cc/virtual/2025/poster/116123)

### Inferences
- Plain version: a normal LLM learns only during training. Afterwards, only its context window (the KV cache) changes. NL/Hope adds MLP "memories" that keep rewriting themselves during use, some every few hundred tokens and some rarely, so new information can stick without overwriting everything.
- Much of the paper is a reframing, such as "optimizers are memories." Only CMS and Hope are concrete changes you could build.

### Gaps
- The blog gives no hyperparameters such as the numeric chunk sizes and frequencies used in experiments. I did not extract them from the paper either.

## 2. From scratch vs. retrofitting pretrained transformers; 2026 follow-ups

### Takeaway
Hope as reported is trained from scratch (760M/30B tokens and 1.3B/100B tokens). The paper does describe a retrofit, "Ad-hoc Level Stacking," which initializes CMS levels from a pretrained model's MLPs. The authors applied it to Llama-3 backbones with 15B tokens of continued pretraining. No official code exists. I found no third-party retrofit of a small open model such as Qwen or Granite, and no Mamba/hybrid variant.

### Cited Findings
- Sec. 7.3, "Ad-hoc Level Stacking: Initializing CMS with Pre-Trained Models": each CMS block MLP(f_i) is initialized from a pretrained MLP block and then updated at its own frequency. Setting the level's learning rate η→0 keeps the block close to its pretrained state. — [arXiv 2512.24695 HTML](https://arxiv.org/html/2512.24695v1)
- Sec. 9.1 uses "Llama3-8B and Llama-3B" (the second name may be a typo) with "continual pre-training with 15B tokens," compared against ICL, EWC, and InCA. These models are called "Hope-Attention": CMS combined with standard attention, replacing the frozen MLPs. — [arXiv 2512.24695 HTML](https://arxiv.org/html/2512.24695v1); [LessWrong, S. Knoche, Jan 29 2026](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- Official code: none. The only community reproduction found is smallhours19/nested-learning-hope, an "unofficial, reduced-scale" PyTorch reproduction. It is a 30.6M-parameter, 4-layer, byte-level, attention-free model trained in about 7 GPU-hours on one 16 GB GPU, with deviations from the paper documented and no hosted weights. The model card states that "the paper has no official code or weights." — [HF: smallhours19/nested-learning-hope](https://huggingface.co/smallhours19/nested-learning-hope)
- A second community repo, obekt/HOPE-nested-learning, is linked from alphaXiv. I did not inspect it. — [alphaXiv resources](https://www.alphaxiv.org/resources/2512.24695)
- 2026 follow-ups from the same group: "Language Models Need Sleep: Learning to Self-Modify and Consolidate Memories" (Behrouz, Hashemi, Javanmard, Mirrokni; arXiv 2606.03979). It proposes a "sleep" phase: Memory Consolidation (upward distillation from a smaller model into a larger one, via on-policy distillation plus RL imitation) and "Dreaming" (RL-generated synthetic curricula). The abstract gives no metrics or compute figures. — [arXiv 2606.03979](https://arxiv.org/abs/2606.03979)
- Other 2026 work mostly borrows the name loosely. Examples are a "Dynamic nested hierarchies" Frontiers article (May 2026) critiquing "static nested learning," an ACL/EMNLP 2026 KV-cache compression paper (NestedKV) "inspired by" CMS, and agent papers using CMS as a metaphor. None of them retrofits CMS layers into a small pretrained LLM. — [Frontiers in AI 2026](https://www.frontiersin.org/journals/artificial-intelligence/articles/10.3389/frai.2026.1804338/full); [NestedKV, EMNLP 2026](https://aclanthology.org/2026.emnlp-main.462/); [arXiv 2601.13186](https://arxiv.org/pdf/2601.13186)
- I found no independent reproduction or critique of the main Hope results at paper scale. — searched; see [Continual Learning in Transition survey, arXiv 2608.06216](https://arxiv.org/pdf/2608.06216) for the general 2026 landscape

### Inferences
- A retrofit is possible in principle for a dense transformer (Granite 4.1 3B, Qwen2.5-Coder) by copying each FFN into several CMS levels. However, the only reported recipe used 15B tokens of continued pretraining on an 8B model. At an optimistic ~1–2k tokens/s for a 3B model on a T4, 30 GPU-h/week is roughly 100–200M tokens/week. That is about 1% of 15B, so the published recipe is out of reach by about two orders of magnitude.
- Granite 4.0-H (hybrid Mamba-2) has no FFN-to-CMS mapping in the paper. It would be original research.

### Gaps
- I could not confirm whether the Llama retrofit kept the CMS levels updating at inference or froze them after continued pretraining.
- I found no exact continual-learning accuracy numbers for the Llama retrofit: Figures 6 and 8 are charts only, and the text gives no values.

## 3. Continual learning promise vs. cheaper stand-ins

### Takeaway
Hope's continual-learning evidence is from the authors only and is mostly in charts. In class-incremental classification (CLINC, Banking, DBpedia) Hope is "best across all continual learning baselines." In a sequential low-resource translation test, the 3-level CMS "nearly recovers" the single-language baseline, while plain ICL forgets. For a small model on a free GPU, the realistic stand-ins are LoRA (which forgets less) plus replay, sparse memory finetuning (FAIR, 2025), and retrieval or a preference memory file for user preferences.

### Cited Findings
- Hope-Attention (Llama-3 backbone) beats ICL, EWC, and InCA on class-incremental benchmarks. Adding CMS levels restores retention in a sequential two-language translation test (MTOB/Kalamang and Manchu). Numbers appear only in Figures 6 and 8. — [arXiv 2512.24695 HTML Sec. 9.1](https://arxiv.org/html/2512.24695v1); [LessWrong](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- The blog itself calls Hope a "proof-of-concept." The LessWrong analysis calls it "probably best understood as a proof of concept" and says it is "unclear" whether it scales. — [Google blog](https://research.google/blog/introducing-nested-learning-a-new-ml-paradigm-for-continual-learning/); [LessWrong](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- Sparse memory finetuning (Lin et al., FAIR/UC Berkeley, arXiv 2510.15103): replace a mid-model FFN with a memory layer and update only the slots that new data activates strongly relative to pretraining (TF-IDF style). Learning a TriviaQA fact stream drops NaturalQuestions by 89% with full finetuning and 71% with LoRA, but only 11% with sparse memory finetuning, at equal retention. The experiments are on a 1.3B model with factual QA. — [arXiv 2510.15103](https://arxiv.org/abs/2510.15103v1)
- Sparse memory finetuning still needs a model with a memory layer. Like CMS, this is an architecture change rather than a drop-in adapter. — [arXiv 2510.15103 HTML](https://arxiv.org/html/2510.15103v1)
- "LoRA Learns Less and Forgets Less" (Biderman et al., TMLR 2024): on code and math, LoRA learns less than full finetuning but preserves out-of-domain performance better than weight decay or dropout. Full finetuning learns perturbations with 10–100× higher rank. — [TMLR listing](https://mlanthology.org/tmlr/2024/biderman2024tmlr-lora); [summary](https://liner.com/review/lora-learns-less-and-forgets-less)
- Adapter-based continual memory on small open models is being explored in 2026: a bioRxiv preprint uses low-rank adapters on Llama-3.1-8B and Qwen3-8B for continual episodic memory. — [bioRxiv 2026.08.24.746712](https://www.biorxiv.org/content/10.64898/2026.08.24.746712v1.full)

### Inferences
- Domain specialization (.NET, C#, networking, security) does not need continual learning. A one-shot QLoRA/LoRA SFT with ~10–30% general/code replay data mixed in captures the "don't forget" benefit within the T4 budget and is supported by Unsloth and PEFT.
- For "keep learning my preferences," the cheapest robust approach is outside the weights: a preference/profile memory or RAG injected into the prompt, plus periodic small LoRA refreshes, each trained on accumulated preference data plus replay and then merged or swapped. This approximates CMS's fast and slow levels: the prompt memory is the fast level and periodic LoRA merges are the slow level.
- Test-time training or CMS-style inference-time weight updates are what NL really adds. They are not achievable inside llama.cpp today (see section 5).

### Gaps
- I found no study of sparse memory finetuning on code models or on Qwen/Granite, and no Unsloth support for memory layers.

## 4. Compute reported; code; HF / PEFT / Unsloth compatibility

### Takeaway
Paper-scale compute is far above free-tier budgets. There is no official code, no Hugging Face transformers integration, and no PEFT or Unsloth support. The only runnable code is a 30M-parameter community toy.

### Cited Findings
- Language modeling results (from scratch), Table 2. Wiki ppl and LMB ppl are lower-is-better. Avg acc is higher-is-better.

  | Model | Size / tokens | Wiki ppl | LMB ppl | Avg acc |
  |---|---|---|---|---|
  | Transformer++ | 760M / 30B | 24.18 | 24.27 | 50.11 |
  | Titans | 760M / 30B | 20.08 | 21.52 | 51.68 |
  | Hope | 760M / 30B | 18.68 | 20.07 | 52.28 |
  | Transformer++ | 1.3B / 100B | 17.92 | 17.73 | 53.38 |
  | Titans | 1.3B / 100B | 15.60 | 11.41 | 56.82 |
  | Hope | 1.3B / 100B | 14.39 | 10.08 | 58.04 |

  Hope is ahead of Titans by about 1–1.5 points of average accuracy. — [arXiv 2512.24695 HTML Sec. 9.3](https://arxiv.org/html/2512.24695v1)
- NIAH, from scratch on about 50B tokens at 16K context: S-NIAH-1 Hope 100.0 vs Titans 100.0; S-NIAH-3 24.8 vs 21.2; MK-NIAH-1 14.8 vs 8.2. — [arXiv 2512.24695 HTML Table 1](https://arxiv.org/html/2512.24695v1)
- The attention-free Hope still trails standard Transformers on short-context recall and NIAH. — [LessWrong](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- The paper gives no training throughput or hardware figures. The M3 optimizer is described as "relatively slower" than Muon and on par with AdaMuon. — [arXiv 2512.24695 HTML](https://arxiv.org/html/2512.24695v1)
- Community reproduction: 30.6M parameters, about 7 GPU-hours on a single 16 GB GPU, MIT license, no Hub weights, no transformers or PEFT integration mentioned. — [HF: smallhours19/nested-learning-hope](https://huggingface.co/smallhours19/nested-learning-hope)

### Inferences
- A T4 can run the toy reproduction for learning purposes (about 7 GPU-h, roughly a quarter of one week's budget). It cannot reproduce paper-scale results or the 15B-token retrofit.
- Without HF integration, Unsloth's speed and memory savings and PEFT adapters do not apply. You would have to write a custom training loop with per-chunk inner-loop weight updates, which also adds memory pressure on 16 GB.

### Gaps
- Exact GPU-hours for the paper's runs are not reported.

## 5. Inference impact and llama.cpp / GGUF support

### Takeaway
Hope/CMS makes inference heavier, not lighter. Weights are written during the forward pass, adding constant-factor compute and per-session state. llama.cpp/GGUF has no support for this, and its static-weight inference model does not fit it.

### Cited Findings
- The write and consolidation steps add a constant-factor compute cost that standard Transformers avoid at inference. Per-user deployment is harder than with Titans because almost all of Hope's parameters can change per session. — [LessWrong](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- The paper's own estimate is that the average update cost for a 4-level Hope-Attention model is O((1/f̂)·(L_layer/5)·d_in²), "only a small number of parameters at each time." Using a lowest frequency of 2K gives a "significantly more efficient forward pass" (no figures given). — [arXiv 2512.24695 HTML Sec. 7.1, 9](https://arxiv.org/html/2512.24695v1)
- The community model's weights change during the forward pass, a form of test-time learning. — [HF model card](https://huggingface.co/smallhours19/nested-learning-hope)
- I found no llama.cpp issue, PR, or GGUF architecture for Titans, Hope, or Nested Learning. — searched; no results ([HF GGUF docs](https://huggingface.co/docs/transformers/gguf) list only standard architectures)

### Inferences
- On a laptop CPU, every CMS write is an extra backward-like matrix update in fp32 or bf16. Quantized GGUF weights (Q4/Q5) cannot simply be updated in place. You would have to keep fp copies of the updated MLPs, which grows both RAM use and latency.
- If CMS levels are frozen after training, inference costs about the same as a model with a few extra MLPs, but then the continual-learning benefit is gone at inference.

### Gaps
- No measured CPU latency numbers exist for Hope or Titans.

## 6. Verdict

### Takeaway
Do not use Nested Learning/Hope here. Use a cheap approximation: QLoRA/LoRA domain SFT on Qwen2.5-Coder or Granite 4.1 3B with replay data mixed in, plus external preference memory (prompt or RAG) and periodic small LoRA refreshes with replay. Optionally run the 30M community reproduction on Colab to learn the ideas.

### Cited Findings
- No official code, and the only reproduction is 30M parameters. — [HF model card](https://huggingface.co/smallhours19/nested-learning-hope)
- The only retrofit recipe needs about 15B continued-pretraining tokens on a Llama-3 8B-class model. — [arXiv 2512.24695 HTML](https://arxiv.org/html/2512.24695v1)
- Inference-time weight writes add cost and per-session state. — [LessWrong](https://www.lesswrong.com/posts/Lby4gMvKcLPoozHfg/are-we-in-a-continual-learning-overhang-1)
- LoRA already reduces forgetting compared with full finetuning. — [Biderman et al., TMLR 2024](https://mlanthology.org/tmlr/2024/biderman2024tmlr-lora)
- Sparse memory finetuning beats LoRA on forgetting (11% vs 71% drop), but needs a memory-layer model. — [arXiv 2510.15103](https://arxiv.org/abs/2510.15103v1)

### Inferences
- Reasons to skip, from the reported facts: (a) no official code and no HF, PEFT, Unsloth, or llama.cpp path; (b) a retrofit compute gap of about 100× over the budget; (c) inference gets heavier and needs fp weight updates that are incompatible with GGUF quantization; (d) the gains are reported only by the authors, at up to 1.3B from scratch, about 1–1.5 points over Titans; (e) no published mapping exists for hybrid Mamba (Granite 4.0-H).
- Worth watching: if Google or the community release Hope-Attention checkpoints or HF code, or llama.cpp adds Titans/Hope ops, re-evaluate. The "Sleep"/consolidation follow-up (arXiv 2606.03979) is conceptually closer to the "periodic LoRA refresh" stand-in than to inference-time updates.

### Gaps
- No independent replication of the main claims at more than 100M parameters was found as of late October 2026.
