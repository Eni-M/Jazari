# Borrow the guard rails, skip the agents

None of the 19 projects most similar to Jazari runs a model as small as Granite 4.0-H Tiny. Their smallest documented local setups use 3B–32B dense models or about 3B-active MoE models. Every source that gives advice for small models steers them toward focused, single-shot work and away from multi-step agents. So Jazari's fixed command set is the right shape for its hardware. The neighbours are better in three places: the deterministic machinery around the model, the housekeeping around llama-server, and how they measure output quality. On the machinery side, Aider picks context with a ranked symbol graph and repairs imperfect patches with flexible matching. Its lint and test loop sends compiler errors back to the model. PR-Agent and Copilot Autofix ground review findings in anchored lines and static-analysis alerts, and they track findings across runs. In housekeeping, Ollama, LM Studio and llama-server unload the model when idle, and llama-server can lock itself to a single caller. The best-value upgrades for Jazari cost no extra model tokens. Lock down the internal llama-server, which Jazari's own launcher starts with no API key. Turn on llama-server's idle sleep to give back about 8.7 GB of RAM. Harden /review with quote normalization, truncation guards, coverage reporting, a diff mode and finding state. Add a Roslyn parse or compile check plus one retry after /edit. Then A/B-test the grammar-constrained JSON edit format: four 2025–2026 small-model studies suggest it may be costing correctness while it guarantees valid syntax. The larger projects are a Roslyn symbol-graph /read, a static-analyzer-first /review, and Ollama-API emulation so Visual Studio and VS Code can use Jazari. None of these recommendations has been measured on the T480 yet, and no public benchmark covers any model at or below about 1B active parameters.

## Nineteen neighbours, and none runs a model this small

The closest neighbours fall into four groups. The first is terminal coding agents, the second is IDE assistants that accept local endpoints, and the third is the local runtimes that wrap llama.cpp. The fourth is review and context tools whose methods Jazari can copy. The table lists the 19 most relevant projects, with what each does better than Jazari today.

| Project | Kind and status (Nov 2026) | What it does better than Jazari |
|---|---|---|
| **Aider** | Terminal pair-programmer, active | Ranked tree-sitter repo map under a 1k-token budget; flexible patch matching; lint after every edit and an optional test loop (`dotnet build && dotnet test` is its own example); auto-commits user changes before an edit |
| **OpenCode** | Terminal agent, active (Windows: WSL recommended) | Plan mode that cannot edit; step cap that ends with "summarize and list what is left"; `/undo` that also restores the prompt; AGENTS.md via `/init` |
| **Crush** (Charmbracelet) | Terminal agent, active, winget/Scoop | Auto-runs "provably read-only" commands; LSP context; `.crushignore`; multiple sessions |
| **Goose** (Linux Foundation AAIF) | Desktop app, CLI and API, active | Built-in llama.cpp with context sized from free memory; tool shim that uses a second "interpreter" model |
| **OpenAI Codex CLI** | Terminal agent, active, native Windows sandbox | `--oss` local mode; AGENTS.md with a byte cap; grammar-constrained `apply_patch`; headless `codex exec` |
| **Qwen Code** | Gemini CLI fork, active | Approval modes from plan to yolo; loop detection; tool-output truncation; auto-compaction at 85% of context; `qwen -p` headless |
| **Open Interpreter** | Rewritten in Rust on Codex, 2026 | Native sandboxing on Windows; any OpenAI-compatible provider |
| **Continue** | IDE extension, active | Rules files scoped by glob or regex; per-role models; local dev-data logging with versioned event schemas |
| **Cline** | VS Code agent, active | "Use Compact Prompt" for local models (reported about 90% smaller); no index, uses tree-sitter plus ripgrep |
| **Kilo Code** | IDE agent, acquired by Anaconda | Clear guidance on what small models are good for |
| **Tabby** | Self-hosted completion and chat server, release pace slowing | Users can add their own docs over REST; acceptance-rate reports |
| **VS Code BYOK / Visual Studio BYOM** | Microsoft IDEs, local models via Ollama or custom URL (VS 18.10 Insiders, Aug 2026) | Native diff review and model picker, if Jazari can be reached as an endpoint |
| **llama.cpp llama-server** | Jazari's own engine | `--sleep-idle-seconds`, `--fit`, slot save/restore, `--api-key-file`, n-gram speculative decoding |
| **Ollama** | Local runtime | Unloads after 5 minutes idle; separate app, server and upgrade logs; no-admin install with clean uninstall |
| **LM Studio** | Local runtime | 60-minute idle TTL for models loaded on demand; `lms runtime survey`; headless daemon |
| **Jan** | Local desktop app | Update checks deferred until after first paint, with an opt-out; features gated on the engine build |
| **PR-Agent** (Qodo, open source) | AI PR reviewer, active | Exact anchoring of finding lines; truncation guard; coverage footer; finding state kept across runs; replacement fixes checked for syntax |
| **Semgrep Assistant / Copilot Autofix** | Analyzer-first AI triage and fixes | The LLM judges deterministic analyzer findings instead of searching for issues unaided |
| **CodeRabbit** | Commercial reviewer, methods published | Dependency graph rebuilt on each review; generated grep checks; learns from user dismissals |

The status column shows how fragile this space has been. **Roo Code shut down on 15 May 2026**, Twinny is archived and Void is paused ([Roo-Code repo](https://github.com/RooCodeInc/Roo-Code)). Gemini CLI moved individual users to a different product in June 2026 ([Gemini CLI README](https://github.com/google-gemini/gemini-cli)). Jazari depends only on the OpenAI and Ollama wire formats, which protects it from this kind of churn.

All of these tools assume much bigger models than Jazari's. The smallest open model on Aider's polyglot leaderboard is 27B. Open models of 27–32B score **4.9–16.4% using the safest whole-file format**. Qwen3 32B reaches **40.0% with search/replace blocks, but only 83.6% of its edits are well-formed** ([Aider leaderboards](https://aider.chat/docs/leaderboards/)). Continue names a 32B model as the best local agent and warns that "smaller models (7B-13B) may struggle with complex tool interactions" ([Continue agent setup](https://docs.continue.dev/ide-extensions/agent/model-setup)). Kilo says small models are most reliable for "autocomplete, explanations, commit messages, focused edits, and single-function generation" ([Kilo local models](https://kilo.ai/local-models)). That list matches Jazari's command set almost exactly.

Granite 4.0-H Tiny's own model card adds two warnings. Its tool-calling score is a modest **BFCL v3 of 57.65**. Its code scores also **drop from 83 to 76 on HumanEval+ and from 80 to 69 on MBPP+** once more tests are added ([Granite 4.0-H Tiny card](https://huggingface.co/ibm-granite/granite-4.0-h-tiny)). The evidence favours keeping Jazari narrow and investing in the code that surrounds the model.

## Six places the neighbours are clearly ahead

**Choosing context by relevance rather than file names.** Jazari's /read picks about 5k tokens of files using filename risk heuristics, so the user's question never affects which files are chosen. Aider parses every file with tree-sitter into definitions and references and builds a file graph. It then runs personalized PageRank, weighting **identifiers mentioned in the chat ×10, references from files already in the chat ×50, and names defined in more than 5 files ×0.1**. A binary search fits the result within 15% of the token budget ([aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)). The whole process uses no embeddings and no model calls, and C# is supported ([Aider languages](https://aider.chat/docs/languages.html)). The rest of the field has moved the same way. Sourcegraph Cody dropped embeddings for an adapted BM25 ([Sourcegraph](https://sourcegraph.com/blog/how-cody-understands-your-codebase)), and Continue deprecated its embeddings-based @Codebase ([Continue](https://docs.continue.dev/reference/deprecated-codebase)). SWE-agent found that a **100-line file window beat showing the whole file (18.0% vs 12.7%)** ([SWE-agent](https://arxiv.org/html/2405.15793)). Small, relevant context beats large context.

**Grounding review in something deterministic.** Jazari's /review asks a 1B model to find issues unaided across 2k-token parts, which is the hardest version of the task. Tools with credible noise numbers start from analyzer output instead. In the Tencent industrial study, LLM triage of static-analysis alarms removed **94–98% of false positives** at 2.1–109.5 s per alarm ([arXiv 2601.18844](https://arxiv.org/abs/2601.18844v1)). Semgrep's triage reached **96% agreement with researchers on true positives**, with a deliberately conservative bias ([Semgrep](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)). Unit size matters a great deal. One 2026 study found review **F1 falling from 0.657 on diffs under 10 lines to 0.043 on diffs over 150 lines**, and the best F1 on real bug-fix PRs was only 0.066 ([arXiv 2606.15689](https://arxiv.org/abs/2606.15689)).

There is a limit to this approach. In ZeroFalse, the smallest model that triaged well was gpt-oss-20b, a reasoning model with 3.6B active parameters, while mixtral-8x7b reached only F1 0.217 ([ZeroFalse](https://arxiv.org/html/2510.02534v1)). Whether a 1B model can give reliable verdicts is untested.

**The lifecycle of findings and honest coverage.** PR-Agent's `persistent_finding_state` tracks findings across runs, and partial runs never mark anything resolved. Its footer lists failed, skipped and clipped chunks ([PR-Agent review docs](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/tools/review.md)). Its code treats a generation that hit the length limit as a failure, under the rule "a truncated diff must never be treated as complete". It also compares quoted lines after dedenting and stripping trailing whitespace ([pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)). Kodus closes a tracked issue automatically when a later PR contains the fix ([Kodus README](https://raw.githubusercontent.com/kodustech/kodus-ai/main/README.md)). Jazari caches results per part, but it keeps no finding state, no coverage list and no memory of what the user dismissed.

**Closing the loop after an edit.** Aider lints every edited file by default and can run `--test-cmd "dotnet build && dotnet test"`. It then "will try and fix any errors" ([Aider lint/test](https://aider.chat/docs/usage/lint-test.html)). Turning off its flexible patch application caused a **9× increase in editing errors** ([Aider unified diffs](https://aider.chat/docs/unified-diffs.html)). In the Qwen2.5-Coder report, a single retry after test feedback added **+0.0 points at 0.5B, +3.0 at 1.5B, +5.3 at 3B and +12.8 at 7B** ([Qwen2.5-Coder report](https://arxiv.org/html/2409.12186)). A 1B-active model sits near the bottom of that range, so expect a small gain and allow one retry at most. Jazari checks C# syntax, but it does not compile the result or retry.

**Runtime housekeeping.** Ollama unloads models after 5 minutes idle ([Ollama FAQ](https://docs.ollama.com/faq)). LM Studio unloads models it loaded on demand after 60 minutes ([LM Studio TTL](https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict)). llama-server's own `--sleep-idle-seconds` unloads "the model and its associated memory (including the KV cache)" and reloads on the next task. Its health, props, models and metrics endpoints do not wake it ([llama-server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)). Jazari keeps about 8.7 GB resident.

The same README says llama-server's default CORS setting "reflects any Origin with credentials". Jazari's `ServerLauncher.cs` starts llama-server on `127.0.0.1:8080` with `--no-webui`, but with no `--api-key` and no `--cors-origins`. Any local process can therefore bypass the gateway's key and queue, and so can browser JavaScript if the pinned build matches the README's default. That should be checked against the pinned build.

**Reach and interoperability.** Visual Studio BYOM went into preview in 18.10 Insiders. It is "enabled by default across Community, Professional, and Enterprise", and Ollama and OpenAI providers accept custom URLs ([Visual Studio blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/)). VS Code BYOK supports Ollama but "does not currently work with completions" ([VS Code blog](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)). Codex, OpenCode and Crush all read AGENTS.md ([Codex config](https://learn.chatgpt.com/docs/config-file/config-advanced); [Crush README](https://github.com/charmbracelet/crush)). Gemini CLI, Qwen Code and Codex each offer a headless mode with JSON output ([Gemini CLI](https://github.com/google-gemini/gemini-cli); [Qwen Code](https://github.com/QwenLM/qwen-code)).

Jazari is ahead in several areas, and the neighbours offer nothing to copy there:

- **Gateway security.** The gateway requires an API key and checks Origin and Host. Ollama has no built-in auth, LeakIX counted **12,269 exposed instances with zero authentication** ([LeakIX](https://blog.leakix.net/2026/02/ollama-exposed/)), and Qwen Code's daemon listens "without bearer authentication" by default.
- **Integrity of downloads.** Jazari pins both the engine and the model by SHA-256.
- **Tuning.** `jazari tune` measures on the user's machine and keys the result to a hardware fingerprint, while every runtime surveyed estimates instead.
- **Docs retrieval.** The law book abstains when the docs do not clearly cover the question. Context7 and the Microsoft Learn MCP server have no version gate or abstention ([Microsoft Learn MCP](https://raw.githubusercontent.com/MicrosoftDocs/mcp/main/README.md)).
- **Review verification.** Exact-once quote checking is stricter than PR-Agent's anchoring.
- **Telemetry.** Jazari has none. Qwen Code turns usage statistics on by default ([Qwen settings](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/)).

## Jazari's edit format is the bet the evidence most doubts

Jazari's /edit has the model emit search/replace pairs as JSON under a grammar. That guarantees a 100% parse rate. Several 2026 small-model studies warn that this guarantee can hide a loss of correctness.

| Study | Models | Finding |
|---|---|---|
| "The Constraint Tax" (abstract read) | Qwen2.5 0.5B/1.5B, SmolLM2-1.7B | Hard schema decoding raised validity from **61.5% to 100%** but cut accuracy from **19.7% to 11.0%**. On a tool-call task, Qwen2.5-1.5B fell from **91.5% to 48.0%** executable accuracy ([arXiv 2605.26128](https://arxiv.org/abs/2605.26128)) |
| "The Format Tax" (abstract read) | Six open-weight models | Most of the loss comes from format instructions in the prompt. Reasoning freely first and formatting in a second step recovers most of it ([arXiv 2604.03616](https://arxiv.org/abs/2604.03616)) |
| Chavan 2026 (abstract read) | 0.6B–4B models | Constrained decoding fixes structure, but not multi-step semantics ([arXiv 2609.23742](https://arxiv.org/abs/2609.23742)) |
| Aider, 2024 (large models only) | Four 2024 frontier models | Every model scored lower returning code in JSON than in markdown fences, and strict mode did not help ([Aider code-in-JSON](https://aider.chat/2024/08/14/code-in-json.html)) |

Diff-XYZ confirms that search/replace is the right family of formats at small sizes. Qwen2.5-Coder-1.5B generated correct diffs **0.20 of the time with search/replace against 0.00 with unified diff**. It also shows the absolute level stays low below 7B ([Diff-XYZ](https://arxiv.org/html/2510.12487)).

The evidence does not settle the question for Jazari's case. None of these studies tests C# edits, Granite, or a hybrid Mamba MoE. The three 2026 papers were read at abstract level only. JSON escaping of C# quotes and backslashes is the one cost that applies to Jazari for certain.

The sensible response is a measurement, not a rewrite. Run the existing /edit scenarios in three arms:

1. The current JSON under a grammar.
2. Plain `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` blocks read by a tolerant parser.
3. "Reason free, constrain late": the model writes the new code in a fenced block, and deterministic code or a short constrained second pass packages it.

Score each arm separately on parse, apply, compile and task-correct. Compare task-correct with the McNemar tooling Jazari already has.

Aider's architect/editor results suggest arm 3 can win even when one model plays both roles. gpt-4o-mini paired with itself scored **60.2% against 55.6% solo** ([Aider architect](https://aider.chat/2024/09/26/architect.html)). Arm 3 roughly doubles /edit latency at 10 tok/s, so it has to earn its place in the timed scenarios.

A related idea is replacing whole C# methods. The edit target becomes a Roslyn-located method body instead of exact SEARCH text. This follows Aider's finding that small models do best with whole-unit output while keeping the output bounded. Whatever wins, keep line numbers out of the schema: Diff-XYZ attributes search/replace's advantage partly to avoiding them.

## Ranked adoption list for a 16 GB CPU laptop at 10 tok/s

The ranking weighs value against effort on the T480 with a ~1B-active model. It strongly favours changes that cost no generated tokens.

| Rank | Change | Borrowed from | Effort | Why it fits 1B at 10 tok/s |
|---|---|---|---|---|
| 1 | Start llama-server with `--api-key-file` (a random per-launch key the gateway holds) and restricted `--cors-origins` | llama-server flags | Hours | Closes a bypass around the gateway; no model cost |
| 2 | `--sleep-idle-seconds` (about 900), health checks only on endpoints that do not wake the model, and a `jazari stop` command | Ollama, LM Studio, LocalAI, llama-server | Hours | Gives back about 8.7 GB when idle; background jobs must not keep it awake |
| 3 | /review hygiene: normalize quotes before verification; break ties with the reported line number; treat length-limited or unparseable output as failed and re-split; add a coverage section; cap findings at 3 per unit and deduplicate | PR-Agent | Hours to 1 day | Pure code; removes silent gaps and false "unverified" results |
| 4 | After /edit, run a Roslyn semantic check or `dotnet build`, then **one** retry with only the error lines and the enclosing method | Aider lint/test, SWE-agent guardrail (+3 points) | 1–2 days | One retry costs about 15 s of generation plus prompt processing; the Qwen data says more retries will not pay at this size |
| 5 | Stronger patch applier: relative-indentation matching, then progressively looser tiers, logged by tier | Aider flexible patching (9× fewer errors) | 1–2 days | No tokens; builds on the existing whitespace-tolerant match |
| 6 | Run the three-arm edit-format A/B test from the previous section with the McNemar tooling | Constraint Tax, Format Tax, Aider architect | 1–2 days | Could raise edit correctness without a new model |
| 7 | `/review --changed`: only changed hunks, each with its enclosing member, plus finding state (new / still present / resolved) | PR-Agent, Kodus | 2–4 days | Minutes instead of hours; small units are where review F1 holds up |
| 8 | Roslyn symbol graph with personalized PageRank to fill /read's 5k budget, seeded from identifiers in the question and kept with a risk prior | Aider repo map | About 1 week | Zero model tokens; reuse FTS5 for a BM25 index of declarations |
| 9 | Static pre-pass (Roslyn and NetAnalyzers, SecurityCodeScan, gitleaks); the model gives one short verdict per finding and keeps anything it is unsure about; rule docs inserted by rule ID | Copilot Autofix, Semgrep, Tencent study | 1–2 weeks | The pre-pass is solid; verdict quality at 1B is unproven, so never let it delete |
| 10 | Read AGENTS.md with a byte cap; add `.jazari/rules/*.md` scoped by glob or regex; add a `.jazariignore` file | Codex, Continue, Crush | 1–2 days | Deterministic and saves context; skip rules that the model chooses itself |
| 11 | Eval hygiene: dev and held-out split; stamp each run with git hash, GGUF hash, build and seed; separate counts for parse, apply, compile and correct; compile code answers instead of matching substrings | Aider benchmark harness, Diff-XYZ, EvalPlus gap on the Granite card | 2–3 days | Stops the 45-question set from being overfitted |
| 12 | Set `-cms` explicitly and re-measure `-ctxcp` before any llama.cpp pin bump; test slot save/restore for cold starts and check it keeps Granite's recurrent state | llama-server | 1–2 days | Upstream now defaults to 32 checkpoints with an 8192-token minimum step, which could stop prefix reuse for hybrid models |
| 13 | Ollama-API emulation (`/api/tags`, `/api/show`, `/api/chat`) with honest capability metadata (real context size, no tools unless proven) | VS BYOM, VS Code BYOK, KoboldCpp | About 1 week | Lets Jazari run inside Visual Studio and VS Code; stream immediately to avoid client timeouts |
| 14 | Windows housekeeping: update check after the first prompt, with an opt-out; `jazari uninstall` that asks before deleting the model; resumable model download; updates from signed tags rather than the tip of main | Jan, Ollama, Ollama updater CVEs | 2–4 days | Fixes the most likely install failure (a 4.2 GB download over Wi-Fi) and update risk |
| 15 | `jazari tune`: use `--fit` with a small `-fitt` for the untuned path; test MoE experts on CPU as a new axis; pin Vulkan with `GGML_VK_VISIBLE_DEVICES`; try n-gram speculative decoding | llama-server, LM Studio `n_cpu_moe`, Ollama | 2–3 days | Measured and opt-in; whether the hybrid model can speculate is unverified |
| 16 | `/plan` read-only mode, `jazari -p --json` headless mode, and checkpoints restorable to step N | OpenCode, Qwen Code, Gemini CLI, Cline | 1–3 days each | Cheap UX that reuses the gateway |

**Ranks 1–3 are the cheapest wins, and two of them are flags on a binary Jazari already ships.** The idle-sleep change has one trap. LM Studio's bug tracker shows models loaded by an explicit call escaping the idle timer ([LM Studio #2051](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2051)). Jazari has the mirror risk: the state-note updater and other background jobs could keep 8.7 GB resident forever unless they are routed through the queue deliberately.

**Ranks 4–8 attack /edit and /review quality, the two places a 1B model fails most visibly.** Their costs are mostly CPU-side Roslyn work, not generated tokens. The repo map ranks lower than its value only because it is the largest single piece of work.

**Rank 9 is the biggest structural change to /review.** It turns an open-ended search into a series of narrow verdicts. Ship the pre-pass first, and add LLM triage only after the eval shows it beats keeping every analyzer finding.

The guard rails for the planned four-tool agent loop are not ranked, because the loop does not exist yet. When it arrives, five rules apply:

- **Step cap with a summary.** Cap the steps, and when the cap is reached prompt the model to summarize its work and list what is left (OpenCode).
- **Loop detection.** Detect identical repeated calls and warn or stop (Qwen Code).
- **Truncate tool output.** Cut tool output well below Qwen's 25,000 characters.
- **Approve reads only.** Auto-approve read-only tools, and always confirm edits (Crush).
- **Constrain tool calls with grammar instead of a second model.** Goose's tool shim needs a second interpreter model (mistral-nemo by default) ([Goose tool shim](https://goose-docs.ai/docs/guides/tool-shim/)), which a 16 GB laptop cannot hold alongside Granite.

Users of other tools most often report silent context truncation, not bad tool calls ([Aider Ollama docs](https://aider.chat/docs/llms/ollama.html)). Jazari controls both ends, so the gateway should refuse requests that exceed `n_ctx` rather than truncate them.

Several neighbour features are clear skips at this size:

- **Multi-step autonomous agents.** Every source rules them out for small models.
- **Embeddings or LLM reranking for code.** The field has moved away from them, and reranking costs thousands of prompt tokens on CPU.
- **Self-reflection that deletes findings.** It is unproven below a 3.6B-active reasoning model.
- **Inline autocomplete.** Continue's example budget is a **150 ms model timeout** ([Continue autocomplete](https://docs.continue.dev/customize/deep-dives/autocomplete)), about 10 times faster than a 10 tok/s CPU can produce one line.
- **Router or multi-model mode, a tray app or Windows service, and `-np` above 1.** Each costs RAM that 16 GB cannot spare.

## Conclusion

The survey shows that Jazari's gap is not intelligence. No neighbour has a trick that makes a 1B model reason better. What they have is code around the model that checks its work, which Jazari only partly has. The projects with the best results all put the model inside deterministic code: a symbol graph chooses what it reads, an analyzer or a quote check decides what counts as a finding, and a compiler judges each edit. That shifts where Jazari's effort should go. Each hour spent on Roslyn-based selection, verification and state tracking does more for a 1B model than the same hour spent on prompts. It also keeps paying off if the model is later swapped for a larger one.

The open question that matters most is whether grammar-constrained JSON is helping or quietly hurting /edit. Jazari already has the McNemar harness to answer it in a day or two. Run that experiment before building the edit-related items on the list, because its answer decides how the retry loop and the patch applier should be shaped.
