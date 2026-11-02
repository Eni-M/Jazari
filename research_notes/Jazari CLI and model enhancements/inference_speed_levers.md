# Remaining llama.cpp speed levers for Jazari on the T480 (Granite 4.0-H Tiny, CPU, late 2026)

Evidence tags:
- **[source @b11982]**: read in llama.cpp at commit `8a199ad31`, the b11982 build Jazari pins (`scripts/jazari-manifest.json`). **[source @master]**: read on master, 2 Nov 2026.
- **[repo-measured]**: a number already measured on this T480 and recorded in the repo (`docs/OPTIMIZATION-LOG.md`, `docs/superpowers/specs/2026-11-02-model-choice-results.md`, `DeviceTuner.cs` comments, `research_notes/Local model load optimization/measured_thread_sweep.md`).
- **[sourced]**: from a linked web page. **[estimate]**: my arithmetic or inference. **[measure]**: needs a run on the T480.

Builds on `research_notes/Whole laptop Jazari optimization/inference_speed.md`, `small_models.md` and `research_notes/Local model load optimization/*`, and does not repeat them. Those notes were written for Granite 4.1 3B (dense). The main new point here is that **Granite 4.0-H Tiny is a hybrid Mamba-2 MoE**, which changes how speculation, prompt caching and `--cache-reuse` behave.

Current flags (from `Tuning.ServerArgs`, `ServerLauncher.cs`, `scripts/start-llama.ps1`):
- Tuned path: `-m <model> -dev <dev> -ngl N -t <half of logical cores> --poll 0 -fa on|off -c 16384 --cache-ram 2048 -np 1 --cache-reuse 256 --no-webui`.
- Fallback path: `-dev none -t 6 --poll 0 -c 16384 --cache-ram 2048 -np 1 --cache-reuse 256`.
- Not passed anywhere: `-tb`, `-ub`, `-b`, `-ctxcp`, `-cms`, `--slot-save-path`, `-lm`, any `--spec-*`, `-cmoe`.

## Which remaining levers are worth testing, in order?

### Takeaway
Most of the cheap dense-model levers have already been tried. For H-Tiny, the free levers left are:
1. A separate prompt thread count (`-tb 8`).
2. Making the hybrid prompt cache actually hit: checkpoint placement, `-ctxcp`/`-cram` sizing, and slot save/restore.
3. Turning **off** `--cache-reuse` (it cannot help a hybrid).
4. A Q4_0 or self-made "all-Q4" quant so that every expert tensor is repacked.

Speculative decoding is a weaker bet on H-Tiny than it was on 4.1 3B, and it already lost on review prose there.

### Cited Findings
- Baseline speeds:
  - H-Tiny on the CPU writes 10.1 tok/s at half the threads (4) against 6.4 at 8. The MX150 partial offload reads 60 tok/s but writes only 6.6 [repo-measured, `DeviceTuner.cs` comment].
  - In the model-choice eval: typical answer 8 s, 47 s for the first answer after `/read`, 12 s for a follow-up [repo-measured, model-choice results].
- Already tried on Granite 4.1 3B [repo-measured, OPTIMIZATION-LOG]:
  - `KMP_BLOCKTIME=0`: answers 20–28% slower.
  - `ngram-simple`, `ngram-mod`, `ngram-map-k`: no real gain.
  - Granite 4.0 350M draft on the MX150: rewrite 5.46 → 7.95 tok/s, but review 5.47 → 2.67 tok/s.
  - Granite 4.0 1B draft: no gain.
  - UHD 620: 12.5 / 4.1 tok/s.
  - `--prio -1`: rejected, writing −21%.
- On 4.1 3B, prompt speed rose from 30.4 tok/s at 4 threads to 35.4 at 8, while writing barely moved (5.5 → 6.1) [repo-measured, measured_thread_sweep.md].

### Inferences
Ranked levers for H-Tiny. The gains are [estimate] unless tagged otherwise.

| # | Lever | Exact setting | Expected gain | Hybrid-specific? | How to measure on T480 |
|---|---|---|---|---|---|
| 1 | Separate prompt threads | add `-tb 8` (or `-tb <logical cores>`), keep `-t 4` | Prompt +10–20% (from 4.1's 30→35 tok/s [repo-measured]); answers unchanged | No | `llama-bench -m h-tiny.gguf -dev none -t 4 -p 512 -n 0` against `-t 8`, then server with `-t 4 -tb 8` and read `timings.prompt_per_second` |
| 2 | Hit the hybrid prompt cache | Byte-stable system prompt as the first message; keep `/read` text and passages at stable positions; check `timings.cache_n` | Removes `prefix_tokens / pp_rate` seconds per turn (for example 2,000 tokens at about 60–100 tok/s is 20–33 s) | **Yes**: reuse only goes back to a checkpoint (next section) | `LLAMA_SERVER_SLOTS_DEBUG=1` plus `cache_n` and `prompt_n` in each response |
| 3 | Size checkpoints to RAM | `-ctxcp 8` (default 32), keep `-cram 2048` | Avoids up to about 1.7 GB of checkpoints (32 × about 55 MiB) evicting useful cache; speed neutral | **Yes** | Log line "created context checkpoint … size = X MiB" |
| 4 | Turn off `--cache-reuse` for H-Tiny | `--cache-reuse 0` | No speed loss; removes a correctness risk | **Yes** | A/B the same edited-middle prompt; compare output and `cache_n` |
| 5 | Slot save/restore of a fixed preamble | `--slot-save-path %LOCALAPPDATA%\Jazari\slots` plus `POST /slots/0?action=restore` at start | Saves the system-prompt prefill on the first question after every server start | Works: b11982 appends checkpoints to the slot file | Time to first token of the first question, with and without restore |
| 6 | All-Q4 quant so every tensor is repacked | Q4_0, or `llama-quantize --pure … Q4_K` from BF16 | Prompt +5–25%, answers about +5% | MoE experts are repacked too | `llama-bench -m q4km,q40 -dev none -t 4 -tb 8 -p 512 -n 64 -r 5` plus the eval set for quality |
| 7 | `-ub` sweep for MoE prompts | `-ub 256,512,1024` in llama-bench | 0–10% on prompts | MoE: larger ubatches reuse experts | `llama-bench -ub 256,512,1024` |
| 8 | MoE experts on CPU, the rest on MX150 | `-dev Vulkan1 -ngl 99 -cmoe` (or `-ncmoe N`) | Unknown: prompts may rise, answers may fall from per-layer syncs | MoE | `llama-bench` with `-ngl 99 -cmoe` against CPU |
| 9 | N-gram speculation for `/edit` only | `--spec-type ngram-mod` with long drafts | 1.0–1.3× on edits, ≤1.0× elsewhere | **Yes**: needs checkpoint rollback, and MoE verify costs more | Same `/edit` with and without; read the acceptance line |
| 10 | Draft model | `-md granite-4.0-350m-Q8_0.gguf -devd Vulkan1` | Probably <1.0× on prose; ≤1.3× on rewrites | **Yes** (as 9) | As in the earlier 4.1 test |

Not worth more time on this hardware, with reasons in the sections below: KV-cache quantization, OpenBLAS/oneDNN, the UHD 620 (Vulkan, SYCL, OpenVINO), `--poll`, `KMP_BLOCKTIME`, low priority, and IQ4_XS.

### Gaps
- No H-Tiny `llama-bench` prompt rate at 4 vs 8 threads is recorded in the repo. The H-Tiny CPU prompt rate itself is not in the notes I read.
- H-Tiny GGUF tensor-type mix (how much is Q6_K) not read. Hugging Face was blocked from this session, so file listings could not be fetched.

## Speculative decoding with a hybrid Mamba-2 target (Granite 4.0-H Tiny)

### Takeaway
It works in b11982, but only through full state checkpoints. Granite hybrid is not on llama.cpp's list of architectures with cheap recurrent rollback. Every draft round therefore copies the Mamba state, and a rejected draft restores it and re-decodes the accepted tokens. H-Tiny is also MoE, so a multi-token verify batch touches more experts and costs more than one token. Expect less gain than the dense 4.1 3B got, which was +46% on rewrites and **−51% on reviews** [repo-measured]. Granite 4.0 350M and 1B are tokenizer-compatible with H-Tiny. Keep speculation off by default, and if anything try only `ngram-mod` on `/edit`.

### Cited Findings
- **Checkpoint mode.** The server classifies the target's memory at startup:
  - It logs "speculative decoding will use checkpoints" when partial sequence removal is not supported (`COMMON_CONTEXT_SEQ_RM_TYPE_FULL`), and disables speculation only when no removal works at all.
  - Before each verify, it saves a target checkpoint (`ckpt.update_tgt(... LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY)`) in FULL mode.
  - A source TODO notes the draft context is also restored and re-evaluated "when not needed" (`TAG_SPEC_AVOID_DRAFT_REEVAL`).
  - [source @b11982](https://github.com/ggml-org/llama.cpp/blob/8a199ad31/tools/server/server-context.cpp); the same code is on [master](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/server-context.cpp).
- **Cheap rollback list.** Bounded per-token recurrent snapshots (`n_rs_seq`) exist only for architectures in `llm_arch_supports_rs_rollback`: Qwen3.5/3.5-MoE, Nemotron-H, LFM2/LFM3, DeepSeek4, Kimi K3 and a few others. **`GRANITE_HYBRID` is not in it.** Other architectures are clamped to `n_rs_seq = 0` ([llama-arch.cpp @master](https://github.com/ggml-org/llama.cpp/blob/master/src/llama-arch.cpp), [llama-context.cpp @master](https://github.com/ggml-org/llama.cpp/blob/master/src/llama-context.cpp)).
- **When `n_rs_seq` is requested.** Even on supported architectures, `need_n_rs_seq()` asks for snapshots only with `draft-mtp`, `draft-eagle3`, `draft-dflash` or `draft-dspark`. `draft-simple` and the n-gram types fall back to checkpoints [source @b11982, [common.h](https://github.com/ggml-org/llama.cpp/blob/8a199ad31/common/common.h)].
- **How Mamba state rolls back.** Recurrent memory can't drop tokens from the end, except through the per-token snapshot index: "models like Mamba or RWKV can't have a state partially erased at the end of the sequence because their state isn't preserved for previous tokens" ([llama-memory-recurrent.cpp @master](https://github.com/ggml-org/llama.cpp/blob/master/src/llama-memory-recurrent.cpp)).
- **PR #19493, "server: speculative checkpointing"** (merged 19 Apr 2026) is the PR that makes speculation work on recurrent and hybrid models via checkpoints ([PR #19493](https://github.com/ggml-org/llama.cpp/pull/19493)):
  - The author says checkpoints are "not as fast" as sequence removal.
  - On partial acceptance, "we have to go back to the checkpoint and execute a shorter batch".
  - One log shows about 150 MiB per checkpoint on Qwen3.5.
  - A tester saw "well above 30%" faster code and about 10% slower ordinary text (Qwen3.5-27B with a 0.8B draft, GPU).
  - The examples are all GPU, Qwen3.5 or Qwen3-Coder-Next.
- **PR #23269** (merged May 2026) changed the default `--spec-draft-n-max` from 16 to 3, made all speculative types see accepted tokens, and allowed chaining types. The author says parallel decoding of recurrent/hybrid models with MTP "still remains highly suboptimal" ([PR #23269](https://github.com/ggml-org/llama.cpp/pull/23269)). In a user's log on that PR, a hybrid prints "speculative decoding will use checkpoints" and "context does not support partial sequence removal" (same page).
- **MoE verify cost** [sourced, preprints, GPU]:
  - Verifying several draft tokens activates more distinct experts. "With three draft tokens, up to 6 experts may be activated, leading to a 3x increase in data movement" (Mixtral 2-of-8) ([Utility-Driven SD for MoE, arXiv 2506.20675](https://web3.arxiv.org/pdf/2506.20675)).
  - At small batch sizes, MoE speculation gains are smaller than for dense models until the active experts saturate ([MoESD, arXiv 2505.19645](https://arxiv.org/pdf/2505.19645)).
  - The fix proposed there is an expert budget during verification ([MoE-Spec, arXiv 2602.16052](https://arxiv.org/html/2602.16052v1)). None of this is in llama.cpp.
- **llama.cpp docs on `ngram-mod`:** "MoEs require long drafts", and dense models "can reduce" n-min and n-max. Defaults are n-match 24, n-min 48, n-max 64 ([speculative.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/speculative.md)).
- **H-Tiny architecture** [sourced]:
  - 7B total and 1B active parameters ([IBM announcement](https://www.ibm.com/new/announcements/ibm-granite-4-0-hyper-efficient-high-performance-hybrid-models)).
  - 4 attention and 36 Mamba-2 layers; 64 experts, 6 active; embedding 1536. These counts are from a third-party listing ([LLM Reference](https://www.llmreference.com/model/granite-4.0-h-tiny)) and were not checked against IBM's card.
  - A mirrored config gives `mamba_d_state` 128, `mamba_expand` 2 and `mamba_n_heads` 48 ([RedHatAI mirror](https://huggingface.co/RedHatAI/granite-4.0-h-tiny/blob/main/config.json)). The repo's own GGUF-metadata note also says 4 of 40 layers use attention (`DeviceTuner.cs`).
- **Tokenizer compatibility** [from existing notes, repo-measured or read from Hugging Face]:
  - The H-Tiny GGUF matches Granite 4.1 3B: vocab 100352, BOS/EOS 100257, 0 text differences from ID 5.
  - `granite-4.0-350m` and `-1b` share the same tokenizer blob, and so do the h-350m and h-1b hybrids. That passes llama.cpp's `common_speculative_are_compatible` check.
  - The repo's draft attempt "Granite 4.0 H-Tiny … too big for the MX150" was about H-Tiny **as a draft**, not as a target.

### Inferences
- **Per-round checkpoint cost.** The Mamba-2 state is about 48 heads × 64 head-dim × 128 state × 4 B ≈ 1.5 MiB per layer, × 36 layers ≈ **54 MiB**, plus a small conv state [estimate from the config above]. Copying that on each draft round costs roughly 5–10 ms on single-channel DDR4 at 10–13 GB/s [estimate]. At about 100 ms per H-Tiny token, that is about 5–10% overhead before any rejection.
- **Rejection cost.** A rejection pays a restore plus a re-decode of the accepted prefix. With a 350M dense draft (not hybrid, so no draft checkpoint), acceptance must be high to break even.
- **H-Tiny gains less than 4.1 did.** On 4.1 3B, review prose already fell to 0.49× at 134/512 acceptance [repo-measured], and H-Tiny adds checkpoint and MoE-verify costs. So expect **<1.0× on reviews and chat**, and maybe 1.0–1.3× on file rewrites [estimate]. The baseline is also higher (about 10 tok/s), which narrows the margin for the draft: the 350M on the MX150 was estimated at about 80 tok/s in earlier notes.
- **If trying n-gram on `/edit`.** Follow the doc's MoE advice: long drafts (`--spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 32 --spec-ngram-mod-n-max 64`). A long accepted run amortises one checkpoint over many tokens, while short drafts pay the checkpoint for little. This is the opposite of the dense-model advice in the earlier notes.
- **Per-request toggling.** Speculation is a server-start setting. To get it only on `/edit`, the gateway would need a second server or a per-request override; whether b11982 has a per-request override was not checked.
- **Cheaper rollback later.** If upstream adds `GRANITE_HYBRID` to the rs-rollback list, rollback becomes cheap, but only for MTP/EAGLE-style drafts, none of which exist for Granite.

### Gaps
- No published speculative-decoding measurement on any Granite 4.0-H model, CPU or GPU.
- No CPU-only measurement of checkpointed speculation on any hybrid model; all of PR #19493's numbers are GPU.
- Real checkpoint size and copy time for H-Tiny should be read from the `created speculative checkpoint … size = X MiB` debug line [measure].
- Whether H-Tiny's 4 attention layers and MoE routing make a 350M dense draft's acceptance differ from 4.1's: unknown [measure].

## Prompt and prefix caching mechanics for a hybrid model

### Takeaway
For H-Tiny, `cache_prompt` (on by default) reuses the prompt only up to the latest **context checkpoint** at or before the first changed token. The recurrent state cannot be truncated, so the cached tokens past that checkpoint are recomputed. b11982 places checkpoints at the start of user messages (and always at the last one) and a few tokens before the prompt end. So a byte-identical system prompt and an unchanged earlier conversation are reused, and an edit anywhere falls back to the previous checkpoint. **`--cache-reuse 256` cannot help a hybrid and should be set to 0 for H-Tiny.** Slot save/restore works and keeps checkpoints.

### Cited Findings
- **Checkpoint restore and fallback.** When the cached state's `pos_min` is past the reuse point, the server searches the slot's checkpoints for one at or before it and restores it (`load_tgt(... PARTIAL_ONLY)`). If none fits it logs "forcing full prompt re-processing due to lack of cache data (likely due to SWA or hybrid/recurrent memory …)". Checkpoints covering tokens not shared with the new prompt are erased [source @b11982, [server-context.cpp](https://github.com/ggml-org/llama.cpp/blob/8a199ad31/tools/server/server-context.cpp)].
- **Which models get checkpoints.** Checkpoints are made only for completion tasks, and only when the model can't do partial removal (FULL), has bounded rollback (RS), or uses SWA [source @b11982, same file].
- **Where checkpoints are placed** [source @b11982, same file]:
  - At a batch that starts a user message (`spans.is_user_start`).
  - Always at the last user message.
  - Near the prompt end: two checkpoints at `4 + n_ubatch` and `4` tokens before it ([PR #20288](https://github.com/ggml-org/llama.cpp/pull/20288), merged 10 Mar 2026, which says a tester saw time to first token drop "quite a lot").
  - Checkpoints closer together than `--checkpoint-min-step` (default 8192) are skipped, unless it is the first one, the last user message, or near the end.
- **Defaults on b11982:** `-ctxcp` 32, `-cms` 8192, `-cram` 8192 MiB, `--cache-reuse` 0 [repo-measured, `--help`, in inference_speed.md].
- **Why `--cache-reuse` isn't switched off for hybrids.** It is disabled only when `llama_memory_can_shift()` is false. For hybrid memory, `get_can_shift()` returns the attention cache's answer ("Shifting is trivially supported for recurrent"), so it stays enabled for Granite hybrid ([llama-memory-hybrid.cpp @b11982](https://github.com/ggml-org/llama.cpp/blob/8a199ad31/src/llama-memory-hybrid.cpp)). A hybrid's `seq_pos_min` is "the max of the two caches' min values" (same file).
- **What chunk reuse does.** It moves matching chunks with `seq_rm(head_p, head_c)` and `seq_add(...)`, ignoring the return values [source @b11982, server-context.cpp]. The recurrent `seq_rm` cannot erase a middle range of a Mamba state (llama-memory-recurrent.cpp, quoted in the speculation section).
- **Hybrid cache bugs elsewhere.** Earlier versions forced full re-processing on every request for Nemotron-H ([issue #16416](https://github.com/ggml-org/llama.cpp/issues/16416)) and LFM2 ([issue #16491](https://github.com/ggml-org/llama.cpp/issues/16491)). Qwen3.5/3.6 still showed it in Aug 2026, and the issue was closed stale ([issue #24714](https://github.com/ggml-org/llama.cpp/issues/24714), via small_models.md).
- **Slot files.** b11982 appends context checkpoints to the slot save file after the state payload (`save_slot_checkpoints` / `load_slot_checkpoints`). A checkpoint loaded from a file that fails to load falls back to full re-processing [source @b11982, server-context.cpp].
- **How to check hits.**
  - Responses carry `"cache_n"`, "number of prompt tokens reused from cache", with total context = `prompt_n + cache_n + predicted_n` ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)).
  - `LLAMA_SERVER_SLOTS_DEBUG=1` prints the old and new tokens around a prefix mismatch [source @master, server-context.cpp].

### Inferences
- **What hits for Jazari's turn shapes** [estimate from the logic above]:
  - Follow-up in the same conversation: the old prompt is a prefix of the new one, and the near-end checkpoint (old end − 4 tokens) is restored. Only the previous answer, about 4 tokens, and the new question are prefilled. This fits the 12 s follow-up [repo-measured] being much faster than the 47 s first answer after `/read`.
  - New conversation with the same system prompt: the checkpoint at the first user-message start covers the system prompt, if that checkpoint survived the 32-entry list and `-cram`. The host prompt cache (`-cram`) holds earlier prompts' states with their checkpoints.
  - Anything the gateway inserts **before** the history (project note, law-book passages, retrieved snippets) that differs between turns sends the restore back to the system-prompt checkpoint, and the whole history is re-read. On a hybrid this costs more than on a dense model: there is no partial match and no `--cache-reuse` rescue. Put variable material at the end, inside the latest user message.
- **`--cache-reuse` on H-Tiny** [estimate].
  - At best it does nothing: the restore then rolls back to a checkpoint before the shifted chunks.
  - At worst, a checkpoint taken from the *previous* prompt at a position inside the shifted region could be paired with shifted attention KV, giving a recurrent state that belongs to different tokens.
  - The `n_tokens > n_prefix` erase rule may prevent that, but I could not confirm it from reading alone.
  - Set `--cache-reuse 0` when the model is hybrid (Tuning and ServerLauncher hard-code 256); keep 256 for dense Qwen3/Granite 4.1.
- **Checkpoint memory.** At about 54 MiB each [estimate], the default `-ctxcp 32` could use about 1.7 GiB per slot, which is more than `-cram 2048` holds. `-ctxcp 8` (system prompt, a few user turns, two near-end) is enough for single-user chat. Lowering `-cms` (for example to 1024) only matters if mid-history checkpoints are wanted; with H-Tiny's 8 KiB/token attention KV the cost is the 54 MiB recurrent copy, not KV.
- **Slot save/restore gain.** It saves `system_prompt_tokens / pp_rate` on the first question after each server start. For example, 800 tokens at about 80 tok/s is about 10 s [estimate; H-Tiny CPU pp rate needs measuring]. The file is about 54 MiB plus 8 KiB × tokens [estimate].
- **Measuring on the T480:** run two identical requests and check that `cache_n` on the second ≈ prompt length − 4. Then change one character in the middle and check that `cache_n` drops to the last checkpoint before it.

### Gaps
- Whether a checkpoint is also made at the end of the *generated* answer (which would save re-reading the answer on the next turn) was not traced in the source.
- No measured hit/miss time-to-first-token for any hybrid on CPU.
- I did not run a `--cache-reuse` correctness test; the risk above comes from reading the source.

## Quantization on an AVX2 laptop CPU (no AVX-512), for a hybrid MoE

### Takeaway
On AVX2, runtime repacking covers only **Q4_0, Q4_K, IQ4_NL and MXFP4**, for both dense matmuls and MoE expert matmuls (`MUL_MAT_ID`). Q5_K, Q6_K and Q8_0 are repacked only on ARM and RISC-V, and IQ4_XS not at all. A stock Q4_K_M keeps some Q6_K tensors that are never repacked, so an all-Q4 file is the remaining format lever, mostly for prompts. Answer speed is bandwidth-bound on about 1B active parameters, so smaller formats help answers only in proportion to bytes read. KV-cache quantization is irrelevant here: only 4 of 40 layers have a KV cache, and the Mamba state is not touched by `-ctk/-ctv`.

### Cited Findings
- **AVX2 repack targets** in `ggml_repack_get_optimal_repack_type`, all needing `ne[1] % 8 == 0` [source @master, [repack.cpp](https://github.com/ggml-org/llama.cpp/blob/master/ggml/src/ggml-cpu/repack.cpp)]:
  - Q4_0 → `q4_0_8x8_q8_0`; Q4_K → `q4_K_8x8_q8_K`; IQ4_NL → `iq4_nl_8x8_q8_0`; MXFP4 → `mxfp4_8x8_q8_0`.
  - Q2_K repacks only with AVX-512.
  - Q5_K and Q6_K repack only with NEON, and Q8_0 only with NEON or RVV.
- **MoE coverage.** `supports_op` accepts `GGML_OP_MUL_MAT_ID` with 3-D repacked weights, so MoE expert tensors are repacked like dense ones (same file).
- The earlier notes found 27% of 4.1 3B Q4_K_M's bytes in Q6_K, not repacked, and `CPU_REPACK` present in the Haswell DLL [repo-measured, inference_speed.md].
- **Old AVX2 data.** An EPYC 7702 run with a Qwen2 3B is shown on bartowski's cards: Q4_K_M faster than Q4_0 at pp512 (about 301 vs 204 t/s), about equal at pp1024 and pp2048, and slower at tg (48%). This **predates** the current Q4_0 8x8 and Q4_K 8x8 repack, so treat it as historical ([bartowski card](https://huggingface.co/bartowski/huihui-ai_Qwen3-14B-abliterated-GGUF)).
- **Quality cost**, Mellum's card, as a reference for a 2026 MoE: Q8_0 KLD 0.008 with 96.1% top-token match; Q4_K_M KLD 0.075 with 88.0% ([JetBrains GGUF card](https://huggingface.co/JetBrains/Mellum2.1-12B-A2.5B-Thinking-GGUF), via small_models.md).
- **KV per token.** H-Tiny's attention KV is about 8 KiB/token, because only 4 of 40 layers use attention [repo, `DeviceTuner.LayersThatFit` comment from GGUF metadata]. q8_0 KV slowed CPU prompts 11–19% in one small test ([edge-cpu-gguf-tuner](https://clawhub.ai/orionshaowswmw/edge-cpu-gguf-tuner), via llamacpp_runtime_tuning.md).

### Inferences
- **Byte arithmetic for H-Tiny.** At about 10 tok/s and about 12–13 GB/s effective bandwidth [repo-measured for this laptop], each answer token reads roughly 1 GB. That is consistent with about 1B active parameters at about 4.5 bits plus the shared and attention weights. An all-Q4 file that removes the Q6_K share might give answers up to about +5–10% [estimate]. The bigger effect should be on prompt speed, where repacked GEMM matters [estimate +5–25%, unmeasured on this CPU].
- **How to make an all-Q4 file:**
  - Download a Q4_0 (if a Granite H-Tiny Q4_0 exists; unchecked, Hugging Face was blocked here).
  - Or build one from BF16: `llama-quantize --pure granite-4.0-h-tiny-BF16.gguf h-tiny-Q4_K-pure.gguf Q4_K_M`. `--pure` disables the k-quant mixture.
  - Or try `MXFP4_MOE`, which leaves non-expert tensors at higher precision and puts experts in MXFP4, now repacked on AVX2.
  - Quality for a non-QAT model in MXFP4 is unknown. Judge any of these on the 45-question eval set, not perplexity alone.
  - Mamba-2 `ssm_*` small tensors and norms stay F32 in normal quantization (estimate), so they are not a lever.
- **Q8_0 drafts and small models.** Q8_0 is not repacked on AVX2. A Q8_0 350M draft runs on the generic path, and a Q4_0 draft would be about 45% fewer bytes plus repacked [estimate]. This only matters if speculation is ever re-enabled, and the draft runs on the MX150 anyway.
- **IQ4_XS: skip.** Not repacked on AVX2, with slower CPU prompts in older tests (via llamacpp_runtime_tuning.md).
- **KV quantization: skip.** At 16k context, f16 KV is about 128 MiB; q8_0 would save about 64 MiB at a likely prompt cost [estimate].
- **Measuring on the T480:**
  - Speed: `llama-bench -m h-tiny-Q4_K_M.gguf,h-tiny-alt.gguf -dev none -t 4 -p 512 -n 64 -r 5`, then `--repack 0,1` to isolate what repack gives.
  - Quality: `eval/run_eval.py` for the score.
  - Do the speed runs cold-started and plugged in; thermal state alone moves prompts about 10% [repo-measured].

### Gaps
- H-Tiny's actual tensor-type breakdown, and whether IBM, Unsloth or bartowski publish Q4_0, IQ4_NL or MXFP4 files: not verified, because Hugging Face was unreachable from this session.
- No 2026 AVX2 benchmark of repacked Q4_0 vs Q4_K_M vs MXFP4 on a MoE or hybrid model was found.
- Mamba-2 SSM op (`ssm_scan`, `ssm_conv`) CPU cost share and its thread scaling: no source found. It is not quantizable either way.

## Other llama.cpp flags and builds: threads, batch, mmap/mlock, flash attention, BLAS, iGPU

### Takeaway
The one untried free flag is **`-tb`**. Jazari passes only `-t`, so prompts are processed on the same half-core count that suits answer generation. Flash attention is already measured by the tuner, and `--poll` is a no-op on this OpenMP build. BLAS and the UHD 620 have no case here. The only new device idea is MoE-aware placement (`-cmoe`) on the MX150, untested.

### Cited Findings
- **`-tb`.** `-tb, --threads-batch N`: "number of threads to use during batch and prompt processing (default: same as --threads)" ([arg.cpp @master](https://github.com/ggml-org/llama.cpp/blob/master/common/arg.cpp)).
- **MoE placement flags** on b11982 [source, arg.cpp @8a199ad31]: `-cmoe, --cpu-moe` "keep all Mixture of Experts (MoE) weights in the CPU", and `-ncmoe, --n-cpu-moe N` for the first N layers. `-ot` and `-otd` override tensor placement for the main and draft models.
- **`--poll` and spin control.** `--poll` is ignored under OpenMP, and `KMP_BLOCKTIME` is the real spin control [source @b11982, inference_speed.md]. `KMP_BLOCKTIME=0` measured −20–28% on answers [repo-measured].
- **Load mode.** Use `-lm auto|none|mmap|mlock|mmap+mlock|dio`; there is no `--mlock` or `--no-mmap` on b11982 [repo-measured, inference_speed.md].
- **BLAS.** It "doesn't affect the generation performance" and may help prompt processing at batch >32 ([build.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/build.md)).
- **UHD 620 and OpenVINO.**
  - UHD 620 full offload of 4.1 3B: 12.5 / 4.1 tok/s against CPU 28.8 / 6.2 [repo-measured].
  - OpenVINO is validated only on Core Ultra ([OPENVINO.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/backend/OPENVINO.md), via inference_speed.md).
- **MX150 on H-Tiny.** Partial offload measured 60 tok/s reading but 6.6 writing, against 10.1 writing on CPU [repo-measured, `DeviceTuner.cs`].

### Inferences
- **`-t 4 -tb 8`.** Expected prompt gain +10–20% from the 4.1 sweep [estimate], with answers unchanged because `-t` stays 4. The cost is more heat and less spare CPU for the user while a prompt is read, and prompt bursts are short. Add `-tb` to `Tuning.ServerArgs` and to the tuner's measurement. For the tuner, `llama-bench` has no separate `-tb`, so measure the pp row at `-t 8` and the tg row at `-t 4`.
- **`-ub` for MoE.** Expert matmuls on a 4-core CPU benefit from more tokens per expert per ubatch. Test `-ub 256,512,1024` with `-b 2048` [estimate 0–10%]. The default 512 is probably near-optimal.
- **`-cmoe` on the MX150.** Experts stay in RAM, and the dense parts (attention, Mamba-2, shared experts, embeddings) go to the 2 GB GPU. This could keep the MX150's prompt advantage, but every answer token then crosses PCIe twice per layer (80 syncs), and writing already fell from 10.1 to 6.6 with partial offload. Expect answers ≤ CPU [estimate]. Worth one `llama-bench -dev Vulkan1 -ngl 99 -cmoe -t 4` run only if prompt time after `/read` (47 s) becomes the bottleneck. Check that the Vulkan backend supports `SSM_SCAN` and `SSM_CONV`; otherwise those ops bounce back to the CPU (unchecked).
- **`-lm mlock`.** It protects time-to-first-token after the user's IDE and browser push the 4+ GB model out of RAM. It is not a throughput gain. Test it if first answers after idle are slow.
- **Builds.** The pinned Vulkan zip includes the Haswell CPU backend with repack. A native, OpenBLAS or oneDNN build is expected to give under 3% [estimate, inference_speed.md]. A newer build (b12014+) brings no known CPU speedups.

### Gaps
- No `-tb` measurement exists for H-Tiny on this laptop [measure].
- Vulkan op coverage for Mamba-2 SSM ops on the MX150 and UHD 620 not checked.
- No source quantifies MoE `-ub` scaling on a 4-core AVX2 CPU.

## Windows-specific: power plan, thermals, priority (beyond existing notes)

### Takeaway
Nothing new and credible was found beyond `windows_thermal_power.md` and inference_speed.md. Their measured or decided points stand: low priority rejected, AC power, Best performance slider, and checking ThrottleStop limit reasons. The one H-Tiny-specific note is about thread count, which is already capped at half.

### Cited Findings
- `--prio -1` cut idle writing 21% (9.4 → 7.5 tok/s); at normal priority other work kept 80% of its speed [repo-measured, model-choice results].
- On battery, the T480's multi-core score fell 19% in Notebookcheck's test ([Notebookcheck](https://www.notebookcheck.net/Lenovo-ThinkPad-T480-i7-8550U-MX150-FHD-Laptop-Review.293682.0.html), via inference_speed.md).
- Thermal state alone moved prompt speed about 10% on this laptop [repo-measured].

### Inferences
- Half the threads (4) leaves headroom that keeps clocks higher during answers. `-tb 8` spends that headroom only during prompt reading [estimate].
- For reproducible A/B runs of the levers above: plugged in, a 2-minute cooldown between runs, the same prompt order, and Jazari closed. Report the median of at least 3 runs.

### Gaps
- No measurement of EcoQoS or Efficiency-mode effects on H-Tiny specifically. The earlier notes cover the mechanism only.
