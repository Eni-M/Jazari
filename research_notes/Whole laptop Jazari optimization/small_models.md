# Small open-weight models for code review and security bug finding on a 4-core CPU laptop (state: 30 October 2026)

Evidence tags used below: **[V]** vendor-reported, **[I]** independent third party, **[R]** read directly from Hugging Face repo metadata during this research (file sizes in decimal GB, tokenizer blob hashes), **[E]** my estimate, scaled from Jazari's measurement. Benchmarks from different vendors are not comparable (see the LiveCodeBench conflict in the "2026 releases" section).

## Ranked shortlist for the three jobs

### Takeaway
Replace nothing blindly, but test **Granite 4.2 3B** as the chat model first. It has the same dense architecture and file size as the current model, plus a thinking switch. **Mellum2.1 12B-A2.5B** is the quality pick for batch, and **Granite 4.2 3B with thinking** is the one that can finish a large project in a single night. For a draft model, **Granite 4.0 1B or 350M** have a tokenizer file byte-identical to Granite 4.1 3B. There is a catch: **moving to Granite 4.2 loses that draft option**, because the 4.2 tokenizer is not compatible with 4.1 and no 4.2 model is smaller than 3B.

### Cited Findings

**(a) Interactive chat, 3 to 5 GB RAM** (speeds are [E], from the method in the speed section)

| Rank | Model | GGUF source and file | Size [R] | License | Est. prompt / answer tok/s | Est. RAM 4k to 16k ctx | Thinking | Evidence |
|---|---|---|---|---|---|---|---|---|
| 1 | Granite 4.2 3B | [ibm-granite/granite-4.2-3b-GGUF](https://huggingface.co/ibm-granite/granite-4.2-3b-GGUF) `granite-4.2-3b-Q4_K_M.gguf` (also [bartowski](https://huggingface.co/bartowski/granite-4.2-3b-GGUF), 2.32 GB) | 2.24 GB | Apache 2.0 | 27 / 5.8 | 3.9 to 4.9 GB | On by default; off and low-effort modes | LCB v6 69.71 [V] ([card](https://huggingface.co/ibm-granite/granite-4.2-3b)); no independent test found |
| 2 | Spark-X2.6-4B | [abenzerps/Spark-X2.6-4B-Quantized](https://huggingface.co/abenzerps/Spark-X2.6-4B-Quantized) Q4_K_M, community; or [XHToken/Spark-X2.6-4B-GGUF](https://huggingface.co/XHToken/Spark-X2.6-4B-GGUF) Q4_K_M | 2.60 GB (community); 3.54 GB (XHToken, oddly large) | Apache 2.0 | 23 / 5.0 | 4.3 to 5.2 GB | On by default; none, low, high | SWE Verified 62.8, BFCL-V4 71.8 [V] ([summary](https://mer.vin/news/spark-x2-6-4b-4b-on-device-agent-model-benchmarks-and-local-setup/)); released 21 October, no independent test |
| 3 | Granite 4.1 3B (current) | [ibm-granite/granite-4.1-3b-GGUF](https://huggingface.co/ibm-granite/granite-4.1-3b-GGUF) Q4_K_M | 2.10 GB | Apache 2.0 | 28.8 / 6.2 measured | 3.8 to 4.7 GB measured | None | HE 81.7, MBPP 71.2 [V]; native FIM listed in the earlier report |
| 4 | Qwen3-4B-Instruct-2507 | [unsloth/Qwen3-4B-Instruct-2507-GGUF](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF) Q4_K_M | 2.50 GB | Apache 2.0 | 24 / 5.2 | 4.2 to 5.1 GB | Non-thinking | LCB v6 35.1 [V], from the earlier report |
| 5 | Qwen3.5-4B | [unsloth/Qwen3.5-4B-GGUF](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF) Q4_K_M | 2.74 GB | Apache 2.0 | 22 or lower / 4.7 | 4.4 to 5.3 GB | On by default | LCB v6 55.8 [V]; demoted because of the prompt-cache issue below |

**(b) Overnight batch reviewer, up to about 10 GB RAM**

| Rank | Model | GGUF source and file | Size [R] | License | Est. prompt / answer tok/s | Est. RAM | Evidence |
|---|---|---|---|---|---|---|---|
| 1 (quality) | Mellum2.1 12B-A2.5B Thinking | [JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF) Q4_K_M | 8.07 GB (MXFP4_MOE 7.03 GB) | Apache 2.0 | 15 to 30 / 3.5 to 7.7, low confidence | 9.5 to 10.5 GB, at the limit | LCB v6 82.0, SWE Verified 47.0, SWE Pro 28.0 [V] ([card](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking)) |
| 1 (finishes the job) | Granite 4.2 3B, thinking on or low-effort | As above; Q5_K_M 2.61 GB or Q6_K 3.01 GB also fit | 2.24 GB | Apache 2.0 | 27 / 5.8 | 4 to 5 GB | LCB v6 69.71 [V]; no agentic RL stage at this size ([guide](https://intuitionlabs.ai/articles/ibm-granite-4-2-model-guide)) |
| 3 | Granite 4.2 8B | [ibm-granite/granite-4.2-8b-GGUF](https://huggingface.co/ibm-granite/granite-4.2-8b-GGUF) Q4_K_M (Q5_K_M 6.25, Q6_K 7.22, Q8_0 9.35) | 5.35 GB | Apache 2.0 | 11 / 2.4 | 7 to 8 GB | LCB v6 73.24, SWE Verified 47.67, SWE Multilingual 30.78 [V] ([card](https://huggingface.co/ibm-granite/granite-4.2-8b)) |
| 4 | Qwen3.5-9B | [unsloth/Qwen3.5-9B-GGUF](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF) Q4_K_M | 5.68 GB | Apache 2.0 | 10 or lower / 2.3 | 7.5 to 8.5 GB | LCB v6 65.6 [V] on its [own card](https://huggingface.co/Qwen/Qwen3.5-9B), 75.4 on [JetBrains' table](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking) |
| 5 | VulnLLM-R-7B (security specialist, second pass) | [mradermacher/VulnLLM-R-7B-GGUF](https://huggingface.co/mradermacher/VulnLLM-R-7B-GGUF) Q4_K_M, community | 4.68 GB | Apache 2.0 ([card](https://huggingface.co/Virtue-AI-HUB/VulnLLM-R-7B/blob/main/README.md)) | 13 / 2.8 | 6.5 to 7.5 GB | Authors report best results on PrimeVul, Juliet, ARVO; not C# |

Watch list for batch: Gemma 4 12B ([unsloth/gemma-4-12b-it-GGUF](https://huggingface.co/unsloth/gemma-4-12b-it-GGUF) Q4_K_M 7.12 GB, Apache 2.0), and K2 Horizon 7B ([IFM/K2-Horizon-7B-GGUF](https://huggingface.co/IFM/K2-Horizon-7B-GGUF) Q4_K_M 5.59 GB, [Apache 2.0, fully open](https://aiweekly.co/alerts/mbzuais-ifm-ships-k2-horizon-six-fully-open-models-from-09b-to-375b-parameters)).

**(c) Tiny draft model, under 1.5 GB**

| Rank | Model | GGUF source | Size [R] | Works with | Notes |
|---|---|---|---|---|---|
| 1 | Granite 4.0 1B | [ibm-granite/granite-4.0-1b-GGUF](https://huggingface.co/ibm-granite/granite-4.0-1b-GGUF) Q4_K_M (Q8_0 1.74 GB is over the limit) | 1.02 GB | Granite 4.1 3B and 8B | Identical tokenizer blob to 4.1 3B [R]; license not re-read this session (Granite family is Apache 2.0) |
| 2 | Granite 4.0 350M | [ibm-granite/granite-4.0-350m-GGUF](https://huggingface.co/ibm-granite/granite-4.0-350m-GGUF) Q8_0 0.38 GB, Q4_K_M 0.24 GB | 0.38 GB | Same | Cheapest; low acceptance likely, so test it |
| 3 | Qwen3.5-0.8B | [unsloth/Qwen3.5-0.8B-GGUF](https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF) Q8_0 | 0.81 GB | Qwen3.5-4B, 9B | Tokenizer blob identical [R]; hybrid-memory risk |
| 4 | Qwen2.5-Coder-0.5B | [Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF) Q8_0 | 0.68 GB | Qwen2.5-Coder 1.5B, 7B | Tokenizer blob identical [R]; the pair with the only CPU speed-up measurement found |

### Inferences
- Granite 4.2 3B should run at nearly the current speed because it is the same dense architecture at 7% larger size. Its LCB of 69.71 needs discounting because that score is presumably with thinking on, and thinking costs hundreds of tokens at under 6 tok/s. Test it in non-thinking and low-effort modes for chat.
- Ranks 1 and 2 in (a) are close, and the order rests on architecture certainty, not on quality. Spark's vendor SWE scores are far above anything else at its size, which calls for suspicion until someone reproduces them.
- If you want draft-model speculation in chat, stay on Granite 4.1 3B. If you want reasoning, go to 4.2 and use `ngram-mod` only. You cannot have both today.

### Gaps
- No 2026 small model has an independent code review or security score, so this ranking relies on vendor numbers and architecture.
- No model on this list has been run on this laptop except Granite 4.1 3B.

## What small models were released in 2026, and what changed since the earlier report?

### Takeaway
Four things changed. Reasoning arrived at 3B (Granite 4.2), a new 4B contender appeared (Spark-X2.6), Mellum2.1 now has official GGUFs, and Qwen3.5 runs in llama.cpp but has a prompt-cache problem. Qwen's newest open models (3.6, 3.8) start at 27B, so nothing small came after 3.5.

### Cited Findings

| Model | Released | Params | License | GGUF | llama.cpp | Thinking | Tool calling |
|---|---|---|---|---|---|---|---|
| Granite 4.2 3B / 8B / 30B | [25 Aug 2026](https://huggingface.co/ibm-granite/granite-4.2-3b) | 3B (Safetensors page says 4B), 8B (9B), 30B; dense | Apache 2.0 | Official IBM repos [R] | bartowski quants [built with b10603](https://huggingface.co/bartowski/granite-4.2-3b-GGUF) | Full, off, low-effort | Yes, `qwen3_coder` parser in vLLM; [reasons before calls](https://intuitionlabs.ai/articles/ibm-granite-4-2-model-guide) |
| Granite 4.1 3B / 8B / 30B | Earlier 2026 | Dense | Apache 2.0 | [unsloth](https://unsloth.ai/docs/models/ibm-granite-4.1), IBM | Works, measured here | None listed | [Function calling listed](https://unsloth.ai/docs/models/ibm-granite-4.1) |
| Spark-X2.6 1.7B / 4B | [21 Oct 2026](https://aiweekly.co/alerts/sparkllm-ships-spark-x26-on-device-models-in-17b-and-4b) | 1.7B, 4B | Apache 2.0 | XHToken and community [R] | [Needs b10828+](https://huggingface.co/XHToken/Spark-X2.6-4B) | None, low, high; on by default | `spark25` parser in SGLang; llama.cpp format unstated |
| Mellum2.1 12B-A2.5B | [8 Oct 2026](https://tech-insider.org/?p=42696) | 12B total, 2.5B active, MoE | Apache 2.0 | [Official](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF) | Standard quants load on older builds; combined MTP files need b11747+ | Reasoning model; off switch not stated | `hermes` parser in vLLM; [FIM not mentioned](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking) |
| Qwen3.5 0.8B / 2B / 4B / 9B | Early 2026 | 9B card says 9B, Hub 10B; hybrid Gated DeltaNet | Apache 2.0 | unsloth [R] | [Fused op in b8233](https://github.com/ggml-org/llama.cpp/issues/20354), SIMD work [followed](https://app.semanticdiff.com/gh/ggml-org/llama.cpp/commit/e30f1fdf74ea9238ff562901aa974c75aab6619b) | On by default | [BFCL-V4 66.1 for 9B](https://huggingface.co/Qwen/Qwen3.5-9B) [V] |
| Qwen3.6, 3.8 | Apr, Aug 2026 | [27B dense, 35B-A3B](https://www.aitraining2u.com/alibaba-qwen3-explained-2026.html); 3.8 27B | 3.6 reported Apache 2.0; 3.8 not checked | [unsloth Qwen3.8-27B](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF) Q4_0 16.06 GB | Too big for this job | Yes | Not checked |
| Gemma 4 E2B / E4B / 12B | 2026 | E4B about 4.5B effective, 8B with embeddings ([model card, via earlier report](https://ai.google.dev/gemma/docs/core/model_card_4)) | Apache 2.0 | [unsloth](https://unsloth.ai/docs/models/gemma-4): E2B Q4_K_M 3.11 GB, E4B 4.98 GB, 12B 7.12 GB [R] | Works | On with `<\|think\|>`; off with `--chat-template-kwargs` on llama-server | Listed as a strength; format undocumented there |
| LFM2.5-2.6B | [4 Aug 2026](https://www.llmreference.com/model/lfm-2.5-2.6b) | [2.69B, convolution and GQA](https://huggingface.co/LiquidAI/LFM2.5-2.6B) | **lfm1.0, not Apache** | [Official](https://huggingface.co/LiquidAI/LFM2.5-2.6B-GGUF) Q4_K_M 1.67 GB | Official GGUF | Always reasons | Pythonic calls; **vendor says not for agentic coding**; LCB v6 59.41 [V] |
| MiniCPM5-1B / 2B | [May](https://www.artificialanalysis.ai/articles/minicpm5-1b-the-leading-1b-open-weights-model), [July 2026](https://www.orcarouter.ai/blog/minicpm5-2b-open-weights-release) | 1B, 2B | Apache 2.0 | [1B](https://huggingface.co/openbmb/MiniCPM5-1B-GGUF) 0.69 GB, [2B](https://huggingface.co/openbmb/MiniCPM5-2B-GGUF) 1.56 GB | Official GGUF | 1B is non-reasoning | Not checked |
| K2 Horizon 7B | [3 Sep 2026](https://www.llmreference.com/model/k2-horizon-7b) | 7B dense | Apache 2.0, fully open | Official [R] | Not checked | Not checked | Not checked |
| Devstral Small 2 | Dec 2025 | 24B | Apache 2.0 | [Q4_K_M 14.33 GB](https://huggingface.co/unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF) [R] | Works, but does not fit in 10 GB | n/a | Strong, [one anecdote](https://github.com/filthyrake/filthy-tool-fixer/releases/tag/v0.3.0) |

- Pre-2026 models still in play: [Phi-4-mini](https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF) Q4_K_M 2.49 GB and [SmolLM3-3B](https://huggingface.co/unsloth/SmolLM3-3B-GGUF) Q4_K_M 1.92 GB. I found no Phi-5 or SmolLM4. [The search](https://www.llmreference.com/best/small) confirmed neither.
- DeepSeek V4 [starts at 284B total](https://www.llmreference.com/best/small); no sub-10B variant was found. No GLM model under 10B was found either.
- **Independent score:** Artificial Analysis gives MiniCPM5-1B 17.9 against 16.3 for Qwen3.5 2B with reasoning [I] ([AA](https://www.artificialanalysis.ai/articles/minicpm5-1b-the-leading-1b-open-weights-model)). Its [small models page](https://artificialanalysis.ai/models/open-source/small) lists Qwen3.5 9B non-reasoning, Gemma 4 12B and Granite 4.2 30B as "not scored" in the fetched view.
- **Qwen3.5 cache problem:** [issue #24714](https://github.com/ggml-org/llama.cpp/issues/24714) reports `llama-server` forcing full prompt re-processing on Qwen3.5-2B and Qwen3.6 with hybrid memory, on CPU-only builds too. Gemma 4 E4B and Qwen3 did not show it. A maintainer called it expected, and it was closed stale on 27 August 2026 with no fix.
- **LiveCodeBench conflict:** Qwen3.5-9B is 65.6 on [Qwen's card](https://huggingface.co/Qwen/Qwen3.5-9B) and 75.4 on [JetBrains' card](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking), so harness differences alone are worth about 10 points.
- JetBrains' own table has Qwen3.5-9B ahead on Terminal-Bench 2.1 (21.7 vs 17.4) [V], and [secondary coverage](https://tech-insider.org/jetbrains-mellum-2-1-open-coding-model-2026/) says it also leads on SWE-bench Verified.
- Quantization cost, from Mellum's card [V]: Q8_0 has KLD 0.008 and 96.1% top-token match, Q4_K_M 0.075 and 88.0% ([GGUF card](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF)).

### Inferences
- Jazari's pinned build 11982 is newer than every requirement above (b10603, b10828, b11747), so no build upgrade is needed for loading. This assumes build numbers are comparable with those `b` tags, which [the log](C:/MyOwnLLM/docs/OPTIMIZATION-LOG.md) supports.
- The Qwen3.5 cache problem hits Jazari directly, because the design depends on stable-prefix prompt caching. It matters much less for batch runs, where each chunk is a new prompt anyway. This is why Qwen3.5-4B fell in the chat ranking despite beating most on LCB.
- Corrections to the earlier report: dual-channel assumption is wrong (see the speed section); Qwen3.5 does run in llama.cpp; Mellum2.1 has official GGUFs; Gemma E4B at 4.98 GB Q4_K_M is outside the chat tier.

### Gaps
- Mellum's thinking off-switch, FIM support and llama.cpp tool-call format are unstated.
- Granite 4.2 cards do not mention FIM. Verify before dropping 4.1 if autocomplete depends on it.
- Whether XHToken is SparkLLM's official organisation was not confirmed. The XHToken Q4_K_M at 3.54 GB versus 2.60 GB for the community build is unexplained. Check its tensor types or quantize it yourself.
- Spark's LiveCodeBench score is in images on the card, so I have no number.

## How poor are small models at finding vulnerabilities, and how do they compare with large ones?

### Takeaway
They are poor, and so are large ones, in absolute terms. On the friendliest benchmark, small models get 20 to 28% precision, so about three or four out of every five findings are false, and they miss roughly half to four-fifths of bugs. **No published study tests any 2026 small model** on CASTLE, SecLLMHolmes, PrimeVul, SecurityEval or BigVul, so everything below is extrapolation from older models.

### Cited Findings

**CASTLE** (250 C micro-benchmarks, 25 CWEs) [I] ([paper](https://arxiv.org/html/2503.09433v1))

| Model | Score | Precision | Recall |
|---|---|---|---|
| GPT-o3 Mini | 977 | 63% | 78% |
| DeepSeek R1 | 956 | 47% | 90% |
| GPT-4o | 954 | 54% | 76% |
| Qwen2.5 Coder Instruct 32B | 708 | 34% | 70% |
| Falcon 3 7B | 521 | 28% | 20% |
| Mistral 7B | 446 | 23% | 41% |
| Gemma 2 9B | 436 | 20% | 40% |
| Llama 3.1 8B | 417 | 20% | 51% |
| *ESBMC, best static/formal tool* | 661 | 62% | 35% |
| *CodeQL* | 634 | 48% | 29% |

Static rows are from [the same paper](https://arxiv.org/pdf/2503.09433). Models under 6B often failed to produce well-structured JSON, and accuracy declines as code grows; in a 400+ line test most LLMs missed the bug and false positives rose ([paper](https://arxiv.org/html/2503.09433v1)).

**Quantized 4-bit models** on C/C++ [I] ([Guessing As A Service](https://ceur-ws.org/Vol-3962/paper14.pdf)): the title gives the verdict.

| Model | Precision | Recall | F1 |
|---|---|---|---|
| Llama 3.1 8B | 0.24 | 0.71 | 0.36 |
| Mistral v0.3 7B | 0.46 | 0.14 | 0.22 |
| CodeQwen1.5 7B | 0.38 | 0.56 | 0.45 |

**SecVulEval** (real C/C++) [I] ([paper](https://arxiv.org/html/2505.19828v1)): function-level F1 is 35 to 47% for 22 to 33B open models, against 52 to 54% for GPT-4.1 and Claude-3.7-Sonnet. Statement-level F1 is 7 to 15% against 22 to 24%. The authors conclude models "are not effective at detecting vulnerable statements."

**PrimeVul** [I]: a 7B model with 68.26% F1 on BigVul dropped to [3.09% on PrimeVul](https://arxiv.org/html/2403.18624v2), which shows how much older datasets flatter results. I saw this in a search excerpt, not the full paper.

**SecLLMHolmes** [I]: all tested models had [high false-positive rates, flagged patched code as vulnerable, and changed answers between runs](https://arxiv.org/pdf/2312.12575).

**Multilingual, seven languages** [I] ([arXiv 2506.07503](https://arxiv.org/pdf/2506.07503)): on a balanced function-level set, zero-shot DeepSeek-Coder, Llama 3 and GPT-4o all sit near 0.50 accuracy, which is coin-flip level. Tuned GPT-4o reached 0.72. Size was "not the decisive factor" at function level, but larger models helped at line level.

**Frontier model, for scale**: Claude Opus 4.6 found [25.1 to 28.5% of CVEs](https://researcher.branch.zeropath.com/blog/benchmarking-opus-4-6-vuln-detection) on paired PrimeVul functions, flagging 36.6 to 52.4% of benign functions. This comes from ZeroPath, a security tooling vendor, on single C functions only.

**Context matters a lot**: [arXiv 2504.13474](https://arxiv.org/pdf/2504.13474) argues earlier results were understated by missing context. With context, DeepSeek-R1 reached 67% accuracy and smaller models about 60%, in a stricter task that also grades the reasoning.

### Inferences
- Do not expect cloud-level results. A realistic target for a 3 to 8B model is a noisy list where a human keeps perhaps one finding in four, and many real bugs go unfound. Report results as "worth checking", not "vulnerabilities".
- Function-at-a-time prompts are the worst case. Feeding the signature map and callers, which Jazari already plans, is likely the largest available quality gain.
- Generation benchmarks like LiveCodeBench say little about review skill. A high LCB does not imply high vulnerability recall.

### Gaps
- No SecurityEval result for small models turned up in my searches.
- No published code review (non-security) benchmark covers these small models.
- [VulnScout-C](https://arxiv.org/pdf/2603.28309) claims a CASTLE score of 1068 and F1 85.4% as a custom C-only classifier. Vendor-style self-report, C only, so not considered.

## Does a thinking model at low speed beat a non-thinking one for batch security review?

### Takeaway
Probably yes, but only modestly, and most of the gain comes early. Budget roughly 300 to 1,000 thinking tokens per chunk. A structured "think, then verify" prompt appears to be worth more than extra reasoning length. Evidence comes from older and larger models.

### Cited Findings
- CASTLE [I]: reasoning models took the top three places, but GPT-4o was only 2 points behind DeepSeek R1 (954 vs 956), and R1 had more false positives than GPT-4o (166 vs 116) ([paper](https://arxiv.org/html/2503.09433v1)).
- [arXiv 2506.07503](https://arxiv.org/pdf/2506.07503) [I]: function-level accuracy was 0.52 for DeepSeek-R1 and 0.53 for QwQ-plus against 0.50 for zero-shot GPT-4o. Line-level F1 was 0.27 and 0.31 against 0.33. The authors conclude reasoning "does not lead to significant improvements."
- [arXiv 2504.13474](https://arxiv.org/pdf/2504.13474) [I]: o3-mini used 500+, 1,000+ and 5,000+ thinking tokens at low, medium and high. Five times more tokens gained under 0.05 accuracy, so the authors call scaling test time "not economical."
- [arXiv 2503.17885](https://arxiv.org/pdf/2503.17885) [I]: structured prompting matters. Think & Verify cut ambiguous answers from 20.3% to 9.1% ([abstract](https://arxiv.org/abs/2503.17885)). DeepSeek-R1 7B averaged 57.19% on vulnerable and 48.95% on patched code, and Codellama 7B 63.70% and 36.32%, so the code model over-flags while reasoning is more balanced. Qwen2.5-coder 7B averaged 36.09% and 54.24%.
- ZeroPath on Opus 4.6 [vendor]: structured reasoning cut functions with spurious findings [from about 60% to about 40%](https://researcher.branch.zeropath.com/blog/benchmarking-opus-4-6-vuln-detection).
- VulnLLM-R [V, academic authors]: the reasoning model beat its own no-reason variant, most on unseen CWEs, while averaging only [362 output tokens](https://arxiv.org/html/2512.07533v1) versus 283 for Claude-3.7-Sonnet. Base Qwen2.5-7B did poorly, which the authors take as proof that [custom training is what generalizes](https://www.sourcepulse.org/projects/24513038). Exact F1 values sit in figures I could not read.

### Inferences
- At an estimated 2.4 to 5.8 tok/s, 1,000 thinking tokens cost 3 to 7 minutes per chunk. Low-effort mode (Granite 4.2) or Spark's "low" budget is the sensible starting point.
- A cheap two-step design fits the evidence: a first pass lists candidates, and a second pass tries to refute each one with its surrounding code. This costs thinking only on suspicious chunks.

### Gaps
- No study tests thinking on versus off within the same small 2026 model on security tasks. A 20-sample test on your own seeded bugs would settle this for the actual models.

## Which model families work best on C#, ASP.NET Core and TypeScript/React?

### Takeaway
No evidence names a best small model for these stacks. Only cloud models have C# security data, and those results are encouraging. Choose by general quality, then test on your own code.

### Cited Findings
- On 10 C# projects with 63 known vulnerabilities [I]: GPT-4.1 F1 0.797, Mistral Large 0.753, DeepSeek V3 0.750, against SnykCode 0.546, CodeQL 0.386 and SonarQube 0.260 ([paper](https://arxiv.org/html/2508.04448)). The set is small (3,000 to 5,500 characters per project), the true-positive criteria were relaxed for LLM locations, and the paper's tables contain inconsistencies.
- Code-specialized models outperformed general ones on C/C++ detection ([abstract](https://arxiv.org/abs/2503.17885)), though Guessing As A Service found [CodeQwen at only 0.38 precision](https://ceur-ws.org/Vol-3962/paper14.pdf).
- SWE-bench Multilingual [V]: Spark-X2.6-4B [64.7, Qwen3.5-4B 27.7](https://mer.vin/news/spark-x2-6-4b-4b-on-device-agent-model-benchmarks-and-local-setup/), Granite 4.2 8B [30.78](https://huggingface.co/ibm-granite/granite-4.2-8b). The cards do not list which languages are included.
- A [.NET generation benchmark](https://arxiv.org/pdf/2608.22529) exists, but it tests only GPT, Gemini, Claude and Grok.
- Community fine-tunes exist, such as [a Qwen3-4B C# model](https://featherless.ai/models/CodeAtCMU/Qwen3-4B-Base_full_sft_CSharp_data_12K) and [a TypeScript 1.5B model](https://featherless.ai/models/sylvester-francis/typescript-slm-1.5b-full), with no published results.

### Inferences
- C# and TypeScript are less exposed than C to memory-safety CWEs, so review findings will be mostly authorization, injection and business-flow issues. These need multi-file context, so context assembly matters more than family choice.
- VulnLLM-R was trained on Python and C/C++ and generalized to Java by its authors' account ([paper](https://arxiv.org/html/2512.07533v1)). C# is untested, so it remains a second-pass experiment.

### Gaps
- I found no ASP.NET Core or React benchmark for any model of this size, and no LocalLLaMA comparison.

## Expected tok/s and RAM on this laptop

### Takeaway
Answer speed scales from about 13 GB/s of effective memory bandwidth, so 8B dense models run at about 2.4 tok/s, and their prompt reading at about 11 tok/s. **A dense 7 to 9B model cannot read EduCore in one night.** Only 3B-class models, and possibly Mellum, leave time to answer. Plan resumable, multi-night runs for anything larger.

### Cited Findings
- Baseline: Granite 4.1 3B Q4_K_M, `-t 6`, 28.8 prompt and 6.2 answer tok/s; single-channel 16 GB at 2400; measured 12 to 13 GB/s ([log](C:/MyOwnLLM/docs/OPTIMIZATION-LOG.md)).
- RAM: 3.8 GB at 4k context and 4.7 GB at 16k for a 2.10 GB file ([design doc](C:/MyOwnLLM/docs/superpowers/specs/2026-10-30-myownllm-design.md)), so overhead is about 1.7 to 2.6 GB.
- Cross-check: a dual-channel i7-8550U did [13.4 prompt and 5.6 answer tok/s on a 7B](https://www.mgroeber.de/benchmarking-llamacpp-on-legacy-hardware-2.html) with 4 threads. My method predicts 12.9 prompt on this laptop and half the answer speed, which fits single channel.
- Scattered datapoints, method unstated: [Qwen3.5 4B about 15 tok/s on i5-12400 with DDR4-3200](https://bestllmfor.com/guide/llm-local-cpu-sans-gpu/), [Granite 4 3B 8.5 to 9 and Gemma 4 E2B 9.9 tok/s on a CPU laptop](https://itsfoss.com/testing-local-llms-without-gpu).
- Memory guidance for Gemma 4 from [Unsloth](https://unsloth.ai/docs/models/gemma-4): E2B 4 GB, E4B 5.5 to 6 GB, 12B 7 to 8 GB at 4-bit.

Estimates [E]: answer tok/s = 13.0 / file GB; prompt tok/s = 28.8 × 2.10 / file GB; RAM = file + 1.7 to 2.6 GB, more for larger models.

| Model (Q4_K_M) | File GB | Prompt tok/s | Answer tok/s | RAM GB (4k to 16k) |
|---|---|---|---|---|
| Qwen2.5-Coder-1.5B | 0.99 | 61 | 13 | 2.5 to 3.5 |
| MiniCPM5-2B | 1.56 | 39 | 8.3 | 3.2 to 4.1 |
| LFM2.5-2.6B | 1.67 | 36 | 7.8 | 3.3 to 4.2 |
| SmolLM3-3B | 1.92 | 31 | 6.8 | 3.6 to 4.5 |
| **Granite 4.1 3B (measured)** | 2.10 | **28.8** | **6.2** | **3.8 to 4.7** |
| Granite 4.2 3B | 2.24 | 27 | 5.8 | 3.9 to 4.9 |
| Phi-4-mini | 2.49 | 24 | 5.2 | 4.2 to 5.1 |
| Qwen3-4B-2507 | 2.50 | 24 | 5.2 | 4.2 to 5.1 |
| Spark-X2.6-4B | 2.60 | 23 | 5.0 | 4.3 to 5.2 |
| Qwen3.5-4B | 2.74 | 22, likely lower | 4.7 | 4.4 to 5.3 |
| VulnLLM-R-7B / Qwen2.5-Coder-7B | 4.68 | 13 | 2.8 | 6.5 to 7.5 |
| Gemma 4 E4B | 4.98 | 12 to 19 | 2.6 to 3.5 | 6.7 to 7.7 |
| Granite 4.2 8B | 5.35 | 11 | 2.4 | 7 to 8 |
| K2 Horizon 7B | 5.59 | 11 | 2.3 | 7.3 to 8.3 |
| Qwen3.5-9B | 5.68 | 10.6 or lower | 2.3 | 7.5 to 8.5 |
| Gemma 4 12B | 7.12 | 8.5 | 1.8 | 8.8 to 10 |
| Mellum2.1 (MoE) | 8.07 | 15 to 30 | 3.5 to 7.7 | 9.5 to 10.5 |

Mellum's upper figures assume 2.5/12 of the weights are touched per token. Real speed will be lower because experts are scattered in memory.

**One-night budget for EduCore's hand-written C#** (about 350k tokens, 300 files, [log](C:/MyOwnLLM/docs/OPTIMIZATION-LOG.md)), 8 hours, each token read once:

| Model | Reading time | Left for answers | Answer tokens per file |
|---|---|---|---|
| Granite 4.2 3B | 3.6 h | 4.4 h | about 300 |
| Mellum2.1, mid estimate (20 / 5) | 4.9 h | 3.1 h | about 185 |
| Mellum2.1, best estimate (30 / 7.7) | 3.2 h | 4.8 h | about 440 |
| VulnLLM-R-7B | 7.5 h | 0.5 h | about 30 |
| Granite 4.2 8B | 8.6 h | none | 0 |

### Inferences
- About 300 tokens per file is enough only with short or no reasoning, so per-file thinking at full budget does not fit in one night, even at 3B.
- Dense 8B needs about two nights for that project. Run it on a filtered high-risk subset (auth, controllers, payments) or resumably across nights.
- Higher quants hurt on this bus. Granite 4.2 8B Q8_0 at 9.35 GB would run about 1.4 tok/s by this method, so stay at Q4_K_M or Q5_K_M unless a test shows a quality gain.
- MoE is the only way to get a large model at a tolerable speed. This makes Mellum the most important single measurement.

### Gaps
- Sustained throttled speed is unmeasured. All estimates assume plugged-in short runs.
- None of the 2026 models has a verified 4-core DDR4 `llama-bench` result in my sources.
- Whether Mellum at 9.5 GB+ stays usable alongside the OS on 16 GB needs a real run. A second RAM stick, which would double bandwidth, is hardware outside this scope but would change every row.

## Is there a tokenizer-compatible draft model for Granite?

### Takeaway
Yes for Granite 4.1: **Granite 4.0 1B and 350M** match exactly. No for Granite 4.2, because IBM changed the special tokens and shipped nothing smaller than 3B. Speculation gives roughly no gain on chat-like text, so measure it on review output.

### Cited Findings
- llama.cpp's [`common_speculative_are_compatible`](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/speculative.cpp) requires the same vocab type, matching BOS/EOS settings, vocab sizes within 128, and identical token text from ID 5 up. It does not translate tokens, and the draft-simple constructor throws on failure.
- [R] `tokenizer.json` blob hash `d7e1714703eb`, with `tokenizer_config.json` `7a6b382740c0`, is shared by [granite-4.1-3b](https://huggingface.co/ibm-granite/granite-4.1-3b), [granite-4.0-micro](https://huggingface.co/ibm-granite/granite-4.0-micro), [granite-4.0-h-micro](https://huggingface.co/ibm-granite/granite-4.0-h-micro), [granite-4.0-1b](https://huggingface.co/ibm-granite/granite-4.0-1b), [granite-4.0-350m](https://huggingface.co/ibm-granite/granite-4.0-350m). The h-1b and h-350m hybrids share it too. Vocab size is 100352 and BOS/EOS IDs match in all of those I checked.
- [R] granite-4.1-8b has a different hash, but a parsed comparison shows identical vocab, added tokens and other keys, so it is compatible.
- [R] **Granite 4.2 is not compatible with 4.1.** Vocab size is the same, but token text differs from ID 100256: `<|pad|>` became `<|im_start|>`, `<|end_of_text|>` became `<|im_end|>`, `<think_on>` became `[INST]`, and `<|start_of_role|>` became `<|unused_11|>`. Its BOS is 100283. Granite 4.2 3B and 8B match each other (`109016081083`).
- [R] Other compatible families: Qwen3.5 0.8B, 2B, 4B and 9B share `5f9e4d4901a9`; Spark-X2.6 1.7B and 4B share `710cce15cf35`; Qwen2.5-Coder 0.5B, 1.5B and 7B share `443909a61d42`. VulnLLM-R-7B's tokenizer (`9c5ae00e602b`) differs from Qwen2.5's, so assume it is not compatible until tested.
- Flags: `-md`/`--spec-draft-model`, `--spec-draft-n-max` (default 3), `--spec-draft-device`, `--spec-default` enables `ngram-mod`; draftless takes precedence over a draft model ([docs](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).
- One CPU measurement: Qwen2.5-Coder 3B, 12.9 tok/s baseline, went to [1.72× overall, 1.96× on code, 1.74× on reasoning, 1.01× on chat](https://inventivehq.com/blog/llama-cpp-speculative-decoding-consumer-gpu), with a Qwen2.5-Coder-0.5B draft. It is a single test.
- MTP is built into some models but [only Qwen3.6 was listed](https://victor-mtp-on-hf-endpoints.static.hf.space/index.html) on that comparison, with +5% decode at the cost of 22% slower prefill on a GPU. JetBrains shows [about 1.05× on an M2 Max](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF), while its model card says the [head is "coming soon"](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking) for vLLM and a [tutorial cites a 1.6× claim](https://tech-insider.org/?p=42696). These conflict.
- MTP and `--cache-reuse` [are silently disabled with `nParallel > 1`](https://victor-mtp-on-hf-endpoints.static.hf.space/index.html).

### Inferences
- Speculation can work on this CPU because batched evaluation (28.8 tok/s) is 4.6 times faster than single-token decoding (6.2 tok/s), so checking a run of drafted tokens is cheap if most are accepted.
- Review prose is new text, so expect a result nearer the chat figure than the code one. Code-rewrite and "output the fixed file" tasks should gain the most. `ngram-mod` is free to try for those.
- MX150 placement: Granite 4.0 350M Q8_0 (0.38 GB) or 1B Q4_K_M (1.02 GB) fit in 2 GB alongside context. This avoids competing for the single memory channel. It needs a GPU-enabled llama.cpp build with `-dev none` for the main model and `--spec-draft-device` for the draft, which is more setup than the current CPU build.
- Suggested first test: `--spec-type draft-simple -md granite-4.0-350m-Q8_0.gguf --spec-draft-n-max 8`, comparing against the baseline on the same prompts. Note the earlier-found page used `--spec-type draft`; [upstream docs](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md) list `draft-simple`, so follow `llama-server --help` on the pinned build.

### Gaps
- GGUF-embedded token metadata was not compared, only source tokenizer files. A one-line load test will confirm it.
- Qwen3.5's recurrent hybrid memory may also limit draft rollback. I found no statement either way, and issue #24714 suggests caution.
- No measured acceptance rate exists for any Granite pair.
