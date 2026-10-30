# Giving Jazari reliable file tools and clean code output (Granite 4.1 3B, llama.cpp, CPU, late 2026)

Research date: 2026-10-30. Evidence labels used below:
- **[MEASURED here]**: run on this laptop today against the local build (b11982-8a199ad31) and `granite-4.1-3b-Q4_K_M.gguf`.
- **[MEASURED by source]**: the source ran a test and reports numbers. Not reproduced here.
- **[DOC]**: the project's own documentation or source code.
- **[CLAIMED]**: vendor, blog or single-reporter statement with no controlled data.

Several source pages were read through a summarizing fetch tool, so the quoted figures are as summarized. Nothing tested here covers a real multi-step agent task.

## Concrete recommendation for Jazari

### Takeaway
Use llama-server's native tool calling with four small tools (`read_file`, `list_dir`, `search`, `edit_file`), one call per turn, and all safety enforced in the client in C#. Use exact-match search/replace for edits, with a verify-then-apply step and a retry that shows the model the real text. See "LIVE_RESULT" below for how the native and text+GBNF edit paths compared on this model. Fix the code-block padding in `MarkdownStream.cs` and add `/copy N` through TextCopy.

### Cited Findings
- llama-server's own built-in tools exist in this build: `--tools` offers read_file, file_glob_search, grep_search, exec_shell_command, write_file, edit_file and get_info, labelled "experimental" and "do not enable in untrusted environments" **[MEASURED here: `llama-server --help`]**. The server README says the `/tools` REST endpoint is internal to the Web UI and may change, and warns against downstream use — [llama.cpp server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).
- Jazari's CLI is the right place for the loop: `InputLoop.cs`, `Session.cs`, `SseChunk.cs` and `MarkdownStream.cs` already exist; the gateway's `PromptBuilder.cs` and `ChatEndpoints.cs` sit in front of llama-server **[MEASURED here: repo listing]**.
- `docs/OPTIMIZATION-LOG.md` lists "File tools for the model" and "Clean code output and copy" as open items **[MEASURED here]**.

### Inferences
Build order, smallest useful first:
1. **Read-only loop (ship first).** `read_file`, `list_dir`, `search`. No approval needed inside the workspace. This alone covers the user's "read a whole project" goal.
2. **Path guard and secret block.** One C# function all tools route through (see the safety section). Must land before or with step 1, because reads are the secret-leak path.
3. **`edit_file` with diff, approval and git checkpoint.**
4. **`/copy N` and the padding fix.** Independent of the tools and quick, so it can go first if wanted.
5. **Skip for now:** a shell tool, parallel calls, LoRA, a vector index, MCP, auto-approve. Add a shell tool only with per-command approval, and only if tests-after-edit prove necessary.

Loop rules: max 6 tool calls per user turn, one call per turn (`parallel_tool_calls: false`), `max_tokens` 400 on tool turns, temperature 0 to 0.2, retry a failed edit at most 2 times (Aider uses 3 reflections), and always print each tool call to the user. Hand the final answer back as normal streamed text.

Do not use llama-server `--tools`: it runs on the server with no diff or approval step, and it would bypass everything Jazari needs to enforce.

### Gaps
- No end-to-end multi-file task has been tested on this model. Run one on this repo before trusting the loop.

## llama-server tool calling: flags, templates, Granite format, parallel, streaming, grammar, bugs

### Takeaway
Tool calling works through `/v1/chat/completions` on this build with no extra flag, since `--jinja` is on by default. Granite 4.1 uses a Hermes-style `<tool_call>` JSON format that the template system supports. Custom `grammar` cannot be combined with `tools`, which forces a choice between the two paths.

### Cited Findings
- `--jinja` default is enabled in this build **[MEASURED here: `--help`]**; [README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) agrees, although [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md) still says to start with `--jinja`. `scripts/start-llama.ps1` does not pass it and does not need to **[MEASURED here]**.
- `/props` on this build reports `supports_tools: true`, `supports_tool_calls: true`, `supports_parallel_tool_calls: true`, `supports_system_role: true`, `supports_object_arguments: true`, `supports_preserve_reasoning: false` **[MEASURED here]**.
- Docs inconsistency: function-calling.md's native list does not name Granite, and its template table lists Granite 3.1 as Generic, yet its "Native support" examples include `ibm-granite/granite-4.1-3b-GGUF:Q4_K_M`. The table is flagged as possibly stale — [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md). The old fixed handlers were replaced by a PEG autoparser that derives parsing from the template — [PR #18675](https://github.com/ggml-org/llama.cpp/pull/18675).
- Granite 4.1 format **[DOC]**: tools are rendered in `<tools></tools>`; the model returns `<tool_call>{"name": ..., "arguments": ...}</tool_call>` — [IBM Granite 4.1 docs](https://www.ibm.com/granite/docs/models/granite4-1).
- Rendered prompt **[MEASURED here: `/apply-template`]**: role `tool` becomes `<|start_of_role|>user<|end_of_role|>` with `<tool_response>...</tool_response>`, and assistant calls are re-rendered as `<tool_call>` blocks. The template also adds a fixed line telling the model to notify the user when a tool does not exist. The template has no default system prompt — [chat_template.jinja](https://huggingface.co/ibm-granite/granite-4.1-3b/raw/main/chat_template.jinja).
- Tools and custom grammar are mutually exclusive: the server throws `Cannot use custom grammar constraints with tools.` when tools are present and tool_choice is not none. `json_schema` and `grammar` together also throw. `grammar_lazy`, `grammar_triggers` and `preserved_tokens` are stripped from requests — [server-common.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/server-common.cpp) **[DOC]**.
- Native tool calls already get a lazy grammar: under `tool_choice: "auto"` the peg-native path enables a grammar triggered by the tool-call marker — [issue #24807](https://github.com/ggml-org/llama.cpp/issues/24807).
- Parallel calls: off in docs unless `parallel_tool_calls: true` — [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md). The source code, however, defaults the field to the template capability, which is true for Granite, so send `false` explicitly — [server-common.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/server-common.cpp) **[DOC; conflict between the two]**.
- Streaming: PR #18675 says its template tests also cover streaming — [PR #18675](https://github.com/ggml-org/llama.cpp/pull/18675). One third-party wiki says partial tool-call JSON can stream to clients — [Factory wiki](https://factory.com/open-source-wikis/llama-cpp?page=systems%2Fchat-templates.md) **[CLAIMED]**.
- `arguments` must be handled as a string or an object: builds around b8233 returned an object, breaking OpenAI clients; fixed by b8240 — [issue #20198](https://github.com/ggml-org/llama.cpp/issues/20198). Maintainers argued clients should accept both.
- Known recent bugs:
  - Granite 4 tool arguments containing `:` or `\u` crashed the server (closed, 2025) — [#16465](https://github.com/ggml-org/llama.cpp/issues/16465). Worth a regression test, since Windows paths and code are full of these.
  - Granite 4 template detection failure (2025) — [#16415](https://github.com/ggml-org/llama.cpp/issues/16415).
  - Malformed tool-call blocks are dropped and the stream aborted rather than recovered, seen about 1 in 128 requests on Qwen3.6 at b9722; fixed by PR #24839 and later builds after b9754 — [#24807](https://github.com/ggml-org/llama.cpp/issues/24807). The client must therefore treat an aborted stream as a retryable tool failure.
  - Parallel calls fail under peg-native when text precedes `<tool_call>`, reported on b10088 — [#20260](https://github.com/ggml-org/llama.cpp/issues/20260). Status at this build not checked.
  - Build b9656 surfaced clean errors on final parse failure, per a secondary editorial — [PSEEDR](https://pseedr.com/stack/hardening-local-agentic-workflows-llamacpps-peg-native-tool-call-parsing-update) **[CLAIMED]**.
- `-ctk q4_0` KV quantization can substantially degrade tool calling — [function-calling.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/function-calling.md). Relevant if cache-RAM tuning reaches for that flag.

### Inferences
- Always send `parallel_tool_calls: false`. It avoids the #20260 class of bug and keeps approvals simple.
- Accumulate streamed tool-call deltas, or use non-streaming on tool turns. At 6 t/s a tool call of about 30 tokens takes about 5 seconds, so streaming its JSON has no user value. Show "calling read_file..." instead.
- Pin the llama.cpp build. This area has changed fast and regressed repeatedly.

### Gaps
- Whether #20260 still reproduces at b11982 (irrelevant if parallel calls stay off).
- No Granite 4.1 specific llama.cpp tool-calling bug report was found. That could mean it works or that few people use it.

## How reliable is tool calling on 3 to 4B models, and which edit format works best?

### Takeaway
Small models pick tools acceptably on simple prompts but over-call, and Granite's 3B-class results show this exact weakness. For edits, content-anchored search/replace is the best-supported choice: it fails visibly, and line-number formats corrupt silently. No source measured a 3B model on real multi-step file editing.

### Cited Findings
Tool-calling reliability:
- Granite 4.1 3B scores 60.80 on BFCL v3 per its own card — [model card](https://huggingface.co/ibm-granite/granite-4.1-3b) **[MEASURED by source, vendor]**.
- 21-model CPU test, 12 prompts × 20 runs: `granite4:3b` scored action 0.800, restraint 0.000, 1 wrong tool, agent score 0.520; `qwen3:1.7b` led with 0.960. Author: size is a weak predictor and restraint separates models — [tool-calling-benchmark](https://github.com/MikeVeerman/tool-calling-benchmark) **[MEASURED by source]**. Caveats: single author, Ollama defaults with 4,096 context, single-turn only, and `granite4:3b` is the Ollama tag and may be Granite 4.0 rather than 4.1.
- Parser fixes moved scores both ways (lfm2.5 0.640 to 0.920; smollm3 0.740 to 0.710) — [same](https://github.com/MikeVeerman/tool-calling-benchmark). Harness quality can matter as much as the model.
- Sub-2B CPU study (100 prompts): 89.4% of 658 failures were JSON recovery failures, wrong-tool only 4.1%; only 5 of 1,000 raw responses parsed strictly. Qwen2.5-1.5B reached 75% greedy and 79% sampled; sampling dropped Qwen2.5-0.5B from 72% to 32% — [arXiv 2609.07370](https://arxiv.org/html/2609.07370v1) **[MEASURED by source]**. It used no grammar, and those models are below Granite's size.
- A secondary guide reports Qwen3.5 falling from 0.661 at 9B to 0.503 at 4B and 0.436 at 2B and calls about 4B a practical floor — [PromptQuorum](https://www.promptquorum.com/power-local-llm/best-local-models-tool-calling-2026) **[CLAIMED, original not checked]**.

Edit formats:
- Apply-step benchmark, 640 edits over 224 files: search/replace and diff "fail cleanly"; line numbers "corrupt 99.1% of files under a one-line shift"; function-name edits hit the wrong function 12.7% of the time; an anchor-and-verify applier recorded 1 silent misapply in 8,320 trials (0.01%) — [arXiv 2609.11957](https://arxiv.org/abs/2609.11957) **[MEASURED by source; single author; apply step only]**.
- Hashline, 3 runs × 180 tasks × 16 cloud models: patch failed 50.7% (Grok 4) and 46.2% (GLM-4.7); hashline averaged +15 points over patch; against str_replace it ranged from +11.3 to −8.3 by model; weaker models gained most — [The Harness Problem](https://stencil.so/blog/the-harness-problem) **[MEASURED by source; author sells the idea]**.
- Small replication: after fixing a harness bug, hashline 19/20 and search/replace 20/20 on Haiku 4.5 — [edit-bench #13](https://github.com/nwyin/edit-bench/issues/13) **[MEASURED by source, tiny]**.
- Llama-3.1-based program repair: best solve rates of search/replace 53%, unified diff 30%, line diff 26% — [arXiv 2507.18755](https://arxiv.org/pdf/2507.18755) **[MEASURED by source, as shown in a search excerpt]**.
- Aider: without flexible patching, editing errors rose 9× on Exercism; unified diffs reduced lazy elision about 3× and lifted a laziness benchmark from 20% to 61% on GPT-4 Turbo — [Aider unified diffs](https://aider.chat/2023/12/21/unified-diffs.html) **[MEASURED by source, 2023]**.
- Aider edit formats: whole, diff (search/replace), diff-fenced, udiff, editor variants — [docs](https://aider.chat/docs/more/edit-formats.html).
- Wrapping code in JSON hurt: "LLMs are bad at returning code in structured JSON responses", with more syntax errors in code — [Aider code-in-JSON](https://aider.chat/2024/08/14/code-in-json.html) **[MEASURED by source, 2024, cloud models]**.
- Copy-as-Decode reported search/replace failing on anchor ambiguity — [arXiv 2604.18170](https://arxiv.org/abs/2604.18170). **The paper was withdrawn on 2026-05-24; do not rely on it.**

Recovery:
- Aider feeds the apply error back as the next message, limit `max_reflections = 3` — [base_coder.py](https://raw.githubusercontent.com/Aider-AI/aider/master/aider/coders/base_coder.py) **[DOC]**.
- Cline `replace_in_file` retries on mismatch; a third-party page reports a past fix for infinite retry loops — [developersdigest](https://www.developersdigest.tech/errors/cline/diff-search-patterns-dont-match) **[CLAIMED]**.
- Roo Code defaults to exact match with an optional 80 to 99% fuzzy threshold, and falls back to whole-file writes if diffs are off — [Roo fast edits](https://docs.roocode.com/features/fast-edits) **[DOC]**.

### Inferences
- Ladder for Jazari's applier: exact match, then line-trimmed match (leading/trailing whitespace only), then fail. No percentage fuzzy matching: on the user's own code a wrong-place edit is worse than a retry. Require exactly one match. The diff preview and approval catch the rest.
- On failure, return the nearest real lines from the file in the tool result, so the retry has correct text. This costs far less than re-reading the file.
- Line numbers in `read_file` output are for reading only. Never let them address an edit (99.1% corruption under a one-line shift).
- Whole-file rewrite is the fallback only for files under about 60 lines. At 11 tokens per line and 6 t/s, a 105-line file costs about 200 seconds to re-emit, and a small model may elide or alter code.
- Granite's restraint failure means the system prompt must say when not to use a tool. The harness should also not count a useless tool call as an error.

### Gaps
- No public benchmark of edit formats on a 3B local model exists that I could find. The only 3B data is the small test in this file.

## Tool design for slow models: count, descriptions, token cost, truncation, 16k context

### Takeaway
Four terse tools cost 378 tokens on this model, versus 643 with ordinary verbose descriptions. Tool definitions are the small cost at 16k; file contents are the big one, and they must be capped hard.

### Cited Findings
- Token cost on the real template **[MEASURED here: `/apply-template` plus `/tokenize`]**:

| Tool set | Tokens added to the prompt |
|---|---|
| 1 lean tool (`read_file`) | 180 |
| 2 lean tools | 234 |
| 4 lean tools, one-sentence descriptions, no param descriptions | 378 |
| 4 verbose tools, 2 to 3 sentences plus param descriptions | 643 |

  So the template's fixed wrapper costs about 120 to 130 tokens and each lean tool about 50 to 70 (inferred from those deltas). `edit_file` is the most expensive lean tool.
- Per-call overhead **[MEASURED here]**: a `<tool_call>` with a typical path is 29 tokens; the `<tool_response>` wrapper is about 8.
- This repo's C# runs at 9.9 to 11.6 tokens per line **[MEASURED here: `Ui.cs`, `PromptBuilder.cs`, `Program.cs`]**.
- Speeds on this laptop: generation about 6 t/s, prompt processing about 35 t/s — [measured_thread_sweep.md](research_notes/Local%20model%20load%20optimization/measured_thread_sweep.md) **[MEASURED here earlier]**.
- Limiting tools available at once cut execution time by up to 70% and power by up to 40% on edge devices — [Less-is-More, arXiv 2411.15399](https://arxiv.org/abs/2411.15399) **[MEASURED by source; success figure not in the abstract]**.
- `cache_prompt` defaults to true, evaluating only the unseen suffix — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC]**. `--chat-template-kwargs` and `/apply-template` are available for tuning — same source.
- Aider's repo map defaults to about 1k tokens, and users report it overrunning its budget — [fast.io](https://fast.io/resources/aider-context-window.md) **[CLAIMED]**.

### Inferences
Budget at `-c 16384` (derived from the figures above):
- System prompt 150 + tools 378 + reserved output 1,500 = about 2,000. That leaves about 14,000 tokens for conversation and file content, or about 1,300 lines of C#.
- A 300-line read (about 3,300 tokens) takes about 95 seconds to ingest at 35 t/s. A 5-line search/replace edit (about 140 tokens with overhead) takes about 25 seconds to generate.
- Time cost of tool definitions: 378 tokens at 35 t/s is about 11 seconds once, then cached, **provided the prefix stays byte-stable**. Keep tools and system text first and unchanging. Put changing state (`GitSummary`, project notes in `PromptBuilder`) after them, or each turn pays again.

Suggested tool defs:
- `read_file(path, start?)`: return at most 150 lines or 1,600 tokens, numbered, ending with `[truncated: lines 151-420 remain, call read_file with start=151]`.
- `list_dir(path)`: at most 100 entries, directories first; skip `.git`, `bin`, `obj`, `node_modules`.
- `search(query, path?)`: at most 30 hits with `path:line: text`, each cut to 160 characters, plus a total count. Implement it in C# (`Directory.EnumerateFiles` plus `File.ReadLines`), so no ripgrep dependency is needed.
- `edit_file(path, old, new)`: see the edit section.

Context management:
- After the model has used a tool result, replace old results in history with a one-line stub (`[read Ui.cs lines 1-150, dropped]`) once the budget passes about 60%. Note that this changes the prompt and costs a re-ingest, so do it rarely and in one step.
- Refuse a read that would overflow rather than letting llama.cpp truncate. Context shift is disabled by default in this build **[MEASURED here: `--help`]**.

### Gaps
- No measured effect of 4 versus 3 tools on Granite's choice accuracy.

## Native templates, or a custom text protocol plus GBNF?

### Takeaway
LIVE_VERDICT

### Cited Findings
- Tools and custom grammar cannot be mixed on the same request **[DOC]** — [server-common.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/server-common.cpp).
- Native calls are guarded by a lazy grammar, so JSON shape errors should be far rarer than in the unconstrained sub-2B study — [#24807](https://github.com/ggml-org/llama.cpp/issues/24807); [arXiv 2609.07370](https://arxiv.org/html/2609.07370v1) found 89.4% of failures were JSON recovery with no grammar in use.
- JSON-wrapped code raises syntax errors, at least on cloud models — [Aider](https://aider.chat/2024/08/14/code-in-json.html).
- llme-cli is one tool that uses a custom protocol against llama-server rather than the tools API — [llme-cli](https://pypi.org/project/llme-cli/0.1.3/).
- The grammar did not stop malformed output in one reported case, because of a grammar bug — [#24807](https://github.com/ggml-org/llama.cpp/issues/24807). Grammars reduce risk but do not remove the need for a validator.
- The model was trained on its `<tool_call>` format, and format fit moves scores — [tool-calling-benchmark](https://github.com/MikeVeerman/tool-calling-benchmark).

LIVE_TABLE

### Inferences
LIVE_INFERENCE

### Gaps
- Sample is small: 3 synthetic edits × 3 seeds on one 14-line file, so it shows direction and failure kinds, not rates.

## Safety for a tool running on the user's own code

### Takeaway
Treat every file as untrusted and enforce safety in deterministic C#, not in the prompt. With no network tool and no shell, Jazari lacks the exfiltration leg of the "lethal trifecta", which is the strongest protection available; keep it that way.

### Cited Findings
Tiering and approval:
- Claude Code: read-only tools need no approval inside the working directory; file modification needs approval; "don't ask again" for edits lasts until session end — [permissions](https://code.claude.com/docs/en/permissions) **[DOC]**.
- Codex: read-only mode cannot edit or run without approval; `workspace-write` plus `on-request` is the preset; `/permissions` switches mode — [Codex sandboxing](https://developers.openai.com/codex/concepts/sandboxing) **[DOC]**.

Paths and secrets:
- Claude Code checks both the requested path and the resolved target, on Windows too, including directory junctions; allow needs both to match, deny needs either — [permissions](https://code.claude.com/docs/en/permissions) **[DOC]**. This followed real bypasses: [CVE-2026-25724](https://cvefeed.io/vuln/detail/CVE-2026-25724), rated 7.5 High, and [CVE-2025-59829](https://osv.dev/vulnerability/CVE-2025-59829).
- Writes to `.git` and `.claude` are treated as protected paths — [permissions](https://code.claude.com/docs/en/permissions) **[DOC]**.
- Read deny rules such as `Read(./.env)` and `Read(./secrets/**)` are the secret mechanism; `.claudeignore` has no effect — [permissions](https://code.claude.com/docs/en/permissions) **[DOC]**.
- NVIDIA's AI Red Team lists blocking egress, blocking writes outside the workspace and blocking writes to agent config as mandatory — [NVIDIA](https://developer.nvidia.com/blog/practical-security-guidance-for-sandboxing-agentic-workflows-and-managing-execution-risk/).

Prompt injection:
- The trifecta: private data, untrusted content, external communication. The author holds that nobody can reliably prevent the attack and that the safe option is avoiding the combination — [Willison](https://simonwillison.net/2025/Jun/16/the-lethal-trifecta/).
- OWASP says to treat all repository content as untrusted input, and notes tool descriptions are also part of the context — [OWASP Secure Coding with AI](https://cheatsheetseries.owasp.org/cheatsheets/Secure_Coding_with_AI_Cheat_Sheet.html). It also recommends interrupt, rollback and audit trails — [OWASP AI Agent](https://cheatsheetseries.owasp.org/cheatsheets/AI_Agent_Security_Cheat_Sheet.html).
- Limiting permissions is called the most reliable mitigation, and none of the controls prevents the injection itself — [GitGuardian](https://blog.gitguardian.com/ai-agent-security-best-practices/). Argument injection through "safe" commands reached RCE in real agents — [Trail of Bits](https://blog.trailofbits.com/2025/10/22/prompt-injection-to-rce-in-ai-agents/).

Undo:
- Aider auto-commits each change and first commits any dirty work so the two stay separate; `/undo` discards its last commit — [Aider git](https://aider.chat/docs/git.html) **[DOC]**.
- Cline keeps a shadow Git repo separate from the project's, committing after each tool use and capturing untracked files; restore can revert files, conversation or both; it can be slow on very large repos — [Cline checkpoints](https://docs.cline.bot/core-workflows/checkpoints) **[DOC]**.
- Claude Code snapshots before each prompt and keeps 100 checkpoints; Bash changes are generally not tracked; symlinked and hard-linked files are skipped; it is explicitly not a replacement for git — [checkpointing](https://code.claude.com/docs/en/checkpointing) **[DOC]**.

### Inferences
Windows path guard, one function that every tool calls (no source tested these specifics; they follow from the Claude Code rules and the CVEs above):
1. Reject before resolving: UNC and `\\?\` paths, device names (`CON`, `NUL`, `COM1`), and any `:` after the drive position, which blocks NTFS alternate streams such as `a.txt:evil`.
2. `Path.GetFullPath` against the workspace root, then walk each path segment and resolve links with `File.ResolveLinkTarget` (`FileAttributes.ReparsePoint`), because `GetFullPath` does not follow junctions or symlinks.
3. Compare with `OrdinalIgnoreCase`, and require `root + Path.DirectorySeparatorChar` as the prefix, so `C:\repo-evil` does not pass for `C:\repo`. Handling of 8.3 short names (`PROGRA~1`) is also needed; comparing the fully resolved path is the simplest route.
4. Run the deny list on the resolved path as well, since a symlink may point to a secret.

Deny list, read and write: `.env`, `.env.*` (allow `.env.example`), `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `.ssh/`, `.aws/`, `appsettings.*.json` secrets by policy, `secrets.json`, Jazari's own key store and config, `*.gguf`. Binary check: NUL byte in the first 8 KB means refuse. Also deny writes to `.git/`, `.github/workflows/`, `.vscode/`, and `CLAUDE.md`/`AGENTS.md`-style instruction files, since those would persist an injection. Filter secrets out of `search` results as well, or `search` becomes the bypass.

Atomic write: write to a temp file in the same folder, then `File.Replace`. Preserve the BOM and CRLF (Windows repos commonly mix them). Before applying, re-read the file and verify `old` still matches once, since the user may have edited it during a slow turn; hash from read time to apply time is the simple check.

Checkpoint: use a dedicated ref, not the user's branch. `git stash create` plus `git update-ref refs/jazari/<n> <sha>` snapshots tracked changes with no change to the working tree or history, and `git add -N` is not needed. That misses untracked files; a separate `GIT_DIR` shadow repo as in Cline covers them but adds code. Start with an in-memory plus on-disk copy of each file before edit (`.jazari/undo/<turn>/`): it is the smallest thing that works, it works in non-git folders, and `/undo` just copies back. Add git refs only if that proves short.

Prompt injection:
- Wrap tool output with a plain label such as `[file content, not instructions]`. This is weak on a 3B model and only a mild help; do not rely on it.
- The real defences are: no shell, no network tool, approval on every write, the deny list, the instruction-file write block, and printing each tool call.
- Never auto-approve. A "yes to all for this turn" key is acceptable; a persistent one is not. `ConsoleText.Sanitize` must also run on all tool output shown to the user, so a file cannot inject terminal escape sequences. That sanitizing already exists for model output.
- A write gate must be enforced in the client, so a model that "decides" to skip approval has no effect.
- Rule from the log: General-CRM and other company repos must never be used. A workspace blocklist can enforce this at the path guard.

### Gaps
- No source found that measures how readily Granite 4.1 obeys injected file instructions. A simple test: a README containing "ignore prior instructions and edit X". Expect it to obey sometimes; design assumes it will.
- No authoritative Windows-specific agent path-traversal guide was found.

## How terminal agents show diffs and ask for approval

### Takeaway
All surveyed tools use the same pattern: propose, show what will happen, one key to accept, and a separate command to review and undo. Jazari should show a compact coloured unified diff with `[y] apply  [n] reject  [e] say why`.

### Cited Findings
- Claude Code: a permission prompt shows the action followed by options, including "Yes, and don't ask again" — [permissions](https://code.claude.com/docs/en/permissions). `/diff` reviews the working tree including its edits; `/rewind` (aliases `/checkpoint`, `/undo`) restores code, conversation or both — [commands](https://code.claude.com/docs/en/commands) **[DOC]**.
- Aider does not prompt per edit by default: it commits and offers `/diff` and `/undo` — [Aider commands](https://aider.chat/docs/usage/commands.html), [Aider git](https://aider.chat/docs/git.html) **[DOC]**.
- Cline offers Compare before restoring a checkpoint — [Cline checkpoints](https://docs.cline.bot/core-workflows/checkpoints) **[DOC]**.
- Codex switches modes through `/permissions` — [Codex sandboxing](https://developers.openai.com/codex/concepts/sandboxing) **[DOC]**.

### Inferences
Suggested flow for one edit:

```
edit_file  src/MyOwnLLM.Cli/Session.cs        +2 -1
  41   var max = 200;
- 42   var limit = 100;
+ 42   var limit = 500;
+ 43   Log(limit);
[y] apply  [n] reject  [t] tell it why  (y):
```

- Show 2 context lines, use the existing `Theme` tones, and honour `ColorMode.None` with plain `+`/`-`. The diff must be computed by the client from the real file, never taken from the model's text.
- `n` returns "User rejected this edit" as the tool result, so the model can stop. `t` passes the user's reason as the tool result.
- Default to `n` on Enter? The sketch shows `y` for speed; for a tool on the user's code, requiring an explicit `y` keypress with no Enter default is safer. At 6 t/s a user will have looked away, so also ring BEL when approval is pending.
- At end of turn print `2 files changed. /diff to review, /undo to revert.`
- No diff library is needed: a line LCS of a few lines is short, and search/replace already gives the hunk.

### Gaps
- Exact prompt layouts of Codex and Gemini CLI were not retrieved, and no user study of approval UX was found.

## Prompt and context engineering for better code from a small model

### Takeaway
Keep house rules very short, feed one or two nearby real examples, and use low temperature. LoRA has the best small-model evidence but is not a first step, because of the data and GPU cost and the risk of overfitting a small personal codebase. A separate check by tests and compiler beats a model self-review.

### Cited Findings
- Granite 4.1 3B's code scores from its card: HumanEval 81.71, MBPP 71.16, BigCodeBench 32.19, CRUXEval-O 40.75 — [model card](https://huggingface.co/ibm-granite/granite-4.1-3b) **[MEASURED by source, vendor]**. The gap between simple functions and library-heavy tasks is why examples may matter.
- AGENTS.md study: context files tended to reduce success versus none, and raised cost by over 20%; LLM-generated files added 3.92 steps (new benchmark) and 2.45 steps (SWE-bench); human-written ones fared better; repo overviews did not help; the advice is minimal requirements only — [arXiv 2602.11988](https://arxiv.org/pdf/2602.11988), summarized at [aiweekly](https://aiweekly.co/alerts/paper-finds-agentsmd-files-hurt-coding-agent-task-success) **[MEASURED by source; large models, Python]**. Abstract versions differ on "no improvement" versus "tend to reduce".
- Few-shot, RAG and LoRA on 1 to 1.3B code models **[MEASURED by source]** — [arXiv 2603.16526](https://arxiv.org/html/2603.16526v1):

| Method | Python Pass@1 change | Scikit-learn | OpenCV (10 tasks) |
|---|---|---|---|
| StarCoder-1B few-shot (3) | −1.4 | −9.3 | +10.0 |
| StarCoder-1B RAG | −2.0 | −8.6 | 0 |
| StarCoder-1B LoRA | +2.3 | +7.2 | +20.0 |
| DeepSeekCoder-1.3B few-shot (3) | +8.5 | −4.0 | −20.0 |
| DeepSeekCoder-1.3B RAG | +9.1 | −3.3 | 0 |
| DeepSeekCoder-1.3B LoRA | +7.9 | +1.0 | +30.0 |

  LoRA used about 20,000 synthetic exercises per domain, cost about $374 in GPT-4o generation, and needed a GPU. Similarity to references rose for nearly all methods. These are base code models, not chat models like Granite.
- Temperature: mixed. One study correlated temperature and correctness at −0.721 and recommends under 0.5 — [arXiv 2408.10577](https://arxiv.org/pdf/2408.10577). A ChatGPT study found a marginal effect compared with top-p — [arXiv 2502.17450](https://arxiv.org/html/2502.17450v1). The sub-2B tool study found sampling at 0.7 cut a 0.5B model from 72% to 32% while not significantly changing a 1.5B model — [arXiv 2609.07370](https://arxiv.org/html/2609.07370v1).
- Server default is temperature 0.8, top_k 40, top_p 0.95, min_p 0.05 **[MEASURED here: `/props`]**. Jazari's code sets no sampling values in `PromptBuilder.cs` or `LlamaClient.cs` by the grep run here **[MEASURED here, grep only; the CLI or settings could set them elsewhere]**.
- Self-review: a formal-verification preprint found models could identify vulnerability classes in review mode that they failed to avoid when generating — [arXiv 2604.05292](https://arxiv.org/pdf/2604.05292) **[MEASURED by source; security only, not small models]**. Fresh-context or other-model reviewers are favoured by practitioners — [managed-code](https://www.managed-code.com/blog-post/ai-agent-code-review) **[CLAIMED]**.
- Lint and test feedback loops exist in Aider through `/lint` and `/test` — [Aider commands](https://aider.chat/docs/usage/commands.html) **[DOC]**.

### Inferences
- House style: a `JAZARI.md`-style file under about 120 tokens of hard rules only, for example "file-scoped namespaces; primary constructors; no new dependency without asking; tests next to code". No project overview. Read it once per session in the stable prefix.
- Retrieval: no embeddings. Before generating, use `search` for the symbol and include one or two short real methods from the same folder (about 200 tokens each). This is cheap, explainable, and works offline. Retrieved examples can hurt too (−9.3 and −8.6 above), so include only same-language, same-folder code, and measure.
- Checklist: 3 to 5 short items at the end of the user turn beat a long system prompt on tokens. The evidence is weak, though, so keep it short.
- Temperature: 0.2 for code and edits, 0 to 0.2 for tool turns, and keep 0.7 for chat. It is one line of change and is low-risk. Add a fixed seed only for evaluation.
- Self-review: at 6 t/s a second pass doubles latency. Run `dotnet build` and tests instead as a deterministic check, on user request. If a model review is added, use a fresh context with only the diff, and only for `/review`.
- LoRA versus retrieval: try retrieval first. The user's repos are likely too small for the roughly 20k examples used above, and adapters raise overfitting and maintenance risk. Revisit LoRA only after an eval shows a stable gap. Other notes in `research_notes/Advanced training methods feasibility` cover training in more depth.

### Gaps
- No study of few-shot from a user's own repo on a 3B chat model was found, and no direct RAG-versus-LoRA study on private code exists that I could find. The numbers above are on benchmark tasks and small test sets (10 OpenCV tasks).
- No sampling recommendation from IBM: neither the model card nor the docs give one.

## Clean code output in a terminal: numbering, /copy N, clipboard, mouse copy, Windows Terminal

### Takeaway
Jazari currently pollutes mouse copy: live mode pads every code line to full width with spaces, and long lines are hard-split at the window width. Remove the padding, stop splitting, number the blocks, and add `/copy [N]` using TextCopy for the native clipboard. OSC 52 is only needed over SSH.

### Cited Findings
Jazari today **[MEASURED here: source read]**:
- `MarkdownStream.cs:53-56`: code lines are split at `width` and, in colour mode, written with `part.PadRight(width)`. The padding is what gives code its background band, and those spaces are real characters that land in any mouse copy; the split adds hard newlines inside long lines.
- `MarkdownStream.cs:76`: the fence language is printed on its own muted line, a natural place for a block number.
- **Correction to the brief:** the CLI does not directly reference TextCopy. `MyOwnLLM.Cli.csproj` lists only `PrettyPrompt 6.0.5`, and no `.cs` file uses a clipboard. TextCopy 6.2.1 arrives transitively through PrettyPrompt (`project.assets.json`). It is present and usable, but add an explicit `PackageReference` if Jazari calls it directly, so an upgrade of PrettyPrompt cannot remove it.

What other tools do:
- Claude Code `/copy [N]`: copies the Nth-latest response; with code blocks present it opens a picker for individual blocks or the full response; `w` writes the selection to a file — [commands](https://code.claude.com/docs/en/commands) **[DOC]**.
- Aider: `/copy` copies the last assistant message; `/copy-context` copies chat context as Markdown — [Aider commands](https://aider.chat/docs/usage/commands.html) **[DOC]**.
- Gemini CLI: `/copy` copies the last response, per a third-party reference — [gradually.ai](https://www.gradually.ai/gemini-cli-befehle/) **[CLAIMED]**. A code comment in another CLI says Gemini CLI and Claude Code prefer direct-to-tty for OSC 52 — [koda-cli](https://docs.rs/crate/koda-cli/latest/source/src/clipboard.rs) **[CLAIMED]**.
- Codex CLI: no `/copy` documentation retrieved (gap).

Padding and wrapping are a known class of bug in agent CLIs:
- Claude Code users report copied text padded with trailing spaces to terminal width — [issue 22389](https://claudeissues.com/issue/22389-copying-text-from-cli-output-includes-trailing-spaces-to-end-of-terminal-width). The reported root cause: empty cells rendered as real 0x20 bytes — [issue 18170](https://claudeissues.com/issue/18170-copy-paste-from-terminal-includes-unwanted-indentation-and-trailing-spaces) **[CLAIMED; user reports seen in search excerpts, the pages could not be re-fetched]**.
- A report asks CLIs to emit long lines without hard newlines so the terminal's soft-wrap applies — [issue 64995](https://claudeissues.com/issue/64995-bug-cli-hardwraps-output-with-real-n-characters-based-on-terminal-window-width) **[CLAIMED]**.

Clipboard:
- TextCopy API: `ClipboardService.SetTextAsync(...)`; Windows, macOS, Linux (Linux needs `xsel`) — [TextCopy](https://github.com/CopyText/TextCopy). The page does not document its Windows mechanism.
- Windows Terminal supports OSC 52 clipboard writes — [terminal #2946](https://github.com/microsoft/terminal/issues/2946), fixed by PR #5823. Format: `OSC 52 ; c ; <base64> BEL` — [go-osc52](https://pkg.go.dev/github.com/aymanbagabas/go-osc52).
- OSC 52 can fail in practice: in a June 2026 report on Windows Terminal 1.24 the write did not reach RDP clipboard redirection, and the reporter suggested native clipboard for local sessions and OSC 52 only over SSH — [Claude issue 67576](https://claudeissues.com/issue/67576-bug-windows-copy-on-select-osc-52-never-reaches-rdp-clipboard-redirection-under) **[CLAIMED]**. Claude Code moved WSL copy-on-select from OSC 52 to PowerShell interop, because some terminals do not support OSC 52 — [issue 64642](https://claudeissues.com/issue/64642-docs-fullscreen-docs-omit-wsl-windows-clipboard-behavior-for-copy-on-select) **[CLAIMED, changelog as quoted]**. tmux needs extra configuration — [Devolutions forum](https://forum.devolutions.net/topics/55613/claude-code-osc-52-copy-text).

Windows Terminal specifics:
- Soft-wrapped lines should copy as one line since PR #5181 — [terminal #5113](https://github.com/microsoft/terminal/issues/5113). Later comments report it persisting on some versions, with fixes in 1.18/1.19 — same page.
- `copyOnSelect` default false, `copyFormatting` default false (plain text), `trimBlockSelection` default true (trailing spaces removed for rectangular Alt-drag selection only), `trimPaste` default true, multi-line paste warning default true — [WT interaction settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/interaction) **[DOC]**.
- OSC 133 marks are stable as of Terminal 1.21, and with `selectOutput` a whole command's output can be selected — [WT shell integration](https://learn.microsoft.com/en-us/windows/terminal/tutorials/shell-integration) **[DOC]**. Applications may emit these marks. Emitting them around code blocks is untested and is a misuse of their meaning, so skip it.

### Inferences
- **Padding fix:** drop `PadRight`. To keep a code background, paint the text only, or emit ANSI background plus `ESC[K` (erase to end of line), which fills the row with the background colour without writing space characters. Whether `ESC[K` cells copy as spaces should be checked by hand in Windows Terminal; xterm-style terminals generally treat them as empty. The simplest safe option is no band at all: a plain colour and a dim left margin outside the code is not safe either, since a `│ ` gutter also lands in the copy.
- **Wrapping fix:** write a code line whole and let the terminal soft-wrap, rather than splitting at `width`. Soft-wrap then copies correctly. The trade-off is that a wrapped line starts at column 0, which is cosmetic.
- **Numbering:** print the header on its own line before the block, outside the code, for example `[2] csharp` (reuse line 76), so the number never enters a code copy.
- **`/copy`:** `/copy` copies the last block, `/copy N` copies block N of the last answer, `/copy all` copies the full raw answer. Store raw block text from the stream, before sanitizing for display and before any wrap, in a per-answer `List<string>` in `MarkdownStream`. Print `Copied block 2 (31 lines).` Handle clipboard failure with a message and fall back to writing a temp file (Claude Code's `w` idea), since `/copy` should not crash on a locked clipboard.
- **Backend:** TextCopy first. Add OSC 52 only if the user runs Jazari over SSH, or when `SSH_TTY`/`SSH_CONNECTION` is set. It is about 3 lines: `Console.Out.Write($"\x1b]52;c;{Convert.ToBase64String(utf8)}\a")`. YAGNI for now, as the stated use is local Windows.
- Line endings: the model emits `\n`; on Windows convert to `\r\n` on copy, or leave as is? Most editors handle both, so copy what the model produced.
- Do not use the alternate screen or a full-screen TUI. Normal scrollback is what keeps mouse selection simple, and Jazari already streams to scrollback.

### Gaps
- Whether `ESC[K` background-filled cells copy as spaces in Windows Terminal was not verified; a one-minute manual test settles it.
- OSC 52 payload size limit in Windows Terminal was not found. A native clipboard avoids the question.
- Codex CLI's copy command and Gemini CLI's official docs were not retrieved.
