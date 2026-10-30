# MyOwnLLM — design

Date: 2026-10-30 · Owner: Eni-M · Status: draft for approval

## Intent

A personal, code-only assistant that runs on Eni-M's own laptop and is specialized in **ASP.NET Core, C#, .NET, networking, system design, system architecture and cybersecurity**. It is a side project with two equal purposes: a tool used every day while coding, and a playground for experimenting with how a model is tuned and slimmed.

The guiding rule: **the lightest model that is still not stupid.** "Not stupid" has a concrete floor: on tasks taken from Eni-M's own repositories, the model's code must **pass the same build and tests** as Eni-M's original **and look close to what Eni-M wrote**.

## Goals

1. Run fully locally on the target laptop. **Latency target (Eni-M):** a question like "Where are we left at?" gets a short summary back in **2 minutes at most**. Chat is the primary use; inline completion is secondary and best-effort.
2. Specialize the model for the domains above, measurably, against Eni-M's own repos.
3. Shrink compute, RAM and heat as far as possible while staying above the quality floor.
4. Expose the model through an OpenAI-compatible HTTP API protected by API keys, for Eni-M's own personal use only (localhost; no other devices or users).

## Non-goals

- Training a model from scratch.
- General knowledge, chat about non-code topics, other programming ecosystems (Java, frontend frameworks) beyond what the base model already knows.
- Renaming/rebranding (lowest priority; may come later, license permits it).
- Multi-user hosting or anything paid.

## Anti-goals (failure even if it "works")

- A smaller/faster model that falls below the quality floor.
- Any use of employer code. **General-CRM and other company repositories are never used** for data, evaluation or tuning.
- Changes that make the laptop unusable while the model runs.
- Spending money (budget is $0).

## Constraints

**Hardware (measured):** Intel 8th-gen U-series 4C/8T 15 W, 16 GB DDR4-2400, NVIDIA MX150 2 GB (Pascal), Intel UHD 620, Windows 11.

| Model (Q4_K_M, CPU, `-t 8 --poll 0`) | Prompt read | Output |
|---|---|---|
| Granite 4.1 3B | 30–47 tok/s | 5–7 tok/s |

**RAM budget: up to 6–8 GB** of the 16 GB (Eni-M). Measured llama-server working set for Granite 4.1 3B Q4_K_M: 3.8 GB at 4k context, 4.1 GB at 8k, 4.7 GB at 16k, 6.0 GB at 32k. RAM is not the bottleneck (memory bandwidth and CPU are), so the spare budget goes to the host-RAM prompt cache (`--cache-ram`) and longer context, not to a bigger model or a higher-precision quant, which would be slower.

Speed swings ~10–30 % with laptop temperature. Reading a 4,000-token context takes ~1.5–2 min, so **input tokens are the main cost**, and each output token costs ~6× an input token.

**Training compute:** free **Google Colab** T4 (16 GB, fp16 only, sessions up to ~12 h, no published quota). Eni-M allows uploading the private repo code to Colab for training. Kaggle is not part of the plan.

**Latency budget for the 2-minute target:** at ~35 tok/s reading and ~6 tok/s writing, a 150-token summary costs ~25 s to write, leaving ~95 s, which is about **3,000 uncached input tokens**. Anything beyond that must come from the prompt cache or be summarized ahead of time.

**Data sources (Eni-M GitHub only):** C# repos **EduCore, WareFlow, LearningRoadmap** (all private). Other Eni-M repos are Java/TypeScript/HTML and out of scope. Because the repos are private, contamination of the base model's pretraining is unlikely.

**License:** Granite 4.1 is Apache 2.0 (fine-tune, rename, serve all allowed). Teacher model for data generation: Qwen2.5-Coder-14B (Apache 2.0). Qwen2.5-Coder-3B is excluded (non-commercial license).

## Decided by Eni-M

- **Base model: IBM Granite 4.1 3B**, one model for both chat and fill-in-the-middle (FIM) completion. (Granite 4.0-H Tiny was faster but failed every FIM test; Qwen3-4B was slower.)
- **Domain:** ASP.NET Core, C#, .NET, networking, system design/architecture, cybersecurity. Code-focused only.
- **Quality floor:** benchmark built from Eni-M's own repos; generated code must pass the same `dotnet build` and tests as the original and be close to Eni-M's original code.
- **"Where are we left at?" answered with a short summary in ≤ 2 minutes.**
- **Colab** for training; private repo code may be uploaded there.
- **API keys for Eni-M only**, personal use.
- **Requests are queued and fed to the model one at a time; output is delivered complete** rather than streamed. *(Research note: buffering saves no compute and adds a blank wait — about 50 s for a 300-token answer. The design keeps buffered delivery as the default, with streaming available as an option the client can request.)*
- **The PC is tuned when the model is in use** (power, heat, background load).
- **Skipped after research:** Next-Concept Modeling, Nested Learning, CompreSSM — none can be applied to an already-trained 3B dense model on a T4 or run in llama.cpp. Nested Learning's goal (learning without forgetting) is approximated by LoRA with replay data and periodic LoRA refreshes.
- Renaming is lowest priority.

## Architecture

```
 client (terminal CLI, editor plugin, any OpenAI-compatible tool)
        │  HTTPS-less localhost, API key
        ▼
 Gateway  (ASP.NET Core, .NET 10)
   - API key check
   - priority queue (System.Threading.Channels): chat > completion; newest completion replaces stale ones
   - cancellation passed through to the model
   - prompt assembly: stable prefix → repo map → retrieved snippets → question
   - exact-match response cache (HybridCache)
   - buffered by default, streaming on request
        │  localhost
        ▼
 llama-server (llama.cpp CPU build), Granite 4.1 3B GGUF
   -np 1, prompt cache on, --cache-reuse, -c 8192
   endpoints used: /v1/chat/completions, /infill
```

**Project state note (serves the 2-minute target).** The Gateway keeps a short, rolling "where we are" note per project (current task, recent changes, open problems, last decisions; capped at ~800 tokens). It is updated in idle time after each exchange and from `git log`/`git diff --stat` of the open repo, and it sits in the stable prompt prefix. "Where are we left at?" then needs only the note plus the question, well inside the ~3,000-token budget, so no repo files have to be re-read.

Context building: Roslyn-generated signature skeletons ("repo map", ~1k tokens) plus SQLite FTS5 keyword search over the open solution, instead of pasting whole files. A stable prompt prefix maximizes llama.cpp prompt-cache hits.

## Phases

Each phase ends with a measurable result before the next starts.

**Phase 0 — Benchmark from own repos.**
- Extract tasks from EduCore, WareFlow, LearningRoadmap: (a) FIM — blank out a method body or block, model fills it, check with `dotnet build` and existing tests where present; (b) method regeneration from signature + doc comment, judged by build/tests and diff to the original; (c) 50–200 short Q&A items on the domains (written/approved by Eni-M).
- Hold out tasks from the most recent commits as an untouched test split.
- Add MultiPL-E C# as a "general skill didn't collapse" alarm.
- Record the untuned Granite 4.1 3B score — this is the baseline every later model must beat or match.

**Phase 1 — Run it locally (usable tool).**
- llama-server + the Gateway + a minimal terminal client, with the queue, cancellation, cache and API keys.
- PC-tuning checklist applied by Eni-M (see below).
- Done when: chat and FIM work end to end through the Gateway with a key, and Phase 0 baseline runs through the same path.

**Phase 2 — Slim it (no training).**
- Build a domain importance matrix (`llama-imatrix`) from C#/.NET text; produce Q4_K_M, Q4_0, IQ4_XS, IQ3_M variants.
- Measure each: KL-divergence vs F16, Phase 0 score, `llama-bench` speed and RAM.
- Keep the smallest/fastest variant that stays above the floor.

**Phase 3 — Specialize it (free T4).**
- Generate training data with Qwen2.5-Coder-14B (AWQ) on Colab; keep only samples that compile/pass.
- Add FIM-format examples and examples drawn from the training split of Eni-M's repos.
- QLoRA in Unsloth (fp16, rank 16–64), with ~5 % general code "replay" data to limit forgetting.
- Export GGUF, re-run Phase 2's quantization, re-score. Keep only if it beats the Phase 2 model on the repo benchmark without failing the MultiPL-E alarm.

**Phase 4 — Shave further (optional experiment).**
- Drop a contiguous block of 15–25 % of layers, "heal" with a short QLoRA pass on teacher outputs.
- Keep only if it stays above the floor and is measurably faster.

**Phase 5 — Preference memory.**
- Preferences live in a stable prompt prefix the Gateway prepends.
- Periodically refresh the LoRA on accumulated accepted answers plus replay data.

## PC tuning checklist (applied by Eni-M; Claude does not change system settings)

- Don't put llama-server in Windows Efficiency mode.
- Test turbo off / max processor state 99 % with `llama-bench`; keep whichever gives better tokens per heat.
- Exclude `*.gguf` and the models folder from Defender scanning and Search indexing.
- Raise the laptop or use a cooling pad; temperature alone moved speed ~10 %.
- Close heavy background apps while long jobs run.

## What's left to the builder

- Exact llama-server flags after checking `llama-server --help` on the pinned build (flag names changed in 2025–2026).
- Prompt templates and token budgets per request type (completion: ~32–64 output tokens; chat: capped, diffs instead of whole files).
- Benchmark harness implementation details and scoring weights.
- Data mix ratios and LoRA hyperparameters within the ranges above.

## Calls made by Claude (Eni-M to check)

- **Scoring the floor:** a task passes only if `dotnet build` succeeds, the original tests pass, **and** similarity to the original is above a threshold (normalized token-level edit similarity; threshold set from Phase 0 data, starting point 0.7). The untuned Granite score is recorded as the baseline; it is expected to sit below the floor, and each phase is judged by how far it closes the gap.
- **MultiPL-E alarm:** a candidate fails if its MultiPL-E C# pass@1 drops more than 3 points below the untuned model.
- **Split rule:** per method. The newest ~20 % of methods (by last-commit date) form the test split, and their whole files are excluded from training data.
- **Repo readiness check is the first Phase 0 step:** confirm each repo builds standalone (no DB, secrets or private feeds) and count tests. Repos without tests are scored on build + similarity only, and flagged.
- **Q&A items:** graded by Eni-M by hand with a short rubric; not part of the unattended benchmark run.
- **First client:** a terminal CLI (Eni-M's original request), which passes the repo path explicitly. Editor integration (llama.vscode or similar, using `/infill`) comes after Phase 1, so completion is best-effort.
- **Laptop stays usable:** llama-server runs with `-t 6 --poll 0` (leaves headroom; measured within ~5 % of 8 threads), RAM capped by `-c 16384` and `--cache-ram 2048`, total ≤ ~7 GB.
- **API:** bound to `127.0.0.1` only, plain HTTP, keys stored as hashes in a local config file; no rate limiting.
- **Phase 5 data:** the Gateway logs prompts, answers and an accept/reject action to a local SQLite file from Phase 1 onward, so LoRA refreshes have data. Logs stay on the laptop except for the training set Eni-M chooses to upload to Colab.
