# Local model runtimes and llama.cpp wrappers: lessons for Jazari's serving, install, update and tuning layers

Research date: 2026-11-03. Claim tags: **[fetched]** = primary page fetched and read this session; **[snippet]** = from search-result summary of the named page, not read in full; **[secondary]** = third-party blog/aggregator; **[inferred]** = my reasoning, not a sourced fact. Items dated before 2025 are flagged "(pre-2025, possibly outdated)".

Jazari baseline used for comparison (from the assignment brief): llama-server Vulkan build, pinned + SHA-256; Granite 4.0-H Tiny Q4_K_M (~4.2 GB) pinned + SHA-256; llama-server started on demand with `--cache-ram 1024 -ctxcp 8 --cache-reuse 256 -np 1`; `jazari tune` = measured llama-bench sweep keyed to a hardware/build fingerprint; ASP.NET gateway with API key, Origin/Host checks, priority queue, response cache; ~8.7 GB server working set on a 16 GB T480 (i5-8350U + MX150 2 GB, which also has an Intel UHD 620 iGPU).

## Project profiles: what each one does, and what Jazari could adopt

### Takeaway
llama.cpp's own llama-server has absorbed most of what the wrappers used to add (auto memory fitting, idle sleep, multi-model routing, Anthropic/Responses APIs, MCP, built-in tools, ngram speculative decoding), so the cheapest wins for Jazari are flags on the binary it already ships, not new gateway code. The wrappers are still ahead on UX: idle unloading by default, one known log folder, no-admin per-user install with clean uninstall, and update checks that never block startup.

### Cited Findings

**llama.cpp llama-server (2025-2026 features)**
- `-fit, --fit [on|off]` "adjusts unset arguments to fit device memory", default `on`; `-fitt/--fit-target` per-device margin default 1024 MiB; `-fitc/--fit-ctx` minimum context that fit may set, default 4096; `-ngl` default `auto` (accepts number, `auto`, `all`) [fetched] — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Fit tunes n_gpu_layers, tensor_split and tensor buffer overrides (including per-layer MoE expert offload to CPU) and may shrink context; a helper `llama-fit-params` prints the equivalent CLI args; example log: 30B MoE on RTX 4090 fitted in ~1 s to `-c 4096 -ngl 48` plus expert overrides [snippet] — [fit-params README mirror](https://huggingface.co/datasets/echodict/llama.cpp/blob/main/tools/fit-params/README.md); [llama-cpp-4 fit docs](https://docs.rs/llama-cpp-4/latest/llama_cpp_4/fit/)
- Host (CPU) memory "is assumed unlimited for `--fit` and memory-based unloading" [fetched] — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `-c` default 0 (= from model); `-fa` default `auto`; `-ctk/-ctv` default `f16`, allowed f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1 [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Prompt cache: `-cram/--cache-ram` default 8192 MiB; `--cache-reuse` default 0; `-ctxcp/--ctx-checkpoints` default 32 per slot; `-cms/--checkpoint-min-step` default 8192 tokens (0 = no minimum); `--cache-prompt` on; `--cache-idle-slots` on (needs `--cache-ram`); `-kvu/--kv-unified` on when slot count is auto; `-np` default -1 (auto) [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Idle sleep: `--sleep-idle-seconds` (default -1, disabled) unloads "the model and its associated memory (including the KV cache)" from RAM; next task auto-reloads; `GET /health`, `/props`, `/models`, `/metrics` do not wake it or reset the timer; `/props` reports `is_sleeping` [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Router mode: start without `-m`; models discovered from `LLAMA_CACHE`, `--models-dir`, `--models-preset` (INI; keys are CLI args without dashes; `[*]` global section; per-model sections; preset-only keys `load-on-startup`, `stop-timeout` default 10 s); `--models-max` default 4; `--models-memory-margin` unloads LRU idle models to keep N MiB free per device; `--models-autoload` default on; `POST /models/load`, `POST /models/unload`; `GET /models` status values `unloaded|loading|loaded|sleeping|downloading|incomplete`, failed loads show `exit_code` [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Slot persistence: `--slot-save-path` (default disabled); `POST /slots/{id}?action=save|restore|erase` with `filename`; `GET /slots?fail_on_no_slot=1` returns 503 when no slot free [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Speculative decoding: `--spec-type` default `none`; values draft-simple, draft-eagle3, draft-mtp, draft-dflash, draft-dspark, ngram-simple, ngram-map-k, ngram-map-k4v, ngram-mod, ngram-cache; `-md` draft model; `--spec-default` enables a default config [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- APIs: OpenAI `/v1/models`, `/v1/completions`, `/v1/chat/completions`, WebSocket `/v1/realtime`; Anthropic `POST /v1/messages` (SSE streaming, tools need `--jinja`, `tool_choice` auto/any/tool, default `max_tokens` 4096) and `POST /v1/messages/count_tokens`; OpenAI `/v1/responses` (implemented by converting to Chat Completions) and `/v1/responses/input_tokens`; docs say the Anthropic endpoint makes no strong compatibility claims [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Structured output: `-j/--json-schema`, `--grammar`; chat endpoint accepts `response_format` `json_object` / `json_schema`; `--jinja` default enabled [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Agent/MCP: `--tools` (default none; built-ins read_file, file_glob_search, grep_search, exec_shell_command, write_file, edit_file, get_info), `--tools-runtime` (docker/podman/ssh), `--mcp-servers-config`/`--mcp-servers-json` (stdio MCP, 30 s timeout), `-ag/--agent` (enables CORS proxy and all built-in tools; default off), web UI `--ui` default on [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Security flags: `--host` default `127.0.0.1`; `--api-key` (comma list) / `--api-key-file`; `--cors-origins` default `*` and the server "reflects any Origin with credentials" (`--cors-credentials` default on); with `--tools` or `--agent` the CORS default becomes `localhost`; `/health` is public [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- The current README lists `--port` default 9931 and all examples use 9931 [fetched] — [README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) (see Gaps: historically 8080)

**Ollama**
- Keep-alive: models stay loaded 5 min by default; `OLLAMA_KEEP_ALIVE` / per-request `keep_alive` (0 = unload now, negative = forever); `ollama stop <model>` [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- Binds `127.0.0.1:11434` by default; CORS allows 127.0.0.1 and 0.0.0.0 origins by default, extend with `OLLAMA_ORIGINS` [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- Flash attention auto-enabled when supported; `OLLAMA_KV_CACHE_TYPE` f16 default, q8_0 ≈ half, q4_0 ≈ quarter, global, needs FA [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- `OLLAMA_NUM_PARALLEL` default 1; `OLLAMA_MAX_LOADED_MODELS` default 3×GPUs (3 on CPU); `OLLAMA_MAX_QUEUE` 512 then 503 [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- GPU placement is estimate-based: "estimates the VRAM a model needs", loads on one GPU if it fits else spreads [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- Default context is VRAM-tiered: <24 GiB → 4k, 24-48 GiB → 32k, ≥48 GiB → 256k; recommends ≥64k for agents/coding; `ollama ps` shows CONTEXT and PROCESSOR split (e.g. "100% GPU") [fetched] — [Ollama context length](https://docs.ollama.com/context-length); the FAQ still says a flat 4096 default [fetched] — [Ollama FAQ](https://docs.ollama.com/faq) (internal doc inconsistency)
- Windows: installer needs no admin, installs to `%LOCALAPPDATA%\Programs\Ollama` (added to user PATH), `/DIR=` to relocate; logs in `%LOCALAPPDATA%\Ollama` as `app.log`, `server.log`, `upgrade.log`; models in `%HOMEPATH%\.ollama`; uninstall via Add/Remove Programs (keeps models if `OLLAMA_MODELS` was moved); standalone `ollama-windows-amd64.zip` can run as a service via NSSM; Vulkan enabled by default; on iGPU+dGPU systems set `GGML_VK_VISIBLE_DEVICES` to the discrete GPU [fetched] — [Ollama Windows](https://docs.ollama.com/windows)
- Updates: Windows app downloads updates automatically, user clicks "Restart to update"; registers as a login item [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)

**LM Studio**
- JIT loading on by default; idle TTL default 60 min for JIT-loaded models, timer resets per request; `ttl` per request field or `lms load <model> --ttl 3600`; models loaded via `lms load` have no TTL; Auto-Evict (default on) keeps at most one JIT model loaded [fetched] — [LM Studio TTL and auto-evict](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict)
- Docs nav lists OpenAI-compat (Chat, Responses, Structured Output, Tool Use), Anthropic-compat Messages, Authentication, "Run LM Studio as a service (headless)", "Using MCP via API" [fetched] — [LM Studio TTL page navigation](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict)
- 0.4.0: `llmster` headless daemon (Windows install via `irm` PowerShell one-liner); `n_parallel` default 4 with unified KV (llama.cpp engine); local MCP servers loaded lazily; `n_cpu_moe` slider; `lms runtime survey` (GPU info); `lms log stream -s runtime` (llama.cpp-level logs); runtime-update release-notes viewer; `lms chat`; breaking API rename in a beta build [fetched] — [LM Studio 0.4.0 changelog](https://lmstudio.ai/changelog/lmstudio-v0.4.0)
- 0.4.1 (Jan 30, 2026) added Anthropic `/v1/messages` [snippet] — [LM Studio 0.4.1 changelog](https://lmstudio.ai/changelog/lmstudio-v0.4.1); a third-party page dates it to 0.4.9 [secondary] — [vantaige.io](https://vantaige.io/fr/ai-tool/lm-studio)
- Known gap: models loaded via an explicit load call (e.g. by a harness "preload") escape TTL/auto-evict [snippet] — [lmstudio-bug-tracker #2051](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2051)

**Jan (janhq)**
- v0.6.10 (Sep 18, 2025): experimental "Auto Optimize" one-click hardware-aware llama.cpp tuning that "automatically detects your CPU, GPU, and memory configuration" (method undisclosed); custom llama.cpp builds importable; backup CDN when GitHub downloads fail; asks before auto-updating llama.cpp [fetched] — [Jan changelog v0.6.10](https://jan.ai/changelog/2025-09-18-auto-optimize-vision-imports)
- v0.8.0 (May 22, 2026): Multi-Token Prediction toggle requires bundled llama.cpp ≥ b9193, older backends disable it with an upgrade hint [snippet] — [Jan v0.8.0](https://www.jan.ai/changelog/2026-05-22-jan-v0.8.0)
- v0.8.1 (May 29, 2026): `check_for_updates` toggle (default on) gates background fetches; `auto_update_engine` requires it; backend settings moved to a JSON file [snippet] — [Jan v0.8.1](https://www.jan.ai/changelog/2026-05-29-jan-v0.8.1)
- v0.8.2 (Jun 1, 2026): app update check toggle; startup backend update check and dependency verification deferred until after first paint so cold start is not blocked [snippet] — [Jan v0.8.2](https://jan.ai/changelog/2026-06-01-jan-v0.8.2)
- Local API server: default `127.0.0.1:1337`, prefix `/v1`; API key optional (empty disables auth); "Trusted hosts" list; CORS enabled by default; verbose request logs on by default; "Execute Tools on Server" off by default; warns about `0.0.0.0` [fetched] — [Jan API server docs](https://docs.jan.ai/desktop/api-server)

**KoboldCpp**
- 1.124.2 (31 Oct): `/mcp` wizard for the bundled Agent; passwords masked in terminal load logs. 1.123.1 (13 Oct): agent MCP tool calls limited to localhost; approval modes on/auto/off; batched (parallel) mode supports grammar, tool calls, vision. 1.122.1 (26 Sep): bundled Agent (`--agent`), MCP from mcp.json, `--autoswapthreshold`. Ships one Windows exe per hardware class: `oldpc` (CUDA 11 + AVX1), `nocuda`, Vulkan recommended for AMD; Anthropic and Ollama API emulation since 1.115.2/1.117.1 [fetched; year not shown on page, assumed 2026] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)
- `--gpulayers -1` auto-estimate introduced in 1.72, flagged experimental ("manual selection still gives better results") (pre-2025, possibly outdated) [snippet] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases); a ROCm fork later accounts for in-use VRAM [snippet] — [koboldcpp-rocm releases](https://github.com/YellowRoseCx/koboldcpp-rocm/releases)

**llamafile (Mozilla.ai)**
- 0.10.0 (Mar 20, 2026) rebuilt the core to track llama.cpp more easily; adds TUI and `--server`; Metal (Dec 2025) and Linux CUDA (Feb 2026) back; Windows GPU reported missing; Windows caps executables at 4 GB so most bundled llamafiles need external weights [secondary] — [Help Net Security](https://www.helpnetsecurity.com/2026/03/20/llamafile-0-10-0-released/); [releases](https://github.com/mozilla-ai/llamafile/releases)
- 0.10.4 added `transcribefile` speech-to-text; 0.10.5 eased upstream sync [secondary] — [WebProNews](https://www.webpronews.com/mozillas-llamafile-0-10-4-adds-speech-to-text-in-one-file-syncs-with-latest-ai-advances/)

**LocalAI**
- `--max-active-backends N` (LRU eviction; `=1` replaces deprecated `--single-active-backend`); eviction skips models with in-flight requests and waits; idle watchdog `--enable-watchdog-idle`, `--watchdog-idle-timeout` default 15m; busy watchdog default 5m kills backends stuck busy [snippet] — [LocalAI VRAM management](https://localai.io/docs/advanced/vram-management/)
- A user asked for "unload idle only when the new model doesn't fit" policy [snippet] — [LocalAI #1729](https://github.com/mudler/LocalAI/issues/1729)
- 2026 versions 4.0-4.3 (agent hub, cluster mode, speech/vision), "60+ backends" from a Backend Gallery installed on the fly — only seen on unofficial mirrors/forks [snippet, unverified] — [mirror](https://github.com/inquam/LocalAI)

**GPT4All (Nomic)**
- Latest release listed as v3.10.0 dated Feb 25, 2026 [secondary] — [aiwiki](https://www.aiwiki.ai/wiki/gpt4all/raw); a review says release pace slowed since early 2025 [secondary] — [PromptQuorum](https://www.promptquorum.com/power-local-llm/gpt4all-review). See Gaps (date likely wrong).

**Microsoft Foundry Local (added: Windows-native, GA 2026)**
- Detects device hardware and picks the best ONNX Runtime execution provider per model, CPU fallback; on Windows uses WinML to acquire EP plugins via the OS/Windows Update; models download from the Foundry catalog to a local cache, then run offline [snippet] — [What is Foundry Local](https://learn.microsoft.com/en-us/azure/foundry-local/what-is-foundry-local); [architecture](https://learn.microsoft.com/en-us/AZURE/foundry-local/concepts/foundry-local-architecture)
- GA Apr 9, 2026; downloads resumable; shared cross-app cache planned [snippet] — [Foundry Local GA blog](https://devblogs.microsoft.com/foundry/foundry-local-ga/)

### Inferences
- [inferred] Jazari already ships everything needed for the top three upgrades: `--sleep-idle-seconds` (RAM), `--api-key`/`--cors-origins` on the internal llama-server (security), and `--slot-save-path` (cold-start latency). None requires a new dependency.
- [inferred] Foundry Local and llamafile are poor fits (ONNX catalog; no Windows GPU / 4 GB exe limit). GPT4All and LocalAI offer little Jazari doesn't have, except LocalAI's busy watchdog idea.
- [inferred] KoboldCpp's per-hardware exe split (oldpc / nocuda / Vulkan) mirrors Jazari's pinned-Vulkan choice and confirms Vulkan as the safe single default on mixed Intel/NVIDIA laptops.

### Gaps
- Could not confirm Ollama's 2025-2026 engine internals (its own "new engine" vs llama.cpp), whether Ollama added an Anthropic-compatible endpoint, or Ollama download resume/digest behaviour from a primary page this session.
- LM Studio's model-loading guardrail algorithm and memory estimator are not documented on pages fetched; LM Studio's closed-source status not re-verified.
- AMD Lemonade Server and Docker Model Runner not researched (time budget).

## Hardware detection and automatic configuration: estimate vs measure

### Takeaway
Every project surveyed estimates rather than benchmarks: llama.cpp `--fit` (now default-on), Ollama VRAM estimation and VRAM-tiered context, KoboldCpp `-1` layers, Jan "Auto Optimize", Foundry Local EP selection. Jazari's measured, fingerprint-keyed `jazari tune` is genuinely more rigorous; the lesson is to use estimation as the zero-wait default and measurement as the refinement, and to add MoE expert offload as a tuning axis.

### Cited Findings
- llama.cpp `--fit` default on, 1024 MiB margin per device, min context 4096, `-ngl auto`, `-fa auto` [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Fit can place MoE expert tensors on CPU per layer and reduce context to fit (example 40960 → 4096) [snippet] — [fit-params README mirror](https://huggingface.co/datasets/echodict/llama.cpp/blob/main/tools/fit-params/README.md); [llama-cpp-4 fit](https://docs.rs/llama-cpp-4/latest/llama_cpp_4/fit/)
- Ollama: VRAM estimate, FA auto, KV type global env var, context tier by VRAM (<24 GiB → 4k) [fetched] — [Ollama FAQ](https://docs.ollama.com/faq); [context length](https://docs.ollama.com/context-length)
- LM Studio exposes `n_cpu_moe` as a slider and `lms runtime survey` for GPU info [fetched] — [LM Studio 0.4.0](https://lmstudio.ai/changelog/lmstudio-v0.4.0)
- Jan Auto Optimize: experimental, detects CPU/GPU/RAM, applies "best parameters" — method undisclosed [fetched] — [Jan v0.6.10](https://jan.ai/changelog/2025-09-18-auto-optimize-vision-imports)
- KoboldCpp auto layers flagged experimental, manual better (pre-2025) [snippet] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)
- Ollama on mixed iGPU/dGPU Windows systems: pin the discrete GPU with `GGML_VK_VISIBLE_DEVICES` when Vulkan picks an unstable iGPU [fetched] — [Ollama Windows](https://docs.ollama.com/windows)
- Foundry Local auto-selects EP per model with CPU fallback [snippet] — [Foundry Local](https://learn.microsoft.com/en-us/azure/foundry-local/what-is-foundry-local)

### Inferences
- [inferred] Adopt: before `jazari tune` has run (or after its fingerprint goes stale), launch llama-server with no `-ngl` and let `--fit on` + `-fa auto` choose. That removes Jazari's own free-VRAM layer arithmetic from the untuned path and gives a sane first run. Keep `jazari tune` as the measured upgrade.
- [inferred] Adopt: seed `jazari tune`'s GPU candidates from `llama-fit-params` output instead of Jazari's own free-VRAM formula — fewer code paths, upstream keeps it correct as model architectures change.
- [inferred] Adopt: add an MoE axis to `jazari tune` — "attention/shared layers + KV on MX150, experts on CPU" (`--n-cpu-moe` / `--cpu-moe`, or what `--fit` produces) versus plain layer split. Granite 4.0-H Tiny is MoE; with 2 GB VRAM this split is the one most likely to beat whole-layer offload. Needs measuring; may still lose to CPU-only on the MX150.
- [inferred] Adopt: pin Vulkan to the MX150 with `GGML_VK_VISIBLE_DEVICES` when tune selects it, so an iGPU enumeration change cannot silently move work to the UHD 620. Add the Vulkan device list to the tuning fingerprint if not already there.
- [inferred] Note: the 1024 MiB default fit margin is half of the MX150's 2 GB; Jazari should pass `-fitt` explicitly (smaller, e.g. 256-384 MiB) or fit will offload almost nothing on this GPU.
- [inferred] Note: llama-bench measures raw pp/tg, not the server's real workload (prompt reuse, checkpoints, long context). A short end-to-end check through llama-server after the sweep (one cached + one uncached request) would catch configs that bench fast but serve slow.
- [inferred] Jazari is clearly better here: none of the others measure on the user's machine or invalidate settings when hardware/build changes.

### Gaps
- No primary detail on how Ollama or LM Studio estimate per-layer memory for hybrid Mamba/transformer models.
- Whether `--fit` handles recurrent-state memory of Granite 4.0-H correctly not verified.
- CUDA vs Vulkan speed on the MX150 (Pascal) not researched; KoboldCpp's `oldpc` CUDA 11 build suggests CUDA 11 is the path for old NVIDIA cards.

## Model management: downloads, checksums, updates, aliases, keep-alive and unloading

### Takeaway
Idle unloading is universal and on by default elsewhere (Ollama 5 min, LM Studio 60 min JIT TTL, LocalAI 15 min watchdog), while Jazari keeps ~8.7 GB resident; llama-server's own `--sleep-idle-seconds` gives Jazari this for free without killing processes. Jazari's SHA-256 pinning is stricter than most; resumable download is the main install-side gap.

### Cited Findings
- Ollama keep-alive 5 min default, per-request override, `ollama stop` [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- LM Studio JIT TTL 60 min, resets per request, auto-evict on [fetched] — [LM Studio TTL](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict)
- LocalAI idle watchdog 15m, busy watchdog 5m, LRU `--max-active-backends` that waits for in-flight requests [snippet] — [LocalAI VRAM](https://localai.io/docs/advanced/vram-management/)
- llama-server `--sleep-idle-seconds` frees model + KV from RAM, auto-wakes on next task, health/props/models/metrics do not wake [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- llama-server router: model status includes `downloading`/`incomplete`; presets INI hold per-model args; `--models-memory-margin` LRU unload [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Jan: backup CDN when GitHub downloads fail; prompts before engine auto-update; MTP toggle gated on minimum bundled llama.cpp build with upgrade hint [fetched/snippet] — [Jan v0.6.10](https://jan.ai/changelog/2025-09-18-auto-optimize-vision-imports); [Jan v0.8.0](https://www.jan.ai/changelog/2026-05-22-jan-v0.8.0)
- Foundry Local downloads are resumable; offline after first download [snippet] — [Foundry Local GA](https://devblogs.microsoft.com/foundry/foundry-local-ga/)
- Ollama uninstall keeps relocated models; models in `%HOMEPATH%\.ollama` [fetched] — [Ollama Windows](https://docs.ollama.com/windows)
- LM Studio: models loaded by explicit load call escape TTL [snippet] — [bug #2051](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2051)

### Inferences
- [inferred] Adopt first: start llama-server with `--sleep-idle-seconds 900` (15 min, LocalAI's default; configurable in Jazari config, `0`/off to disable). Gateway health polling must use `/health` or `/props` only, which upstream guarantees will not wake the model. Show `is_sleeping` from `/props` in `jazari doctor`. Expected effect: ~4-5 GB of model + KV returned to Windows when idle, at the cost of a reload (4.2 GB from SSD, likely seconds) on the next prompt.
- [inferred] Avoid LM Studio's trap: whatever Jazari does at startup (warm-up, preload) must not create a state that the idle timer ignores. With llama-server's sleep, any non-exempt request resets the timer — so a Jazari background job (e.g. state-note updater) polling the model would keep 8.7 GB pinned forever. Route background work through the priority queue and count it as activity deliberately.
- [inferred] Adopt: `jazari stop` (or `jazari unload`) as an immediate free-RAM command, like `ollama stop`.
- [inferred] Adopt: resumable model download in the installer via Windows-native BITS (`Start-BitsTransfer`) or HTTP Range, then verify SHA-256 — a 4.2 GB download on laptop Wi-Fi is the most likely install failure. Add a mirror fallback (Jan's backup-CDN lesson) for the llama.cpp zip only if GitHub failures are actually observed.
- [inferred] Adopt: version-gate features on the pinned llama.cpp build like Jan's MTP toggle — `jazari doctor` / startup can check `llama-server --version` and disable flags the pinned build lacks, which matters if the pin is bumped and flags like `--checkpoint-min-step` or `--fit` change.
- [inferred] Skip: router mode / multi-model aliases. Jazari serves one pinned model; the only reusable idea is the INI preset format as a place to persist tuned args, which is optional.
- [inferred] Jazari is clearly better on integrity: SHA-256 pinning of both engine and model, atomic folder swap. Ollama's lesson (Bleeding Llama, below) shows unpinned model pulls are an attack path.

### Gaps
- Did not verify how Ollama verifies blob digests or resumes pulls from a primary source this session.
- Real reload time of a 4.2 GB GGUF from the T480's SSD after sleep not measured (mmap page cache may make it near-instant if Windows has not evicted the pages).

## Serving: APIs, structured output, tool calling, prompt cache reuse, concurrency

### Takeaway
OpenAI + Anthropic `/v1/messages` + OpenAI `/v1/responses` is now table stakes (llama-server and LM Studio both ship all three); structured output and tool calling are handled by llama-server's `--jinja` and `response_format`. The most valuable serving technique for Jazari is persisting the warmed prompt cache with slot save/restore, and checking the new checkpoint defaults that matter for its hybrid model.

### Cited Findings
- llama-server: `/v1/chat/completions`, `/v1/responses` (converted to chat), `/v1/messages` + `count_tokens`, tool use with `--jinja`, `response_format` json_object/json_schema, `--grammar` [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- LM Studio: OpenAI Chat/Responses/Structured Output/Tool Use, Anthropic Messages; `n_parallel` default 4 with unified KV [fetched] — [LM Studio docs nav](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict); [LM Studio 0.4.0](https://lmstudio.ai/changelog/lmstudio-v0.4.0)
- KoboldCpp: OpenAI, Anthropic and Ollama API emulation; parallel mode supports grammar and tool calls [fetched] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)
- Ollama: `NUM_PARALLEL` 1, queue 512 then 503 [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- llama-server prompt cache defaults: `--cache-ram` 8192 MiB, `-ctxcp` 32, `--checkpoint-min-step` 8192 tokens, `--cache-idle-slots` on, `--cache-reuse` 0 [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Slot save/restore to `--slot-save-path` via `/slots/{id}?action=save|restore` [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Speculative `--spec-type` includes draft-free n-gram modes (ngram-simple, ngram-mod, ngram-cache…) and draft-mtp [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- [inferred] Adopt: slot save/restore for cold start. Jazari starts llama-server on demand, and with idle sleep it will reload more often; each reload re-processes the system prompt on an i5-8350U. Save slot 0 after the first warmed request (`--slot-save-path` under Jazari's data folder), restore it after start/wake, key the file name to model SHA + build + system-prompt hash. Verify it restores Granite's recurrent state, not only attention KV.
- [inferred] Check before any llama.cpp pin bump: Jazari's `-ctxcp 8` is below today's default 32, and the new `--checkpoint-min-step` default of 8192 tokens may mean no checkpoints are taken for typical Jazari prompts. Hybrid Mamba models cannot truncate recurrent state, so missing checkpoints turn partial prefix matches into full reprocessing. Pass `-cms` explicitly (e.g. 0 or ~1024) and measure cache-hit latency with the eval harness.
- [inferred] Try in `jazari tune` (measured, opt-in): `--spec-type ngram-mod` or `ngram-cache`. Code-edit outputs repeat input text heavily, and n-gram speculation needs no draft model (no extra RAM). Whether llama.cpp supports speculative rollback for hybrid recurrent models is unverified (Gap).
- [inferred] Optional: expose `/v1/messages` through the gateway by proxying to llama-server's own endpoint, so Anthropic-protocol clients (IDE agents) can use Jazari. Low code since upstream already translates; the gateway only needs to apply its key/Origin checks and queue.
- [inferred] Keep `-np 1`: on 16 GB, LM Studio's default 4 slots would multiply KV memory; the gateway's priority queue is the right single-user design. Reconsider `-np 2` only if a background task measurably blocks interactive prompts.
- [inferred] Jazari's response cache in the gateway is something none of these runtimes offer; it is a differentiator (but must be bypassed for non-deterministic sampling).

### Gaps
- Speculative decoding compatibility with Granite 4.0-H (hybrid Mamba-2) in the current llama.cpp not verified.
- Whether slot save files include recurrent state for hybrid models not verified.
- MCP: llama-server and LM Studio/KoboldCpp/Jan support MCP clients, but Jazari's need for it was not assessed.

## Install, update and uninstall UX on Windows; service vs on-demand; diagnostics and logs

### Takeaway
The Windows norm is a per-user, no-admin install into `%LOCALAPPDATA%\Programs`, one log folder with separate app/server/upgrade logs, Add/Remove Programs uninstall, and update checks that are opt-out and never block startup. Jazari's on-demand background start already matches LM Studio JIT / Ollama tray behaviour without a resident service; its gaps are a single logs location, an uninstall path, and update-check timing.

### Cited Findings
- Ollama: no-admin installer, `/DIR=`, binaries in `%LOCALAPPDATA%\Programs\Ollama` on user PATH, logs `%LOCALAPPDATA%\Ollama\{app,server,upgrade}.log`, uninstall via Settings, standalone zip runnable as service via NSSM [fetched] — [Ollama Windows](https://docs.ollama.com/windows)
- Ollama auto-downloads updates; "Restart to update" in tray; login item disabled via Task Manager Startup apps [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- LM Studio llmster headless daemon installs on Windows via `irm` PowerShell one-liner; `lms log stream -s runtime`; runtime-update release notes viewer; fixes for CLI failing after service wakes [fetched] — [LM Studio 0.4.0](https://lmstudio.ai/changelog/lmstudio-v0.4.0)
- Jan: update checks gated by `check_for_updates`; engine auto-update needs it; app/back-end update checks deferred until after first paint; asks before updating llama.cpp [snippet/fetched] — [Jan v0.8.1](https://www.jan.ai/changelog/2026-05-29-jan-v0.8.1); [Jan v0.8.2](https://jan.ai/changelog/2026-06-01-jan-v0.8.2); [Jan v0.6.10](https://jan.ai/changelog/2025-09-18-auto-optimize-vision-imports)
- KoboldCpp: single exe per hardware class, no installer [fetched] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)
- llamafile: single-file executables blocked on Windows above 4 GB [secondary] — [Help Net Security](https://www.helpnetsecurity.com/2026/03/20/llamafile-0-10-0-released/)
- Two Ollama Windows updater vulnerabilities, CVE-2026-42248 and CVE-2026-42249 (CVSS 7.7), reported unpatched as of May 2026 [secondary] — [Indusface](https://www.indusface.com/blog/cve-2026-7482-bleeding-llama-vulnerability/)

### Inferences
- [inferred] Adopt: one documented log folder (e.g. `%LOCALAPPDATA%\Jazari\logs\` with `llama-server.log`, `gateway.log`, `update.log`), size-capped, printed by `jazari doctor`. Ollama's three-file split is the model.
- [inferred] Adopt: `jazari uninstall` (or documented steps) that removes the tool, private source copy and binaries but asks before deleting the 4.2 GB model and tuning data — Ollama keeps relocated models on uninstall.
- [inferred] Adopt (Jan lesson): never let an update check block the first prompt; run it after the CLI is interactive, cache the result, and allow `check_for_updates=false` for offline/air-gapped users.
- [inferred] Updater hardening (Ollama updater CVEs lesson): `jazari update` pulls GitHub `main` and builds it. Write staging folders under Jazari's own `%LOCALAPPDATA%` path (not `%TEMP%`), keep SHA-256 checks on every downloaded binary, and consider updating to signed tags/releases rather than tip-of-main so an unreviewed commit cannot auto-ship.
- [inferred] Skip: a Windows service or tray app. On-demand start + idle sleep gives the same "always available" feel without a resident process; Ollama's login-item behaviour is a common user annoyance on battery laptops (not sourced, see Gaps).
- [inferred] Adopt (cheap): `jazari doctor` shows an `ollama ps`-style line from llama-server `/props` + `/slots`: loaded/sleeping, context size, GPU layers or device, RSS. Ollama's PROCESSOR column ("100% GPU") is the pattern.

### Gaps
- Exact attack mechanics of CVE-2026-42248/42249 not read from a primary advisory.
- No sourced user sentiment on Ollama's login-item/tray behaviour.

## Security defaults and known vulnerabilities

### Takeaway
Ollama has no built-in auth and its exposed-instance count runs from ~12k to an estimated 300k, with a 2026 unauthenticated memory leak via crafted GGUF; llama-server's default CORS reflects any Origin with credentials. Jazari's gateway (API key, Origin/Host checks, localhost) is ahead of every runtime here, but the llama-server it spawns behind the gateway is likely left with upstream's permissive defaults.

### Cited Findings
- Bleeding Llama, CVE-2026-7482: heap out-of-bounds read in Ollama's GGUF loader, CVSS 9.1, before 0.17.1; crafted GGUF declares tensor larger than data; unauthenticated memory exfiltration in a few API calls, via push to an attacker registry [snippet/secondary] — [Cyera](https://www.cyera.com/blog/bleeding-llama-a-critical-memory-leak-in-the-worlds-most-popular-local-ai-platform); [Indusface](https://docs.indusface.com/en/article/19558641-3fc7-4243-9509-88fd8c59d532); [WZ-IT](https://wz-it.com/en/blog/bleeding-llama-ollama-cve-2026-7482-self-hosted-ai-hardening/)
- Timeline: reported Feb 2, 2026; fixed in 0.17.1 ~Feb 25 without being flagged as a security fix; CVE assigned by a third-party CNA Apr 28, published May 1 [secondary] — [Indusface](https://www.indusface.com/blog/cve-2026-7482-bleeding-llama-vulnerability/); [NYU RITS](https://rits.shanghai.nyu.edu/ai/bleeding-llama-critical-unauthenticated-memory-leak-hits-300000-ollama-servers/); one source conflicts on the ship date [secondary] — [trackr.live](https://www.trackr.live/?p=643)
- Exposure: LeakIX found 12,269 internet-exposed Ollama instances with zero authentication (Feb 2026) [snippet] — [LeakIX](https://blog.leakix.net/2026/02/ollama-exposed/); 31% of 5,200+ queried instances answered unauthenticated prompts [snippet] — [CSA research note](https://labs.cloudsecurityalliance.org/wp-content/uploads/2026/05/CSA_research_note_exposed_ai_infrastructure_selfhosted_security_20260507-csa-styled.pdf); 175,000 (Jan 2026) and ~300,000 (Cyera estimate) [secondary] — [NYU RITS](https://rits.shanghai.nyu.edu/ai/bleeding-llama-critical-unauthenticated-memory-leak-hits-300000-ollama-servers/). Counts measure different things and are not comparable.
- 2025 Ollama CVEs: CVE-2025-1975 DoS via crafted manifest (fixed 0.5.12) [snippet] — [GitLab advisory](https://advisories.gitlab.com/pkg/golang/github.com/ollama/ollama/CVE-2025-1975/); CVE-2025-0312 crafted GGUF null-pointer crash ≤0.3.14, CVSS 7.5 [snippet] — [GitLab advisory](https://advisories.gitlab.com/pkg/golang/github.com/ollama/ollama/CVE-2025-0312/); CVE-2025-0315/0317 resource-exhaustion/divide-by-zero DoS [snippet] — [cvefeed](https://cvefeed.io/vuln/detail/CVE-2025-0315); CVE-2025-51471 cross-domain token exposure [snippet] — [Go vuln DB](https://pkg.go.dev/vuln/GO-2025-3824); CVE-2025-63389 described as an "authentication bypass" in ≤0.12.3 [snippet, framing questionable since Ollama has no auth] — [BitNinja](https://bitninja.com/?p=25480)
- Ollama binds 127.0.0.1 by default; CORS allows 127.0.0.1/0.0.0.0 origins [fetched] — [Ollama FAQ](https://docs.ollama.com/faq)
- llama-server: `--host 127.0.0.1`; `--cors-origins` default `*`, reflects any Origin with credentials; becomes `localhost` only when `--tools`/`--agent` used; `/health` public; `--api-key`/`--api-key-file` available [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Jan: 127.0.0.1 default, API key optional, trusted-hosts list, CORS on by default, warns on 0.0.0.0 [fetched] — [Jan API server](https://docs.jan.ai/desktop/api-server)
- KoboldCpp limits agent MCP tool calls to localhost and masks passwords in logs [fetched] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)

### Inferences
- [inferred] Highest-priority fix: the internal llama-server is reachable from any webpage in the user's browser (127.0.0.1 is same-machine, and CORS `*` with credentials lets cross-origin JS read responses). Jazari should start it with `--api-key-file` (random per-install or per-launch key the gateway holds) and `--cors-origins` restricted or empty, so only the gateway can drive it. This also blocks other local processes from bypassing the gateway's queue and key. Verify the pinned build's actual CORS default and whether it validates Host (DNS rebinding).
- [inferred] Never enable `--tools`, `--agent`, `--mcp-servers-*` or the web UI on Jazari's internal llama-server; pass `--no-ui` explicitly (default is on) to shrink the surface.
- [inferred] Bleeding Llama lesson: GGUF parsers are an attack surface. Jazari's SHA-pinned single model neutralises the "crafted model" path, but the pinned llama.cpp build will age; `jazari doctor` should warn when the pin is older than N months, and the project should bump pins for upstream parser fixes.
- [inferred] Disclosure lesson: Ollama shipped a critical fix silently; Jazari release notes should label security fixes so `jazari update` can surface "security update available" more urgently.
- [inferred] Jazari is clearly better: API key on by default + Origin/Host checks at the public boundary is stronger than Ollama (none), Jan (key optional, CORS on) and llama-server defaults.

### Gaps
- Pre-2025 Ollama issues not re-verified this session (possibly outdated, from prior knowledge only): "Probllama" CVE-2024-37032 path traversal to RCE via `/api/pull`, and DNS-rebinding issues in 2024. Both reinforce Host-header validation and not exposing pull endpoints.
- No primary advisory text for CVE-2026-42248/42249.
- Did not find whether llama-server validates the Host header.

## What users praise and complain about

### Takeaway
Sourced sentiment is thin and mostly from blogs: the recurring complaints are silent context truncation from small defaults (Ollama), unloading that doesn't happen when expected (LM Studio, LocalAI), slow or blocking update checks (Jan, inferred from its fixes), and opaque security fixes (Ollama). Praise centres on one-command setup and "same engine, simpler UX".

### Cited Findings
- Ollama's small default context silently truncates long prompts, making models look weak; check `ollama ps` [secondary] — [besthub.dev](https://www.besthub.dev/articles/ollama-vs-llama-cpp-on-ubuntu-choose-convenience-or-performance-plus-the-context-window-trap-4d44ed9bdfa5); [bestllmfor.com](https://bestllmfor.com/guides/ollama-vs-llama-cpp/)
- Speed: Ollama vs llama-server results conflict (within noise on matched settings vs 95 vs 82 tok/s on one GPU vs half speed on two of three cards); gaps usually come from different defaults (context, offload, batch) [secondary] — [popularai.org](https://www.popularai.org/p/llama-cpp-vs-ollama-vs-lm-studio-speed); [dev.to](https://dev.to/mrsaynothing/llamacpp-vs-ollama-which-should-you-run-in-2026-7po)
- LM Studio: explicitly loaded/preloaded models not evicted despite auto-evict [snippet] — [bug #2051](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2051)
- LocalAI: user wanted "unload only when the next model doesn't fit" [snippet] — [LocalAI #1729](https://github.com/mudler/LocalAI/issues/1729)
- Jan deferred update/dependency checks to after first paint so cold start is not blocked [snippet] — [Jan v0.8.2](https://jan.ai/changelog/2026-06-01-jan-v0.8.2)
- Ollama fixed Bleeding Llama without labelling it a security fix [secondary] — [Indusface](https://www.indusface.com/blog/cve-2026-7482-bleeding-llama-vulnerability/)
- KoboldCpp's own notes say manual GPU layer choice beats its estimate (pre-2025) [snippet] — [KoboldCpp releases](https://github.com/LostRuins/koboldcpp/releases)

### Inferences
- [inferred] Jazari avoids the truncation complaint only if the gateway reports when a request exceeds context instead of letting llama-server shift/truncate silently; worth a `jazari doctor`/eval check.
- [inferred] Jazari's measured tuning directly answers the "estimates are wrong" complaint (KoboldCpp, speed-comparison blogs); worth stating in its README.

### Gaps
- No systematic Reddit/GitHub sentiment survey done (time budget); GPT4All, llamafile and Foundry Local user sentiment not found.
- GPT4All release dating conflicts: the "v3.10.0, Feb 25, 2026" date is from a wiki and may be the Feb 2025 release misdated; project status unverified from a primary source.
- LocalAI 4.x version history only seen on unofficial mirrors.

## Where these projects beat Jazari, and what to adopt first

### Takeaway
Jazari leads on integrity (SHA pinning, atomic update), measured tuning and gateway security. Others beat it on idle memory release, cold-start reuse, safe defaults for the internal engine, and Windows housekeeping (logs, uninstall, non-blocking update checks). All top items are flags or small CLI changes.

### Cited Findings
- Idle unload by default: Ollama 5 min [fetched] — [Ollama FAQ](https://docs.ollama.com/faq); LM Studio 60 min [fetched] — [LM Studio TTL](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict); LocalAI 15 min [snippet] — [LocalAI VRAM](https://localai.io/docs/advanced/vram-management/); llama-server `--sleep-idle-seconds` [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Auto memory fit default on, MoE-aware [fetched/snippet] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md); [fit-params](https://huggingface.co/datasets/echodict/llama.cpp/blob/main/tools/fit-params/README.md)
- Slot save/restore [fetched] — [llama.cpp README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Single log folder with app/server/upgrade logs and Add/Remove uninstall [fetched] — [Ollama Windows](https://docs.ollama.com/windows)
- Non-blocking, opt-out update checks; engine version gating [snippet] — [Jan v0.8.2](https://jan.ai/changelog/2026-06-01-jan-v0.8.2); [Jan v0.8.0](https://www.jan.ai/changelog/2026-05-22-jan-v0.8.0)
- Busy watchdog that kills stuck backends [snippet] — [LocalAI VRAM](https://localai.io/docs/advanced/vram-management/)

### Inferences
Ranked for a single-user 16 GB Windows laptop [all inferred]:
1. **Lock down the internal llama-server**: `--api-key-file` + restricted `--cors-origins` + `--no-ui`. Small change, closes a real cross-origin hole behind the gateway.
2. **`--sleep-idle-seconds` (~900)** with health polling only on exempt endpoints, plus `jazari stop` for immediate release. Biggest user-visible win against the 8.7 GB working set.
3. **Slot save/restore** of the warmed system prompt across restarts/wake-ups (verify hybrid recurrent state is included).
4. **Pin `-cms`/`-ctxcp` explicitly** and re-measure cache reuse before any llama.cpp pin bump (new defaults: 32 checkpoints, 8192-token minimum step).
5. **Untuned path uses `--fit on` / `-fa auto`** with a smaller `-fitt` for the 2 GB MX150; **add MoE expert-on-CPU as a `jazari tune` axis**; pin Vulkan device via `GGML_VK_VISIBLE_DEVICES`.
6. **Windows housekeeping**: one logs folder shown by doctor, `jazari uninstall` that asks before deleting the model, update check after first prompt and opt-out.
7. **Resumable model download** (BITS/Range) in the installer.
8. **Busy watchdog** in the gateway: restart llama-server if a generation stalls beyond a timeout (LocalAI pattern).
9. Optional: proxy `/v1/messages` and `/v1/responses` through the gateway; try n-gram speculative decoding in tune.
Skip: router/multi-model mode, tray app or Windows service, multi-slot `-np` > 1, llamafile packaging, Foundry Local.

### Gaps
- None of these recommendations were tested on the T480; effects (reload time, MoE split speed, n-gram speedup) are unmeasured.
- Which of the above flags exist in Jazari's currently pinned llama.cpp build was not checked against the repo (out of scope for this web research; check `llama-server --help` on the pinned build).
- The README's default `--port` 9931 conflicts with the long-standing default of 8080 (prior knowledge); Jazari should always pass `--port` explicitly rather than rely on the default.
