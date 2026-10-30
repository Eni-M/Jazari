# Queueing, Context Construction and Caching for a Small Local Coding LLM (CPU, llama-server)

Baseline used for all arithmetic below (from the assignment, not a source): Granite 4.1 3B on CPU via llama-server, prompt processing (PP) ~39 tok/s, generation (TG) ~6 tok/s. Rule of thumb from that: **every 100 prompt tokens costs ~2.6 s; every 100 output tokens costs ~17 s.** A 2,000-token prompt = ~51 s before the first token; a 300-token answer = ~50 s more. So output tokens are ~6.5x more expensive per token than input tokens, but input volume is usually 5-20x larger, so both matter.

## 1. Does chunking input ("feeding small amounts") reduce compute? What do -b/-ub already do? Is pausing between chunks useful for thermals?

### Takeaway
No. Splitting a prompt into small pieces does not reduce total prefill work (every token must still be pushed through every layer once), and llama-server already splits prompts internally into `-b`/`-ub` batches; smaller batches are, if anything, less efficient per token. The only real "feed less" win is sending fewer tokens or not recomputing tokens already in the KV cache (sections 4 and 6).

### Cited Findings
- llama-server defaults: `-b/--batch-size` "logical maximum batch size (default: 2048)"; `-ub/--ubatch-size` "physical maximum batch size (default: 512)"; continuous batching "default: enabled" — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Prompt processing speed rises with batch size in the official llama-bench example (7B Q4_0, CUDA): ~1436 t/s at batch 128 to ~2498 t/s at batch 1024. GPU data, not CPU — [llama-bench README (mirror)](https://docs.rs/crate/llama-cpp-sys-4/0.4.4/source/llama.cpp/tools/llama-bench/README.md)
- CPU thread scaling example in the same README (7B Q4_0, pp64): ~6.17 t/s on 1 thread, ~12.31 on 2, ~23.18 on 4, i.e. CPU prefill is compute-bound and scales with cores — [llama-bench README (mirror)](https://docs.rs/crate/llama-cpp-sys-4/0.4.4/source/llama.cpp/tools/llama-bench/README.md)
- A tuning guide recommends CPU batch 512-2048; larger batches speed prompt processing at the cost of memory (guidance, no measurements) — [performance tuning notes](https://www.mintlify.com/ggml-org/llama.cpp/advanced/performance-tuning)
- llama.vim's design study (M1 Pro / M2 Ultra) shows prompt speed drops as context fills (e.g. Qwen2.5-Coder 1.5B Q8_0 on M1 Pro: ~1150 t/s empty, ~480 t/s half, ~320 t/s full context), i.e. per-token cost grows with context length because of attention — [llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)
- `n_predict: 0` evaluates the prompt into the cache without generating ("When 0, no tokens will be generated but the prompt is evaluated into the cache") — usable for background pre-warming — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- Total prefill FLOPs ≈ 2 × params × tokens plus an attention term that grows with context; chunking changes neither. Chunking only helps if chunks are *reused* across requests (KV cache), which is what llama.vscode's ring buffer does (section 4).
- Pausing between chunks for thermals: it lowers peak temperature and fan noise but does not lower energy per request (same work, plus idle baseline power for longer), and adds latency 1:1. A cleaner thermal knob is fewer threads (`-t`, e.g. physical cores minus 1-2): steady lower clocks, roughly linear throughput loss per the thread-scaling data above. Windows power plan / "Best power efficiency" mode does the same at OS level.
- The useful form of "feed small amounts" is **pre-warming during idle time**: send stable context (system prompt, file under edit) with `n_predict: 0` while the user is typing, so the actual request only pays for the delta. This is exactly llama.vim/llama.vscode's background-ring-update trick.
- 39 tok/s PP for a 3B model is low for a modern laptop CPU; worth running `llama-bench -p 512 -t <n> -ub 256,512` once to check thread count / quant (Q4_0/Q4_K_M with repacking is usually fastest on CPU). Not verified for this machine.

### Gaps
- No published CPU-specific `-ub` sweep found; the user should measure with `llama-bench -p 512 -b ... -ub ... -t ...`.
- No source quantifying energy/heat of paused vs continuous prefill on laptops.

## 2. Single-slot FIFO vs parallel slots / continuous batching on CPU; priority, cancellation, dedup

### Takeaway
On a CPU-bound single-user laptop, use 1 slot (or at most 2) plus an app-level queue. Extra slots add RAM (KV per slot, or a shared pool split among them) and split the same compute, without adding meaningful aggregate throughput; their only benefit is keeping a second cached prefix warm, which `--cache-ram` now does more cheaply in host RAM.

### Cited Findings
- `--parallel/-np`: "number of server slots (default: -1, -1 = auto)"; `--kv-unified`: "use single unified KV buffer shared across all sequences (default: enabled if number of slots is auto)"; `-sps/--slot-prompt-similarity`: how much a prompt must match a slot's prompt to use that slot "(default: 0.10)"; `id_slot` pins a request to a slot — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `--cache-ram/-cram`: host-RAM prompt cache, "default: 8192" MiB; `--cache-idle-slots` saves idle slots to the prompt cache on a new task (default enabled) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- PR #16391 (merged Oct 9, 2025) added host-memory prompt caching: cached prompts act as "extra slots"; the server measures prefix similarity and swaps a cached prompt back in when that reduces processing, so "a single server slot" avoids trashing the prompt cache (motivated by llama.vscode's agent). No benchmark numbers given — [llama.cpp PR #16391](https://github.com/ggml-org/llama.cpp/pull/16391)
- CPU data point (Ampere A1, `-np 8`): aggregate tok/s stayed roughly flat (~10-11 tok/s) across concurrency levels — throughput ceiling set by CPU/memory bandwidth. Single setup, anecdotal — [OCI Ampere blog series](https://quartoblog-52a.pages.dev/blog/tech/benchmark%20llm%20-%20flags)
- Official throughput tool `scripts/server-bench.py` (PR #14668) reports total and per-slot generation speed for a chosen `--parallel` — [commit](https://cdn04132025.gitlink.org.cn/replica/llama.cpp/commit/494c5899cb76859f32ddd913534f2685fd684a3d)
- Ollama: `OLLAMA_NUM_PARALLEL` default 1; RAM scales with NUM_PARALLEL × CONTEXT_LENGTH (e.g. 2K ctx × 4 parallel = 8K); `OLLAMA_MAX_QUEUE` default 512, returns 503 beyond; queued requests processed in order — [Ollama FAQ](https://docs.ollama.com/faq)

### Inferences
- With continuous batching on CPU, 2 concurrent requests each run at roughly half speed (decode is memory-bandwidth bound, prefill compute bound). For one user, total wall time for both is about the same as FIFO, but the first answer arrives later. FIFO + priority beats parallel slots for perceived latency.
- Recommended app design (ASP.NET Core): `System.Threading.Channels` bounded channel (or a `PriorityQueue<T,int>` guarded by a `SemaphoreSlim(1)`) in front of llama-server with `-np 1`. Priorities: interactive chat > explicit "explain/fix" > FIM autocomplete > background warm/index. Autocomplete should not preempt chat; instead, **drop stale autocomplete requests** (newer keystroke supersedes older — "latest wins" coalescing per document).
- Cancellation matters more than anything else in the queue: propagate `HttpContext.RequestAborted` / `CancellationToken` into the `HttpClient` call; llama-server stops generating when the client disconnects (streaming), which frees the CPU. With non-streamed requests, verify that disconnect actually stops generation (not verified here).
- Dedup: key identical in-flight requests by hash(model, normalized prompt, sampling params) and share one `Task` (single-flight). Cheap and safe with temperature 0.
- Bound the queue (e.g. depth 4-8) and fail fast with 429/503 instead of letting work pile up; at 6 tok/s a queue of 5 chat requests is minutes of backlog.

### Gaps
- No measurement found of 1 vs 2 slots for a 3B model on a laptop x86 CPU. Measure with `server-bench.py` (override its GPU-layers default for CPU).
- Granite 4.x includes hybrid Mamba-2/attention variants; whether "Granite 4.1 3B" is dense or hybrid affects KV size and cache-reuse behavior (SSM state can't be KV-shifted; llama.cpp uses context checkpoints instead). Not verified.

## 3. Buffered (non-streamed) vs streamed delivery

### Takeaway
Zero compute difference: the model generates the same tokens either way. Streaming only changes when bytes leave the server. At 6 tok/s, buffering a 300-token answer means ~50 s of blank screen, so stream to the UI and, if a "full" result is needed (e.g. a diff to apply), assemble it client-side or apply on completion.

### Cited Findings
- `n_predict` default -1 (unbounded); `t_max_predict_ms` sets a time limit for generation, measured from the first generated token, triggering after a newline (default 0 = off) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

### Inferences
- Streaming has a real compute benefit indirectly: the user can stop a bad answer after 5 s instead of waiting 50 s, and a disconnect/abort stops generation. That is the main lever, so the UI needs a Stop button wired to cancellation.
- "Queue output and deliver in full" is appropriate only for machine-consumed outputs (JSON tool calls, patches, commit messages); combine with a strict `n_predict` and grammar so the buffered wait is bounded.
- `t_max_predict_ms` is a server-side wall-clock cap useful as a thermal/latency guard (e.g. 30,000 ms).

### Gaps
- No source measured SSE overhead in llama-server; assumed negligible relative to 6 tok/s.

## 4. Reducing tokens in: retrieval, AST trimming, prompt compression, prefix-stable ordering, --cache-reuse

### Takeaway
The biggest wins, in order: (1) **prefix-stable prompts + llama-server prompt cache** (cached tokens cost ~0 instead of ~26 ms each); (2) **send signatures, not files** (Roslyn/tree-sitter repo map, aider-style, ~1k-token budget); (3) **BM25/lexical retrieval** over chunks for chat questions. LLMLingua-2-style compression is not worth it on this hardware: it needs its own model pass on the same CPU, and even on GPUs the measured end-to-end win is small.

### Cited Findings
- `cache_prompt` defaults to `true`: "Re-use KV cache from a previous request if possible" (warning: may cause nondeterminism because logits can differ across batch sizes) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- With `cache_prompt`, the server reuses the **longest common token prefix** from the previous request — [llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)
- `--cache-reuse N` (PR #9866): reuses cached KV chunks that appear at a *different position* by shifting them (`llama_kv_cache_seq_add`), similar to KoboldCpp context shifting; requires `cache_prompt`; "Only chunks without control/special tokens will be reused"; original implementation stops at the first non-reusable token (contiguous reuse only). Example: with `--cache-reuse 512`, second request reused 516 tokens and computed 1 — [llama.cpp PR #9866](https://github.com/ggml-org/llama.cpp/pull/9866)
- README: `--cache-reuse` "min chunk size to attempt reusing from the cache via KV shifting ... (default: 0)" — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- llama.vim guidance: `--cache-reuse` should be 0 or larger than n_predict; 256 suggested as a general value — [llama.vim source (Debian mirror)](https://sources.debian.org/src/llama.cpp/8064%2Bdfsg-2/examples/llama.vim)
- KV shifting "is not mathematically identical to full reprocessing but is empirically effective" — [llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)
- llama.vim/llama.vscode ring buffer: client keeps `ring_n_chunks` chunks of `ring_chunk_size` lines from yanks, buffer enter/leave and saves; near-duplicate chunks evicted; chunks sent as `input_extra` on `/infill`; ring updated and sent to the server during inactivity to prime the KV cache. Example run: 15,186 tokens of context but only 260 newly processed prompt tokens, 1,245 ms on M1 Pro. Target ~1 s per suggestion — [llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)
- Sizing: ring_n_chunks=64 × ring_chunk_size=64 needs ~32k context — [llama.vim (mirror)](https://sources.debian.org/src/llama.cpp/8064%2Bdfsg-2/examples/llama.vim)
- `/infill` `input_extra`: array of `{filename, text}` inserted before the FIM prefix; repo-level pattern used if the model has `FIM_REPO`/`FIM_FILE_SEP` tokens — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- aider repo map: graph where files are nodes and dependencies are edges, ranked; keeps the most relevant parts within `--map-tokens` (default 1k tokens); can expand when no files are in chat — [aider docs](https://aider.chat/docs/repomap.html)
- Tabby indexes repos by parsing into an AST and uses that context for completion, chat and search (retrieval details not documented on that page) — [Tabby docs](https://tabby.tabbyml.com/docs/administration/context/)
- Continue autocomplete example config: `maxPromptTokens: 1024`, `prefixPercentage: 0.3`, `maxSuffixPercentage: 0.2`, `debounceDelay: 250`, `modelTimeout: 150`, `onlyMyCode: true` (example values, not stated defaults) — [Continue docs](https://docs.continue.dev/customize/deep-dives/autocomplete)
- LLMLingua-2 original claims (V100 GPU): 1.6x-2.9x end-to-end speedup at 2x-5x compression; compressor 3x-6x faster than prior methods — [LLMLingua-2 paper](https://arxiv.org/html/2403.12968v1)
- Independent 2026 study (Kummer et al., arXiv 2604.02985, Apr 2026): up to 18% end-to-end speedup only when prompt length, ratio and hardware match; "Outside this operating window ... the compression step dominates and cancels out the gains"; optimized serving largely erases TTFT gains; compressor latency scales linearly with prompt size; tested on A100, GTX 1080 Ti, M1 Pro (no x86 CPU); quality drops sharply on retrieval/counting/few-shot classification — [Emergent Mind summary of 2604.02985](https://www.emergentmind.com/papers/2604.02985)

### Inferences
- Prompt layout for maximal prefix hits (most stable first): `[system prompt (fixed, versioned)] [project summary / repo map (changes rarely)] [retrieved snippets (sorted deterministically, e.g. by path)] [conversation history] [current file excerpt] [user question]`. Never put timestamps, request IDs, or "today is..." near the top; one changed byte invalidates everything after it. Use the same chat template and tool list every time.
- Value per request: a 1,000-token fixed prefix that hits cache saves ~26 s at 39 tok/s. That is larger than any other single optimization.
- With 1 slot, alternating chat and autocomplete would evict each other's prefix; `--cache-ram` (default 8 GiB host RAM) mitigates this by swapping prompts in from RAM. For a 3B model KV is small, so a modest `--cache-ram` (1-2 GiB) is likely enough on a RAM-constrained laptop (inference, not measured).
- For C#: Roslyn (`Microsoft.CodeAnalysis.CSharp`) can emit a "signature skeleton" (namespaces, type declarations, member signatures, XML doc first line, bodies replaced by `;`), typically a fraction of file size; include full bodies only for the member at the cursor / named in the question. Roslyn is native to the user's stack, gives semantic info (symbol references, call sites) tree-sitter lacks. Tree-sitter is the fallback for non-C# files.
- Retrieval: for code, BM25 over identifier-split chunks (e.g. Lucene.NET, or SQLite FTS5) is cheap on CPU and strong for identifier-heavy queries; a small embedding model adds an indexing cost on the same CPU that competes with the LLM. Start with BM25 + Roslyn symbol lookup; add embeddings only if recall is measurably poor (inference; no C#-specific benchmark found).
- Use `/infill` with `input_extra` for FIM autocomplete rather than hand-built chat prompts; it already implements the repo-level FIM layout if the model supports FIM tokens (whether Granite 4.1 3B has FIM tokens: not verified).
- Skip LLMLingua-2: on this CPU its BERT-size compressor runs on the same cores as the LLM, the independent study shows small or negative net gains outside a narrow window, and code is a content type where token dropping risks breaking identifiers.

### Gaps
- No published numbers for Roslyn skeleton token reduction on real C# repos; measurable by the user with the model's tokenizer (`/tokenize` endpoint).
- No x86-CPU measurements of LLMLingua-2 overhead found.
- Whether Granite 4.1 3B supports FIM tokens and how cache-reuse behaves if it is a hybrid SSM model: not verified.

## 5. Reducing tokens out: max_tokens, stop sequences, grammars, concise instructions, diff-only edits

### Takeaway
At 6 tok/s, output is the most expensive token type, so hard caps are the single most important output control: set `n_predict`/`max_tokens` per request type (e.g. 64 for autocomplete, 256-512 for chat), add stop sequences, and request diffs/search-replace blocks instead of whole files. Grammar-constrained output (GBNF/json_schema) prevents rambling and malformed output; its CPU sampling overhead is small with LLGuidance but can be bad with naive GBNF repetition.

### Cited Findings
- `n_predict` default -1 = infinity; `t_max_predict_ms` time cap — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- `grammar` and `json_schema` fields constrain sampling (default none) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- GBNF docs warn certain unbounded repetition constructs "may result in extremely slow sampling"; use bounded `x{0,N}` — [llama.cpp grammars README (mirror)](https://huggingface.co/OpenTransformer/llama.cpp-prismml/blob/main/grammars/README.md)
- LLGuidance backend (build with `LLAMA_LLGUIDANCE`): ~50 µs single-core per token mask average on JSON Schema Bench, p99 0.5 ms, p100 20 ms; schema is not inserted into the prompt, so the prompt must describe the structure — [llguidance docs (mirror)](https://huggingface.co/OpenTransformer/llama.cpp-prismml/blob/main/docs/llguidance.md)
- llama.vim's FIM budget assumes ~32 generated tokens per suggestion — [llama.cpp PR #9787](https://github.com/ggml-org/llama.cpp/pull/9787)

### Inferences
- Cost table at 6 tok/s: 32 tokens ≈ 5 s, 128 ≈ 21 s, 512 ≈ 85 s. A whole-file rewrite of a 300-line C# file (~3,000 tokens) ≈ 8+ minutes; a 30-line unified diff (~300 tokens) ≈ 50 s. Diff-only edits are a 5-10x win for edits.
- Autocomplete: stop on `\n` (single-line) or `\n\n`, cap 32-64 tokens, cap by `t_max_predict_ms`.
- Concise-answer system instructions ("answer in ≤5 sentences, code only, no preamble") are free and stack with caps; small models follow them imperfectly, so caps remain the enforcement.
- At 6 tok/s, a 50 µs per-token mask cost is <0.1% overhead, so grammars are effectively free with LLGuidance; with default GBNF, keep grammars simple and bounded.
- Disable "thinking"/reasoning mode if the model template supports one; reasoning tokens are pure output cost (inference; Granite 4.x template behavior not verified).

### Gaps
- No CPU benchmark of default-GBNF overhead per token found.

## 6. Response caching (exact and semantic)

### Takeaway
Exact-match caching (hash of normalized prompt + params + model + relevant file content hashes) is cheap and safe at temperature 0. Semantic caching is risky for code questions: false hits return answers for a different file/version, and published false-positive rates are high unless thresholds are strict; for a single user it rarely pays off.

### Cited Findings
- MeanCache (arXiv 2403.02694): low cosine threshold gives false hits, high gives false misses; optimum ~0.78 for one embedding model with F-score 0.88; reported ~17% higher F-score and 20% higher precision than GPTCache — [MeanCache paper](https://arxiv.org/pdf/2403.02694)
- Practitioner (vendor, unverified) figures: threshold 0.95 → ~1-3% false positives; 0.85 → ~15-30% — [dev.to article](https://dev.to/gauravdagde/llm-semantic-caching-the-95-hit-rate-myth-and-what-production-data-actually-shows-8ga)
- Banking case study cited in an InfoQ article: 0.7 threshold gave false positive rates up to 99% for some embedding models; 0.9 cut to ~27% for one model but pushed LLM fallback to 47%; best design ~3.8% FP — [InfoQ](https://www.infoq.com/articles/reducing-false-positives-retrieval-augmented-generation/)

### Inferences
- In .NET: `HybridCache` (Microsoft.Extensions.Caching.Hybrid, .NET 9+) or `IMemoryCache` keyed by SHA-256 of the canonical request, plus content hashes of any files included as context, so editing the file invalidates the entry automatically. HybridCache also gives stampede protection (single-flight) for free, covering dedup from section 2.
- Exact cache only helps for repeated identical questions (e.g. "explain this file" twice); hit rates for a single developer are likely low, but the cost is a few lines. The bigger "cache" win is the KV prompt cache (section 4), not response caching.
- If semantic caching is tried at all, scope it to non-code, file-independent questions (e.g. "what does `[Authorize]` do?") with a strict threshold (≥0.95) and show "cached answer" in the UI.

### Gaps
- No hit-rate data for single-user coding assistants found.

## 7. Existing open-source tools to reuse rather than build

### Takeaway
llama-server itself already provides most of the requested "queue + cache" machinery (slots, continuous batching, prompt cache, host-RAM prompt cache, cache-reuse/KV shifting, `/infill` with extra context, grammar, time caps). llama.vscode/llama.vim already implement the best-known context strategy for CPU FIM. What is missing, and worth a small ASP.NET Core layer: priority + stale-request dropping, cancellation propagation, prompt assembly with Roslyn skeletons, exact response cache.

### Cited Findings
- llama-server feature set (slots, `--cache-ram`, `--cache-reuse`, `id_slot`, `input_extra`, `grammar`, `json_schema`, `t_max_predict_ms`, `--slot-save-path` to persist slot KV to disk) — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- llama.vscode: ring context from open/edited files and yanked text; "smart context reuse" to support very large contexts on low-end hardware — [ecosyste.ms listing of llama.vscode](https://awesome.ecosyste.ms/projects/github.com%2Fggml-org%2Fllama.vscode)
- llama-swap: swaps upstream server by `model` field, `ttl` auto-unload, `matrix` DSL for concurrent models, `stripParams`/`setParams` request rewriting, `/api/models/pause`; no request queue or response cache described — [llama-swap GitHub](https://github.com/mostlygeek/llama-swap)
- Ollama: FIFO queue (`OLLAMA_MAX_QUEUE` 512, 503 on overflow), `OLLAMA_NUM_PARALLEL` default 1, `keep_alive` default 5 min — [Ollama FAQ](https://docs.ollama.com/faq)
- Continue: autocomplete with debounce, prompt token budget, prefix/suffix split, model timeout — [Continue docs](https://docs.continue.dev/customize/deep-dives/autocomplete)
- Tabby: self-hosted server with AST-based repo indexing used for completion/chat context — [Tabby docs](https://tabby.tabbyml.com/docs/administration/context/)
- aider: tree-sitter/graph-ranked repo map with ~1k-token default budget — [aider docs](https://aider.chat/docs/repomap.html)

### Inferences
- Suggested minimum stack: llama-server `-np 1 --cache-reuse 256 --cache-ram 2048 -t <physical cores-1>` (+ `--slot-save-path` to persist a warm system-prompt slot across restarts) → thin ASP.NET Core proxy (Channels-based priority queue, latest-wins for autocomplete, CancellationToken passthrough, HybridCache exact cache, Roslyn skeleton builder, prefix-stable prompt assembler) → editor via llama.vscode (FIM) or Continue (chat). No need for LiteLLM or llama-swap with a single model.
- llama-swap is only useful if switching between multiple models (e.g. a FIM model and a chat model) on limited RAM; with one model it adds a hop for nothing.
- Order of expected impact for this machine: output caps/diffs > prefix-stable prompt + KV cache reuse > signature-only context > cancellation/stale-drop > exact response cache > parallel slots (likely negative) > prompt compression (likely negative).

### Gaps
- LiteLLM proxy features (queueing, caching) were not researched in this pass; it is Python and mainly a multi-provider router, likely overkill for single-user single-model.
- Tabby's current CPU support and retrieval method (BM25 vs embeddings) were not confirmed from docs.
- No sources were dated late 2026 for llama.cpp flags beyond the current README; PR #16391 (Oct 2025), #9866 and #9787 (late 2024) are older but the features remain in the current README.
