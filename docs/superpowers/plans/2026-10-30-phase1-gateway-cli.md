# Phase 1 — Local Gateway and CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Granite 4.1 3B usable every day from the terminal: a localhost ASP.NET Core gateway in front of llama-server (API keys, one-at-a-time priority queue, cancellation, project state note, response cache, local feedback log) plus a `myllm` CLI.

**Architecture:** `llama-server` (llama.cpp CPU build) runs the GGUF on `127.0.0.1:8080`. `MyOwnLLM.Gateway` (minimal API, `127.0.0.1:5080`) authenticates, queues and assembles prompts, then calls llama-server through `ILlamaClient`. `MyOwnLLM.Cli` is a thin HTTP client. All state (keys, SQLite DB) lives in `%LOCALAPPDATA%\MyOwnLLM\`.

**Tech Stack:** .NET 10 (SDK 10.0.400 installed), ASP.NET Core minimal APIs, Microsoft.Data.Sqlite, Microsoft.Extensions.Caching.Hybrid, xUnit, Microsoft.AspNetCore.Mvc.Testing; llama.cpp b11982.

**Spec:** `docs/superpowers/specs/2026-10-30-myownllm-design.md`

**Not in this plan (later plans):** Phase 0 benchmark harness; Roslyn repo map + SQLite FTS5 context search; editor integration; Phases 2–5.

## Global Constraints

- Model: `granite-4.1-3b-Q4_K_M.gguf`; llama-server flags `-dev none -t 6 --poll 0 -c 16384 --cache-ram 2048 -np 1 --cache-reuse 256`, bound to `127.0.0.1`.
- Total RAM for model server + gateway ≤ ~7 GB.
- Gateway binds `127.0.0.1` only, plain HTTP; API keys are personal-use only and stored as SHA-256 hashes, never logged.
- Requests reach the model one at a time; priority chat > completion > background; a new completion cancels the older pending/running completion.
- Delivery is buffered by default; streaming only when the request asks (`stream: true` / `--stream`).
- "Where are we left at?" must answer with a short summary in ≤ 120 s; the project state note is capped at ~800 tokens (3,200 chars) and output for status at 150 tokens.
- Prompt order is fixed for cache hits: system prompt → state note → repo context → history → question.
- Never read or log employer repositories (General-CRM); this plan touches only paths the user passes.
- Logs (prompts, answers, feedback) stay in the local SQLite file.

## Review Focus

1. **llama-server not running or dies mid-request** → the caller gets HTTP 503 with "model server not reachable at http://127.0.0.1:8080" within 5 s of connect failure, and the queue keeps serving later requests. (Task 1, Task 6)
2. **User presses Ctrl+C / client disconnects** → the request's token is cancelled, llama-server's HTTP call is aborted, and the next queued request starts. (Task 3, Task 6)
3. **Input larger than the context window** → HTTP 413 "input too large (~N tokens, limit M)" instead of a llama-server error or hang. (Task 4, Task 6)
4. **Missing, wrong or non-Bearer API key** → HTTP 401 with no detail about which part failed, and the presented key never appears in logs. (Task 2, Task 6)
5. **Status for a folder that isn't a git repo, or a path with spaces** → status still answers from the state note alone; no exception. (Task 4)

---

### Task 1: llama-server launch script and `LlamaClient`

**Files:**
- Create: `MyOwnLLM.slnx`, `src/MyOwnLLM.Gateway/MyOwnLLM.Gateway.csproj`, `tests/MyOwnLLM.Gateway.Tests/MyOwnLLM.Gateway.Tests.csproj`
- Create: `scripts/start-llama.ps1`
- Create: `src/MyOwnLLM.Gateway/Llama/ILlamaClient.cs`, `src/MyOwnLLM.Gateway/Llama/LlamaClient.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/LlamaClientTests.cs` (uses a fake `HttpMessageHandler`)

**Interfaces:**
- Produces:
  - `public sealed record ChatMessage(string Role, string Content);`
  - `public interface ILlamaClient { Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct); IAsyncEnumerable<string> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct); Task<string> InfillAsync(string prefix, string suffix, int maxTokens, CancellationToken ct); }`
  - `public sealed class LlamaUnavailableException(string baseUrl, Exception inner) : Exception` — message `"model server not reachable at {baseUrl}"`.
  - `LlamaClient(HttpClient http)`; `http.BaseAddress` is the llama-server URL.

- [ ] **Step 1: Verify flags on the pinned build.** Run `spike/bin/llama-server.exe --help` and confirm `-dev`, `--poll`, `--cache-ram`, `--cache-reuse`, `-np` exist. Expected: all present; if a name differs, use the build's name in the script and note it in the commit message.
- [ ] **Step 2: Write `scripts/start-llama.ps1`.** Parameters `-ModelPath` (default `spike/models/granite-4.1-3b-Q4_K_M.gguf`), `-Port 8080`; runs `spike/bin/llama-server.exe` with the Global Constraints flags plus `--host 127.0.0.1`.
- [ ] **Step 3: Write failing tests.**
  - `ChatAsync_posts_openai_body_with_cache_prompt`: captured request is `POST /v1/chat/completions`, JSON has `messages`, `max_tokens == 200`, `cache_prompt == true`, `stream == false`; returns `choices[0].message.content` (`"hi"`).
  - `InfillAsync_posts_infill_body`: `POST /infill` with `input_prefix`, `input_suffix`, `n_predict == 64`; returns `content`.
  - `ChatStreamAsync_yields_sse_deltas`: handler returns `data: {"choices":[{"delta":{"content":"a"}}]}\n\ndata: {"choices":[{"delta":{"content":"b"}}]}\n\ndata: [DONE]\n\n`; result is `["a","b"]`.
  - `Connect_failure_throws_LlamaUnavailableException`: handler throws `HttpRequestException`; message equals `"model server not reachable at http://127.0.0.1:8080/"`.
  - `Cancellation_aborts_request`: cancelled token → `OperationCanceledException`, not `LlamaUnavailableException`.
- [ ] **Step 4: Run** `dotnet test --filter LlamaClientTests`. Expected: FAIL (types missing).
- [ ] **Step 5: Implement `LlamaClient`.** System.Text.Json with snake_case; streaming reads the response with `HttpCompletionOption.ResponseHeadersRead` and parses `data:` lines; wrap `HttpRequestException` only (not cancellation).
- [ ] **Step 6: Run** `dotnet test --filter LlamaClientTests`. Expected: 5 passed.
- [ ] **Step 7: Commit** `feat: add llama-server launch script and LlamaClient`.

### Task 2: API keys

**Files:**
- Create: `src/MyOwnLLM.Gateway/Auth/ApiKeyStore.cs`, `src/MyOwnLLM.Gateway/Auth/ApiKeyFilter.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/ApiKeyStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ApiKeyStore(string keyFilePath) { public string AddKey(); public bool IsValid(string? presentedKey); }` — key format `mollm_` + 32 random bytes base64url; file is JSON `{"hashes":["<hex sha256>",...]}`; comparison with `CryptographicOperations.FixedTimeEquals`.
  - `public sealed class ApiKeyFilter(ApiKeyStore store) : IEndpointFilter` — accepts only `Authorization: Bearer <key>`; otherwise `Results.Unauthorized()`.

- [ ] **Step 1: Write failing tests.**
  - `AddKey_returns_prefixed_key_and_stores_only_hash`: returned key starts with `mollm_`; file content does not contain the key; `IsValid(key) == true`.
  - `IsValid_rejects_null_empty_and_wrong_keys`.
  - `Keys_survive_reload`: new `ApiKeyStore` on the same file validates the earlier key.
  - `Missing_file_means_no_valid_keys`.
- [ ] **Step 2: Run** `dotnet test --filter ApiKeyStoreTests`. Expected: FAIL.
- [ ] **Step 3: Implement `ApiKeyStore` and `ApiKeyFilter`.** Filter tests come in Task 6 (endpoint level).
- [ ] **Step 4: Run** `dotnet test --filter ApiKeyStoreTests`. Expected: 4 passed.
- [ ] **Step 5: Commit** `feat: add hashed API key store`.

### Task 3: Priority request queue

**Files:**
- Create: `src/MyOwnLLM.Gateway/Queue/RequestQueue.cs`, `src/MyOwnLLM.Gateway/Queue/QueueWorker.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/RequestQueueTests.cs`

**Interfaces:**
- Produces:
  - `public enum WorkKind { Chat = 0, Completion = 1, Background = 2 }`
  - `public sealed class QueueFullException : Exception`
  - `public sealed class SupersededException : OperationCanceledException` — thrown to the caller of a completion replaced by a newer one (lets Task 6 map it to 409 and plain cancellation to "client gone").
  - `public sealed class RequestQueue(int capacity = 8) { public Task<T> EnqueueAsync<T>(WorkKind kind, Func<CancellationToken, Task<T>> work, CancellationToken ct); public Task RunAsync(CancellationToken stoppingToken); }` — single consumer; picks the lowest `WorkKind` value first, FIFO within a kind; work receives a token linked to the caller's token.
  - `QueueWorker : BackgroundService` calls `RunAsync`.

- [ ] **Step 1: Write failing tests** (use `TaskCompletionSource` gates to control timing).
  - `Runs_one_item_at_a_time`: two items; second starts only after first completes.
  - `Chat_runs_before_waiting_completion_and_background`: while a blocker runs, enqueue Background, Completion, Chat; observed order Chat, Completion, Background.
  - `New_completion_cancels_pending_completion`: second Completion enqueued while the first waits → first task throws `SupersededException`.
  - `New_completion_cancels_running_completion`: running completion's token is cancelled when a newer completion arrives.
  - `Caller_cancellation_cancels_work_and_queue_continues`: cancel the caller token of a running item → work sees cancellation; next item still runs.
  - `Full_queue_throws_QueueFullException`: capacity 2, two waiting items + 1 running, third waiting enqueue throws.
- [ ] **Step 2: Run** `dotnet test --filter RequestQueueTests`. Expected: FAIL.
- [ ] **Step 3: Implement `RequestQueue`** with a lock-protected list of pending items plus a `SemaphoreSlim` signal (`System.Threading.Channels` is not used because items must be reordered and removed).
- [ ] **Step 4: Run** `dotnet test --filter RequestQueueTests`. Expected: 6 passed.
- [ ] **Step 5: Commit** `feat: add priority request queue`.

### Task 4: Prompt builder, project state note and git summary

**Files:**
- Create: `src/MyOwnLLM.Gateway/Prompt/PromptBuilder.cs`, `src/MyOwnLLM.Gateway/State/ProjectStateStore.cs`, `src/MyOwnLLM.Gateway/State/GitSummary.cs`, `src/MyOwnLLM.Gateway/Storage/LocalDb.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/PromptBuilderTests.cs`, `tests/MyOwnLLM.Gateway.Tests/ProjectStateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PromptParts(string? StateNote, string? RepoContext, IReadOnlyList<ChatMessage> History, string Question);`
  - `public static class PromptBuilder { public const string SystemPrompt; public static IReadOnlyList<ChatMessage> Build(PromptParts parts, int contextTokens, int maxOutputTokens); public static int EstimateTokens(string text); }` — `EstimateTokens = ceil(chars / 4)`; throws `InputTooLargeException(int estimated, int limit)` when total > `contextTokens - maxOutputTokens`; message `"input too large (~{estimated} tokens, limit {limit})"`.
  - `SystemPrompt` text: `"You are MyOwnLLM, a concise coding assistant for ASP.NET Core, C#, .NET, networking, system design, system architecture and cybersecurity. Prefer short answers and diffs over whole files."`
  - `public sealed class LocalDb(string dbPath) { public SqliteConnection Open(); }` — creates tables `project_state(project TEXT PRIMARY KEY, note TEXT, updated_utc TEXT)` and `interactions(...)` (Task 5 schema) on first open.
  - `public sealed class ProjectStateStore(LocalDb db) { public Task<string?> GetAsync(string projectPath); public Task SetAsync(string projectPath, string note); }` — key is `Path.GetFullPath(projectPath).TrimEnd('\\').ToLowerInvariant()`; note truncated to 3,200 chars.
  - `public static class GitSummary { public static Task<string> GetAsync(string repoPath, CancellationToken ct); }` — runs `git -C "<path>" log --oneline -10` and `git -C "<path>" status --short` via `ProcessStartInfo.ArgumentList`; returns `""` for non-repos or when git is missing; output capped at 2,000 chars.

- [ ] **Step 1: Write failing tests.**
  - `Build_orders_system_state_context_history_question`: roles/contents in exactly that order; null parts omitted.
  - `Build_keeps_identical_prefix_for_same_state`: two builds with different questions share all messages except the last.
  - `Build_throws_when_over_budget`: 70,000-char question, `contextTokens 16384`, `maxOutputTokens 512` → `InputTooLargeException` with `Estimated == 17500`, `Limit == 15872`.
  - `State_set_get_roundtrip_and_truncates_to_3200_chars`.
  - `State_key_ignores_case_and_trailing_slash`: `C:\Repo\` and `c:\repo` hit the same note.
  - `GitSummary_returns_empty_for_non_repo`: temp folder → `""`.
  - `GitSummary_handles_path_with_spaces`: temp folder `"my repo"` with `git init` and one commit → output contains the commit subject.
- [ ] **Step 2: Run** `dotnet test --filter "PromptBuilderTests|ProjectStateTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the four classes.
- [ ] **Step 4: Run** the same filter. Expected: 7 passed.
- [ ] **Step 5: Commit** `feat: add prompt builder and project state note`.

### Task 5: Interaction log and feedback

**Files:**
- Create: `src/MyOwnLLM.Gateway/Storage/InteractionLog.cs`
- Modify: `src/MyOwnLLM.Gateway/Storage/LocalDb.cs` (schema)
- Test: `tests/MyOwnLLM.Gateway.Tests/InteractionLogTests.cs`

**Interfaces:**
- Consumes: `LocalDb` (Task 4).
- Produces:
  - Table `interactions(id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, project TEXT, kind TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL, duration_ms INTEGER NOT NULL, accepted INTEGER NULL)`.
  - `public sealed record Interaction(string? Project, string Kind, string Prompt, string Answer, long DurationMs);`
  - `public sealed class InteractionLog(LocalDb db) { public Task<long> AddAsync(Interaction item); public Task<bool> SetFeedbackAsync(long id, bool accepted); }`

- [ ] **Step 1: Write failing tests.**
  - `AddAsync_returns_increasing_ids`.
  - `SetFeedbackAsync_sets_accepted_and_returns_true`; reading the row back gives `accepted == 1` / `0`.
  - `SetFeedbackAsync_unknown_id_returns_false`.
- [ ] **Step 2: Run** `dotnet test --filter InteractionLogTests`. Expected: FAIL.
- [ ] **Step 3: Implement** with parameterized commands.
- [ ] **Step 4: Run** the same filter. Expected: 3 passed.
- [ ] **Step 5: Commit** `feat: add local interaction log`.

### Task 6: Gateway endpoints, cache and error mapping

**Files:**
- Create: `src/MyOwnLLM.Gateway/Program.cs`, `src/MyOwnLLM.Gateway/Endpoints/ChatEndpoints.cs`, `src/MyOwnLLM.Gateway/GatewayOptions.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/EndpointTests.cs` (`WebApplicationFactory<Program>`, fake `ILlamaClient`, temp data dir)

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces (HTTP, all require `Authorization: Bearer <key>`):
  - `POST /v1/chat/completions` — body `{ "messages": [...], "max_tokens"?: int (default 512, max 1024), "stream"?: bool }`, header `X-Project-Path` optional. Buffered response is OpenAI shape with `"id": "ix-<logId>"` and `choices[0].message.content`; `stream: true` returns `text/event-stream` deltas ending `data: [DONE]`. Uses the state note for the project in the prompt. After answering, enqueues a `Background` job that rewrites the state note (prompt: old note + this exchange + `GitSummary`, "update the project state note, ≤ 150 words", `max_tokens 220`).
  - `POST /v1/infill` — `{ "prefix", "suffix", "max_tokens"?: int (default 64) }` → `{ "id", "content" }`, `WorkKind.Completion`.
  - `POST /v1/status` — `{ "project_path" }` → `{ "id", "summary" }`; prompt = state note + `GitSummary` + question `"Where are we left at? Answer with a short summary."`, `max_tokens 150`, `WorkKind.Chat`.
  - `POST /v1/feedback` — `{ "id": "ix-12", "accepted": bool }` → 204, or 404 for an unknown id.
  - `GatewayOptions`: `LlamaUrl = "http://127.0.0.1:8080"`, `DataDir = %LOCALAPPDATA%\MyOwnLLM`, `ContextTokens = 16384`, `RequestTimeout = 5 min`.
  - CLI arg on the gateway: `dotnet run --project src/MyOwnLLM.Gateway -- add-key` prints a new key once and exits.
  - Kestrel URL `http://127.0.0.1:5080`.
  - Exact-repeat cache: `HybridCache`, key = SHA-256 of `(endpoint, project, state note, messages/prefix+suffix, max_tokens)`, 1 h expiry; not used for `stream: true` or `/v1/status`.
  - Error mapping: `LlamaUnavailableException` → 503 `{"error":"model server not reachable at ..."}`; `QueueFullException` → 429; `InputTooLargeException` → 413 with its message; `SupersededException` → 409.

- [ ] **Step 1: Write failing tests.**
  - `Requests_without_valid_bearer_key_get_401` (none, wrong key, `Basic` scheme); logged output does not contain the presented key.
  - `Chat_buffered_returns_openai_shape_with_ix_id_and_logs_interaction`.
  - `Chat_stream_returns_sse_and_done`.
  - `Identical_chat_request_is_served_from_cache` (fake client called once).
  - `Status_uses_state_note_and_caps_tokens_at_150` (fake client asserts `maxTokens == 150` and the note text is in the second message).
  - `Feedback_known_id_204_unknown_404`.
  - `Llama_down_returns_503_and_next_request_succeeds`.
  - `Oversized_input_returns_413`.
  - `Client_abort_cancels_llama_call`: abort the HttpClient request; fake client observes cancellation.
- [ ] **Step 2: Run** `dotnet test --filter EndpointTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `Program.cs` (DI: `HttpClient` for `LlamaClient` with `Timeout = RequestTimeout`, singletons for stores and queue, `QueueWorker`, `AddHybridCache`) and `ChatEndpoints.cs`. Make `Program` visible to tests (`public partial class Program;`).
- [ ] **Step 4: Run** `dotnet test`. Expected: all tests pass.
- [ ] **Step 5: Commit** `feat: add gateway endpoints`.

### Task 7: `myllm` CLI

**Files:**
- Create: `src/MyOwnLLM.Cli/MyOwnLLM.Cli.csproj` (assembly name `myllm`), `src/MyOwnLLM.Cli/Program.cs`, `src/MyOwnLLM.Cli/CliArgs.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/CliArgsTests.cs` (reference the CLI project)

**Interfaces:**
- Consumes: HTTP endpoints from Task 6.
- Produces:
  - Commands: `myllm ask "<question>" [--repo <path>] [--stream]`, `myllm status [--repo <path>]` (default: current directory), `myllm feedback <ix-id> up|down`.
  - Env: `MYOWNLLM_URL` (default `http://127.0.0.1:5080`), `MYOWNLLM_KEY` (required; missing → exit code 2 with `"set MYOWNLLM_KEY (run: dotnet run --project src/MyOwnLLM.Gateway -- add-key)"`).
  - `public sealed record CliArgs(string Command, string? Text, string? RepoPath, bool Stream, string? Id, bool? Accepted); public static CliArgs Parse(string[] args);` — throws `ArgumentException` with a usage line on bad input.
  - Output: the answer, then a dim line `[ix-12 · 41.2 s]`; HTTP errors print the gateway's `error` text and exit 1.

- [ ] **Step 1: Write failing tests** for `CliArgs.Parse`: `ask` with/without `--repo` and `--stream`; `status` defaulting `RepoPath` to `null` (resolved to the current directory at run time); `feedback ix-3 up` → `Accepted == true`; `feedback ix-3 maybe` throws; no arguments throws.
- [ ] **Step 2: Run** `dotnet test --filter CliArgsTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `CliArgs` and `Program.cs` (no extra packages; streaming prints deltas as they arrive).
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat: add myllm CLI`.

### Task 8: End-to-end check on the laptop

**Files:**
- Create: `docs/phase1-runbook.md` (start order, `add-key`, env vars, PC tuning checklist from the spec, measured timings)

- [ ] **Step 1:** Start `scripts/start-llama.ps1`, then the gateway; run `add-key` and set `MYOWNLLM_KEY`.
- [ ] **Step 2:** `myllm ask "In ASP.NET Core, how do I register a scoped service?"`. Expected: an answer and an `ix-` id; gateway and llama-server RAM together ≤ 7 GB (Task Manager).
- [ ] **Step 3:** In `C:\Users\enim\EduCore` (an Eni-M repo), run `myllm ask` twice about the project, then `myllm status`. Expected: a short summary in **≤ 120 s** (time it with `Measure-Command`). If over, record the timing breakdown from the llama-server log and stop. Report before tuning.
- [ ] **Step 4:** Ctrl+C a running `ask`; the llama-server log shows the task cancelled, and the next command runs.
- [ ] **Step 5:** Stop llama-server; `myllm ask` prints `model server not reachable at http://127.0.0.1:8080` and exits 1.
- [ ] **Step 6:** `myllm feedback <id> up` succeeds; record all timings in `docs/phase1-runbook.md`.
- [ ] **Step 7: Commit** `docs: add Phase 1 runbook with measured timings`.
