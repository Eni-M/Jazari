# Gateway to llama-server bridge: speed, reliability and correctness (Granite 4.1 3B hybrid, CPU, -np 1, late 2026)

Research date: 2026-10-30. This is desk research only: nothing was run here. The local build is b11982, and flag behaviour should be checked with `llama-server --help` on it. The earlier file `Whole laptop Jazari optimization/agent_tools_and_output.md` already covers tool calling, the `/apply-template` token costs of tools, the tools/grammar exclusivity, sampling defaults and `cache_prompt` defaulting to true. Those points are not repeated here.

Labels: **[DOC]** means the llama.cpp README or a PR/issue thread. **[CLAIMED]** means a third-party write-up. **[INFERENCE]** means my reasoning, untested.

## Ranked bridge improvements (single user, slow CPU, hybrid model)

### Takeaway
On this setup, nearly all the value is in not losing the prompt cache. A miss on 5k tokens costs about 3 minutes. Granite 4.1 is a hybrid recurrent model, so llama-server can only resume from an exact token prefix plus a saved checkpoint, and `--cache-reuse` chunk shifting does not help it. Next come visible progress, exact token counts and real cancellation. Model switching and sleep are low value and carry known bugs.

### Cited Findings
Ranked list. The exact fields and evidence are in the sections below.

1. **Make the prompt append-only between turns.** Move anything that changes per question (repo context retrieved for this question, the project state note if it changes) to after the history, just before the question. Otherwise every later token is re-ingested. Hybrid models reprocess from the nearest checkpoint at or before the first changed token, or from token 0 if there is none — [particula analysis](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache) **[CLAIMED, reads master source]**; [#24714](https://github.com/ggml-org/llama.cpp/issues/24714) **[DOC]**.
2. **Do not drop the oldest history pair on every turn.** Doing so changes the prompt near its start each turn. Compact rarely and in one big step (see the context section).
3. **Tune checkpoints for a 16k context.** Set `--checkpoint-min-step` to about 1024–2048 (default 8192) and keep `--ctx-checkpoints` at 32 or so. With the default 8192 on a 16k context, only about 1 or 2 checkpoints survive besides the last-user-message one — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md), [PR #24176](https://github.com/ggml-org/llama.cpp/pull/24176) **[DOC]**.
4. **Show prompt-processing progress.** Send `stream: true` and `return_progress: true`, and render `prompt_progress {total, cache, processed, time_ms}` as "reading prompt 1,200/5,000 (cached 3,100), ~2 min left" — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC]**.
5. **Read back cache-hit telemetry on every reply** and log it: `timings.cache_n`, `timings.prompt_n`, `usage.prompt_tokens_details.cached_tokens`. Alarm when `cache_n` drops unexpectedly. That is the only cheap way to catch prefix breaks in production — same README.
6. **Count tokens exactly** with `POST /apply-template` then `POST /tokenize`, instead of chars/4. Read `n_ctx` once from `/props` (`default_generation_settings`) — same README.
7. **Cancel properly.** When the CLI's stream closes, abort the upstream `HttpClient` request so llama-server's closed-connection handling stops the task ([PR #18459](https://github.com/ggml-org/llama.cpp/pull/18459), merged 2025-12-29). Also cap `max_tokens`/`n_predict` on every request.
8. **Save the project prefix to disk** with `--slot-save-path` and `/slots/0?action=save|restore`, so it survives restarts. This is high value if it works with the hybrid state. Untested for Granite.
9. **Supervise the server process.** Poll `/health` (503 "Loading model" then 200), restart on crash, and gate the request queue on readiness. Turn on `--metrics` for diagnostics.
10. **Structured output.** Use `response_format` with a `json_schema`, keep schemas small with `maxLength` and `maxItems`, set `additionalProperties: false`, and always set `max_tokens`.
11. **Hardening (low effort, low value locally).** `--no-webui`, `--alias granite`, `--api-key` (optional on 127.0.0.1; `/health` stays exempt).
12. **Low priority:** router mode or llama-swap for a second model, and `--sleep-idle-seconds`. Sleep has an open hang and crash race; see the lifecycle section.

### Inferences
- Items 1–3 together should turn most turns into "process only the new question plus the previous answer". At 28 tok/s that is seconds, not minutes. **[INFERENCE]**
- Items 4–5 are the cheapest wins for user experience and debugging, and need no server change.

### Gaps
- No source tests Granite 4.x specifically with checkpoints at b11982. Verify with `-lv 4` and grep `forcing full prompt|context checkpoint|Checking checkpoint|n_past =` ([particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache)).

## Prompt cache correctness: what breaks reuse, and how cache_prompt, --cache-reuse, --cache-ram, id_slot and slot save/restore work

### Takeaway
On a hybrid Mamba/attention model, reuse needs a byte- and token-identical prefix. The server resumes from the newest checkpoint at or before the first differing token. `--cache-reuse 256` relies on KV shifting, which the recurrent state cannot do, so treat it as off for Granite. `--cache-ram` keeps whole earlier prompt states in RAM, which helps when alternating between job types (chat vs /review). Disk slot saves can persist a project prefix across restarts, but nobody has documented that working on hybrids.

### Cited Findings
How hybrids cache:
- Checkpoints store only state that cannot be rolled back (SWA KV or recurrent state, `LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY`). Full-attention KV is just truncated. Recurrent state "can't have a state partially erased". If no checkpoint fits, the server sets `n_past` to 0, logs `forcing full prompt re-processing due to lack of cache data`, and erases the checkpoints after position 0. That log line is at trace level 4 and hidden at the default `-lv 3` — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache) **[CLAIMED; reads master as of 2026-07-29]**.
- Granite 4 support split recurrent state from the KV cache (PR #13550, July 2025) — [search summary of commit history](https://git.peisongxiao.com/CS348Project/llama.cpp/commits/commit/b3ad3a0191994d6c47b2bd389d5c7431526ecd2c/src/llama-model.cpp) **[secondary]**. Hybrid reprocessing despite saved checkpoints was reported on Nemotron-H after PR #16382 — [#16416](https://github.com/ggml-org/llama.cpp/issues/16416).
- [PR #24176](https://github.com/ggml-org/llama.cpp/pull/24176) (merged 2026-06-23) creates checkpoints at the start of every user and tool-response message, and always at the last user message. It also raised the default `checkpoint_min_step` from 256 to 8192. Related: #24411 (skip checkpoints beyond pos_next), #25472 (evict checkpoints within min-step, merged July 2026). The linked #24055 reports checkpoints "always invalidated" on hybrid/recurrent models, unresolved in that thread **[DOC]**.
- [#24714](https://github.com/ggml-org/llama.cpp/issues/24714) (Qwen3.5 hybrid, b9642): extending a prompt still forced full reprocessing in some message layouts. A maintainer-side contributor called it expected when the earlier prompt changed. It was closed as not planned on 2026-08-27 with no fix. One user found that sending history as all-user messages kept the cache valid, while splitting into assistant turns broke it. `--checkpoint-min-step 0` did not help that reporter **[DOC]**.
- Flags: `-ctxcp, --ctx-checkpoints N` (default 32 per slot); `-cms, --checkpoint-min-step N` (default 8192, `0` = no minimum); `--swa-full` (irrelevant: no SWA) — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md). `--checkpoint-every-n-tokens` was removed by PR #22929 (2026-05-25). Disk-backed checkpoints (`--cache-disk`, #20697) are not implemented — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache).

The flags and fields:
- `cache_prompt` (request, default `true`) reuses the longest common token prefix. The README warns that results can be nondeterministic across batch sizes — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- `--cache-reuse N` / request `n_cache_reuse` (default 0) is the "min chunk size to reuse from cache via KV shifting". Designed for llama.vim's ring buffer, it re-RoPEs shifted K values ("not mathematically identical" to reprocessing) and keeps chunk order — [PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787). It is "silently disabled with a log line when the context cannot do it" — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache) **[CLAIMED]**.
- `--cache-ram N` / `-cram` (MiB, default 8192; `-1` no limit). The help text is inconsistent about what `0` means. `--cache-idle-slots` (default on, needs cache-ram) saves idle slots into this RAM prompt cache — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md). When a new prompt arrives, the server compares similarity against cached prompts (log fields `sim_best`, `f_keep`) — [#24714](https://github.com/ggml-org/llama.cpp/issues/24714).
- `id_slot` (default -1, any idle slot). `-np` now defaults to `-1` (auto) with `--kv-unified` on when slots are auto — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md). A rejected oversized request can push the next request to another slot by LRU, discarding that slot's checkpoints — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache).
- Slot persistence: `--slot-save-path PATH`; `POST /slots/{id}?action=save` with `{"filename":...}` returns `n_saved`, `n_written`, `timings.save_ms`; `?action=restore` returns `n_restored`, `n_read`, `timings.restore_ms`; `?action=erase` returns `n_erased` — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- Debugging: `--log-prompts-dir` dumps prompts so consecutive ones can be diffed — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache) **[CLAIMED; not in the README text read]**.

What breaks the prefix (from the above plus template behaviour):
- Any change earlier in the prompt: an edited system text, a timestamp or git status in the state note, reordered tools, retrieved context that differs per question, or a dropped oldest history pair.
- Re-rendering the assistant's earlier reply differently from how it was generated: trimmed whitespace, stripped markdown, or a reasoning trace that was removed. The particula article lists stripped reasoning and reordered tool definitions as common causes.
- Sending a partial assistant message or different `chat_template_kwargs` between turns.

### Inferences
- **[INFERENCE]** Granite: do not pass `--cache-reuse 256`, or leave it set knowing it is a no-op. It does nothing for the recurrent layers, and the attention layers alone cannot be shifted independently. Confirm from the startup or per-request log on b11982.
- **[INFERENCE]** Store the assistant reply exactly as streamed (raw content, no trimming) and send it back unchanged. That gives the best chance the next prompt extends the live state without needing a checkpoint at all.
- **[INFERENCE]** With `-np 1`, `--cache-ram 2048` keeps alternate prefixes such as a "review" prompt and a "chat" prompt in RAM. Check that 2048 MiB fits at least two full 5k-token states on this model. If logs show evictions, raise it moderately, since RAM is the constraint on this laptop.
- **[INFERENCE]** Project prefix on disk. On first open of a project, send the stable prefix (system + project note + repo map) with `n_predict: 0` (README: "0 evaluates the prompt without generating"), then `POST /slots/0?action=save {"filename":"<project>-<sha256 of prefix>-b11982.bin"}`. On a later start, `restore` first, then send the request. Key the file name on the prefix hash, model file and build, and delete it on mismatch. Whether restore of a hybrid recurrent state then extends correctly is untested, so check `timings.cache_n` after the first request.
- Ordering for Jazari: `system (static) → tools (static) → project note (static per session) → repo map (static per session) → history (append-only) → per-question retrieved snippets → question`. Per-question snippets are then re-paid each turn, but only they and the question are.

### Gaps
- Whether `/slots` save/restore works for Granite's hybrid state at b11982: no report found.
- Real checkpoint size for Granite 4.1 3B: not found. The ~150–200 MiB figures are for 26B+ models ([PR #24176](https://github.com/ggml-org/llama.cpp/pull/24176)). The 3B recurrent state should be far smaller, but this is not measured.
- Whether the "all-user messages keep the cache" quirk from #24714 applies to Granite's template.

## Exact token counting, telemetry and progress

### Takeaway
Replace chars/4 with `/apply-template` + `/tokenize`, read `n_ctx` from `/props`, and stream `prompt_progress` so a 3-minute prefill shows a live bar and an ETA.

### Cited Findings
- `POST /apply-template {messages}` returns the rendered `prompt` without inference. `POST /tokenize {content, add_special=false, parse_special=true, with_pieces=false}` returns `tokens`. `/detokenize {tokens}` — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- `GET /props` returns `default_generation_settings` (includes `n_ctx`), `total_slots`, `model_path`, `chat_template`, `chat_template_caps`, `build_info` and `is_sleeping` — same.
- `return_progress` (default false) streams `prompt_progress` with `total`, `cache`, `processed` and `time_ms`. Overall progress is `processed/total`; the timed rate is `(processed-cache)/(total-cache)` — same.
- `timings`: `cache_n`, `prompt_n`, `prompt_ms`, `prompt_per_second`, `predicted_n`, `predicted_ms`, `predicted_per_second`; context used = `prompt_n + cache_n + predicted_n`. `usage.prompt_tokens_details.cached_tokens` on the OAI endpoint. `/completion` also returns `tokens_cached`, `tokens_evaluated` and `truncated`. `timings_per_token` is optional — same.

### Inferences
- Tokenize once per new piece (system, each history message, retrieved block) and cache the counts in the gateway keyed by content hash. Calling `/apply-template` on the full message list costs only CPU-cheap template work, but `/tokenize` competes with the single inference slot for CPU. Do it before queueing, not during generation. **[INFERENCE]**
- ETA = `(total - processed) / measured rate`, with the rate from `processed`/`time_ms` after the first progress event (about 28 tok/s here).
- Show `cached X / Y tokens` after each answer, since it teaches the user (and you) when the cache broke.

### Gaps
- The README text read does not say explicitly whether `return_progress` works on `/v1/chat/completions` streams or only `/completion`. Verify with one curl.

## Cancellation

### Takeaway
Mainline llama-server handles closed connections for tasks (PR #18459, Dec 2025), including during prompt processing. So the gateway should abort its upstream request as soon as the CLI disconnects or the user presses Ctrl+C, and also cap output length as a backstop.

### Cited Findings
- [PR #18459](https://github.com/ggml-org/llama.cpp/pull/18459) "server : handle closed connection for tasks", merged 2025-12-29. It fixes crash #18414, reproduced by stopping curl during prompt processing. The PR page does not state streaming vs non-streaming coverage **[DOC]**.
- Earlier attempt [PR #9679](https://tgfed.nhyvvfeijnh.workers.dev/ggml-org/llama.cpp/pull/9679) (2024) on cancelling prompt processing and non-streamed requests on disconnect: the author found it flawed because no data is sent until the end of a non-streamed reply — search summary **[secondary]**. A non-streaming disconnect error report on Mac: [#18063](https://github.com/ggml-org/llama.cpp/issues/18063).
- A third-party write-up says llama-server's `/completion` handler stops the slot when the connection closes — [contracollective](https://contracollective.com/blog/local-llm-request-cancellation-abort-disconnect-m5-max-2026) **[CLAIMED, no version]**.
- `t_max_predict_ms` (time cap, counted from the first token after a newline, meant for FIM) exists — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).

### Inferences
- Use streaming for everything long, even when the gateway buffers the result, because disconnect detection is most reliable when the server is writing. **[INFERENCE]**
- In ASP.NET Core, link `HttpContext.RequestAborted` into the `CancellationToken` of the `HttpClient.SendAsync(..., HttpCompletionOption.ResponseHeadersRead, ct)` call and of the stream read loop, then dispose the response. Remove the job from the queue if it has not started.
- After cancelling, poll `GET /slots` (`--slots` default on) until `is_processing` is false before sending the next job, and log if it does not stop within a few seconds. That turns a silent Windows-specific failure into a visible one.
- Windows: no Windows-specific disconnect bug was found.

### Gaps
- Not verified on b11982 whether a cancelled prefill keeps its partially processed tokens in the cache. If it does, a cancelled question is not wasted work.

## Structured output

### Takeaway
Use `response_format: {"type":"json_schema", ...}` (or the `json_schema` field). It compiles to a GBNF grammar. Keep schemas flat and bounded, describe the schema in the prompt too, and always cap tokens. `grammar` and `json_schema` cannot be combined, and neither can be combined with tools (see the earlier notes).

### Cited Findings
- `json_schema` request field, CLI `-j/--json-schema`, `-jf`; `response_format` accepts `{"type":"json_object"}` and schema forms with a `schema` key — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- The schema "is only used to constrain the model output" and is not injected into the prompt. `maxLength` → `char{1,N}`; `minItems`/`maxItems` → bounded repetition; `additionalProperties` defaults to false (true is "slow and seems prone to hallucinations" and "may produce keys that contain unescaped newlines"); `pattern` needs `^...$`; unsupported keywords are "skipped silently"; nested `$ref` and `prefixItems` are "broken" — [grammars README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/grammars/README.md).
- Performance: "Grammars currently have performance gotchas". Writing `x? x? ...` for N repetitions "may result in extremely slow sampling"; use `x{0,N}` — same. Nested JSON grammars blew up to about 800K grammar stacks in [#4218](https://github.com/ggerganov/llama.cpp/issues/4218) (2023, older behaviour).
- LLGuidance is an optional build-time backend that is "very fast" for JSON Schema but needs a Rust toolchain. Official Windows binaries are not stated to include it — [llguidance doc mirror](https://huggingface.co/hekod19045/llama-cuda/blob/main/docs/llguidance.md).

### Inferences
- Put `maxLength` on every free-text string (for example 400 for a review comment) and `maxItems` on arrays. That bounds the classic failure where a small model never closes a string, and bounds grammar cost. **[INFERENCE]**
- Set `max_tokens` from the schema bound plus margin, and treat `finish_reason: "length"` as a parse failure that gets one retry.
- On CPU at 6 tok/s, grammar overhead per token is small compared with the ~165 ms decode time, unless the schema is large or ambiguous. Keep it flat. **[INFERENCE, not measured]**
- Don't put code inside JSON fields where avoidable (the earlier notes cite Aider's evidence). For /review, prefer JSON with short findings plus line references.

### Gaps
- No 2026 measurement of grammar sampling cost on CPU was found.

## Health, lifecycle, server flags and templates

### Takeaway
Supervise llama-server from the gateway: start it, poll `/health` until 200, restart on exit, and expose `/props`/`/metrics` diagnostics. Most hardening flags are cheap. Leave `--sleep-idle-seconds` off for now because of an open hang and crash race.

### Cited Findings
- `/health` (also `/v1/health`, no API key check): 503 `{"error":{"code":503,"message":"Loading model","type":"unavailable_error"}}` while loading, 200 `{"status":"ok"}` when ready — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- `--metrics` (default off) exposes `llamacpp:prompt_tokens_total`, `prompt_seconds_total`, `prompt_tokens_seconds`, `tokens_predicted_total`, `predicted_tokens_seconds`, `requests_processing`, `requests_deferred`, `n_tokens_max`, `n_decode_total` and others. `--slots` (default on) exposes `GET /slots`, and `?fail_on_no_slot=1` returns 503 when busy — same.
- `-to, --timeout N` read/write timeout, default 3600 s; `--api-key` / `--api-key-file`; `-a, --alias` (without it, `/v1/models` id is the `-m` path); `--ui/--no-ui` (aliases `--webui/--no-webui`); `--jinja` default on; `--reasoning-format` default `auto`; `--chat-template-kwargs` JSON; `--reasoning-budget` — same.
- `--sleep-idle-seconds N` (default -1) unloads the model and KV cache, and the next task reloads it. `/health`, `/props`, `/models` and `/metrics` don't wake it or reset the timer; `/props` shows `is_sleeping` — same.
- Sleep race at b11368: a request arriving just before sleep either hangs until another request arrives (issue [#29689](https://github.com/ggml-org/llama.cpp/issues/29689)) or crashes the process with SIGSEGV in the tokenizer. The window grows with prompt length (hit reliably with 17,653 tokens). Workaround: send a 1-token completion first, or keep sleep disabled. No fix released as of the write-up — [dev.to](https://dev.to/homelabpm/llama-servers-sleep-mode-loses-or-crashes-on-a-request-that-arrives-just-before-it-sleeps-5e41) **[MEASURED by source]**.
- Granite 4.1 template and tokens: no thinking mode is documented by IBM ([Granite 4.1 docs](https://www.ibm.com/granite/docs/models/granite4-1)). The tokenizer nevertheless contains `<think>`, `</think>`, `<think_on>` and `<think_off>` tokens ([tokenizer_config](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/tokenizer_config.json)). The earlier notes measured `supports_preserve_reasoning: false` on `/props`.

### Inferences
- Gateway startup: spawn llama-server with fixed args, poll `/health` every 500 ms with backoff, and report "loading model" to the CLI. Read `/props` once to log `build_info`, `n_ctx` and `chat_template`, and refuse to run if `n_ctx` differs from config.
- On process exit or a connection refused error: fail in-flight requests with a clear error, restart with backoff (for example 1 s, 5 s, 30 s), and re-run the warm-up prefix (or slot restore).
- Set gateway-side timeouts as an idle-read timeout (no bytes for N minutes) instead of a fixed 30-minute total. A 5k prefill with progress events is never idle. Keep server `-to` at least as large as the longest expected prefill.
- Flags to add: `--no-webui`, `--alias granite-4.1-3b`, `--metrics`, and `-lv 4` only while debugging the cache. `--api-key` adds little on 127.0.0.1 for a single user, so it is optional. If set, pass it via `--api-key-file`, not the command line.
- Reasoning: Granite 4.1 does not document thinking. Leave `reasoning_format` default and do not send `chat_template_kwargs` unless needed. If one is sent, send the same value every time, because it changes the rendered prefix.

### Gaps
- Whether `#29689` is fixed by b11982: not found.

## Model switching and unloading

### Takeaway
llama-server now has a built-in router mode (`--models-dir`/`--models-preset`, `/models/load|unload`) and memory-based LRU unloading, which makes llama-swap optional. On a single slow CPU with tight RAM, a second model for review costs a load plus losing the chat model's warm cache, so it is low priority.

### Cited Findings
- Router mode: start without `-m`; `--models-dir`, `--models-preset <ini>` (`[*]` global section; keys are CLI args without dashes; preset-only keys `load-on-startup`, `stop-timeout` default 10 s, `dedup-cache-models`), `--models-max` (default 4), `--models-memory-margin N` (LRU-unloads idle models, and never one that is serving), `--models-autoload` (default on; `?autoload=` per request). Routing is by the JSON `model` field. `GET /models` status values: `unloaded/loading/loaded/sleeping/downloading/incomplete`; `POST /models/load`, `/models/unload`; `/models/sse` events. `/metrics` needs `?model=` in router mode — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- llama-swap: swaps upstream servers by requested model, `ttl` unload, a `matrix` DSL for co-resident models, `stripParams`/`setParams` filters, aliases, and proxies `/infill`, `/completion` and `/props`. Windows binaries are available — [llama-swap README](https://raw.githubusercontent.com/mostlygeek/llama-swap/main/README.md).

### Inferences
- With one 3B model, keep a plain single-model server. If a review model is added later, use router mode with presets rather than llama-swap. That is one fewer process, and the gateway already speaks the API. Save the chat slot before switching (`/slots/0?action=save`) and restore after, if the save/restore check above passes.
- Idle unload: prefer gateway-controlled shutdown after N idle minutes, with a warm restart and slot restore, over `--sleep-idle-seconds`, until #29689 is fixed.

### Gaps
- Whether router-mode child instances honour `--slot-save-path` and cache-ram per model was not checked.

## Context management beyond dropping history

### Takeaway
Dropping the oldest pair every turn is the worst policy for a hybrid cache, because it changes the prompt near the front each turn. Use high-water/low-water compaction: when the prompt passes about 80% of n_ctx, replace the oldest history in one step with a short summary, which leaves room for many append-only turns. Keep context shift off and refuse prompts that do not fit.

### Cited Findings
- `--context-shift` default disabled; `n_keep` / `--keep` (default 0, -1 = all) only apply when the context is exceeded; responses carry `truncated` — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- Practitioner guidance: "Avoid oversized requests… Compact context before hitting the limit"; don't edit earlier messages — [particula](https://particula.tech/blog/prompt-reprocessing-swa-hybrid-models-kv-cache) **[CLAIMED]**.

### Inferences
- Policy: count exactly. At >80% of `n_ctx - max_tokens`, summarize the oldest half of history into one message of about 200–300 tokens. Use a separate non-streaming request with `max_tokens` 350 (about 1 minute at 6 tok/s) and place the summary right after the static prefix. Then stay append-only until the next high-water mark. Each compaction costs one re-ingest of history, instead of one per turn. **[INFERENCE]**
- Never rely on `n_keep`/context shift. Shifting is not possible for recurrent state anyway, and a silently truncated prompt is worse than an explicit "too long" error.
- Tool results and file contents are the big consumers. Stub them only at compaction time, never turn by turn (consistent with the earlier notes).

### Gaps
- No measured summary-vs-drop quality comparison on a 3B model.

## What mature clients do

### Takeaway
The clients most tuned for llama-server (llama.vim/llama.vscode) keep all context state client-side, send an append-friendly prompt with `cache_prompt`, and bound time with `t_max_prompt_ms`/`t_max_predict_ms`. llama-swap shows that a thin proxy with health gating and TTL unloads is enough. Neither addresses hybrid models, so Jazari must be stricter on prefix stability than they are.

### Cited Findings
- llama.vim: ring buffer of `ring_n_chunks` chunks of `ring_chunk_size` lines, gathered on yank, buffer enter/leave and save. Similar chunks are evicted, chunks are sent as `input_extra [{filename,text}]`, and `--cache-reuse 256` shifts surviving chunks — [PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787). A readout shows 260 new prompt tokens processed against a 15,186-token context — [llama.vim](https://github.com/ggml-org/llama.vim).
- `/infill` fields: `input_prefix`, `input_suffix`, `input_extra`, `prompt`; with `FIM_REPO` and `FIM_FILE_SEP` tokens the repo-level pattern is used, otherwise `input_extra` is prefixed at the start — [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- Granite 4.1 tokenizer has `<|fim_prefix|>`, `<|fim_middle|>`, `<|fim_suffix|>`, `<|fim_pad|>`, `<|reponame|>` and `<|filename|>`, all non-special, but no `file_sep` token — [tokenizer_config](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/tokenizer_config.json).
- LocalAI had a bug where a dead stream kept decoding until the generation finished; the fix returns early so a cancel is posted — [LocalAI #11860](https://github.com/mudler/LocalAI/pull/11860).

### Inferences
- For `/infill` on Granite: `input_extra` chunks go at the start (no FIM_SEP), so they form the cacheable prefix. Keep their order stable and append-only, just like the chat prompt. llama.vim's similarity-based chunk eviction would break the hybrid cache, because shifting can't rescue it. Check the server startup log for whether the GGUF marks FIM PRE/SUF/MID tokens. If not, `/infill` may fail or behave badly. **[INFERENCE]**
- Set `t_max_predict_ms` (for example 3000–5000) and a small `n_predict` (for example 64) on `/infill` so completions stay snappy at 6 tok/s.
- Continue, Aider and Open WebUI docs were not fetched in this pass. No llama-server-specific practices from them are claimed here.

### Gaps
- Continue, Aider, Open WebUI and LiteLLM llama.cpp integration details were not researched (tool-call budget). The earlier notes cover Aider's edit formats.
- Whether Granite 4.1's GGUF exposes FIM token metadata was not verified.
