# A minimal, safe tool loop for Jazari on a small local model (late 2026)

Research date: 2026-11-02. This note builds on `research_notes/Whole laptop Jazari optimization/agent_tools_and_output.md` (call it "the earlier note"), which already settled: native llama-server tool calls over a custom protocol, four lean tools at 378 tokens, `parallel_tool_calls: false`, exact then line-trimmed search/replace, a Windows path guard and deny list, approval UX, and a 16k budget. Those points are not repeated here except where new evidence changes them. The main change since that note is the model choice: **Granite 4.0-H Tiny is a hybrid Mamba-2/transformer MoE**, and that changes how prompt caching behaves inside a loop.

Labels: **[SOURCED]** has a link; **[INFERRED]** is my reasoning from sourced facts; **[MEASURE]** needs a test on the laptop. Several hosts could not be reached from this session (huggingface.co, qwen.readthedocs.io, learn.microsoft.com), so model-card figures come from mirrors and search excerpts and are marked that way.

Repo facts used below come from reading the code today **[SOURCED: repo]**:
- `ChatRequest` (`ChatEndpoints.cs`) has no `tools`, `tool_choice` or `parallel_tool_calls` field. `ChatMessage` is passed as role plus content, with no `tool_calls` or `tool_call_id`.
- `LlamaClient.ReadChunk` reads only `delta.content`, so streamed `delta.tool_calls` would be silently dropped. `ChatWithEndAsync` reads only `message.content`.
- The non-streaming chat path caches whole answers for 1 hour, keyed on the prompt hash.
- `PromptBuilder.Build` drops history in steps of 8 messages counted from the start, and estimates tokens as chars/4.
- `StatusFor` maps every llama-server 400 to 413 ("context overflow").
- `ProjectFiles.Load` reads a single named file with no root check. `Walk` skips reparse-point *directories* but not file symlinks, and `IsSecret` checks the file name only.
- `FileEdit.Apply` does exact match, then a line-trimmed match with an indentation shift, and returns a short problem string.

## llama-server tool calling: templates, parsers, streaming, grammar, current bugs

### Takeaway
Keep native tool calls through `/v1/chat/completions`, but the gateway has to be extended first: as written it cannot carry tools or tool calls, and its streaming parser would drop them. Granite 4.x and Qwen3 both use the Hermes-style `<tool_call>{json}</tool_call>` shape, which the template-derived parser handles. Several open bugs from Sept–Oct 2026 argue for a narrow request shape: chat completions only, tools without `response_format`/`json_schema`, no parallel calls, and a client-side validator behind the server's lazy grammar.

### Cited Findings
- function-calling.md (current master) still lists Qwen 2.5, but not Qwen3, among native formats. Its "native support" examples include `ibm-granite/granite-4.1-3b-GGUF:Q4_K_M`. Unrecognised templates fall back to "Generic", visible in the log as `Chat format: Generic`, and "Generic support may consume more tokens and be less efficient than a model's native format" — [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md) **[SOURCED]**. Verify a template through the `chat_template` / `chat_template_tool_use` properties at `/props` — same source.
- The same doc warns that extreme KV quantization (`-ctk q4_0`) "can substantially degrade the model's tool calling performance" — [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md) **[SOURCED]**.
- Qwen's convention is JSON inside `<tool_call>` tags (the "Hermes/Qwen" parser), with ReAct as a separate style — [Twinkle parser docs](https://modelscope.github.io/twinkle-web/docs/components/template/toolcallparsers/) **[SOURCED, third party]**. The official Qwen docs could not be fetched (host blocked).
- Server flags in current master **[SOURCED: [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)]**:
  - `--reasoning on|off|auto` and `--reasoning-budget N` (0 = end thinking immediately).
  - `--reasoning-format` controls whether thoughts are extracted into `reasoning_content`.
  - Request-level `reasoning_effort: "none"` disables thinking.
  - `parallel_tool_calls` is "only supported on some models, verification is based on jinja template".
  - `--skip-chat-parsing` forces everything, tool calls included, into content.
- Open or recent bugs relevant to Jazari's request shape (from llama.cpp issue search; titles as filed):
  - #31342 (open, 2026-10-28): `tools` + `tool_choice=auto` + `response_format=json_object` fails with 400 "failed to parse grammar", because the generated GBNF has an empty root rule — [#31342](https://github.com/ggml-org/llama.cpp/issues/31342).
  - #30773 (open, 2026-10-18): `/v1/responses` reports a truncated tool call as completed — [#30773](https://github.com/ggml-org/llama.cpp/issues/30773).
  - #30490 (closed, 2026-10-14): a model emitted `<tool_call>` XML as plain content, and the calls were "silently lost" — [#30490](https://github.com/ggml-org/llama.cpp/issues/30490).
  - #29006 (open, 2026-09-17): a JSON Schema grammar fails on chat-template control tokens while the equivalent GBNF works, reported for Granite 3.1 and Qwen3 — [#29006](https://github.com/ggml-org/llama.cpp/issues/29006).
  - Issue bodies were not readable from this session except via summary; the titles are as filed.
- The earlier note already covers: `--jinja` on by default, Granite rendering of `tool` as `<tool_response>`, tools plus custom `grammar` being mutually exclusive, lazy grammar on native calls, the `parallel_tool_calls` default conflict, the #24807 stream-abort bug and the #16465 `:`/`\u` crash — see [earlier note](../Whole%20laptop%20Jazari%20optimization/agent_tools_and_output.md).

### Inferences
Gateway changes required before any loop works **[INFERRED from the repo facts]**:
1. **Request.** Add `Tools`, `ToolChoice`, `ParallelToolCalls` to `ChatRequest`. Pass them through to llama-server. Reject `JsonSchema` together with `Tools` with a clear 400, which avoids #31342 and the grammar conflict.
2. **Messages.** Extend `ChatMessage` with optional `ToolCalls` and `ToolCallId`, and serialise `content` as `""` rather than null for tool-call turns. Accept `arguments` as either a string or an object (#20198, in the earlier note).
3. **Response.** Make `ChatWithEndAsync` return `message.tool_calls` and `finish_reason`. Use non-streaming for tool turns, as the earlier note suggests. That avoids a second streaming parser in `ReadChunk`, and the user sees "calling read_file..." either way. Keep the streamed path for the final answer only. Note that the step that turns out to be the answer is not known in advance; see the latency section.
4. **Answer cache.** Do not use the 1-hour answer cache for tool turns. Either bypass it whenever `Tools` is present, or put the tool list and the file-mtime state in the key. A cached `read_file` decision is harmless, but a cached final answer computed from a file the user has since edited is wrong.
5. **History trimming.** `PromptBuilder` drops history in 8-message steps, which can split an assistant `tool_calls` message from its `tool` result. Templates expect them paired. Within a loop, never trim. If the budget is exceeded mid-loop, stop the loop with a message. (Cross-turn trimming belongs to the memory effort.)
6. **Error mapping.** `StatusFor` treats every 400 as context overflow. With tools, a 400 can also be a template, grammar or parse failure. Pass llama-server's message through instead.
7. **Detecting lost calls.** If `finish_reason` is `length` on a tool turn, or `content` contains `<tool_call>` or `{"name":`, treat it as a failed call and retry once with the format error message (see the tool design section). Do not show it as an answer. This covers the #30490 and #30773 class of bug.
8. **Endpoint and build.** Use `/v1/chat/completions`, not `/v1/responses` (#30773). Keep the KV cache at f16 or q8_0, never q4_0. Pin the build and add a 10-case smoke test, run after each llama.cpp upgrade, covering a path with backslashes, `:` and quotes.
9. **Template check.** At gateway start, read `/props` and log the chat format line. If it says Generic for Granite 4.0-H Tiny, the template was not recognised and tool calls will be weaker. **[MEASURE]**

### Gaps
- No Granite 4.0-H-specific tool-calling bug was found in the issue search. Whether #31342 or #29006 affect Granite 4.0-H Tiny's template on the pinned build b11982 is unknown. **[MEASURE]**
- Whether `tool_choice: "none"` still renders the tool list into the prompt was not confirmed. If it drops the tools, the prefix changes and caching breaks for the "now answer" step. **[MEASURE with `/apply-template`]**
- Streaming tool-call deltas on this build remain untested (also a gap in the earlier note).

## Tool-calling accuracy of small models, and thinking mode

### Takeaway
Every ≤4B candidate sits in the same narrow BFCL v3 band of about 57 to 62, so the benchmark does not separate them. Granite 4.0-H Tiny's 57.65 is the lowest of the shortlist, and its value is speed (about 1B active parameters), not accuracy. Multi-turn agentic scores for this class are much lower than single-call scores. Plan for errors through the harness (validation, retries, a step cap), not through model choice.

### Cited Findings
BFCL v3 scores for the candidates **[SOURCED, from vendor tables via mirrors]**:

| Model | Architecture | BFCL v3 | Source |
|---|---|---|---|
| Granite 4.0 Micro | dense 3B | 59.98 | IBM table via [Unsloth mirror](https://huggingface.co/unsloth/granite-4.0-micro) |
| Granite 4.0-H Micro | hybrid dense 3B | 57.56 | same table |
| **Granite 4.0-H Tiny** | hybrid MoE, 7B total / 1B active | **57.65** | same table, and [Docker Hub](https://hub.docker.com/r/ai/granite-4.0-h-tiny) |
| Granite 4.0-H Small | hybrid MoE 32B / 9B active | 64.69 | same table |
| Granite 4.1 3B | dense | 60.8 | earlier note, model card |
| Qwen3-4B, non-thinking | dense | 57.6 | Qwen3-4B-Instruct-2507 card comparison table, via [benchmarklist](https://benchmarklist.com/models/qwen3-4b-instruct-2507/) |
| Qwen3-4B-Instruct-2507 | dense, non-thinking only | 61.9 | same; column assignment inferred by the search tool from table order |

- Granite 4.1 sizes per an IBM-related write-up: 60.8 (3B), 68.3 (8B), 73.7 (30B) — via search excerpt, [tinyweights](https://tinyweights.dev/posts/run-ibm-granite-4-1-locally/) **[SOURCED, secondary]**.
- Qwen3-4B-Instruct-2507 has no thinking mode; a separate Thinking-2507 model does reasoning — mirror of the model card, [gitcode](https://gitcode.com/hf_mirrors/unsloth/Qwen3-4B-Instruct-2507-GGUF) **[SOURCED, mirror]**.
- Aggregator figures for Qwen3-4B-Instruct-2507: BFCL v4 overall 35.7% and a multi-turn score of 22.1% — [llmbase](https://llmbase.ai/compare/qwen3-4b-2507-instruct,qwen3-4b-instruct/) / [llmrun](https://llmrun.dev/model/qwen-qwen3-4b-instruct-2507/benchmarks) **[SOURCED, aggregators, not verified against BFCL]**. BFCL v3 is described as superseded by the current leaderboard — [BenchLM BFCL v4](https://benchlm.ai/md/benchmarks/bfcl-v4.md).
- Continue's docs warn that 7B–13B models "may struggle with complex tool interactions", and a later version calls even 20–30B local models "challenging" for agent mode. Continue falls back to XML tools in the system message for models without native tools — [Continue model setup](https://docs.continue.dev/features/agent/model-setup) (via search snippet and doc mirrors) **[SOURCED]**.
- A single-author CPU benchmark measured `granite4:3b` at restraint 0.000 — [earlier note](../Whole%20laptop%20Jazari%20optimization/agent_tools_and_output.md) / [tool-calling-benchmark](https://github.com/MikeVeerman/tool-calling-benchmark).

### Inferences
- **Thinking.** On CPU, thinking is off for tool turns. Granite 4.0 Instruct is a non-reasoning model, so this only matters if Qwen3-4B (the hybrid thinking original) is tried. Then pass `reasoning_effort: "none"`, or `--reasoning off`, or `--reasoning-budget 0`.
  - Cost: at the earlier note's measured 6 t/s, 300 thinking tokens per step is about 50 seconds, multiplied by every step.
  - Benefit: none shown here. The one comparable pair is Qwen3-4B non-thinking 57.6 versus the non-thinking-only 2507 at 61.9, and the gain there came from the newer training, not from thinking.
  - Option: a thinking pass can be offered only for the final answer of `/review`. **[INFERRED]**
- **Multi-turn is the weak point.** BFCL v3 headline scores average many single-call categories. The aggregator's 22.1% multi-turn figure for a 4B model (unverified) is consistent with Continue's warning. Expect step 3+ of a loop to be where things go wrong (re-reading, wrong tool, giving up). **[INFERRED]**
- **Granite 4.0-H Tiny versus Granite 4.1 3B for the loop.** About 3 points lower on BFCL v3, but with about 1B active parameters per token it should generate and prefill faster on CPU. The trade is only worth making if the speed gain is real on this laptop and the cache works within the loop (next sections). Run the earlier note's 20-case eval on both before committing. **[MEASURE]**

### Gaps
- The official BFCL v4 leaderboard was not checked directly, and no BFCL v4 or multi-turn score for any Granite 4.0/4.1 small model was found.
- The Granite 4.0-H Tiny card could not be fetched (huggingface.co blocked). Its numbers come from the Unsloth/Docker mirrors of IBM's table.
- No source measures thinking on versus off for tool selection on a ≤4B model.

## Tool design for small models: count, schemas, errors, steps, stopping, loops

### Takeaway
Keep the earlier note's four lean tools and add a harness around them, following mini-swe-agent and Anthropic's tool guidance: exactly one action per step, a hard step cap, steering messages for errors and truncation, a duplicate-call guard, and a forced final answer. Small models need the harness to say what to do next. A bare error code is not enough.

### Cited Findings
- mini-swe-agent default config **[SOURCED: [default.yaml](https://raw.githubusercontent.com/SWE-agent/mini-swe-agent/main/src/minisweagent/config/default.yaml)]**:
  - It requires "exactly ONE bash code block with ONE command" per response.
  - Its format error says "Please always provide EXACTLY ONE action..., found N actions".
  - If the output hit the token limit: "Respond more concisely and provide exactly one action".
  - Outputs over 10,000 characters are cut to the first 5,000 and last 5,000, with an `<elided_chars>` count and a warning to use `head`/`tail`/`sed` or a more selective search.
  - `step_limit` and `cost_limit` default to 0 (unlimited) in this file. The cap is meant to be set per run.
- Anthropic's tool-writing guidance **[SOURCED: [Writing tools for agents](https://www.anthropic.com/engineering/writing-tools-for-agents)]**:
  - "More tools don't always lead to better outcomes", and overlapping tools distract.
  - Name parameters unambiguously (`user_id`, not `user`).
  - Truncate with sensible defaults and tell the agent how to narrow the query. Claude Code caps tool responses at 25,000 tokens.
  - Errors should give "specific and actionable improvements, rather than opaque error codes or tracebacks", ideally with an example of valid input.
  - Return names rather than opaque IDs.
  - Evaluate on realistic multi-call tasks and track tool-call counts, runtime and errors.
- Aider retries a failed edit up to 3 reflections; Cline had infinite retry loops on mismatched search blocks — [earlier note](../Whole%20laptop%20Jazari%20optimization/agent_tools_and_output.md).
- I did not get to smolagents, OpenHands or Goose source this session (see Gaps).

### Inferences
Schemas (refines the earlier note's tool definitions) **[INFERRED]**:
- Use flat objects with only `string` and `integer` types. No nested objects, arrays, enums with more than 3 values, or `oneOf`. Required parameters first, at most one optional each.
- Name parameters for what they hold: `path` (relative to project root, forward slashes), `start_line`, `query`, `old_text`, `new_text`.
- **Single edit per call.** `FileEdit.Schema` allows up to 8 edits in an array for `/edit`. For the tool, a flat `edit_file(path, old_text, new_text)` is simpler for the model and keeps `FileEdit.Apply` unchanged (pass one `Replacement`).
- Accept `\` and absolute paths inside the root and normalise them, rather than rejecting. The earlier note's test saw the model send `C:\MyOwnLLM\...` correctly escaped.

Error and result messages: each one states what happened and the next action, in one or two lines.
- Not found: `No file src/Foo.cs. Closest: src/Cli/Foo.cs, src/Gateway/FooBar.cs. Call read_file with one of these.` Use the cheap fuzzy filename match from `ProjectFiles.Walk`.
- Edit mismatch: `old_text not found in Session.cs. Nearest lines 40-44 are:` followed by the real lines, then `Copy old_text exactly from these.` This extends `FileEdit`'s `"this text isn't in the file: ..."`.
- Ambiguous: `old_text appears 3 times (lines 12, 58, 90). Include the line above it.`
- Denied: `Refused: .env is a secret file. Do not try other paths to it.` Wording for a refusal must not invite retries.
- Truncation, in mini-swe-agent's style: `[showing lines 1-150 of 420; call read_file with start_line=151]`.

Loop control:
- **Step cap.** 6 tool calls per user turn (the earlier note), counted in C#. At the cap, send one last request with `tool_choice: "none"` (if the tools stay in the prompt; see the Gaps of the first section) and a user-role line: `Tool limit reached. Answer now from what you have, and say what you could not check.`
- **Duplicate guard.** Hash `(name, normalised args)`. On a repeat within the turn, do not re-run it. Return `You already called read_file on this path at step 2; the result is above. Use it or answer.` On a second repeat, end the loop. This is the cheapest defence against the classic small-model loop.
- **Error budget.** At most 2 consecutive failed calls (format errors, refusals or mismatches). Then force the answer step. Edits keep their own limit of 2 retries (earlier note).
- **Restraint.** Since Granite's measured restraint is weak, a zero-tool answer is a success, not a miss. Do not add a "you must use a tool" nudge.
- **Stopping.** The loop ends when a response has no `tool_calls`. Do not ask the model to call a `finish` tool; that is one more thing to get wrong.

Visibility: print each call as `→ read_file src/Cli/Session.cs (lines 1-150)`, with the step number `[2/6]` and the elapsed seconds.

### Gaps
- smolagents (default `max_steps`, its code-agent versus tool-calling-agent results on small models), Goose and OpenHands small-model guidance were not reached within the tool-call budget.
- No source measures the duplicate-guard or error-message wording on a 3B model. All of the above should go into the 20-case eval. **[MEASURE]**

## Safety: path sandboxing on Windows, limits, injection, no network or shell

### Takeaway
The earlier note's path guard is right, but it still checks by name and then opens by name, and that leaves a time-of-check to time-of-use gap. New 2026 CVEs in agent sandboxes and in .NET are exactly this bug. The robust pattern is: open the handle first, then verify the handle's final path against the root, then read or write through that same handle. Two existing CLI helpers also need tightening before the model can drive them.

### Cited Findings
- CVE-2026-44112 (OpenClaw / OpenShell): a time-of-check to time-of-use symlink swap let sandbox writes escape the mount root, CWE-367, fixed in 2026.4.22 — [VulnCheck](https://www.vulncheck.com/advisories/openclaw-symlink-swap-race-condition-in-openshell-fs-bridge-writes); [Lutra](https://fieldguide.lutrasecurity.com/CVE-2026-44112/) **[SOURCED]**.
- CVE-2026-50526 is described as a .NET path traversal where intermediate components that are links or junctions are not checked, framed as a time-of-check to time-of-use race — [SentinelOne](https://www.sentinelone.com/vulnerability-database/cve-2026-50526/) **[SOURCED, but vague: no versions named; verify against Microsoft's advisory]**.
- A Windows broker exploit used an oplock to pause at the open step and swap the destination for an NTFS junction — [Packet Storm](https://packetstormsecurity.com/files/author/11974) **[SOURCED]**.
- One agent sandbox fixed this by walking components with no-follow and testing swaps at both final and intermediate components. A related issue notes that directory listing re-resolves links after the sandbox check — [cleverthis PR #120](https://git.cleverthis.com/cleveragents/cleveractors-core/pulls/120), [issue #126](https://git.cleverthis.com/cleveragents/cleveractors-core/issues/126) **[SOURCED, POSIX]**.
- `File.ResolveLinkTarget` resolves symbolic links and junctions, follows up to 63 links on Windows for the final target, and returns null for non-links — [.NET docs](https://learn.microsoft.com/en-ca/dotnet/api/system.io.file.resolvelinktarget?view=net-6.0) (via search snippet) **[SOURCED]**.
- Lethal trifecta, OWASP, Claude Code junction checks, deny lists, and checkpoints are covered in the [earlier note](../Whole%20laptop%20Jazari%20optimization/agent_tools_and_output.md).

### Inferences
Handle-first file access, one `SafeFs` class all four tools use **[INFERRED; no Windows-specific source confirmed it]**:
1. Do the earlier note's lexical checks first: no UNC, `\\?\`, device names or `:` streams, then `GetFullPath` under the root with an `OrdinalIgnoreCase` plus separator prefix check.
2. Open with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)`. Writes are handled separately below.
3. Call Win32 `GetFinalPathNameByHandle(handle.SafeFileHandle, ..., FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)` through P/Invoke. Strip the `\\?\` prefix and check the result against the root's own final path, computed the same way once at startup, so that 8.3 names, junctions and symlinks in any component all resolve. Run the deny list on this final path too.
4. Read from the same stream. A swap after open cannot redirect a handle that is already open.
5. Writes: never write the target by name after the check. Write the temp file in the verified final directory, then re-open the target for the hash compare, verify its final path again, and `File.Replace`. The remaining window there is small and requires a local attacker racing the user's approval keypress. Accept that, and document it.
6. `list_dir` and `search`: enumerate with `AttributesToSkip = FileAttributes.ReparsePoint` for both files and directories. The current `Walk` uses `AttributesToSkip = 0` and only skips reparse *directories*, so a file symlink to `C:\Users\me\.ssh\id_rsa` named `notes.txt` passes both `Wanted` and `IsSecret`. Then apply the handle check to every file `search` opens.

Fixes to existing code before the model drives it **[INFERRED from repo facts]**:
- `ProjectFiles.Load(target)` reads any existing file with no root check ("asking for it by name overrides the folder rules"). That is fine for a human typing `/read`, but the tool path must go through `SafeFs` and never call `Load` with a model-supplied path.
- `IsSecret` matches the file name only. Add folder rules (`.ssh/`, `.aws/`, `.git/`), and run it on the final resolved path.

Limits: per-call output 1,600 tokens (earlier note). Per-turn total tool output is about 8,000 tokens, after which the loop forces the answer. Search runs at most about 2,000 files and 2 seconds of wall time. Keep `MaxFileBytes` at 256 KB, plus the NUL check already in `TryRead`.

Injection:
- With no shell and no network tool, an injected instruction can at most cause reads inside the root, which are shown, or an edit, which needs approval.
- The one new leak path in a loop is `search` or `read_file` pulling a secret into context, followed by the model echoing it in an answer the user then pastes elsewhere. The deny list on final paths is the control.
- Also show the user any tool argument the model took from file content. For example, if a README says "read ../../secrets", the printed call line makes that visible.

### Gaps
- No primary Microsoft doc on `GetFinalPathNameByHandle` semantics could be fetched (host blocked), and no source confirms the handle-first pattern for Windows agent sandboxes. It needs a unit test: create a junction inside the root that points outside, then a file symlink, then an 8.3 path. **[MEASURE]**
- Whether creating symlinks requires Developer Mode or admin on the user's Windows setup (which affects how likely the attack is) was not checked. Junctions need no privilege.

## Latency budget: keeping a 3–5 step loop tolerable on CPU

### Takeaway
A step costs new prompt tokens divided by prefill speed, plus generated tokens divided by generation speed. In a loop the new prompt is mostly the previous tool result, so the file content dominates. On a pure transformer the cached prefix makes earlier steps free. On Granite 4.0-H Tiny's hybrid architecture that is only true while the conversation is strictly append-only and byte-identical on re-render. Any divergence can force a full re-prefill, because recurrent state cannot roll back without a checkpoint.

### Cited Findings
- Recurrent and hybrid models (Mamba, RWKV, Jamba) "can't roll their recurrent state back to an arbitrary position". llama-server uses prompt checkpoints for partial restore — [rig-llama-cpp checkpoint.rs](https://docs.rs/crate/rig-llama-cpp/latest/source/src/checkpoint.rs) (describing llama-server's `server_prompt_checkpoint`) **[SOURCED, secondary]**.
- Current master flags **[SOURCED: [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md)]**:
  - `--ctx-checkpoints N`: max checkpoints per slot, default 32.
  - `--checkpoint-min-step N`: minimum spacing between checkpoints, **default 8192 tokens**.
  - `--cache-ram`: default 8192 MiB.
  - `--cache-reuse N`: default 0.
- #30357 (open, 2026-10-11) **[SOURCED: [#30357](https://github.com/ggml-org/llama.cpp/issues/30357)]**:
  - The server strips whitespace around `<think>` tags and the template cannot reproduce the model's exact bytes. On hybrid models, a divergence with no checkpoint before it logs "forcing full prompt re-processing".
  - Example: a 25-token prompt growth caused 7,260 tokens to be re-processed (about 41 seconds on a GPU at 177 t/s).
  - Tool calls are affected at the boundary before `<tool_call>`, where trailing newlines in content are re-rendered differently.
  - Suggested client workaround: strip leading and trailing whitespace from responses. No fix is linked.
- #22940 (closed as stale, 2026-06-28): for memory backends that only support full sequence removal (Mamba, Jamba, hybrid), cache restore aborted and every turn re-prefilled. A 12-turn agent test took 298.5 seconds with p50 time-to-first-token of 18.7 seconds, versus 121.7 seconds and 3.15 seconds with the reporter's unsubmitted patch — [#22940](https://github.com/ggml-org/llama.cpp/issues/22940) **[SOURCED; status at build b11982 unknown]**.
- Other hybrid cache issues are open as of Sept–Oct 2026: slot save/restore gives no reuse on hybrid models because checkpoints are not persisted ([#28194](https://github.com/ggml-org/llama.cpp/issues/28194)); `--cache-ram -1` grows RAM about 640 MiB per short prompt with hybrid models ([#29324](https://github.com/ggml-org/llama.cpp/issues/29324)) **[SOURCED: titles]**.
- Speeds on this laptop for Granite 4.1 3B: about 35 t/s prefill and 6 t/s generation; caching worked inside a tool loop (424 tokens, then 10–21 per call) — [earlier note](../Whole%20laptop%20Jazari%20optimization/agent_tools_and_output.md) **[SOURCED: measured here earlier, dense model]**.

### Inferences
Budget model **[INFERRED; Granite 4.0-H Tiny speeds are a MEASURE item]**. Using the 4.1 3B speeds as a stand-in:

| Step | New prompt tokens | Generated | Approx. time |
|---|---|---|---|
| 1: question to `search` | ~60 (system and tools cached) | ~30 | 2 + 5 = **7 s** |
| 2: 30 hits (~600) to `read_file` | ~640 | ~30 | 18 + 5 = **23 s** |
| 3: 150 lines (~1,650) to answer | ~1,700 | ~200 | 49 + 33 = **82 s** |
| **Total, 3 steps** | | | **~2 minutes** |

- Every extra `read_file` adds about 50 seconds; an edit step adds 25+ seconds plus approval time. A 5-step loop with two reads lands near 3 to 4 minutes.
- The levers are to read less (`start_line` ranges, search first) and generate less (short tool turns, `max_tokens` 300 on tool steps). Faster prefill would also help, if Granite 4.0-H Tiny's 1B active parameters deliver it.

Rules to keep the hybrid cache valid inside a loop:
1. **Strictly append.** Within one user turn, never change, trim or stub earlier messages, and never reorder system parts. The earlier note's "stub old tool results past 60%" must happen only between user turns, owned by the memory effort. On a hybrid model, doing it mid-loop is a likely full re-prefill.
2. **Echo the assistant tool-call message exactly as returned.** Keep `arguments` as the identical string, with `content` trimmed to `""`. Strip leading and trailing whitespace from any assistant content before appending it (the #30357 workaround).
3. **Same request shape every step.** Keep the same `tools` array (byte-identical JSON, fixed order), the same system text, and the same `tool_choice` except for the final forced answer step.
4. **Do not interleave other work on the slot during a loop.** `RequestQueue` serves `/infill`, `/status` and `/warm` too. One of those between steps can evict the loop's state, and on a hybrid model it may not come back via checkpoint. Either hold the queue for the duration of the loop, or run llama-server with 2 slots and pin the loop to one with `id_slot`. **[INFERRED; MEASURE RAM cost]**
5. **Checkpoint spacing.** The default `--checkpoint-min-step` of 8192 tokens means that at a 16k context there are at most one or two checkpoints. A divergence anywhere tends to fall back to a full re-prefill of about 8k tokens, which is about 4 minutes at 35 t/s. Try `--checkpoint-min-step 1024` and check RAM. Hybrid checkpoints are small, but the size on this model is not known.
6. **Measure every step.** The gateway already gets `timings` (`prompt_n`, `cache_n`). Log both per loop step. If `prompt_n` on step k+1 is much larger than the tool result just appended, the prefix diverged. Have the CLI show a warning in debug mode.
7. If items 1–6 cannot hold the cache on Granite 4.0-H Tiny, that is a strong reason to run the loop on dense Granite 4.1 3B or Qwen3-4B-2507, where the KV cache truncates cleanly to any prefix.

### Gaps
- Whether build b11982 includes the checkpoint behaviour described in current master, and what `--checkpoint-min-step` defaults to there, is unknown. Check `llama-server --help`. **[MEASURE]**
- Granite 4.0-H Tiny prefill and generation speeds on this laptop, and the size of its hybrid state checkpoints, are not known. **[MEASURE]**
- Whether Granite's template re-renders a returned tool call byte-identically was not tested. Test it by comparing `/apply-template` output against the raw generated text from a non-streamed call with `--verbose` logging. **[MEASURE]**
