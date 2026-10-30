# Practical baseline: specializing and shrinking a 1.5B–3.4B coding LLM for ASP.NET Core / C# / .NET on a $0 budget (late 2026)

Scope: the "boring, proven" methods that the user's research methods (Next-Concept Modeling, Nested Learning, CompreSSM) should be compared against. Candidate bases are Granite 4.1 3B, Qwen2.5-Coder 1.5B, and Qwen2.5-Coder 3B. Hardware is free Colab or Kaggle T4s (16 GB, SM75). The target is CPU inference with llama.cpp/GGUF.

Research budget note: these notes come from about 15 search and fetch calls. Many numbers are from search snippets or abstracts, not full-paper reads. Those are flagged, so verify them before quoting in a final report.

## Structured pruning + distillation (Minitron, Sheared-LLaMA, LLM-Pruner, ShortGPT, width pruning)

### Takeaway
Pruning plus distillation works, but the published results come from 7B–15B teachers with tens to hundreds of billions of distillation tokens. That is far beyond a free T4. At 1.5–3B on $0, the realistic options are:
- Light depth pruning (ShortGPT-style layer dropping, about 10–25% of layers) or LLM-Pruner-style ~20% pruning.
- Then LoRA "healing" on a few tens of thousands of examples.
- Expect a real quality drop that has to be measured.

Width pruning gives better quality per parameter than depth pruning. Depth pruning gives more speed and is the simplest to export to GGUF.

### Cited Findings
- Minitron (Llama 3.1 8B → 4B). Width pruning beat depth pruning on quality:

  | Model | MMLU | GSM8K | MBPP |
  |---|---|---|---|
  | Llama 3.1 8B teacher | 65.3 | 48.6 | 42.3 |
  | 4B-Width | 60.5 | 41.2 | 32.4 |
  | 4B-Depth | 58.7 | 16.8 | 30.7 |

  Both 4B models used 94B distillation tokens, versus 15T for the teacher. — [Minitron in Practice, arXiv 2408.11796](https://arxiv.org/html/2408.11796)
- Code ability fell more than MMLU in Minitron: MBPP 42.3 → ~31–32 (about −25% relative) at 50% parameter removal. — [arXiv 2408.11796](https://arxiv.org/html/2408.11796)
- Depth-pruned Minitron 4B had about 2.7× throughput over the 8B; width-pruned had about 1.8×. — [arXiv 2408.11796](https://arxiv.org/html/2408.11796)
- Minitron lessons:
  - "Teacher correction" (lightly fine-tuning the teacher on the distillation dataset, about 100B tokens there) is called crucial when the original pretraining data is unavailable.
  - The distillation loss is forward KL on logits only.
  - Dropping a contiguous block of layers beat dropping individually "unimportant" layers.

  — [arXiv 2408.11796](https://arxiv.org/html/2408.11796)
- The original Minitron work (Nemotron-15B → 8B/4B) claims up to 40× fewer training tokens per model than training from scratch. — [arXiv 2407.14679](https://arxiv.org/pdf/2407.14679)
- Mistral-NeMo-Minitron-8B (pruned from 12B) used about 380B distillation tokens and matched or beat its teacher on MMLU (69.5 vs 69.0). — [arXiv 2408.11796](https://www.arxiv.org/pdf/2408.11796)
- Sheared-LLaMA pruned LLaMA2-7B to 1.3B and 2.7B, using 0.4B tokens for pruning and 50B tokens of continued pretraining.
  - 2.7B averaged 56.7, versus the 64.6 source and 55.7 for Open-LLaMA-3B-v2 (trained on 1T tokens).
  - The authors claim about 3% of the compute of training from scratch.

  — [Sheared-LLaMA project page](https://princeton-nlp.github.io/sheared-llama), [arXiv 2310.06694](https://arxiv.org/pdf/2310.06694)
- LLM-Pruner (NeurIPS 2023) removes gradient-selected coupled structures. Its abstract says LoRA recovers the pruned model "in merely 3 hours" with about 50K examples. I did not retrieve the exact 20% numbers. — [arXiv 2305.11627](https://arxiv.org/abs/2305.11627v3)
- Follow-up work argues that fixed-rank LoRA heals structurally pruned models sub-optimally, because pruning changes the architecture unevenly. — [RankAdaptor, arXiv 2406.15734](https://arxiv.org/html/2406.15734v1)
- ShortGPT ranks layers by "Block Influence" (how much a layer changes its hidden states) and deletes the lowest-scoring ones.
  - LLaMA-2-13B: removing 10 of 40 layers (25%) dropped MMLU only from 55.0 to 52.2, with no retraining.
  - A secondary review cites about 91.6% average retention, better than LLM-Pruner, SliceGPT and LaCo.

  These figures come from snippets, so verify them. — [ShortGPT arXiv 2403.03853](https://arxiv.org/html/2403.03853v3); [Liner review](https://liner.com/review/shortgpt-layers-in-large-language-models-are-more-redundant-than)
- Granite 4.1 3B is a dense decoder-only transformer: 40 layers, 2560 hidden size, 40 attention heads, 8 KV heads, 8192 MLP size, GQA, RoPE, SwiGLU, RMSNorm, and shared input/output embeddings. That standard shape makes it amenable to layer dropping and width pruning. — [IBM Granite 4.1 blog](https://huggingface.co/blog/ibm-granite/granite-4-1)

### Inferences
- **T4 feasibility, by method:**
  - ShortGPT-style layer dropping is fully feasible. Scoring needs only forward passes on a few hundred calibration samples, and LoRA healing of a 1.5–3B model fits a T4 with QLoRA.
  - LLM-Pruner at about 20% is likely feasible for ≤3B, since its gradient importance pass is similar in memory to a fine-tuning step.
  - Full Minitron-style logit distillation is the hard part. The student and teacher logits must both fit in memory, the vocabulary is ~150k, and the published runs used 94B+ tokens. On a T4, a few hundred million tokens is realistic. That is about 100× less, so quality retention well below Minitron's numbers should be expected.
- **Practical recipe for the baseline:** drop about 15–25% of layers in a contiguous block, chosen by Block Influence or by validation loss on C# text. Then QLoRA-heal on domain code plus about 5% general replay, using the teacher's outputs as targets (sequence-level distillation) instead of logit KD. This avoids holding teacher logits in memory.
- **Expected effect of 50% removal:** Minitron lost about 25% relative on MBPP even with 94B tokens. A $0, 50% prune of a 1.5–3B model will very likely hurt coding much more ("stupid" territory). 20–25% looks like the realistic ceiling.
- **llama.cpp compatibility:**
  - Depth pruning only changes the layer count (`num_hidden_layers`), so the model stays a standard Llama/Qwen2/Granite architecture and should convert with `convert_hf_to_gguf.py` unchanged.
  - Uniform width pruning (the same smaller FFN or head count in every layer) is also standard.
  - Non-uniform per-layer widths, which is LLM-Pruner's default, may not be expressible in a standard GGUF architecture.

  I found no source confirming per-layer variable-width support for Qwen2 or Granite in llama.cpp, so this is a risk to test.
- **Speed vs. quality:** for CPU inference, decode speed scales roughly with bytes read per token. A 25% layer drop gives roughly 25% faster decode and a smaller file, which stacks with quantization.

### Gaps
- No published Minitron, ShortGPT, or LLM-Pruner results on Qwen2.5-Coder 1.5B/3B or Granite 4.1 3B, and none on C# specifically. This must be measured.
- Exact LLM-Pruner numbers at 20% and 50%, and ShortGPT's code-benchmark (HumanEval) retention, were not retrieved. ShortGPT-style papers mostly report MMLU or commonsense tasks, and code generation is often more fragile to layer removal.
- NVIDIA ModelOpt/NeMo pruning tooling support for Qwen2.5 or Granite on a T4 was not checked.
- llama.cpp support for non-uniform pruned shapes is unverified.

## Domain-adaptive continued pretraining (CPT) and SFT with LoRA/QLoRA on a few hundred to a few thousand examples

### Takeaway
LoRA/QLoRA SFT on 1.5–3B models is easily feasible on a free T4 with Unsloth, which also exports GGUF directly. LoRA "learns less and forgets less" than full fine-tuning. For a few hundred to a few thousand examples, that trade-off is the right one: it adapts style and API usage with limited catastrophic forgetting. Mixing in about 5% general "replay" data is a reasonable starting point.

### Cited Findings
- LoRA vs full fine-tuning on code continued pretraining:
  - The best LoRA run (r=256) at 20B tokens roughly matched full fine-tuning at 4B tokens, and the gap grew with more data.
  - LoRA better preserved out-of-domain performance, and mitigated forgetting more than weight decay or dropout.

  — [LoRA Learns Less and Forgets Less, arXiv 2405.09673](https://arxiv.org/abs/2405.09673)
- The same paper finds full fine-tuning learns weight perturbations of 10–100× higher rank than typical LoRA configurations. — [arXiv 2405.09673](https://arxiv.org/html/2405.09673v2)
- Unsloth provides free Colab T4 notebooks for Qwen2.5 and Qwen2.5-Coder at all sizes, with export to GGUF via `save_pretrained_gguf` / `push_to_hub_gguf` (e.g. q4_k_m). — [Unsloth Qwen-Coder blog](https://unsloth.ai/blog/qwen-coder); [Continue.dev community post](https://blog.continue.dev/a-custom-autocomplete-model-in-30-minutes-using-unsloth)
- Unsloth pitfall: for Qwen 2.5, the pad token must not be `<|endoftext|>`, or fine-tuning yields infinite generations. — [Unsloth Qwen-Coder blog](https://unsloth.ai/blog/qwen-coder)
- Continue.dev documented training a custom autocomplete (FIM) model with Unsloth in about 30 minutes. That makes it a direct template for repo-specific FIM tuning. — [Continue.dev blog](https://blog.continue.dev/a-custom-autocomplete-model-in-30-minutes-using-unsloth)
- Granite 4.1 3B has Unsloth GGUF builds and docs. Unsloth notes llama.cpp needs `--jinja`, and warns against CUDA 13.2 (garbled output). — [unsloth/granite-4.1-3b-GGUF](https://huggingface.co/unsloth/granite-4.1-3b-GGUF); [Unsloth Granite 4.1 docs](https://unsloth.ai/docs/de/modelle/ibm-granite-4.1.md)
- Replay (mixing in general data):
  - In EvoLM, a 1B model under domain CPT forgot general knowledge. About 5% replay of general data mitigated this, and both too little and too much replay hurt. — [EvoLM, arXiv 2506.16029](https://arxiv.org/pdf/2506.16029)
  - Replaying generic pretraining data during fine-tuning can also improve target-task performance, not just prevent forgetting. — [The Finetuner's Fallacy, arXiv 2603.16177](https://arxiv.org/pdf/2603.16177)
  - Optimal replay ratios under compute limits "remain poorly understood". — [NeurIPS 2025 workshop, Balancing Synthetic Data and Replay](https://neurips.cc/virtual/2025/123885)
  - A 2026 preprint on spaced-repetition replay reports recovering 5–37 points of forgotten old-knowledge accuracy compared with naive CPT. — [alphaXiv 2608.17530](https://www.alphaxiv.org/abs/2608.17530.md)

### Inferences
- **What a few hundred to a few thousand SFT examples can do:** teach format, conventions, and preferred patterns (e.g. minimal APIs vs controllers, DI style, EF Core idioms, the user's naming). They will not inject much new knowledge. CPT on raw .NET text (docs plus repos, millions of tokens) is a separate step that needs far more data. On a T4, CPT of a few tens of millions of tokens with QLoRA is plausible in Kaggle's ~30 GPU-hours per week. This is an estimate to measure.
- **Forgetting controls:**
  - Use LoRA (not full fine-tuning) and a moderate rank (16–64).
  - Include ~5% general code/instruction replay (e.g. a slice of the base model's own instruct outputs or open code data).
  - Measure before and after on a general benchmark (HumanEval/MBPP or MultiPL-E Python) and on C#.
- **Base choice:** Qwen2.5-Coder 1.5B is FIM-capable and Apache 2.0. Qwen2.5-Coder 3B is stronger but under the Qwen Research license (see the distillation section).

### Gaps
- No source gave a tested minimum dataset size for C#/.NET SFT at 1.5–3B. The data-quantity-vs-gain curve must be measured.
- Exact T4 throughput (tokens/sec) for QLoRA on 1.5B/3B in current Unsloth was not retrieved.
- Unsloth support for Granite 4.1 fine-tuning (as opposed to just GGUFs) on a T4 was only indirectly confirmed via the docs page.

## Vocabulary/embedding trimming for a narrow domain

### Takeaway
Vocabulary trimming is proven for multilingual models, with up to ~50% memory savings in small models. For an English+C# coding model the real savings are modest and risky:
- The embedding is a minority of a 3B model's weights.
- Code relies on a broad token set (identifiers, symbols, Unicode in strings).
- Modified tokenizers can break GGUF conversion and chat templates.

It is low priority for the baseline.

### Cited Findings
- The core method: remove tokens, and their embedding rows, that never appear in a target corpus, optionally keeping only the top-n most frequent. — [Vocabulary Trimming, arXiv 2305.15020](https://arxiv.org/pdf/2305.15020)
- Trimming by Unicode script or by corpus cut memory use of small models by nearly 50%, with up to 25% faster generation. Corpus-based selection changes behavior less but shrinks less. — [arXiv 2311.09709](https://arxiv.org/html/2311.09709v2)
- COMPACT combines vocabulary pruning with FFN channel pruning across Qwen 2.5, LLaMA 3 and Gemma 3 (0.5B–70B). A tool release was promised but not found. — [COMPACT, arXiv 2509.06836](https://arxiv.org/html/2509.06836v3)
- Packaged tools exist: VocabSlim (PyPI) and yangjianxin1's LLMPruner (vocabulary pruning; not to be confused with horseee's LLM-Pruner). — [VocabSlim](https://pypi.org/project/vocabslim/); [LLMPruner](https://www.sourcepulse.org/projects/2335224)
- VocabTrim shrinks a draft model's LM head by up to 75% for speculative decoding, without training. — [arXiv 2506.22694](https://arxiv.org/html/2506.22694v1)
- No GGUF-specific trimming tool was found. — (search result, see [arXiv 2305.15020](https://arxiv.org/pdf/2305.15020) context above)
- Granite 4.1 uses shared (tied) input/output embeddings, so a trim saves the matrix once, not twice. — [IBM Granite 4.1 blog](https://huggingface.co/blog/ibm-granite/granite-4-1)

### Inferences
- **Rough size of the potential saving (from memory of the Qwen2.5 config; verify against `config.json`):**
  - Qwen2.5-Coder-1.5B has a vocabulary of about 152k and hidden size 1536. That is about 233M embedding parameters, roughly 15% of the model.
  - Trimming to ~50k tokens would save about 150M parameters, around 10% of the file, and less after Q4 quantization.
  - For Granite 4.1 3B (hidden 2560), the share depends on its vocabulary size, which was not retrieved.
- **Speed on CPU:** the LM-head matmul cost scales with vocabulary size, so trimming helps decode speed somewhat for small models. A safer variant is to trim only the output head for sampling while keeping the tokenizer intact, VocabTrim-style. llama.cpp has no flag for this that I found.
- **GGUF risk:** trimming requires rewriting `tokenizer.json`, the merges, and the token IDs. `convert_hf_to_gguf.py` identifies pre-tokenizers by hash, so a modified tokenizer may fail conversion or need a manual pre-tokenizer entry. This must be tested.

### Gaps
- No measured results of vocabulary trimming on a code model, or on C# specifically.
- No confirmation of llama.cpp conversion success with trimmed Qwen2 or Granite tokenizers.
- Granite 4.1 vocabulary size was not retrieved.

## Quantization-aware options beyond Q4_K_M (domain imatrix, IQ3/IQ2 at 3B, QAT)

### Takeaway
Quantization is the cheapest and most reliable shrink, and the baseline every other method must beat.

The steps are:
1. Generate a domain imatrix with `llama-imatrix` on C#/.NET text, which is CPU-feasible.
2. Quantize to Q4_K_M, Q5_K_M, IQ4_XS, and IQ3_M.
3. Measure KL divergence against FP16 on held-out C#.

IQ2-class quants at 3B are generally described as low quality, so test them but do not rely on them. QAT through Unsloth/TorchAO is available but targets TorchAO INT4 formats, not GGUF K-quants.

### Cited Findings
- Generating your own imatrix (for domain-specific calibration) requires llama.cpp's binaries; llama-cpp-python does not expose it.
  - The guide's advice: quality and diversity beat quantity, and include your specific use cases.
  - It expects 30 minutes to several hours on consumer hardware.
  - It notes the biggest benefit at lower bit levels.

  — [llm-gguf-tools imatrix docs](https://git.tomfos.tr/tom/llm-gguf-tools/src/branch/main/docs/imatrix_data.md)
- Calibration data:
  - kalomaze (llama.cpp discussion 5263) reported lower KL divergence than WikiText at the same length using the "groups_merged" mixed dataset, which includes code. This is anecdotal, not a controlled study. — [froggeric/imatrix README](https://huggingface.co/datasets/froggeric/imatrix/blob/6bc8bf98402ccff0a35ab25c80f1e8dbf8276bcc/README.md?code=true)
  - Others note that, without testing, calibration choices "can't really" be judged. — [dbrx-instruct iMat discussion](https://huggingface.co/dranger003/dbrx-instruct-iMat.GGUF/discussions/1)
- llama.cpp's `llama-perplexity --kl-divergence` compares quantized logits to FP16 logits (0 means identical distributions). This is the right metric for comparing calibration sets and quant levels. — [llama.cpp perplexity README](https://docs.rs/crate/llama-cpp-sys-4/0.4.4/source/llama.cpp/tools/perplexity/README.md)
- Bartowski's model cards describe IQ2_M as "relatively low quality, uses SOTA techniques to be surprisingly usable". — [bartowski GGUF cards](https://huggingface.co/bartowski/Meta-Llama-3.1-70B-Instruct-GGUF)
- Community Granite 4.1 3B GGUFs already ship IQ3_M (SandLogic) and Q2_K (Dzluck) variants. — [SandLogic granite-4.1-3b-GGUF](https://huggingface.co/SandLogicTechnologies/granite-4.1-3b-GGUF); [Dzluck](https://huggingface.co/Dzluck/granite-4.1-3b-GGUF)
- QAT (Unsloth + TorchAO):
  - Fake-quantizes weights during fine-tuning, then converts to real INT4.
  - Unsloth claims "up to 70%" recovery of lost accuracy: 66.9% on Gemma3-4B GPQA, and 45.5% on Gemma3-12B BBH.
  - No inference overhead, and 1.73× speed over BF16.
  - These are vendor numbers, not independently replicated.

  — [Unsloth QAT docs](https://unsloth.ai/docs/blog/quantization-aware-training-qat); [PyTorch QAT blog II](https://pytorch.org/blog/quantization-aware-training-in-torchao-ii/)

### Inferences
- **Small models are hurt more:** small models are generally reported to be more sensitive to low-bit quantization than large ones, because they have fewer redundant weights. For a 1.5–3B code model:
  - Q4_K_M or Q5_K_M is likely the floor for "not stupid".
  - IQ3_M is borderline.
  - IQ2 is likely too lossy for code, where single-token errors break compilation.

  This is a hypothesis to measure via KLD plus compile/test pass rate.
- **Approximate CPU sizes (computed from bits per weight):**

  | Model | Q4_K_M (~4.8 bpw) | IQ3_M (~3.7 bpw) |
  |---|---|---|
  | 3B | ~1.8–2.0 GB | ~1.4 GB |
  | 1.5B | ~1.0 GB | — |

- **Domain imatrix is free and CPU-only.** Running `llama-imatrix` on an F16 GGUF of a 1.5–3B model with a few hundred kilobytes of C#/.NET text (code, docs, the user's repos, excluding eval items) is cheap. Hold out a separate C# set for KLD.
- **QAT does not map to GGUF:** Unsloth/TorchAO QAT produces TorchAO INT4 checkpoints. There is no established path from it to llama.cpp K-quants or IQ-quants, so for a GGUF target QAT is mostly not applicable. An exception is Q4_0, which is plausibly compatible with Google-style Q4_0 QAT releases. That path is unverified for Qwen or Granite.

### Gaps
- No controlled study found of domain-specific (code) imatrix vs generic imatrix at IQ3/IQ2 on 1–3B models. This is a clean, cheap experiment to run.
- No published KLD/perplexity tables for Qwen2.5-Coder 1.5B/3B or Granite 4.1 3B across quant types were retrieved.
- No source confirmed a QAT → GGUF pipeline for these models.

## Building a domain eval set from a developer's own GitHub repos, and existing C# benchmarks

### Takeaway
Combine three things:
1. Public C# benchmarks for comparability: MultiPL-E C# (HumanEval/MBPP translated) and CrossCodeEval, which includes C# with cross-file context.
2. A private repo-derived set: FIM span masking plus unit-test-checked function regeneration.
3. Commit-time-based splits to limit contamination.

There is no public C# FIM benchmark built from private repos, so the user must build one.

### Cited Findings
- MultiPL-E C# pass@1 reported in the Qwen2.5-Coder tech report: Qwen2.5-Coder-1.5B-Instruct 64.6 and Qwen2.5-Coder-3B-Instruct 78.5. OpenCoder reproduces the 1.5B figure (64.6) using Qwen's evaluation code. — [Qwen2.5-Coder Technical Report, arXiv 2409.12186](https://arxiv.org/pdf/2409.12186); [OpenCoder, arXiv 2411.04905](https://arxiv.org/pdf/2411.04905)
- CrossCodeEval covers Python, Java, TypeScript and C# with cross-file dependencies from GitHub repos. — [DevBench, arXiv 2601.11895](https://arxiv.org/html/2601.11895v1)
- Benchmarks built from public sources are prone to training-data contamination and overfitting. — [DevBench, arXiv 2601.11895](https://arxiv.org/html/2601.11895v1)
- The Long Code Arena-based context-collection challenge uses commit histories to split the repository snapshot from the file being completed. It evaluates FIM and has public and private subsets (Python and Kotlin, not C#). — [arXiv 2510.04349](https://arxiv.org/pdf/2510.04349)
- RepoMasterEval masks snippets and predicts them from prefix, suffix and BM25-retrieved repository context, then checks with tests. It is Python/TypeScript only. — [RepoMasterEval (alphaXiv)](https://alphaxiv.org/benchmarks/bytedance/repomastereval)
- Safurai-Csharp is prior work on synthetic-data C#-specific code LLMs, a reference point for C# data generation. — [arXiv 2311.03243](https://arxiv.org/pdf/2311.03243)
- Models perform poorly on private repositories because private APIs and cross-module dependencies are absent from pretraining data. This is the motivation for a personal eval. — [Repoformer ICML 2024 slides](https://icml.cc/media/icml-2024/Slides/33155.pdf)

### Inferences
- **Recommended eval design (methods, not sourced results):**
  - **FIM:** mask method bodies or statement spans in the user's C# files. Score by exact match, edit similarity, and, most importantly, whether `dotnet build` passes and the existing tests pass after insertion.
  - **"Match or beat my code":** regenerate a method from its signature, XML doc and surrounding file. Pass if the project's unit tests pass. Rank "beat" via secondary metrics such as Roslyn analyzer warnings or cyclomatic complexity, or via a pairwise LLM judge. The judge should be treated as noisy.
  - **Contamination:**
    - Public GitHub repos may already be in Qwen's or Granite's pretraining data, which has cutoffs around 2024–2025.
    - Prefer code committed after the base model's data cutoff, or private repos.
    - Never put eval files into SFT, imatrix, or distillation-prompt data.
    - Split by commit date, as in the Long Code Arena method.
  - **Knowledge Q&A** (networking, system design, security): build a small rubric-graded set (50–200 questions). Accept that grading is subjective, and use a larger local model as judge, with spot checks.
- **Coverage gap in public benchmarks:** they test generic C# algorithms, not ASP.NET Core. Report both, because a domain fine-tune can raise repo scores while lowering MultiPL-E, which is the forgetting signal.
- **Benchmark harness:** MultiPL-E C# needs the .NET SDK in the eval sandbox. It runs on CPU, so Kaggle or a local machine is fine.

### Gaps
- Base-model (not instruct) MultiPL-E C# scores for Qwen2.5-Coder 1.5B/3B, and any C# scores for Granite 4.1 3B, were not found. They must be measured with the same harness.
- No existing tool for auto-generating test-verified C# FIM tasks from a repo was found.
- No public "HumanEval-C#" separate from MultiPL-E's translation was confirmed in this search.

## Distilling from a larger open teacher (Qwen2.5-Coder 14B/32B) to generate .NET training data for free on Kaggle

### Takeaway
Use Qwen2.5-Coder-14B-Instruct (Apache 2.0) as the teacher, as an AWQ 4-bit build (~4 GB of weights) under vLLM on Kaggle's T4s. Generate instruction/response pairs and FIM targets for .NET, filtering them by compilation and tests. This is sequence-level distillation (SFT on teacher outputs), which fits $0.

32B AWQ is likely only feasible on 2×T4 with tensor parallelism and short contexts, and is unverified. Watch licenses: the 3B student and any Qwen-Research artifacts carry restrictions.

### Cited Findings
- **Licenses:**
  - Qwen2.5-Coder 0.5B, 1.5B, 7B, 14B and 32B are Apache 2.0.
  - Qwen2.5-Coder-3B (base and instruct) is under the Qwen Research license.

  — [Qwen2.5-Coder family blog](https://qwenlm.github.io/blog/qwen2.5-coder-family); [Qwen2.5-Coder-3B model card](https://huggingface.co/Qwen/Qwen2.5-Coder-3B)
- The Qwen2.5-Coder-14B-Instruct-AWQ checkpoint is Apache 2.0, and AWQ reduces its weights to about 3.7 GB. — [dev.co listing of Qwen2.5-Coder-14B-Instruct-AWQ](https://dev.co/ai/llms/qwen2-5-coder-14b-instruct-awq) (secondary source)
- Qwen's official speed benchmark shows Qwen2.5-14B-Instruct AWQ under vLLM at about 92.7 tok/s on one GPU with short input, about 59 tok/s at 30k input. The GPU is not named in the excerpt and it is not the Coder variant. — [Qwen speed benchmark](https://qwen.readthedocs.io/en/v2.5/benchmark/speed_benchmark.html)
- On T4 (SM75), vLLM falls back to the Triton attention backend because FlashAttention 2 is unavailable. Validated dual-T4 coverage in the kaggle-vllm package is narrow (Qwen2.5-3B FP16 at TP=2). — [kaggle-vllm on PyPI](https://pypi.org/project/kaggle-vllm/)
- No direct, citable benchmark exists for Qwen2.5-Coder-14B AWQ throughput on Kaggle 2×T4. — [markaicode note](https://markaicode.com/benchmarks/vllm-qwen-25-a100-80gb-throughput-benchmark/); [kaggle-vllm](https://pypi.org/project/kaggle-vllm/)
- Minitron found that adapting (correcting) the teacher to the distillation data distribution matters when the original pretraining data is unavailable. — [arXiv 2408.11796](https://arxiv.org/html/2408.11796)

### Inferences
- **Clean pipeline:**
  1. Teacher: Qwen2.5-Coder-14B-Instruct-AWQ (Apache 2.0) on Kaggle.
  2. Student: Qwen2.5-Coder-1.5B (Apache 2.0) or Granite 4.1 3B (check Granite's license; Granite models are generally Apache 2.0, but this was not verified here).
  3. Avoid Qwen2.5-Coder-3B if the result may be redistributed commercially, until the Qwen Research license text is reviewed.
- **Throughput estimate (unmeasured):** with batching on vLLM, aggregate throughput far exceeds single-stream speed. Even at a conservative few hundred aggregate tok/s on a T4, Kaggle's weekly GPU quota could yield millions of tokens, roughly thousands to tens of thousands of examples. Measure this in the first session.
- **Quality filter (essential):** keep only samples whose C# compiles (`dotnet build` in a CPU sandbox) and, where tests exist, passes them. Self-instruct seeded from the user's repos and Microsoft Learn topics (ASP.NET Core, EF Core, auth, middleware, networking) gives domain coverage.
- **Logit-level KD:** logit KD (Minitron-style) from a 14B AWQ teacher to a 1.5B student on one T4 is likely memory-infeasible online. A workaround is to precompute top-k teacher logits offline and store them. Qwen2.5 teacher and student share a tokenizer, which makes this possible, but Granite does not share it, which rules out logit KD from Qwen to Granite.

### Gaps
- No measured Kaggle T4 throughput for 14B or 32B AWQ under vLLM.
- Whether vLLM's AWQ kernels on SM75 are performant, or whether to use llama.cpp or Transformers for generation instead, was not verified.
- The Qwen Research license terms (commercial use, use of outputs) were not retrieved, and neither was Granite 4.1's license.
- No published result for "distill .NET data from 14B into 1.5B" with measured C# gains. The closest prior work is Safurai-Csharp ([arXiv 2311.03243](https://arxiv.org/pdf/2311.03243)), whose numbers were not retrieved.
