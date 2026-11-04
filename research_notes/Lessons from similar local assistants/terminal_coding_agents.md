# Open-source terminal coding agents that run on local models, and what Jazari could adopt

Research date: 2026-11-03. Evidence labels used on every line: **[sourced]** = read on the fetched primary page; **[snippet]** = only seen in a search-result summary or a secondary blog, not verified on the primary page; **[inferred]** = my reasoning, not a source claim. **[pre-2025]** = the source is older than 2025 and may be outdated.

Jazari reference point (from the brief, not re-verified): Granite 4.0-H Tiny (~1B active), llama.cpp on CPU/Vulkan, ~10 tok/s on a 16 GB T480, Windows-only, ASP.NET Core gateway with API key, queue, cache, state notes, law book, BM25 conversation pinning; client with /read /diff /review /edit (constrained-JSON search/replace + C# syntax check + confirm + /undo) /commit /docs /save /status /copy; `jazari doctor|tune|update`; no agent loop yet.

## Project profiles and what Jazari could adopt (objective)

### Takeaway
Every project surveyed targets models at least 3-4x larger than Jazari's (their smallest local examples are 3B-32B dense or ~3B-active MoE), and the only published edit-format benchmark (Aider) shows open models of 27-32B scoring 5-16% even with the safest "whole file" format. The most transferable ideas for a ~1B-active model are not the agent loops themselves but the guard rails around them: flexible patch matching, a ranked repo map under a fixed token budget, a build/test feedback loop, step caps that end with "summarize and list what is left", loop detection, read-only auto-approval, and AGENTS.md interop.

### Cited Findings

#### Aider (Python; Paul Gauthier)
- Status: latest numbered release on the history page is v0.86.1 (`reasoning_effort` for GPT-5); "main branch (unreleased)" adds Claude 4.5/4.6, GPT-5.1-5.4, Gemini 3 preview, a `/ok` shortcut to approve proposed changes. The page shows no release dates. [sourced] — [Aider release history](https://aider.chat/HISTORY.html)
- Edit formats: `whole` (full file, "the simplest possible editing format" but slow/costly), `diff` (SEARCH/REPLACE blocks in git-merge-conflict style, "efficient... only needs to return parts of the file which have changes"), `diff-fenced` (path inside fence, for Gemini), `udiff` (simplified unified diff, used to cut GPT-4 Turbo "lazy coding"), `editor-diff` / `editor-whole` (streamlined versions for architect mode's editor model). "Aider is configured to use the optimal format for most popular, common models." [sourced] — [Aider edit formats](https://aider.chat/docs/more/edit-formats.html)
- Later additions: v0.82.0 added a `patch` format (for GPT-4.1) and `editor-diff-fenced`; v0.83.0 added `udiff-simple` (Gemini 2.5 Pro); v0.85.5 pinned GPT-5 to `diff`. [sourced] — [Aider release history](https://aider.chat/HISTORY.html)
- Repo map: tree-sitter parses files into definitions/references; "a graph ranking algorithm" over a file-dependency graph keeps the most-referenced symbols; `--map-tokens` budget "defaults to 1k tokens"; map size "adjusts... dynamically based on the state of the chat" and grows when no files are in the chat. Example map shows file → class → method signatures with `⋮...` elisions; C# is among the supported tags.scm languages. [sourced; tree-sitter post is pre-2025] — [Aider repo map docs](https://aider.chat/docs/repomap.html); [Aider repo map blog, 2023-10-22](https://aider.chat/2023/10/22/repomap.html)
- Git: auto-commits every edit with a descriptive message written by the `--weak-model` from "a copy of the diffs and the chat history" (Conventional Commits by default); before editing a dirty file it "will first commit any preexisting changes" so user work stays separate; `/undo` "will undo and discard the last change"; `/diff` shows changes since last message; commits get "(aider)" appended to author/committer name. [sourced] — [Aider git docs](https://aider.chat/docs/git.html)
- Lint/test loop: lints every edited file by default (`--no-auto-lint` to disable), `--lint-cmd`, per-language `--lint "language: cmd"`; `--auto-test` + `--test-cmd`, with the docs' own example `--test-cmd "dotnet build && dotnet test"`; "Aider will try and fix any errors if the command returns a non-zero exit code." [sourced] — [Aider lint/test docs](https://aider.chat/docs/usage/lint-test.html)
- Local models: Ollama "uses a 2k context window by default" and "silently discards context that exceeds the window"; Aider by default sizes `num_ctx` per request "large enough for each request you send" plus reply room (v0.74.0). [sourced] — [Aider Ollama docs](https://aider.chat/docs/llms/ollama.html); [Aider release history](https://aider.chat/HISTORY.html)
- Windows: `/run` uses PowerShell or cmd.exe (v0.57.0); linter argument quoting on Windows fixed via `oslex` (v0.83.0). [sourced] — [Aider release history](https://aider.chat/HISTORY.html)

#### OpenCode (TypeScript/Bun; formerly sst/opencode)
- Issue URLs and the Homebrew tap now use `anomalyco` (e.g. `anomalyco/tap/opencode`); docs say the install script/npm install OpenCode 1 while the default Homebrew formula installs OpenCode 2, and the two cannot coexist under the `opencode` command. [sourced for docs; repo rename inferred from issue URLs] — [OpenCode intro docs](https://opencode.ai/docs/); [OpenCode issue #21354](https://github.com/anomalyco/opencode/issues/21354)
- Agents: Build (all tools), Plan (denies edits "except Markdown plan files", bash follows permission config), General and Explore subagents (Explore is fast and read-only), plus hidden "compaction, title, summary" system agents. [sourced] — [OpenCode agents docs](https://opencode.ai/docs/agents/)
- Step cap: `steps` (legacy `maxSteps` deprecated) caps agentic iterations; when reached "the agent gets a system prompt telling it to summarize its work and recommend remaining tasks"; without a limit it runs until the model stops or the user interrupts. [sourced] — [OpenCode agents docs](https://opencode.ai/docs/agents/)
- Permissions: `edit` (gates write/edit/apply_patch), `bash`, `webfetch` each `ask|allow|deny`; bash accepts glob patterns; "last matching rule wins". Agents can be Markdown files with frontmatter in `.opencode/agents/`. Default temperature usually 0 (0.55 for Qwen). [sourced] — [OpenCode agents docs](https://opencode.ai/docs/agents/)
- `/init` writes `AGENTS.md` (commit it); `/undo` reverts changes and restores the original prompt, repeatable; `/redo`. [sourced] — [OpenCode intro docs](https://opencode.ai/docs/)
- Local models via `@ai-sdk/openai-compatible`: Ollama (`localhost:11434/v1`), LM Studio (`127.0.0.1:1234/v1`), llama.cpp (`127.0.0.1:8080/v1`, example `qwen3-coder:a3b` with `limit.context: 128000`, `limit.output: 65536`); local models need `limit` set so OpenCode can track remaining context; Ollama tip: "If tool calls aren't working, try increasing `num_ctx`... Start around 16k - 32k." [sourced] — [OpenCode providers docs](https://opencode.ai/docs/providers/)

#### Crush (Go; Charmbracelet)
- Install includes Windows `winget install charmbracelet.crush` and Scoop, plus npm, Homebrew, Linux packages, `go install`. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Config: `crushrc` (Bash with Crush builtins; project `./.crushrc` or global) is primary; `crush.json` "should be considered deprecated"; "Both `crushrc` and `crush.json` are trusted code" — review before launching in a project. Context files: `CRUSH.md` (global Crush rules), `AGENTS.md` (generic; project context saved there by default). [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Local: auto-discovers models from Ollama, LM Studio, llama.cpp; custom OpenAI-compatible via `--type completions`. LSP servers add context (`lsp add go --command "gopls"`); MCP over stdio/http/sse. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Permissions: asks before tools by default; `permissions allow|deny`; "The `bash` tool auto-runs a set of provably read-only commands"; `--yolo` skips all prompts. Multiple sessions per project, switch model mid-session keeping context; `crush serve` shared backend for multiple TUI clients; `.crushignore`. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Telemetry: pseudonymous usage metrics, "prompts and responses are NEVER collected"; opt-out `CRUSH_DISABLE_METRICS=1` or `DO_NOT_TRACK=1`. [sourced] — [Crush README](https://github.com/charmbracelet/crush)

#### Goose (Rust; originated at Block, now Linux Foundation AAIF)
- "part of the Agentic AI Foundation (AAIF) at the Linux Foundation"; desktop app (macOS/Linux/Windows), CLI, embeddable API; 15+ providers incl. Ollama; "70+ extensions via the Model Context Protocol"; ACP lets it use Claude/ChatGPT/Gemini subscriptions. Repo now appears under `aaif-goose/goose`. [sourced; repo move snippet] — [goose README](https://github.com/block/goose); [aaif-goose env-var docs](https://github.com/aaif-goose/goose/blob/main/documentation/docs/guides/environment-variables.md)
- Tool shim (experimental): for models that lack native tool calling or print tool calls as text, a separate **interpreter model** converts text into structured calls; `GOOSE_TOOLSHIM=true`, `GOOSE_TOOLSHIM_BACKEND` = `ollama` (default) | `local` | `llama.cpp`, `GOOSE_TOOLSHIM_OLLAMA_MODEL` default `mistral-nemo`; `qwen2.5:3b` suggested if slow; strips `<think>` content automatically; "most locally hosted models... need the shim"; no accuracy benchmarks published. [sourced] — [goose Tool Shim guide](https://goose-docs.ai/docs/guides/tool-shim/)
- A GitHub issue says there is still no guide on when to enable the shim or which models need it. [snippet] — [goose issue #8269](https://github.com/aaif-goose/goose/issues/8269)
- Built-in local inference (2026-04-24): llama.cpp embedded in the runtime, GGUF from Hugging Face, `num_ctx` set from available memory, "Local models typically offer 4K-8K tokens"; tool calling is "the single biggest difference" from cloud; non-native models get emulated tool calling "limited to shell commands"; recommends "Code Mode" for small context; first request 30-120 s for model load. [sourced] — [goose blog: built-in local inference](https://goose-docs.ai/blog/2026/04/24/use-goose-with-built-in-local-inference/)

#### OpenAI Codex CLI (Rust `codex-rs`; Apache-2.0)
- Install: npm, Homebrew cask, curl script, Windows PowerShell install script; ChatGPT sign-in or API key. [sourced] — [Codex README](https://github.com/openai/codex)
- Local: `--oss` against Ollama or LM Studio; choose with `--local-provider` or `oss_provider`; "If neither is set, the interactive CLI prompts you to choose; `codex exec` exits with an error." Custom `model_providers` (base URL, auth, headers); `wire_api = "responses"` in examples; Chat Completions providers ignore `model_verbosity`. [sourced] — [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)
- Default `--oss` model is gpt-oss:20b; community reports say the 20b "occasionally misformats tool calls on complex multi-step tasks". [snippet] — [Codex + GPT-OSS guide (third-party)](https://codex.danielvaughan.com/2026/05/03/codex-cli-gpt-oss-local-models-ollama-lm-studio-mlx-setup-guide/)
- AGENTS.md read on the first turn, capped by `project_doc_max_bytes`, with `project_doc_fallback_filenames`; transcripts saved under `CODEX_HOME` (`persistence = "none"` disables; `history.max_bytes` caps); `approval_policy` = `on-request` | `never` | granular object ("no longer support `approval_policy = "untrusted"`"); `sandbox_mode` `workspace-write` / `danger-full-access` (`.git/` may stay read-only in workspace-write); OTel export off by default; `[analytics] enabled = false` to turn analytics off; `--profile` loads `~/.codex/<name>.config.toml`. [sourced] — [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)
- Edits: `apply_patch` is a freeform tool where the model writes a raw patch constrained by a Lark grammar (`codex-rs/core/src/tools/handlers/apply_patch.lark`), not JSON parameters. [snippet; third-party crate docs] — [locode-packs apply_patch source](https://docs.rs/crate/locode-packs/latest/source/src/codex/apply_patch/mod.rs); [pi-extension-codex-apply-patch](https://pi.dev/packages/pi-extension-codex-apply-patch?page=44)
- Windows sandbox: OpenAI evaluated AppContainer, Windows Sandbox and Mandatory Integrity Control and chose per-process SIDs and write-restricted tokens (May 2026 post); an "elevated" mode runs commands as a dedicated sandbox user and restricts network via Windows Firewall rules; elevated path described as alpha around v0.136 (June 2026); known gap: restricted token cannot stop writes where Everyone already has write access. Sources disagree on AppContainer's role. [snippet] — [OpenAI: building the Codex Windows sandbox](https://openai.com/it-IT/index/building-codex-windows-sandbox/); [SD Times](https://sdtimes.com/sdt_dev/openai-releases-codex-for-windows/); [v0.136 guide (third-party)](https://codex.danielvaughan.com/2026/06/02/codex-cli-v0136-release-guide-osc8-hyperlinks-session-archiving-app-server-stdio-windows-elevated-sandbox/)

#### Gemini CLI (TypeScript/Node; Google)
- Now "maintained for enterprise Gemini Code Assist workflows", "Not optimized for Gemini 4+"; "No free tier or Google AI plans"; "Free individual access and Google AI Pro / Ultra (Google One) moved to Antigravity CLI on June 18, 2026." No local/OpenAI-compatible model support mentioned. Features listed: GEMINI.md context files, checkpointing ("Save and resume conversations"), sandboxing, headless `gemini -p` with `--output-format json` and `stream-json` (NDJSON events), MCP via `~/.gemini/settings.json`, weekly preview / stable channels. [sourced] — [Gemini CLI README](https://github.com/google-gemini/gemini-cli)

#### Qwen Code (TypeScript/Node; Alibaba Qwen; Gemini CLI fork)
- "originally based on" Gemini CLI v0.8.2; "Starting from Qwen Code v0.1, we stopped syncing with upstream." Supports OpenAI/Anthropic/Gemini/Qwen APIs and "any third-party provider or local model (Ollama / vLLM)"; Windows PowerShell installer; npm needs Node 22+; headless `qwen -p`; SDKs for TS/Python/Java; `qwen serve` HTTP+SSE daemon on 127.0.0.1:4170 which "listens on loopback without bearer authentication" unless `QWEN_SERVER_TOKEN` + `--require-auth`; SWE-bench Verified 76.40-77.80% across versions 0.0.14-0.22.0 with Qwen 3.7 Max. [sourced] — [Qwen Code README](https://github.com/QwenLM/qwen-code)
- Settings: `tools.approvalMode` = `plan` | `default` | `auto-edit` | `auto` (an LLM classifier approves safe actions) | `yolo`, default `"auto"`; `context.autoCompactThreshold` 0.85; `tools.truncateToolOutputThreshold` 25000 chars and `tools.truncateToolOutputLines` 1000; `model.loopProtection` `warn|stop|off`; `model.skipLoopDetection` default true (re-enable suggested for headless); `model.maxToolCallsPerTurn` 100 (adaptive when unset, backstop 1000); `model.maxSessionTurns` -1; `model.sessionTokenLimit` -1; `privacy.usageStatisticsEnabled` default true. [sourced] — [Qwen Code settings docs](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)

#### Open Interpreter (now Rust, based on Codex)
- README: "This is the new Rust version of Open Interpreter, based on Codex"; the original Python project "lives on as a community-maintained fork at endolith/open-interpreter"; `interpreter --chat-completions` runs any OpenAI-compatible provider; "Runs commands inside native sandboxing on macOS, Linux, and Windows"; Windows PowerShell installer; site tagline "Coding agent for open models". [sourced] — [Open Interpreter README](https://github.com/openinterpreter/open-interpreter); [openinterpreter.com](https://www.openinterpreter.com/)
- Secondary coverage dates the Rust line to rust-v0.0.x May-July 2026, says license moved AGPL-3.0 → Apache-2.0 and targets Kimi K3/Qwen/DeepSeek; sources disagree on details. [snippet] — [promptquorum review](https://www.promptquorum.com/power-local-llm/open-interpreter-review); [ai-tldr release note](https://ai-tldr.dev/releases/openinterpreter-rust-0-0-26/)

### Inferences
- Adoption shortlist for Jazari, ranked by value per effort at ~1B active / 10 tok/s:
  1. **Flexible matching when applying search/replace edits** (Aider): whitespace-normalized match, relative-indentation match, then progressively looser. Pure client code, zero extra tokens. Aider measured a 9x error increase without it. Highest value.
  2. **Build/test feedback after /edit** (Aider `--auto-test`, its own `dotnet build && dotnet test` example): run `dotnet build`, feed the first few compiler errors back once, cap at 1-2 retries. Each retry costs a full generation at 10 tok/s, so keep the cap tight.
  3. **Ranked C# symbol map under a fixed budget** (Aider repo map): Roslyn syntax trees instead of tree-sitter, rank by reference count, cap ~1k tokens. Gives the small model file/symbol names without /read of whole files. It would cost prompt-processing time, not generation time.
  4. **Agent-loop guard rails** for the planned 4-tool loop: `steps` cap that ends with "summarize and list what is left" (OpenCode), identical-call loop detection plus warn/stop (Qwen), tool-output truncation (Qwen uses 25k chars; Jazari needs far less), auto-approve read-only tools (Crush's "provably read-only" rule), edits always confirmed.
  5. **Grammar-constrained tool calls instead of a second interpreter model.** Goose's tool shim needs a second model (mistral-nemo ~12B by default) which Jazari's 16 GB laptop cannot afford next to the main model; Jazari already owns llama-server and uses constrained JSON, which is the same idea as Codex's grammar-constrained `apply_patch`.
  6. **AGENTS.md interop**: read `AGENTS.md` (byte-capped like Codex's `project_doc_max_bytes`) and fold it into state notes; optionally `/init` to write one (OpenCode). Cheap.
  7. **Headless one-shot mode** (`jazari -p "..." --json`), like `gemini -p --output-format json`, `qwen -p`, `codex exec`. Jazari's gateway already exposes the API, so this is mostly CLI plumbing.
  8. **Git hygiene from Aider**: commit the user's dirty changes before an edit, mark Jazari commits (Aider appends "(aider)").
  9. **Plan mode** (OpenCode Plan agent / Qwen `plan`): edits denied except a Markdown plan file. Cheap to build; gives a safe two-pass "plan then /edit" flow.
- Where these projects are clearly better than Jazari today: an agent loop with permissions and step caps (all except Aider), repo-wide context without manual /read (Aider repo map, LSP in Crush), automatic build/test repair (Aider), flexible patch application (Aider), OS-level sandboxing of shell commands on Windows (Codex, Open Interpreter), headless JSON output (Gemini/Qwen/Codex), AGENTS.md interop (Codex, OpenCode, Crush), multi-session and session resume (Crush, Codex, Gemini).
- Where Jazari is already ahead on its own terms: the gateway requires an API key by default (Qwen's daemon does not); no telemetry (Crush, Qwen, Codex all collect some by default); grounded docs retrieval (law book) and quote-verified review findings have no equivalent in these tools; idle-time state notes are an automatic version of AGENTS.md.

### Gaps
- No project publishes edit-format or tool-calling results for models at or below ~1B active parameters; the smallest model in Aider's leaderboard is a 27B dense model. Whether any of these loops work at Jazari's size is untested in public sources.
- Aider release dates and the current maintenance pace could not be confirmed (history page has no dates; main branch has unreleased work).
- Codex's auto-compaction key and the official current Windows sandbox mechanics were not confirmed on primary docs.

## How does each handle editing files, and what do their benchmarks say about formats for small/weak models?

### Takeaway
Aider is the only project with public format benchmarks: weak open models get near-perfect well-formedness only with `whole` file output but still solve few tasks, while search/replace (`diff`) breaks more often in mid-size models; flexible application of imperfect edits matters more than the format choice. Codex constrains `apply_patch` with a grammar; OpenCode and Crush rely on the model's native tool calling plus undo.

### Cited Findings
- Aider polyglot leaderboard (last updated 2025-11-20) open-weight entries: Qwen2.5-Coder-32B-Instruct 16.4% correct, `whole`, 99.6% well-formed; Llama 4 Maverick 15.6%, `whole`, 99.1%; Gemma-3-27b-it 4.9%, `whole`, 100.0%; QwQ-32B 20.9%, `diff`, 67.6% well-formed; Qwen3 32B 40.0%, `diff`, 83.6%; gpt-oss-120b (high) 41.8%, `diff`, 79.1%; DeepSeek V3 (0324) 55.1%, `diff`, 99.6%. Top: gpt-5 (high) 88.0%, `diff`, 91.6%. No Granite entries. [sourced] — [Aider leaderboards](https://aider.chat/docs/leaderboards/)
- Unified-diff study (GPT-4 Turbo, 89 refactoring tasks): SEARCH/REPLACE baseline 20%, unified diff 61%, lazy comments down from 12 tasks to 4. Principles: FAMILIAR, SIMPLE (no escaping, "JSON-wrapped code is error-prone", line numbers handled poorly), HIGH LEVEL (whole functions; without it "30-50% increase in editing errors"), FLEXIBLE. [sourced; pre-2025, ~late 2023] — [Aider unified diffs](https://aider.chat/docs/unified-diffs.html)
- Flexible patching: normalize hunks, recover lines missing a `+`, match on relative leading whitespace, split big hunks into overlapping sub-hunks, vary context size, combine progressively more permissive; "disabled show a 9X increase in editing errors". [sourced; pre-2025] — [Aider unified diffs](https://aider.chat/docs/unified-diffs.html)
- Architect/editor split (2024-09-26): same-model pairs beat solo: GPT-4o-mini + GPT-4o-mini (`whole`) 60.2% vs 55.6% solo; GPT-4o + GPT-4o 75.2% vs 71.4%; Sonnet + Sonnet 80.5% vs 77.4%. "Deepseek is surprisingly effective as an Editor model." `whole`-format pairs are "quite slow, so probably not practical for interactive use". [sourced; pre-2025] — [Aider architect blog](https://aider.chat/2024/09/26/architect.html)
- Aider validates edits by auto-linting each edited file and optionally running tests, then asks the model to fix errors on non-zero exit; undo is git-based (`/undo` discards the last auto-commit). [sourced] — [Aider lint/test](https://aider.chat/docs/usage/lint-test.html); [Aider git](https://aider.chat/docs/git.html)
- Codex `apply_patch`: raw patch text constrained by a Lark grammar instead of JSON arguments. [snippet] — [locode-packs apply_patch source](https://docs.rs/crate/locode-packs/latest/source/src/codex/apply_patch/mod.rs)
- OpenCode gates `write`, `edit`, `apply_patch` behind one `edit` permission; `/undo` restores the original prompt too; `/redo`. [sourced] — [OpenCode agents](https://opencode.ai/docs/agents/); [OpenCode intro](https://opencode.ai/docs/)

### Inferences
- At 10 tok/s, `whole` output is the wrong default for Jazari: a 300-line C# file is roughly 3,000+ output tokens, i.e. ~5 minutes per edit. Jazari's constrained search/replace JSON is the right shape; the Aider data says the next gain is in the applier (flexible matching), not in the format.
- Aider's "JSON-wrapped code is error-prone" finding was for unconstrained JSON on GPT-4; grammar-constrained decoding removes the syntax-failure part of that risk, but not escaping cost or the model's accuracy in reproducing the SEARCH text. Worth measuring: constrained JSON vs a grammar-constrained raw SEARCH/REPLACE block (no string escaping of C# quotes/backslashes).
- A same-model two-pass "plan in prose, then emit edits" (Aider's architect gains held even with mini models) is plausible for Jazari's multi-file /edit, at roughly double the generation time.

### Gaps
- No public benchmark below ~27B parameters; no data for hybrid Mamba MoE models like Granite 4.0-H.
- The Aider format studies are on GPT-4-era models (2023-2024); no 2025-2026 rerun on small models was found.

## How do they handle tool calling and agent loops with local models that call tools poorly?

### Takeaway
Two fallback strategies exist: a second "interpreter" model that turns text into tool calls (Goose), and grammar-constrained output (Codex `apply_patch`). Everyone else depends on native tool calling and caps damage with step limits, loop detection, permission prompts and sandboxes. User reports show the most common "small model can't call tools" failure is actually a silently truncated context window.

### Cited Findings
- Goose tool shim: separate interpreter model (default `mistral-nemo` via Ollama, or built-in llama.cpp) converts text tool calls into structured ones; strips `<think>`; experimental; no accuracy numbers. [sourced] — [goose Tool Shim guide](https://goose-docs.ai/docs/guides/tool-shim/)
- Goose built-in local inference: non-native models get emulated tool calling "limited to shell commands"; suggested workflow is step-by-step shell commands. [sourced] — [goose blog 2026-04-24](https://goose-docs.ai/blog/2026/04/24/use-goose-with-built-in-local-inference/)
- OpenCode: `steps` cap → system prompt to summarize and recommend remaining tasks; `ask|allow|deny` permissions with globs. [sourced] — [OpenCode agents](https://opencode.ai/docs/agents/)
- Qwen Code: `maxToolCallsPerTurn` 100 (adaptive, backstop 1000), `maxSessionTurns`, streaming loop detection with `loopProtection` warn/stop/off, `auto` approval mode uses an LLM classifier. [sourced] — [Qwen Code settings](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)
- Crush: prompts before tools, auto-runs "provably read-only" bash commands, `--yolo`. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Codex: `approval_policy` on-request/never/granular; `sandbox_mode` workspace-write keeps `.git/` read-only; native Windows sandbox with restricted tokens. [sourced for config; snippet for Windows internals] — [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced); [OpenAI Windows sandbox post](https://openai.com/it-IT/index/building-codex-windows-sandbox/)
- OpenCode user reports: Gemma 4 e4b returns valid `tool_calls` that OpenCode does not recognize (suspected AI SDK OpenAI-compatible streaming parsing); qwen2.5-coder:14b calls tools for plain chat; with a too-small Ollama context "you will not see any error in OpenCode but Ollama logs will show them". [snippet] — [OpenCode issue #21354](https://github.com/anomalyco/opencode/issues/21354); [OpenCode issue #3122](https://github.com/anomalyco/opencode/issues/3122); [Reddit snapshot](https://reddit.sentinel-team.org/posts/1rbxfj8/snapshots/2026-02-23T12%3A34%3A47.370773Z)
- Crush + Ollama: most-reported failure is the model describing actions without running tools, attributed to a 4,096-token default context truncating tool schemas. [snippet; third-party blog, unverified] — [localaimaster Crush + Ollama](https://localaimaster.com/blog/crush-ollama-setup)
- Codex + Ollama: weak tool use shows as invented commands, ignored output, broken JSON, repeated loops; advice is a smaller task scope. [snippet; third-party] — [knightli troubleshooting](https://knightli.com/en/2026/07/10/codex-ollama-local-model-errors-troubleshooting/)

### Inferences
- For Jazari, the Goose two-model shim is a poor fit (RAM and latency); llama-server grammar/JSON-schema constraints on the tool-call turn give the same guarantee with one model. Jazari controls both ends, so it can also check every request's prompt token count against n_ctx and refuse rather than truncate silently, which is the top failure mode reported for other tools.
- Keep the tool list tiny: generic agents send many tool schemas every turn (OpenCode tip asks for 16k-32k context just for that); at CPU prompt-processing speeds a long tool preamble could dominate latency. Jazari's planned four tools with short schemas fit this.
- Copy OpenCode's cap behavior (force a final summary and "remaining work" list) and Qwen's repeated-call detector; both are cheap and protect against the looping that small models show.

### Gaps
- No primary-source measurements of tool-call success rates for any model under ~3B active in these tools.
- Codex GitHub issue #21979 (labels bug, custom-model, tool-calls) content not seen. — [Codex issue #21979](https://github.com/openai/codex/issues/21979)

## How do they build context: repo maps, file selection, token budgeting, compaction?

### Takeaway
Aider's ranked tree-sitter repo map under a 1k-token budget is the standout context technique; the agent tools instead let the model search with tools and compact the conversation by summarization near a threshold (Qwen 0.85). Local-model setups repeatedly fail on context sizing, which Aider and Goose fix by sizing `num_ctx` per request or per machine.

### Cited Findings
- Aider repo map: tree-sitter definitions/references, graph ranking, `--map-tokens` default 1k, grows when no files are in chat. [sourced] — [Aider repo map](https://aider.chat/docs/repomap.html); [Aider repo map blog (pre-2025)](https://aider.chat/2023/10/22/repomap.html)
- Aider `/context` command (v0.79.0) "identifies which files need editing for a request"; chat history summarization sped up (v0.85.0); history token limit raised 4k → 8k (v0.71.0). [sourced] — [Aider release history](https://aider.chat/HISTORY.html)
- Aider sizes Ollama `num_ctx` per request because Ollama "silently discards context that exceeds the window". [sourced] — [Aider Ollama docs](https://aider.chat/docs/llms/ollama.html)
- Goose sets `num_ctx` from available memory; local models "typically offer 4K-8K tokens"; "Code Mode" recommended to reduce token use and context rot. [sourced] — [goose blog 2026-04-24](https://goose-docs.ai/blog/2026/04/24/use-goose-with-built-in-local-inference/)
- OpenCode needs `limit.context` / `limit.output` set for local models to track remaining context; hidden compaction agent. [sourced] — [OpenCode providers](https://opencode.ai/docs/providers/); [OpenCode agents](https://opencode.ai/docs/agents/)
- Qwen Code: `context.autoCompactThreshold` 0.85; tool output truncated at 25,000 chars / 1,000 lines. [sourced] — [Qwen Code settings](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)
- Crush: LSP servers supply extra context; `.gitignore` + `.crushignore` exclude files. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Codex: AGENTS.md included on first turn, capped by `project_doc_max_bytes`. [sourced] — [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)

### Inferences
- Summarization-based compaction costs output tokens, which is Jazari's scarcest resource (10 tok/s). Jazari's BM25 pinning of earlier facts plus idle-time state notes is a better fit than mid-turn summarization; if compaction is added, do it in idle time like state notes.
- A Roslyn-based C# symbol map (no build needed for syntax trees) is the most valuable missing context piece; budget it like Aider (~1k tokens) and include it only when no files are /read, mirroring Aider's dynamic sizing in reverse.
- An ignore file (`.jazariignore`, gitignore syntax like `.crushignore`) is a cheap addition for /review and any future search tool.

### Gaps
- OpenCode and Crush compaction thresholds and summary prompts were not found on the fetched pages.

## What terminal UX do they offer (commands, modes, keybindings, sessions, headless mode, config files)?

### Takeaway
Common 2026 baseline: a plan/read-only mode, approval modes from "ask" to "yolo", AGENTS.md project instructions, saved and resumable sessions, and a headless `-p`/`exec` mode with JSON output. Jazari has strong single-purpose commands but lacks plan mode, headless JSON, AGENTS.md and session resume.

### Cited Findings
- OpenCode: Tab toggles Plan/Build; `/init` → AGENTS.md; `/undo` (restores prompt), `/redo`; agents as Markdown files. [sourced] — [OpenCode intro](https://opencode.ai/docs/); [OpenCode agents](https://opencode.ai/docs/agents/)
- Crush: ctrl+p command palette, ctrl+l model picker; multi-session per project; switch model mid-session; `crush serve` for several clients on one workspace. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Aider: `/diff`, `/undo`, `/commit`, `/test`, `/run`, `/context`, unreleased `/ok` to approve proposed changes; architect mode. [sourced] — [Aider git](https://aider.chat/docs/git.html); [Aider lint/test](https://aider.chat/docs/usage/lint-test.html); [Aider release history](https://aider.chat/HISTORY.html)
- Gemini CLI: GEMINI.md, checkpointing, `-p` with `--output-format json` / `stream-json`. [sourced] — [Gemini CLI README](https://github.com/google-gemini/gemini-cli)
- Qwen Code: approval modes plan/default/auto-edit/auto/yolo; `qwen -p`; `qwen serve` HTTP+SSE daemon; `context.fileName` sets the context-file name. [sourced] — [Qwen Code README](https://github.com/QwenLM/qwen-code); [Qwen Code settings](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)
- Codex: `codex exec` headless (errors if no local provider chosen), `--profile`, saved transcripts. [sourced] — [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)
- Crush/Codex/OpenCode converge on AGENTS.md (Crush saves project context there by default; Codex reads it with a byte cap and fallback names). [sourced] — [Crush README](https://github.com/charmbracelet/crush); [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)

### Inferences
- Cheapest UX wins for Jazari: `/plan` (read-only, writes a plan file), `jazari -p ... --json` reusing the gateway, read AGENTS.md, and a `/resume` built on the existing /save output. `/undo` restoring the original prompt (OpenCode) is a small, nice touch for a slow model where retyping a prompt after a bad edit is the common case.

### Gaps
- Gemini CLI's /memory, /restore and /compress were not confirmed on the current README.

## Install, update, config, telemetry; Windows support quality

### Takeaway
Native Windows is now first-class in Crush (winget/Scoop), Codex (PowerShell installer + native sandbox), Qwen Code and Open Interpreter (PowerShell installers), while OpenCode still recommends WSL. Most collect some telemetry by default with opt-outs; none describes Jazari-style pinned-checksum self-update.

### Cited Findings
- Crush: winget/Scoop/npm/Homebrew/Linux packages; config is executable Bash (`crushrc`) flagged as trusted code; metrics opt-out via env vars. [sourced] — [Crush README](https://github.com/charmbracelet/crush)
- Codex: PowerShell install script; installers download from `releases.openai.com` with GitHub Releases fallback (`CODEX_INSTALLER_USE_RELEASES_OPENAI_COM=false`); analytics can be disabled; OTel off by default. [sourced] — [Codex README](https://github.com/openai/codex); [Codex advanced config](https://learn.chatgpt.com/docs/config-file/config-advanced)
- OpenCode on Windows: "WSL is recommended"; Chocolatey/Scoop/npm/Mise/Docker/binaries available; Bun-based Windows install "still in progress". [sourced] — [OpenCode intro](https://opencode.ai/docs/)
- Qwen Code: PowerShell `irm | iex`, Node 22+ for npm; `privacy.usageStatisticsEnabled` default true. [sourced] — [Qwen Code README](https://github.com/QwenLM/qwen-code); [Qwen Code settings](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)
- Gemini CLI: npm/npx/Homebrew/MacPorts/Anaconda; weekly preview, stable and nightly channels; telemetry listed as "Usage tracking". [sourced] — [Gemini CLI README](https://github.com/google-gemini/gemini-cli)
- Goose: desktop app for Windows; CLI install via curl script (repo contains `download_cli.ps1`). [sourced] — [goose README](https://github.com/block/goose)
- Open Interpreter: PowerShell install; native sandboxing on Windows. [sourced] — [Open Interpreter README](https://github.com/openinterpreter/open-interpreter)
- Aider: Windows `/run` uses PowerShell/cmd; Windows lint quoting fix. [sourced] — [Aider release history](https://aider.chat/HISTORY.html)

### Inferences
- Crush's warning that project config is trusted code applies to Jazari if it ever reads per-project config or AGENTS.md that can change tool permissions: treat it as data, never as permission grants.
- Jazari's `doctor` could adopt the recurring community fix list: confirm actual context size of the running server (the `ollama ps` advice), confirm the model supports the tool format, and flag truncation.

### Gaps
- No primary source found on update mechanisms (self-update, checksum pinning) for these tools beyond package managers and Codex's installer host fallback.

## What do users praise and complain about?

### Takeaway
Complaints about local use cluster on three things: silent context truncation, tool calls not recognized or misformatted by small models, and models looping or calling tools when they should chat. Praise in primary sources is mostly maintainer claims (Aider's editor-model results, Qwen's SWE-bench numbers); no reliable large-scale user sentiment data was found.

### Cited Findings
- Ollama default context silently drops data (Aider docs); OpenCode users see no error while Ollama logs show truncation. [sourced / snippet] — [Aider Ollama docs](https://aider.chat/docs/llms/ollama.html); [Reddit snapshot](https://reddit.sentinel-team.org/posts/1rbxfj8/snapshots/2026-02-23T12%3A34%3A47.370773Z)
- OpenCode: valid Gemma 4 tool_calls not recognized; qwen2.5-coder:14b calls tools on plain chat; llama.cpp fixed some errors Ollama didn't. [snippet] — [OpenCode issue #21354](https://github.com/anomalyco/opencode/issues/21354); [OpenCode issue #6223](https://github.com/anomalyco/opencode/issues/6223); [OpenCode issue #20995](https://github.com/anomalyco/opencode/issues/20995)
- Goose: users asked for a guide on when the tool shim is needed. [snippet] — [goose issue #8269](https://github.com/aaif-goose/goose/issues/8269)
- Codex with gpt-oss:20b occasionally misformats tool calls on multi-step tasks (community reports). [snippet] — [Codex + GPT-OSS guide](https://codex.danielvaughan.com/2026/05/03/codex-cli-gpt-oss-local-models-ollama-lm-studio-mlx-setup-guide/)
- Gemini CLI individual users were moved to Antigravity CLI on 2026-06-18; the CLI now has no free tier. [sourced] — [Gemini CLI README](https://github.com/google-gemini/gemini-cli)
- Qwen's daemon defaults to no bearer auth on loopback; docs warn about shared hosts. [sourced] — [Qwen Code README](https://github.com/QwenLM/qwen-code)

### Inferences
- The failure modes users hit are ones Jazari's design already partly avoids (it owns llama-server and sets n_ctx; it uses constrained output). The remaining risk is the agent loop itself; the evidence supports keeping it narrow, capped and read-mostly.

### Gaps
- Issue bodies for the OpenCode and Codex reports were not read in full; counts and current status unknown.
- No quantitative user-satisfaction data (surveys, issue-label counts) was found for any project.
