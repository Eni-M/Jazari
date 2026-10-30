# Free-GPU LoRA/QLoRA Fine-Tuning of Small Coding LLMs and Export to GGUF/Ollama

Research date: 2026-10-29. Sources are mostly 2026 Unsloth docs plus 2026 third-party reviews. Older or undated material is flagged inline.

## 1. Free-tier limits of Colab and Kaggle (late 2026)

### Takeaway
Both platforms still give a free 16 GB-class T4. Colab publishes no quota and promises at most ~12 h sessions depending on availability. Kaggle gives 12 h GPU sessions and a weekly quota that is commonly reported as ~30 h but is not officially fixed. Kaggle's P100 is effectively broken for current PyTorch (see section 3), so in practice the free hardware is "T4 or 2x T4".

### Cited Findings
- Colab free: Google does not name the GPU (users report T4) and says GPU types "vary over time". Sessions run at most 12 h "depending on availability and your usage patterns". No published usage limits, so weekly-hour figures come from users. Idle runtimes are interrupted and the VM is deleted. SSH, remote desktops and web UIs are not allowed without a paid balance. Reviewed 21 Sep 2026. — [gpuperhour.com, Free Cloud GPUs in 2026](https://gpuperhour.com/blog/free-cloud-gpus-and-credits)
- Colab free tier is described as ~15 GB usable VRAM by Unsloth. Notebooks for large models exceed it. — [Unsloth Notebooks](https://unsloth.ai/docs/get-started/unsloth-notebooks)
- Colab idle-timeout figures conflict across third-party blogs (~90 min, 30 min, 6 h). One guide estimates ~15–30 GPU-h/week and says that at peak times a session can drop to CPU. These are unofficial. — [WebSearch aggregate incl. hivenet](https://www.hivenet.com/post/google-colaboratory-gpu-complete-guide-to-free-cloud-gpu-access-and-limitations), [tooldirectory.ai review, Sep 2026](https://tooldirectory.ai/tools/google-colaboratory)
- Kaggle: T4 and P100-class GPUs, and the accelerator menu changes. Sessions last 12 h for CPU/GPU (9 h for TPU) per Kaggle docs. The weekly quota is shown in account settings and "floats". About 30 h is commonly reported but is not stated by Kaggle. Colab Pro/Pro+ subscribers who link accounts get +15/+30 Kaggle GPU h/week. — [gpuperhour.com, Sep 2026](https://gpuperhour.com/blog/free-cloud-gpus-and-credits)
- Conflict: some 2026 guides state Kaggle GPU sessions are 9 h and the 30 h quota is "guaranteed/fixed". — [gmicloud blog](https://www.gmicloud.ai/en/blog/best-free-gpu-cloud-options-for-ai-startups-and-researchers); contradicted by Kaggle docs as quoted in [gpuperhour.com](https://gpuperhour.com/blog/free-cloud-gpus-and-credits)
- Kaggle hardware is commonly described as P100 16 GB or 2x T4 (32 GB total). — [aimultiple (FR)](https://aimultiple.com/fr/free-cloud-gpu)
- (Older, pre-2026) The Kaggle quota resets Saturday 00:00 UTC, and users saw 30–41 h/week. — [Towards Data Science (older)](https://towardsdatascience.com/free-gpus-for-training-your-deep-learning-models-c1ce47863350)

### Inferences
- For planning, treat the budget as ~30 Kaggle T4-hours/week in sessions of 12 h or less, plus opportunistic Colab T4 sessions of a few hours. Checkpoint to Drive, Kaggle output or the HF Hub, because Colab can disconnect early.
- Kaggle is the more predictable platform for runs over 2 h. Colab is fine for short runs of 1 h or less.

### Gaps
- No official Google statement of Colab free weekly GPU hours exists. Google does not publish it.
- I could not confirm from an official Kaggle page that the 2x T4 option is still offered in late 2026. Verify it in the notebook accelerator menu.

## 2. Tooling support per model family and VRAM by size

### Takeaway
Unsloth is the practical default on a free T4. It has official free-T4 Colab/Kaggle notebooks for Qwen2.5-Coder, Qwen3, Phi-4, Gemma 3/4, Llama 3.x, Granite 4.0 and gpt-oss-20B. Its documented QLoRA minimums are ~3.5 GB (3B), 5 GB (7B), 6 GB (8B) and 8.5 GB (14B), so everything up to 14B fits on 16 GB at modest sequence lengths. Unsloth documents the 30B-A3B MoE at 17.5 GB, which is above one T4.

### Cited Findings
- Unsloth official minimum VRAM, QLoRA 4-bit / LoRA 16-bit: 3B 3.5/8 GB, 7B 5/19 GB, 8B 6/22 GB, 9B 6.5/24 GB, 11B 7.5/29 GB, 14B 8.5/33 GB, 27B 22/64 GB, 32B 26/76 GB. The docs call these "absolute minimums". OOM is most often caused by batch size, so use 1–3. Minimum CUDA capability is 7.0 (T4 and V100 are listed as supported). — [Unsloth Requirements](https://unsloth.ai/docs/get-started/fine-tuning-for-beginners/unsloth-requirements.md)
- A third-party measurement: 8B QLoRA at seq len 2048 peaked at 6.6 GB reserved. The guide gives a range of ~7–12 GB for 7–8B and 14–18 GB for 13–14B at 2048 tokens. — [computingforgeeks](https://computingforgeeks.com/fine-tune-llm-unsloth-qlora/)
- Unsloth notebook catalog (free Colab T4 unless noted):
  - Qwen2.5-Coder 14B Conversational. Qwen2.5-Coder 1.5B Tool Calling (Colab + Kaggle T4).
  - Qwen3 14B Reasoning-Conversational (Colab + Kaggle), Qwen3-2507-4B, Qwen3 4B GRPO (Colab + Kaggle T4). Qwen3 32B needs an A100.
  - Qwen3-Coder: no dedicated notebook.
  - Phi-4 14B Conversational/GRPO (Colab + Kaggle T4). Phi-4-mini: no notebook.
  - Gemma 3 4B/1B/270M, Gemma 4 E2B/E4B (free). Gemma 4 31B is listed as a free Kaggle notebook.
  - Llama 3.1 8B, Llama 3.2 1B/3B (Colab + Kaggle).
  - Granite-4.0-H (free Colab).
  - DeepSeek-R1-0528-Qwen3 8B GRPO (free).
  - StarCoder2: no notebook (CodeGemma 7B instead).
  - MoE: gpt-oss-20B fine-tuning on free Colab and Kaggle T4. Qwen3-30B-A3B, Qwen3.5-35B-A3B, Nemotron-3-Nano-30B-A3B and GLM-4.7-Flash are "Large LLMs" needing an A100.
  — [Unsloth Notebooks](https://unsloth.ai/docs/get-started/unsloth-notebooks)
- The Qwen2.5-Coder-7B model card says a free Colab T4 notebook covers "Qwen 2.5 (all model sizes)". — [HF unsloth/Qwen2.5-Coder-7B](https://huggingface.co/unsloth/Qwen2.5-Coder-7B)
- Qwen3-30B-A3B QLoRA in Unsloth uses 17.5 GB VRAM, and router-layer fine-tuning is off by default. A Feb 2026 Unsloth post says its MoE kernels support Qwen3 (30B, 235B, VL, Coder). 16-bit LoRA on 30B-A3B uses 63 GB. — [Unsloth Qwen3 docs](https://unsloth.ai/docs/models/qwen), [Reddit r/LocalLLaMA Unsloth post](https://lr.eu.psf.lt/r/LocalLLaMA/comments/1kd531l/qwen3_finetuning_now_in_unsloth_2x_faster_with_70)
- The Qwen team's own docs include an Unsloth training guide. — [Qwen docs: Unsloth](https://qwen.readthedocs.io/en/latest/training/unsloth.html)
- LLaMA-Factory provides a Colab notebook for fine-tuning Llama-3 on a free T4 (dated Apr 2024, older) and supports 2–8-bit QLoRA via bitsandbytes/GPTQ/AWQ/HQQ/EETQ. — [LLaMA-Factory README mirror](https://huggingface.co/spaces/nubifere/vis-llm-ft/raw/main/LLaMA-Factory/README.md)
- Axolotl lists LoRA, QLoRA, DPO/IPO, FSDP/DeepSpeed, multipacking and Flash Attention. I found no T4-specific documentation. — [jimmysong landscape: axolotl](https://landscape.jimmysong.io/projects/axolotl/)

### Inferences (model-size feasibility on one 16 GB T4)
| Size | Examples | QLoRA on free T4? | Est. VRAM @ 2k ctx, bs 1–2 |
|---|---|---|---|
| 0.5–1.5B | Qwen2.5-Coder-1.5B, Qwen3-1.7B, Gemma 3 1B | Easy; could even use 16-bit LoRA | ~2–5 GB |
| 3–4B | Qwen2.5-Coder-3B, Qwen3-4B, Phi-4-mini (3.8B), Gemma 3 4B, Llama 3.2 3B, Granite 3B-class | Easy; 4k ctx fine | ~4–7 GB |
| 7–8B | Qwen2.5-Coder-7B, Qwen3-8B, Llama 3.1 8B, DeepSeek-Coder 6.7B, StarCoder2-7B | Yes, comfortably | ~6–12 GB |
| 14B | Qwen2.5-Coder-14B, Qwen3-14B, Phi-4 | Yes (official free notebooks), keep ctx ≤2k and bs 1–2 | ~9–16 GB, tight |
| 20–32B / 30B-A3B MoE | gpt-oss-20B (fits per Unsloth notebook), Qwen3-Coder-30B-A3B (~17.5 GB+) | gpt-oss-20B yes; 30B-A3B does not fit one T4, only maybe split across 2x T4 | >16 GB |
- These per-size estimates are interpolated from the Unsloth minimums and the third-party measurement. They are not measured per model.
- StarCoder2, DeepSeek-Coder (v1) and Phi-4-mini have no official Unsloth notebooks. They are standard HF architectures, so Unsloth or TRL+PEFT should still load them, but that is unverified here. Older code-only models (DeepSeek-Coder 2023, StarCoder2 Feb 2024) are dated relative to Qwen2.5/Qwen3-Coder.
- Plain TRL+PEFT+bitsandbytes works on a T4 but uses more VRAM and runs slower than Unsloth (Unsloth claims ~2x faster and up to 70% less memory). LLaMA-Factory is a reasonable GUI/YAML alternative. Axolotl's default configs lean on bf16/FlashAttention-2, which a T4 lacks (inference from background knowledge, not a fetched source).

### Gaps
- No verified QLoRA VRAM number exists for Qwen3-Coder-30B-A3B specifically.
- No verified support statement was found for StarCoder2 or DeepSeek-Coder in current Unsloth.
- Granite size and variant specifics: only the Granite-4.0-H notebook is confirmed.

## 3. T4 (no bf16) and P100 issues; does Kaggle multi-GPU help?

### Takeaway
On a T4, use fp16 (Unsloth `dtype=None` auto-selects it). Forcing bf16 is reported to be ~4x slower. The Kaggle P100 (sm_60) no longer works with the current Kaggle PyTorch image, which needs sm_70+, and Unsloth also requires compute capability 7.0+, so avoid the P100. Unsloth multi-GPU is still not officially supported. 2x T4 helps mainly to split a model that does not fit on one card (`device_map="balanced"`) or to run DDP via accelerate/torchrun, at the cost of extra setup.

### Cited Findings
- Kaggle docker-python issue opened 20 Mar 2026: the installed PyTorch "requires CUDA capability sm_70 or higher" but the Tesla P100 is sm_60, giving "no kernel image is available for execution on the device". Suggested workaround: install a CUDA 12.6 PyTorch build, or switch to T4. — [Kaggle/docker-python #1546 mirror](https://github-gitlab-huggingface.pages.dev/Kaggle/docker-python/issues/1546), [PyTorch forum: P100 on Kaggle](https://discuss.pytorch.org/t/getting-an-error-while-running-my-code-on-p100-gpu-on-kaggle/224730)
- A forum reply says Pascal support was dropped for PyTorch builds using CUDA 12.8 or newer. This is a single forum source, not confirmed against release notes. — [PyTorch forum](https://discuss.pytorch.org/t/getting-an-error-while-running-my-code-on-p100-gpu-on-kaggle/224730)
- Unsloth minimum CUDA capability is 7.0. — [Unsloth Requirements](https://unsloth.ai/docs/get-started/fine-tuning-for-beginners/unsloth-requirements.md)
- On a T4, `is_bfloat16_supported()` returns False and Unsloth falls back to fp16. — [CSDN Colab guide](https://deepseek.csdn.net/6a76abf8662f9a54cb99d723.html)
- Conflict: `torch.cuda.is_bf16_supported()` can return True on a T4 even though there are no bf16 tensor cores. bf16 ran ~4x slower than fp16 on a Kaggle T4 (ModernBERT, 78 vs 18 min). This is not an Unsloth benchmark. — [earino blog](https://earino.github.io/applied-deep-learning/site/blog/05_bf16_on_t4.html)
- Unsloth multi-GPU status: "no official multi-GPU support yet". It works via Accelerate/DeepSpeed (DDP, FSDP), launched with `accelerate launch train.py` or `torchrun --nproc_per_node N`. `device_map="balanced"` splits a 4-bit model across GPUs. A Kaggle Magistral-2509 notebook uses multi-GPU to fit 24B. — [Unsloth Multi-GPU](https://unsloth.ai/docs/basics/multi-gpu-training-with-unsloth)

### Inferences
- For 14B and smaller, a single T4 is enough, and 2x T4 adds complexity for at most ~2x throughput via DDP. DDP in a notebook requires exporting the code to a script. The second T4's main value is splitting 20–30B models (e.g. 30B-A3B MoE at ~17.5 GB+) across 32 GB.
- Use the Unsloth-pinned environment and use pre-quantized `unsloth/*-bnb-4bit` checkpoints to save download time and RAM.

### Gaps
- No measured Unsloth benchmark of 2x T4 DDP speedup for a 7B model was found.
- I did not confirm whether the Kaggle image has since been fixed for the P100.

## 4. Dataset size, training time, and DPO/ORPO feasibility

### Takeaway
Hundreds to a few thousand SFT examples for style/preference tuning is a short job on a T4: tens of minutes for 1.5–8B at 1–2k tokens, roughly 1–2 h for 14B (estimate). DPO and ORPO both have official Unsloth notebooks that run on a free T4 (Zephyr 7B DPO, Llama 3 8B ORPO), so preference tuning is feasible at 7–8B.

### Cited Findings
- Official notebooks: DPO with Zephyr 7B on Colab and Kaggle T4. ORPO with Llama 3 8B on Colab and Kaggle T4. KTO via a community notebook. GRPO notebooks for Qwen3 4B, Llama, Phi-4, Gemma and gpt-oss on free T4. — [Unsloth Notebooks](https://unsloth.ai/docs/get-started/unsloth-notebooks)
- Unsloth claims DPO/ORPO run 1.9x faster with 43% less memory than HF+FA2 (GPU unspecified). The DPO notebook loads `unsloth/zephyr-sft-bnb-4bit` with LoRA r=64 on all projections. — [HF blog: Unsloth + TRL](https://huggingface.co/blog/unsloth-trl), [Unsloth README fork](https://github.com/RapidAI/unsloth)
- Model loading on a free T4 typically takes under 2 min, mostly downloading. — [Towards AI](https://pub.towardsai.net/you-dont-need-a-cloud-gpu-cluster-to-fine-tune-your-own-llm-83a0baf8e626)
- GGUF export in Colab takes ~5–10 min. — [Unsloth: Saving to Ollama](https://unsloth.ai/docs/basics/inference-and-deployment/saving-to-ollama)

### Inferences
- Rough budget: 1,000 examples × ~1k tokens × 2–3 epochs is ~2–3M tokens. At a typical T4 QLoRA throughput this is well under an hour for 3–8B, and a 14B run fits in one 12 h Kaggle session with margin. These throughput figures are estimates, not sourced.
- For DPO with LoRA in TRL, the reference model is normally the base with the adapter disabled, so no second model copy is needed (background knowledge of TRL behaviour, not fetched). Pairs double the tokens per example, so use roughly half the context or batch of SFT. ORPO needs no reference model at all and is the cheaper option.
- Suggested sequence: SFT (a few hundred to 2k examples) first, then an optional DPO/ORPO pass on a few hundred preference pairs. Both fit in the free weekly quota several times over.

### Gaps
- I found no published T4 tokens/sec or steps/sec benchmark for Qwen2.5-Coder 7B/14B QLoRA. Measure it from trainer logs on the first run.
- I found no T4-specific DPO VRAM measurement.

## 5. Export: merge LoRA, convert to GGUF, quantize, load into Ollama

### Takeaway
Unsloth does the whole path in one call: `model.save_pretrained_gguf(dir, tokenizer, quantization_method="q4_k_m")` (or `push_to_hub_gguf`) merges, converts via llama.cpp and quantizes, and also writes an Ollama Modelfile containing the training chat template. The manual path is `save_pretrained_merged(..., "merged_16bit")`, then `convert_hf_to_gguf.py`, then `llama-quantize`, then a Modelfile with `FROM ./x.gguf` and `ollama create <name>`. The most common failure is a chat template or EOS mismatch.

### Cited Findings
- `model.save_pretrained_gguf("directory", tokenizer, quantization_method="q4_k_m")` saves locally. `model.push_to_hub_gguf(...)` uploads. `model.save_pretrained_merged("merged_model", tokenizer, save_method="merged_16bit")` produces the 16-bit merge. Quant options include q8_0, q4_k_m, q5_k_m, q6_k, f16 and the iq2/iq3 types, with q4_k_m and q5_k_m recommended. — [Unsloth: Saving to GGUF](https://unsloth.ai/docs/basics/inference-and-deployment/saving-to-gguf)
- Manual route: build llama.cpp, then run `python llama.cpp/convert_hf_to_gguf.py merged_model --outfile model-F16.gguf --outtype f16` (`--outtype bf16` and `q8_0` also work). If saving crashes, lower `maximum_memory_usage` from the default 0.75 to 0.5. — [Unsloth: Saving to GGUF](https://unsloth.ai/docs/basics/inference-and-deployment/saving-to-gguf)
- Unsloth auto-generates the Modelfile Ollama needs, with settings and the fine-tuning chat template, and it can be printed. In the notebook, enable only one quant row (Q8_0 is the fastest) because selecting all of them is very slow. A LoRA adapter on its own is ~100 MB. Poor output in Ollama is most often caused by a chat template mismatch, a wrong EOS token or a doubled or missing BOS. — [Unsloth: Saving to Ollama](https://unsloth.ai/docs/basics/inference-and-deployment/saving-to-ollama)
- Ollama import: a Modelfile with `FROM /path/to/file.gguf`, then `ollama create my-model`. Split GGUFs use a wildcard. Ollama "does not quantize GGUF models during import", so quantize first with `llama-quantize`. A safetensors directory can also be imported with `FROM <dir>`. — [Ollama docs: Import](https://docs.ollama.com/import)
- A community guide shows the custom-name flow: `ollama create my-model -f Modelfile` then `ollama run my-model`. — [dnsoc Notion guide](https://dnsoc.notion.site/Export-to-Ollama-GGUF-and-Import-Steps-27627f6eb2538041a6a8c685b017253f)

### Inferences
- Recommended $0 path: train on Kaggle or Colab with Unsloth, then call `save_pretrained_gguf(q4_k_m)` or `push_to_hub_gguf` to a private HF repo. Download the GGUF together with Unsloth's generated Modelfile, edit `FROM` to the local path, run `ollama create mycoder -f Modelfile`, and test with `ollama run mycoder`.
- Keep the 16-bit merged model or the LoRA adapter on the HF Hub so you can re-quantize to other levels later without retraining.
- Disk and RAM: a merged 14B fp16 model is ~28 GB, which is near Colab's disk/RAM limits. For 14B, prefer Kaggle (more disk) or `push_to_hub_gguf` (estimate, not sourced).

### Gaps
- I did not verify Ollama's `ADAPTER` (GGUF LoRA) import path or the `ollama create --quantize` flag on the fetched page.
- I did not verify the exact Unsloth attribute for printing the Modelfile (often cited as `tokenizer._ollama_modelfile`). The docs show it only in screenshots.
