# Licenses of small open-weight coding LLMs: fine-tuning, rebranding, and serving via your own OpenAI-compatible API

Not legal advice. Researched 2026-10-29. Always re-check the LICENSE file of the exact checkpoint (license can differ by size within one family).

## Q1. Per-family license terms (fine-tune, rename, attribution, hosted API, user thresholds, AUP, outputs)

### Takeaway
Apache 2.0 / MIT families (Qwen2.5-Coder except 3B, Qwen3/Qwen3-Coder, Gemma 4, Phi-4, Granite 4.0, Devstral Small 2) allow fine-tuning, renaming, and hosted commercial API use with only notice/attribution-file obligations. Llama (name must start with "Llama" + "Built with Llama"), Gemma 1-3 (Gemma ToU, API = distribution, flow-down of prohibited uses), DeepSeek-Coder/V2-Lite and StarCoder2 (RAIL-style use restrictions that must be flowed down to API users) are usable but carry strings. Qwen2.5-Coder-3B (Qwen Research License) and Codestral 22B (Mistral Non-Production License) are not usable for a commercial/public API without a separate license.

### Cited Findings

**Qwen2.5-Coder**
- Sizes 0.5B / 1.5B / 7B / 14B / 32B are Apache 2.0; 3B is under the Qwen Research license — [Qwen blog](https://qwenlm.github.io/blog/qwen2.5-coder-family); [HF model card 3B](https://huggingface.co/Qwen/Qwen2.5-Coder-3B)
- Qwen RESEARCH LICENSE (3B): use "FOR NON-COMMERCIAL PURPOSES ONLY"; commercial use requires requesting a license; non-commercial = research or evaluation — [HF LICENSE, Qwen2.5-Coder-3B-Instruct](https://huggingface.co/Qwen/Qwen2.5-Coder-3B-Instruct/blob/main/LICENSE)
- Same license: modified files need prominent change notices (3b); must retain notice "Qwen is licensed under the Qwen RESEARCH LICENSE AGREEMENT, Copyright (c) Alibaba Cloud" (3c); if Materials or their outputs are used to create/train/fine-tune a distributed or publicly available model, must "prominently display 'Built with Qwen' or 'Improved using Qwen'" in product docs (4b); no trademark license (5b); you own your derivatives if compliant (5a); governed by Chinese law — [same](https://huggingface.co/Qwen/Qwen2.5-Coder-3B-Instruct/blob/main/LICENSE)

**Qwen3 / Qwen3-Coder / later Qwen**
- Qwen3-Coder-30B-A3B-Instruct (30.5B total / 3.3B active MoE, 256K context) is Apache 2.0, ungated, commercially deployable — [dev.co](https://dev.co/ai/llms/qwen3-coder-30b-a3b-instruct); [morphllm](https://www.morphllm.com/qwen3-coder); [featherless](https://featherless.ai/models/Qwen/Qwen3-Coder-30B-A3B-Instruct) (secondary sources; release dates disagree among them)
- Qwen3.5-27B listed as Apache 2.0 (created 2026-02-24); Qwen3.6-35B-A3B reported released 2026-04-16 — [SiliconFlow compare](https://www.siliconflow.com/zh/models/compare/qwen3-coder-30b-a3b-instruct-vs-qwen3-5-27b); [vantaige.io](https://vantaige.io/ai-tool/qwen) (secondary)

**DeepSeek-Coder / DeepSeek-Coder-V2-Lite (DeepSeek Model License, OpenRAIL-style)**
- Perpetual, worldwide, royalty-free, irrevocable copyright license for the Model and derivatives (s.2); commercial use not prohibited, limited only by Attachment A restrictions (s.5) — [DeepSeek LICENSE-MODEL](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
- Explicitly allows hosting "for Third Party remote access purposes" (SaaS) subject to conditions (s.4) — [same](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
- Conditions: use-based restrictions "MUST be included as an enforceable provision" in agreements governing further use/distribution (4a); give recipients a copy of the License (4b); modified files must carry change notices (4c) — [same](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
- Attachment A bans: unlawful use, military use "in any way", harm to minors, harmful disinformation, fully automated decisions adversely affecting legal rights, discrimination — [same](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
- "DeepSeek claims no rights in the Output" (s.6); no trademark rights (s.8); DeepSeek may "restrict (remotely or otherwise) usage" violating the License (s.7). No naming requirement found — [same](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)

**Microsoft Phi-4 family**
- MIT License, copyright Microsoft; only condition: copyright + permission notice "shall be included in all copies or substantial portions" — [HF microsoft/phi-4 LICENSE](https://huggingface.co/microsoft/phi-4/blob/main/LICENSE)

**Google Gemma 1/2/3 (Gemma Terms of Use, last modified 2026-04-01)**
- Model Derivatives include modifications, works based on Gemma, and models trained on Gemma outputs (e.g., synthetic data / distillation) — [Gemma Terms](https://ai.google.dev/gemma/terms)
- "Distribution" explicitly includes making Gemma or its functionality available "as a hosted service via API, web access" — [same](https://ai.google.dev/gemma/terms)
- On distribution: include use restrictions "as an enforceable provision in any agreement"; give all third-party recipients a copy of the Agreement; non-hosted distributions need a NOTICE: "Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms" — [same](https://ai.google.dev/gemma/terms)
- Gemma Prohibited Use Policy is incorporated; Google "claims no rights in Outputs"; no rights to Google trademarks and no implying endorsement; Google may "restrict (remotely or otherwise) usage" — [same](https://ai.google.dev/gemma/terms)
- No naming requirement (derivative need not be called "Gemma"); no user-count threshold — [same](https://ai.google.dev/gemma/terms)

**Google Gemma 4 (Apr 2026)**
- "the first in the Gemmaverse to be released under the OSI-approved Apache 2.0 license", sizes from edge to 31B; post dated 2026-04-02 — [Google Open Source Blog](https://opensource.googleblog.com/2026/03/gemma-4-expanding-the-gemmaverse-with-apache-20.html)
- Gemma ToU page itself states Gemma 4 is governed by a separate Apache 2.0-based license — [Gemma Terms](https://ai.google.dev/gemma/terms)
- Press: replaces the custom Gemma ToU for this generation — [The Register](https://www.theregister.com/2026/04/02/googles_gemma_4_open_weights/); [noze.it](https://noze.it/en/insights/gemma-nuova-release)

**Meta Llama 3.1 (Llama 3.1 Community License) — same structure in Llama 4**
- Distributing Llama Materials or derivatives: provide a copy of the Agreement and "prominently display 'Built with Llama'" on a related website/UI/docs (1.b.i) — [Llama 3.1 license](https://dev.meta.ai/llama/llama3_1/license)
- If Llama Materials "or any outputs or results" are used to create/train/fine-tune/improve an AI model that is distributed or made available, you must "include 'Llama' at the beginning of any such AI model name" (1.b.i) — [same](https://dev.meta.ai/llama/llama3_1/license)
- Notice: "Llama 3.1 is licensed under the Llama 3.1 Community License, Copyright © Meta Platforms, Inc. All Rights Reserved." (1.b.iii); Acceptable Use Policy incorporated by reference (1.b.iv) — [same](https://dev.meta.ai/llama/llama3_1/license)
- >700 million MAU in the preceding calendar month (at release date) requires a separate license from Meta (s.2) — [same](https://dev.meta.ai/llama/llama3_1/license)
- Llama 4 Community License repeats: "Built with Llama", name must begin with "Llama", NOTICE file "Llama 4 is licensed under the Llama 4 Community License...", 700M MAU, AUP at llama.com/llama4/use-policy — [Llama 4 LICENSE, GitHub](https://github.com/meta-llama/llama-models/blob/main/models/llama4/LICENSE)

**BigCode StarCoder2 (BigCode OpenRAIL-M v1)**
- StarCoder2 is "licensed under the BigCode OpenRAIL-M v1 license agreement"; can reproduce training code verbatim, so outputs may carry original code's license/attribution duties (search index provided) — [HF starcoder2-15b](https://huggingface.co/bigcode/starcoder2-15b)
- OpenRAIL-M permits use "for research, commercial or non-commercial purposes"; it is not OSI open source; paragraph 4 (use restrictions) or a similar provision must carry forward to any redistribution; "Compliance with the restrictions in Attachment A is a condition to the use of the Model"; modifications need explanatory documentation — [BigCode OpenRAIL-M FAQ](https://www.bigcode-project.org/docs/pages/bigcode-openrail/)
- Attachment A includes: no malware generation, no harmful false info, no publishing machine-generated content without disclaiming it as machine-generated, no impersonating humans, no fully automated legal-rights decisions without disclaimer — [same](https://www.bigcode-project.org/docs/pages/bigcode-openrail/)
- Output "may be subject to third party rights or licenses, including open source licenses" — [same](https://www.bigcode-project.org/docs/pages/bigcode-openrail/)

**IBM Granite 4.0**
- Apache 2.0; HF cards (e.g., granite-4.0-h-small-base, tiny-preview) carry apache-2.0 tag; lineup H-Small 32B/9B active, H-Tiny 7B/1B active, H-Micro & Micro 3B — [HF granite-4.0-tiny-preview](https://huggingface.co/ibm-granite/granite-4.0-tiny-preview); [InfoQ](https://www.infoq.com/news/2025/11/ibm-granite-mamba2-enterprise); [dev.co](https://dev.co/ai/llms/granite-4-0-h-small)

**Mistral**
- Devstral Small 2 (Dec 2025): Apache 2.0, production use and redistribution of fine-tunes allowed — [VentureBeat](https://venturebeat.com/ai/mistral-launches-powerful-devstral-2-coding-model-including-open-source); [OpenSourceForU](https://www.opensourceforu.com/2025/12/mistral-makes-coding-ai-portable-with-open-source-devstral-small-2/)
- Devstral 2 (123B): modified MIT with revenue cap — companies above $20M monthly revenue need a commercial license; applies to derivatives too — [VentureBeat](https://venturebeat.com/ai/mistral-launches-powerful-devstral-2-coding-model-including-open-source); [implicator.ai](https://www.implicator.ai/mistrals-open-source-trick-build-a-great-model-gate-it-behind-revenue-caps-call-it-freedom/)
- Codestral 22B open weights: Mistral Non-Production License (research/testing/evaluation only); production requires a commercial license or Mistral's hosted endpoint — [Enterprise DNA](https://enterprisedna.co/resources/blog/practitioner-mistral-codestral); [ITPro](https://itpro.com/software/development/mistral-ai-just-launched-codestral-its-own-competitor-to-code-llama-and-github-copilot-and-its-fluent-in-over-80-programming-languages). One blog claims a later Apache 2.0 re-release — unverified, conflicts with other sources.

**Apache 2.0 obligations (applies to all Apache families above)**
- Redistribution: give a copy of the License, mark modified files, retain copyright/attribution notices, and include the NOTICE file's attribution if one exists (s.4); no trademark grant except for describing origin (s.6) — [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)

### Inferences
- Hosting behind an API: under Apache 2.0/MIT, serving a model via API is generally not "distribution" of the weights, so even NOTICE duties arguably are not triggered unless you share the weights; including attribution anyway is cheap. Under Gemma ToU, hosting is expressly distribution, so all flow-down obligations apply to API users.
- Renaming: Apache/MIT/Gemma/DeepSeek/OpenRAIL permit any new name, but you may not use the vendor's trademark to imply endorsement. Only Llama mandates the name (must begin with "Llama", e.g., "Llama-MyCoder"); Qwen Research license requires "Built with Qwen" in docs.
- Llama 3.2 1B/3B (relevant small sizes) very likely carry the same naming/Built-with/700M-MAU clauses as 3.1 and 4 (not fetched directly).
- Outputs: none of the licenses reviewed claim ownership of outputs. Llama, Gemma (≤3), and Qwen Research attach obligations when outputs are used to train other models (naming/attribution or derivative status).

### Gaps
- Did not fetch the actual Qwen3 / Qwen3-Coder HF LICENSE files directly; Apache 2.0 status rests on secondary sources (consistent with each other).
- Did not verify Qwen2.5 non-coder 3B/72B "Qwen License" (Tongyi Qianwen) text with its 100M MAU clause; not needed for Coder sizes except 3B.
- Could not read full BigCode OpenRAIL-M v1 text (HF Space didn't render); notice/copy requirements inferred from FAQ only.
- Did not verify Gemma 4 HF checkpoints for any added prohibited-use policy alongside Apache 2.0; Google's blog mentions none.
- Llama 3.2 multimodal EU restriction and current Llama AUP contents not fetched.
- Did not verify Mistral Ministral/Mistral Small 3.x licenses (believed Apache 2.0) or Granite official IBM release page.
- No survey of other 2025-2026 small coding models (e.g., Seed-Coder, OlympicCoder, Kimi/GLM small variants) within tool budget.

## Q2. Which licenses are cleanest for a personal project that rebrands the model and exposes an API?

### Takeaway
Cleanest: MIT (Phi-4) and Apache 2.0 (Qwen2.5-Coder 0.5/1.5/7/14/32B, Qwen3-Coder-30B-A3B, Gemma 4, Granite 4.0, Devstral Small 2): rename freely, serve commercially to anyone, no user caps, no mandatory AUP flow-down. Avoid Qwen2.5-Coder-3B and Codestral 22B for anything beyond private evaluation.

### Cited Findings
- MIT requires only keeping the copyright/permission notice in copies — [Phi-4 LICENSE](https://huggingface.co/microsoft/phi-4/blob/main/LICENSE)
- Apache 2.0: notice/NOTICE retention on redistribution, no trademark license — [Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0)
- Gemma 4 moved to Apache 2.0 — [Google Open Source Blog](https://opensource.googleblog.com/2026/03/gemma-4-expanding-the-gemmaverse-with-apache-20.html)
- Devstral Small 2 Apache 2.0 allows redistributing fine-tunes — [VentureBeat](https://venturebeat.com/ai/mistral-launches-powerful-devstral-2-coding-model-including-open-source)
- Llama forces "Llama" prefix in derivative names and "Built with Llama" — [Llama 3.1 license](https://dev.meta.ai/llama/llama3_1/license)

### Inferences
- Ranking for "rebrand + API": (1) MIT/Apache (Phi-4, Qwen Apache sizes, Qwen3-Coder, Gemma 4, Granite, Devstral Small 2); (2) DeepSeek-Coder license / StarCoder2 OpenRAIL-M / Gemma ≤3 — usable, but need ToS that flows down use restrictions; (3) Llama — usable but cannot fully rebrand; (4) Qwen Research (3B), Codestral MNPL — not for public/commercial API.
- Base model choice also matters for training data: fine-tuning an Apache model on synthetic data produced by Llama or Gemma ≤3 may import those licenses' obligations (Llama naming rule; Gemma derivative status).

### Gaps
- No case law or authoritative legal analysis found on whether API serving counts as "distribution" under Apache 2.0 for model weights.

## Q3. Gotchas: attribution/NOTICE files, "Built with X", prohibited-use flow-down to API users

### Takeaway
Main traps: Llama's naming + "Built with Llama" + NOTICE; Gemma ≤3 treating API hosting as distribution (must give users the Gemma ToU and bind them to the Prohibited Use Policy); DeepSeek and OpenRAIL-M requiring use restrictions as enforceable terms in your API ToS; Qwen "Built with Qwen" when outputs train a distributed model; StarCoder2 verbatim code reproduction; remote-restriction clauses (Gemma, DeepSeek).

### Cited Findings
- Gemma: API = distribution; must pass restrictions as enforceable provision and give recipients a copy of the Agreement — [Gemma Terms](https://ai.google.dev/gemma/terms)
- DeepSeek: SaaS hosting allowed, but use restrictions "MUST be included as an enforceable provision" — [DeepSeek LICENSE-MODEL](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
- OpenRAIL-M: Attachment A compliance is a condition of use and must carry forward; includes a duty to disclaim machine-generated content when published — [BigCode OpenRAIL-M](https://www.bigcode-project.org/docs/pages/bigcode-openrail/)
- StarCoder2 can emit verbatim training code subject to its original licenses — [HF starcoder2-15b](https://huggingface.co/bigcode/starcoder2-15b)
- Llama: NOTICE text file, "Built with Llama", "Llama" name prefix, AUP — [Llama 4 LICENSE](https://github.com/meta-llama/llama-models/blob/main/models/llama4/LICENSE)
- Qwen Research: "Built with Qwen"/"Improved using Qwen" when outputs train distributed models — [Qwen2.5-Coder-3B LICENSE](https://huggingface.co/Qwen/Qwen2.5-Coder-3B-Instruct/blob/main/LICENSE)
- Remote-restriction rights reserved by Google and DeepSeek — [Gemma Terms](https://ai.google.dev/gemma/terms); [DeepSeek LICENSE-MODEL](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)

### Inferences
- Practical checklist even for Apache/MIT: keep the upstream LICENSE + NOTICE in any shared weights, state in your docs/model card "fine-tuned from X (license Y)", don't use vendor logos/names in your brand, and keep a basic API ToS with an acceptable-use clause (required for Gemma ≤3/DeepSeek/OpenRAIL-M, good hygiene otherwise).
- "Built with X" is mandatory only for Llama (always on distribution) and Qwen Research/Qwen License (when used to build distributed models); not required by Apache/MIT/Gemma/DeepSeek.

### Gaps
- Exact wording of Gemma Prohibited Use Policy and Llama 4 AUP not fetched.
