# Open-source IDE AI coding assistants with local models: what Jazari could adopt (as of 2026-11-03)

Labels on each claim:
- **[sourced]**: the primary page was fetched in this session.
- **[snippet]**: comes only from a search-result summary. The page was not read in full, so the claim may be paraphrased or out of context.
- **[inferred]**: the researcher's own reasoning.

Statements from the researcher's prior knowledge that were not re-checked are kept in the Gaps sections and marked "unverified". Pre-2025 material is flagged as possibly outdated.

## Which projects are alive in late 2026, and what are they? (per-project profiles)

### Takeaway
The field consolidated sharply in 2026. Several projects stopped:
- Roo Code shut down on 2026-05-15.
- Twinny is archived.
- Void is paused.
- Refact's cloud is being wound down.
- Kilo Code was acquired by Anaconda.

Still actively shipping local-model support are Continue, Cline, Tabby (more slowly), Kilo, and Microsoft's own BYOK/BYOM in VS Code (April 2026) and Visual Studio 18.10 Insiders (August 2026). The Microsoft paths matter most to Jazari because they connect straight to an OpenAI-compatible or Ollama endpoint.

### Cited Findings
**Continue (continue.dev).** Active. It is an IDE extension with chat, agent, edit and autocomplete.
- It has a rules system in `.continue/rules` [sourced] — [Continue rules](https://docs.continue.dev/customize/deep-dives/rules)
- Autocomplete is configured in `config.yaml` under `autocompleteOptions`; the older `tabAutocompleteOptions` JSON form is deprecated [sourced] — [Continue autocomplete](https://docs.continue.dev/customize/deep-dives/autocomplete)

**Tabby (TabbyML).** A self-hosted completion and chat server.
- The README calls it "Self-contained, with no need for a DBMS or cloud service" and says it "Supports consumer-grade GPUs". Its example uses StarCoder-1B for completion and Qwen2-1.5B-Instruct for chat. It has 33.9k stars [sourced] — [TabbyML/tabby README](https://github.com/TabbyML/tabby)
- The last versioned item in the README news is v0.30 (2025-07-02, GitLab MR indexing). The 2025-12-12 news item is about "Pochi" tasks in vscode@0.20.0, an agent product [sourced] — [TabbyML/tabby README](https://github.com/TabbyML/tabby)
- An aggregator says v0.32.0 shipped in January 2026 (Mistral embedding API, generic OAuth, multi-branch indexing) [snippet, third-party] — [everydev.ai](https://www.everydev.ai/developers/tabbyml)
- [inferred] Tabby's server-side focus seems to be moving toward the Pochi agent, and its core release cadence has slowed.

**Cline.** Active. It is an agentic VS Code extension.
- Its local-model docs cover Ollama (`http://localhost:11434`), LM Studio (`http://localhost:1234`) and Atomic Chat (`http://127.0.0.1:1337/v1`) [sourced] — [Cline: running models locally](https://docs.cline.bot/running-models-locally/overview)
- It mentions a "Cline Desktop" Local Models page that recommends models for your hardware [sourced] — [Cline read-me-first](https://docs.cline.bot/running-models-locally/read-me-first)

**Roo Code.** Shut down.
- The repository banner reads "archived by the owner on May 15, 2026. It is now read-only". The README also says "The Roo Code Extension was shut down on May 15th" and points users to the community fork ZooCode, and to Cline [sourced] — [RooCodeInc/Roo-Code](https://github.com/RooCodeInc/Roo-Code)
- Docs still exist at roocodeinc.github.io; the Ollama page was last updated 2026-05-15 [sourced] — [Roo Code Ollama docs](https://roocodeinc.github.io/Roo-Code/providers/ollama)

**Kilo Code.** Active.
- The docs carry a banner saying "Kilo has been acquired by Anaconda" (no date shown). It supports Ollama, LM Studio and Atomic Chat [sourced] — [Kilo local models](https://kilo.ai/docs/advanced-usage/local-models)

**Twinny.** Archived.
- Its site shows "TWINNY HAS BEEN ARCHIVED", and the extension has not been updated since about August 2025. Whether the GitHub repo is formally archived is unclear [snippet, review sites] — [promptquorum review](https://www.promptquorum.com/power-local-llm/twinny-review); [selfhostedworld](https://selfhostedworld.com/software/twinny)

**Void editor** (VS Code fork, Apache-2.0). Paused.
- The README says the team "paused work on the Void IDE" to explore other ideas, that features may stop working without maintenance, and that issues and PRs are not reviewed [snippet, README quoted in search results] — [bestofjs Void](https://bestofjs.org/projects/void); [codersera 2026 guide](https://codersera.com/blog/void-ide-complete-guide-2026/amp/)

**Refact.ai.**
- Refact Cloud is being wound down, with BYOK and self-hosting now recommended. The self-hosted server is free (BSD-3) and runs in Docker with a GPU. The VS Code plugin repo is archived and the code has moved into a monorepo [snippet] — [Refact self-hosted docs](https://docs.refact.ai/guides/version-specific/self-hosted/); [api-evangelist/refact-ai](https://github.com/api-evangelist/refact-ai)

**VS Code BYOK.**
- BYOK covers OpenRouter, Ollama, Google, OpenAI and others. An "OpenAI Compatible" provider accepts "any OpenAI-compatible API endpoint" but was Insiders-only as of October 2025.
- Foundry Local is available through the AI Toolkit extension.
- The Language Model Chat Provider API (v1.104) lets extensions contribute models.
- "BYOK does not currently work with completions."
- [sourced] — [VS Code blog, 2025-10-22](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)
- GitHub's changelog says BYOK reached Copilot Business/Enterprise in April 2026, including Ollama and Foundry Local [snippet] — [GitHub changelog 2026-04-22](https://github.blog/changelog/2026-04-22-bring-your-own-language-model-key-in-vs-code-now-available/)

**Visual Studio BYOM.** Preview in Visual Studio 18.10 Insiders, announced 2026-08-24.
- It is "enabled by default across Community, Professional, and Enterprise".
- Providers are Microsoft Foundry, OpenAI, Anthropic and Ollama, with custom URLs supported for OpenAI and Ollama.
- It covers chat and Agent (Preview) and works "whether or not you're signed in to GitHub".
- [sourced] — [Visual Studio blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/)

### Inferences
- [inferred] For a Windows / .NET / Visual Studio user, the realistic "IDE integration" targets for Jazari in 2026 are:
  1. Visual Studio BYOM (Ollama or OpenAI custom URL).
  2. VS Code BYOK (Ollama provider or OpenAI Compatible).
  3. Continue or Cline as third-party extensions.
- Building a Jazari VS extension is much lower priority now that both Microsoft IDEs accept local endpoints.
- [inferred] Roo Code, Twinny and Void should be treated as sources of design ideas only, not as integration targets.

### Gaps
- No official TabbyML changelog page was fetched; the v0.32.0 date is from an aggregator.
- The date of Kilo's Anaconda acquisition and its effect on the open-source extension were not found.
- Whether ZooCode (the Roo fork) is actively maintained was not checked.
- Unverified, from prior knowledge: Kilo Code began as a fork of Roo Code/Cline, and Void was a Cursor-style VS Code fork with first-class Ollama support.

## How do they integrate local models, and what do they require of the endpoint?

### Takeaway
Almost everyone speaks either Ollama's native API or the OpenAI-compatible `/v1` API.
- Agent features effectively require native OpenAI-style tool calling. Roo Code removed its XML fallback before shutting down, and Visual Studio flags which models support tools.
- Autocomplete requires a FIM-trained model and a completion-style endpoint, separate from chat.
- Microsoft's IDE integrations are chat/agent only, with no completions.

### Cited Findings
- Cline connects to Ollama, LM Studio and Atomic Chat (an OpenAI-compatible `/v1` server) [sourced] — [Cline overview](https://docs.cline.bot/running-models-locally/overview)
- Roo Code uses Ollama's native API by default; an OpenAI-compatible `/v1` handler "exists but isn't required". It "defers to the Modelfile's `num_ctx`" rather than setting the context size itself. Its OOM table suggests `num_ctx` 16384 for about 8 GB VRAM, 32768 for about 16 GB, and 65536 for 24 GB or more [sourced] — [Roo Code Ollama docs](https://roocodeinc.github.io/Roo-Code/providers/ollama)
- Kilo base URLs are Ollama `http://127.0.0.1:11434`, LM Studio `http://127.0.0.1:1234/v1` and Atomic Chat `http://127.0.0.1:1337/v1`. It can auto-discover loaded Atomic Chat models (`atomicChat.autoDetect`) [sourced] — [Kilo local models](https://kilo.ai/docs/advanced-usage/local-models)
- Kilo's LM Studio docs say API requests time out after 10 minutes by default, "which can be too short for slow local models" [snippet] — [Kilo LM Studio](https://kilo.ai/docs/ai-providers/lmstudio)
- VS Code has a `github.copilot.chat.customOAIModels` setting that lets you "explicitly configure the edit tools available for custom models" [sourced] — [VS Code BYOK blog](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)
- VS Code's Ollama provider queries the local Ollama server and lists the pulled models [snippet, third-party] — [startdebugging.net](https://startdebugging.net/2026/04/github-copilot-vs-code-byok-anthropic-ollama-foundry-local/)
- A third-party guide warns that some custom-endpoint models "can silently vanish from Agent mode" [snippet] — same source
- In a Visual Studio BYOM hands-on, Ollama was auto-filled at `http://127.0.0.1:11434/v1`, the model appeared in the normal picker, and "Visual Studio flagged which models supported tools" [snippet] — [Visual Studio Magazine 2026-08-25](https://visualstudiomagazine.com/articles/2026/08/25/hands-on-previewing-bring-your-own-local-ai-model-to-visual-studio-insiders.aspx)
- Microsoft's post lists tool calling only as a future "granular control". Reader comments report that only one OpenAI-compatible provider can be added at a time, and that some local servers (LM Studio) fail with a URL-format error [sourced] — [Visual Studio BYOM blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/)
- Roo Code 3.36.13 (December 2025) made native tool calling the default, and 3.42.0 dropped legacy XML tool calling for new tasks [snippet, release notes] — [Roo v3.42.0 notes](https://docs.roocode.com/update-notes/v3.42.0); [Roo v3.36.13 notes](https://docs.roocode.com/update-notes/v3.36.13)
- A community PR (#10347, closed) complained that this broke OpenAI-compatible servers without native tools, such as vLLM and SGLang [snippet] — [Roo PR #10347](https://github.com/RooCodeInc/Roo-Code/pull/10347)
- Continue autocomplete requires models trained on a specific completion prompt format (templates in `AutocompleteTemplate.ts`). General chat models "won't generate useful completions" [sourced] — [Continue autocomplete](https://docs.continue.dev/customize/deep-dives/autocomplete)

### Inferences
- [inferred] **Top adoption candidate: Ollama-API emulation on the Jazari gateway.** This means `/api/tags`, `/api/show` and `/api/chat` in front of llama-server, protected by the same API key or a localhost-only rule.
  - Visual Studio BYOM and VS Code BYOK both have a first-class Ollama provider with custom URL support, and LM Studio-style OpenAI URLs reportedly hit URL-format errors in Visual Studio.
  - Looking like Ollama is probably the most reliable way into both Microsoft IDEs.
  - It costs no model capacity, so it is fully viable at 1B/10 tok/s.
- [inferred] **Be honest about capabilities.**
  - If Granite plus llama-server tool calling is not solid, Jazari's `/v1/models` and `/api/show` should not advertise tools. The IDE would then offer chat/Ask rather than Agent mode for Jazari, which avoids the "uneven agent" failure.
  - If tools are passed anyway, the gateway could strip them or return a clear error rather than let a 1B model produce garbage tool calls.
- [inferred] **Timeouts and keep-alive.** At about 10 tok/s, a 2,000-token answer takes about 3+ minutes of decode alone. Jazari's gateway should stream promptly (send the first chunk or role delta immediately) so IDE clients do not time out while it waits in the priority queue.
- [inferred] The `num_ctx` guidance is irrelevant to llama-server (Jazari sets `-c` itself). The lesson is that every client assumes 16k–64k context, so Jazari must state its real context limit in `/api/show` / `/v1/models` metadata and truncate server-side.

### Gaps
- The exact capability fields VS Code and Visual Studio read from Ollama `/api/show` (e.g. `capabilities: ["tools"]`, `context_length`) were not verified in primary docs. Check them before implementing emulation.
- Whether the OpenAI Compatible provider in VS Code has reached stable by November 2026 was not confirmed.
- Whether Visual Studio BYOM calls `/v1/models` or needs a manually typed model id is not stated in the Microsoft post.
- Unverified, from prior knowledge: Granite 4.0 models ship a tool-calling chat template, and llama-server needs `--jinja` to parse OpenAI-style tool calls from it.

## How do they handle small models that call tools badly?

### Takeaway
There are three strategies:
1. **Shrink the prompt and toolset.** Cline's "Use Compact Prompt" is reported as about 90% smaller.
2. **XML tools in the system message instead of native tools.** Continue's experimental "system message tools".
3. **Give up on small models for agents.** Roo went native-only; Continue and CodeCompanion recommend 20–32B for agent use.

Nobody claims a 1B-active model can run a reliable multi-step agent. Vendors steer small models toward autocomplete, explanation, commit messages and focused single-function edits, which is almost exactly Jazari's current command set.

### Cited Findings
- Cline: enable "Use Compact Prompt" under Settings → Features. It is listed as a Quick Start step for local models [sourced] — [Cline overview](https://docs.cline.bot/running-models-locally/overview)
- Cline's LM Studio guide says compact prompt reduces prompt size by 90% while keeping core functionality [snippet] — [Cline LM Studio docs](https://docs.cline.bot/running-models-locally/lm-studio.md)
- An AMD write-up says turning compact prompts off makes responses "noticeably more nuanced", so it is a trade-off [snippet] — [Cline blog: local models on AMD](https://cline.bot/blog/local-models-amd)
- Continue "system message tools":
  - Tools are converted to XML and placed in the system message, and the model emits XML that Continue parses [snippet] — [Continue model capabilities](https://docs.continue.dev/customize/deep-dives/model-capabilities)
  - The docs say "Models that struggle with native tools often perform better with system message tools" [snippet] — same source
  - It is experimental and "not automatically used as a fallback and must be explicitly configured". An older mirrored page claims automatic selection, so the docs conflict [snippet] — [Continue model capabilities](https://docs.continue.dev/customize/deep-dives/model-capabilities); contradicted by [mirror of agent model-setup](https://gitea.varghacsongor.hu/GithubMirror/continue/src/commit/d4d39aaeb05e90d43d495beb8fb7f623f79bf42e/docs/features/agent/model-setup.mdx)
- Continue agent setup calls Qwen2.5-Coder 32B the best local agent model and says "smaller models (7B-13B) may struggle with complex tool interactions" [snippet] — [Continue agent model setup](https://docs.continue.dev/ide-extensions/agent/model-setup)
- Kilo says small models are "most reliable for autocomplete, explanations, commit messages, focused edits, and single-function generation". Its picks are Gemma 4 12B for tool calling with 12–16 GB, Falcon H1R 7B for 8 GB GPUs, and Qwen 3.5 9B for bounded reasoning [snippet, vendor marketing page] — [kilo.ai/local-models](https://kilo.ai/local-models)
- Kilo LM Studio troubleshooting: if the agent misses tool calls, check the context window, use a model with demonstrated tool use, and test smaller tasks before giving more autonomy [snippet] — [Kilo LM Studio](https://kilo.ai/docs/ai-providers/lmstudio)
- CodeCompanion (a Neovim plugin, not one of the target projects):
  - Small models "call the wrong tool, pass malformed arguments or stop halfway through a task"; it suggests about 20B or more for agentic use [snippet] — [CodeCompanion local models guide](https://codecompanion.olimorris.dev/guides/running-local-models)
  - Ollama's tool-support flag alone is not proof a model can use tools reliably [snippet] — same source
- Roo removed XML tools entirely (3.42.0), which locked out servers without native tool support [snippet] — [Roo v3.42.0](https://docs.roocode.com/update-notes/v3.42.0); [PR #10347](https://github.com/RooCodeInc/Roo-Code/pull/10347)

### Inferences
- [inferred] **Jazari's current design (fixed slash commands, each one a single-shot prompt with server-side structure) is the right shape for a 1B model.** It matches what Kilo/Continue say small models are good at. An open-ended agent loop would hit the documented failure modes.
- [inferred] If Jazari adds a tool loop, the lessons for 1B/10 tok/s are:
  1. **Tiny toolset.** 2–4 tools, such as `read_file`, `search` (BM25/law book) and `propose_edit`, instead of Cline's large set; the compact-prompt result shows prompt size is the main lever.
  2. **One tool call per turn.** Use a tolerant parser, and repair malformed calls server-side rather than re-prompting (each retry costs tens of seconds at 10 tok/s).
  3. **Prefer grammar-constrained output over hoping.** llama.cpp GBNF / JSON-schema `response_format` can force a valid tool-call JSON shape. Jazari already uses search/replace JSON for /edit, so this is an extension of an existing pattern.
  4. **Keep both modes.** Native tools where llama-server parses them, plus a text/XML fallback. Roo's removal of XML is a cautionary tale for exactly Jazari's kind of backend.
  5. **The gateway decides when to call tools wherever possible.** Jazari's law book "insert only when it clearly covers the question" is already this pattern: retrieval without asking the model to decide.
- [inferred] **Advertise a "compact" system prompt for IDE clients.** When VS Code or Visual Studio send their own large agent prompts, Jazari could detect known client system prompts and replace or trim them before forwarding. This is risky, but it is the server-side equivalent of Cline's compact prompt. A safer start is to log and measure incoming prompt sizes.

### Gaps
- Cline's compact prompt contents (which tools and sections it removes) were not documented on the pages fetched.
- No source was found testing any of these extensions with a model of about 1B active parameters, or with Granite 4.0-H specifically.
- Unverified, from prior knowledge: Cline historically used XML-in-text tool calls, which let it work with any chat endpoint, and later added native tool calling. Its current default was not confirmed.

## Context features: indexing, embeddings vs keyword, @-mentions, docs indexing, rules files

### Takeaway
- **Rules.** The most transferable cheap idea is Continue's rules with selective inclusion: glob, regex and description frontmatter. Glob and regex matching is deterministic and costs nothing at 1B.
- **Docs ingestion.** Tabby lets users push their own docs over a REST API and indexes GitLab MRs and multiple branches. That is a model for opening Jazari's law book to project/internal docs.
- **Embeddings.** Most tools use embeddings-based codebase and docs indexing. Jazari's keyword/FTS5 approach with a confidence gate is a defensible choice for a CPU box, not a deficit.

### Cited Findings
- **Continue rules:**
  - Location and order: `.continue/rules/*.md` with YAML frontmatter, loaded in lexicographic order (numeric prefixes control order).
  - Frontmatter: `globs` (include when matching files are in context), `regex` (include when file content matches), `description` (the agent decides whether to include), `alwaysApply`.
  - Rules are joined into the system message and apply to Agent, Chat and Edit, "but not to autocomplete or apply".
  - The base system message can be overridden per model (`chatOptions.baseSystemMessage`).
  - The agent can write rules with a `create_rule_block` tool.
  - [sourced] — [Continue rules](https://docs.continue.dev/customize/deep-dives/rules)
- Continue's rules page does not mention AGENTS.md, .cursorrules or CLAUDE.md [sourced, absence] — [Continue rules](https://docs.continue.dev/customize/deep-dives/rules)
- **Tabby:**
  - Repo-level context for code completion is RAG-based (since v0.3.0), plus "locally relevant snippets" (April 2024; possibly outdated).
  - v0.27 (2025-03-31) added a richer `@` menu in chat; v0.29 (2025-05-20) lets users add their own documentation through REST APIs; v0.30 (2025-07-02) indexes GitLab Merge Requests.
  - v0.28 turns Answer Engine messages into persistent, shareable "Pages".
  - [sourced] — [TabbyML/tabby README](https://github.com/TabbyML/tabby)
- Tabby v0.32.0 added a Mistral embedding API option and indexing across multiple branches [snippet, aggregator] — [everydev.ai](https://www.everydev.ai/developers/tabbyml)
- Kilo lists "Limited Features" for local models: they "often lack prompt caching, computer use, and others" [sourced] — [Kilo local models](https://kilo.ai/docs/advanced-usage/local-models)
- Roo Code tracks token usage for Ollama models [sourced] — [Roo Ollama docs](https://roocodeinc.github.io/Roo-Code/providers/ollama)

### Inferences
- [inferred] **Adopt glob/regex-scoped rules.**
  - Example: `.jazari/rules/*.md` with `globs: ["**/*.razor"]` or `regex: "\\[Authorize"`, injected only when matching files are in the prompt.
  - It is fully deterministic and costs no model calls, so it works at 1B.
  - It also saves context, since only relevant rules are inserted. That matters more for Jazari than for cloud-model tools.
  - Skip the `description`-based "agent decides" path: a 1B model choosing rules is unreliable.
- [inferred] **Read existing rules files.** Reading AGENTS.md, and possibly `.continue/rules` and `.clinerules`, as additional "state notes" is a low-cost interoperability win for users who already use other tools.
- [inferred] **The law book compares well.**
  - Tabby/Continue-style docs indexing usually means crawl, embed and retrieve top-k.
  - Jazari's FTS5 search plus an "only when it clearly covers" gate avoids filling a small model's context with marginal chunks. This is likely better for a 1B model than always-on top-k RAG.
  - What Tabby does better: user-extensible docs (REST ingestion of the user's own docs) and indexing beyond the file tree (MRs, branches).
  - Jazari could add a `jazari lawbook add <folder|url>` ingestion path into the same FTS5 store.
- [inferred] **Hybrid retrieval later.** Embeddings would need a second model resident in 16 GB RAM, plus CPU time at index and query. A tiny embedding model (tens of MB) is cheap enough to try later as a reranker over BM25 hits, but it should not replace BM25.
- [inferred] **Persistent "pages".** Tabby's shareable Answer Engine "Pages" parallel Jazari's /save. Little to adopt beyond making /save output linkable from state notes.

### Gaps
- Continue's current @-mention providers (@docs crawling, @codebase status, @url) were not re-verified this session. Unverified, from prior knowledge: Continue offered @docs with site crawling plus embeddings, and moved away from the @codebase provider toward agent tools in 2025.
- Unverified, from prior knowledge: Roo Code/Kilo codebase indexing used an embeddings provider plus a Qdrant vector store, and Cline uses .clinerules plus a "memory bank" convention.
- Whether Cline and Continue read AGENTS.md in 2026 was not checked.

## Autocomplete/FIM: latency, model sizes, and viability at ~10 tok/s

### Takeaway
- **Model sizes.** The recommended FIM models are dedicated code models of 0.5–7B (Qwen2.5-Coder 1.5B/7B, Codestral, StarCoder-1B in Tabby's example).
- **Latency.** Continue's example config sets `debounceDelay: 250` ms and `modelTimeout: 150` ms, a budget roughly 10x tighter than a 10 tok/s CPU can meet for even a short line.
- **Microsoft IDEs.** BYOK/BYOM do not support completions at all.
- **Verdict.** As-you-type ghost text stays non-viable for Jazari. A manual-trigger, single-line, short-prompt mode with a separate tiny FIM model is the only plausible variant.

### Cited Findings
- **Continue autocomplete:**
  - Recommended models: Qwen2.5-Coder 1.5B/7B, Codestral (via the Mistral API) and Mercury Coder. "Most of the state-of-the-art autocomplete models are no more than 10b parameters", and going larger doesn't significantly help. "Closed models are slightly better than open models."
  - Example config: `debounceDelay: 250`, `modelTimeout: 150`, `maxPromptTokens: 1024`, `maxSuffixPercentage: 0.2`, `prefixPercentage: 0.3`, `multilineCompletions: auto|always|never`, `onlyMyCode: true`.
  - Thinking models are discouraged; set `think: false` via `requestOptions.extraBodyProperties`.
  - [sourced] — [Continue autocomplete](https://docs.continue.dev/customize/deep-dives/autocomplete)
- Tabby's README example runs StarCoder-1B for completion and Qwen2-1.5B-Instruct for chat on CUDA (`--gpus all --device cuda`) [sourced] — [TabbyML/tabby README](https://github.com/TabbyML/tabby)
- Reviews list "needing a GPU for good performance" as a Tabby drawback [snippet, third-party] — [stackpick Tabby](https://www.stackpick.net/tools/tabby/)
- VS Code: "BYOK does not currently work with completions" [sourced] — [VS Code BYOK blog](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)
- Visual Studio BYOM covers chat and Agent only [sourced] — [Visual Studio BYOM blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/)
- Kilo lists autocomplete first among the things small local models do reliably [snippet] — [kilo.ai/local-models](https://kilo.ai/local-models)

### Inferences
- [inferred] **The arithmetic.** A useful single-line completion is about 8–20 tokens, which is 0.8–2 s of decode at 10 tok/s, plus prefill of up to 1,024 prompt tokens on CPU. That is well above Continue's 150 ms example timeout. Also, Granite 4.0-H Tiny is a general instruct model, not a FIM-trained code model, and Continue says general models "won't generate useful completions".
- [inferred] **What could make it semi-viable:**
  1. Use a separate tiny FIM model (Qwen2.5-Coder-0.5B/1.5B base, Q4/Q8) in its own llama-server instance, not Granite.
  2. Trigger only manually (keybinding), never on every keystroke.
  3. Generate single-line only, with `n_predict` of about 16 and stop at newline.
  4. Cap prompts at 512–1,024 tokens (Continue's `maxPromptTokens` idea).
  5. Reuse the KV/prompt cache between requests on the same file.
- [inferred] Even with all of that, completion would sit in the "1–2 s on demand" range. It would also compete with chat for the same CPU cores, so Jazari's priority queue would need to preempt or deprioritize it.
- [inferred] **Hybrid-model caveat.** Prefix-cache reuse is less effective with Granite's hybrid Mamba layers, because recurrent state cannot be partially rolled back like a transformer KV cache. Another reason to keep FIM on a small pure-transformer model.
- [inferred] **Verdict.** Jazari's earlier judgment (no inline completion) holds. The only cheap experiment is pointing Continue's autocomplete role at a separate 0.5–1.5B llama-server `/infill` endpoint to measure real T480 latency; Jazari's gateway would not need changes beyond proxying.

### Gaps
- No primary benchmark of FIM latency on CPU-only laptops (e.g. an 8th-gen i5/i7 like the T480's) was found.
- Unverified, from prior knowledge: ggml-org's `llama.vscode` / `llama.vim` extensions do FIM against llama-server `/infill` with ring-buffer context and cache reuse, designed for local low-end hardware. They are probably the most relevant reference for a CPU FIM experiment and were not fetched this session.
- Unverified: whether Granite 4.0-H Tiny was trained with FIM tokens (some Granite releases advertise FIM). Check the model card before ruling it in or out.
- Unverified: Continue's autocomplete context sources (recently edited ranges, LSP definitions, imports) and "next edit" features were not re-checked.

## Apply/edit flows: diff review, checkpoints, undo

### Takeaway
The pages fetched this session said little about apply/edit UX, so most of this section relies on unverified prior knowledge (see Gaps).
- Confirmed from the pages: Continue keeps a separate "apply" step that rules do not touch, Microsoft's custom-model support lets you restrict which edit tools a custom model gets, and small models are recommended for "focused edits".
- Main idea worth adopting: checkpoints, meaning a workspace snapshot before each AI change, which goes beyond Jazari's per-edit undo. The other is presenting diffs in the IDE's native diff view.

### Cited Findings
- Continue rules apply to Agent/Chat/Edit "but not to autocomplete or apply", so apply is a separate pipeline stage [sourced] — [Continue rules](https://docs.continue.dev/customize/deep-dives/rules)
- VS Code's `github.copilot.chat.customOAIModels` lets you "explicitly configure the edit tools available for custom models" [sourced] — [VS Code BYOK blog](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)
- Kilo: small models are most reliable for "focused edits" and "single-function generation" [snippet] — [kilo.ai/local-models](https://kilo.ai/local-models)
- Roo Code's five modes are Code, Architect, Ask, Debug and Custom [sourced] — [RooCodeInc/Roo-Code](https://github.com/RooCodeInc/Roo-Code)

### Inferences
- [inferred] **Checkpoints.** Before each /edit batch, Jazari could snapshot the touched files, or the working tree via a shadow git repo or `git stash create`-style commit object that leaves the user's index alone. That turns "undo last edit" into "restore to checkpoint N" across multi-step sessions. Cheap, model-independent, and suited to a 1B model that will sometimes make bad edits.
- [inferred] **Separate apply step.** A small model emits a rough edit and a deterministic or second pass applies it. Jazari's search/replace JSON already avoids the "apply model" cost; keep it, since exact-match search/replace is the most 1B-friendly format.
- [inferred] **Ask/Architect-style read-only mode.** When Jazari is reached through VS Code or Visual Studio, steer users to Ask/chat mode. Agent mode in those IDEs would let the IDE's own edit tools drive a 1B model.
- [inferred] **Diff review UX.** Jazari's terminal /diff and /edit confirm cannot match an IDE diff editor. The cheapest bridge is writing proposed edits as a patch file, or invoking `code --diff old new`, without building an extension.

### Gaps
- Unverified, from prior knowledge, about Cline: it shows each file change in a VS Code diff view for approve/reject, keeps workspace checkpoints (shadow git) with "compare" and "restore", and has Plan/Act modes.
- Unverified: Continue had an "Instant Apply" path using fast apply models, and Roo Code had multiple diff strategies (search/replace blocks, unified diff).
- None of these were re-fetched this session; verify on docs.cline.bot and docs.continue.dev before citing in the final report.

## What do users praise and complain about with local/small models?

### Takeaway
- **Praise:** privacy, offline use, no API cost, and that the plumbing works. Local models now appear in the same picker as cloud models in Visual Studio.
- **Complaints:**
  - Agent quality is "uneven".
  - Small models misuse tools.
  - Long prompts are slow (hence compact prompts).
  - Timeouts with slow local servers.
  - Breakage when tools drop non-native tool-calling paths.
  - Connection quirks with OpenAI-compatible URLs.
  - Abandonment risk: Roo, Twinny and Void all stopped in 2025–26.

### Cited Findings
- Visual Studio BYOM hands-on: the plumbing worked and the local model showed up beside Claude and GPT in the picker, but "the actual agentic coding experience was considerably more uneven" [snippet] — [Visual Studio Magazine](https://visualstudiomagazine.com/articles/2026/08/25/hands-on-previewing-bring-your-own-local-ai-model-to-visual-studio-insiders.aspx)
- Microsoft: "Not every model supports every Agent Mode capability today"; "Where a feature depends on a capability a model doesn't offer, we aim to fail gracefully" [sourced] — [Visual Studio BYOM blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/)
- Visual Studio BYOM reader comments: only one OpenAI-compatible provider at a time, and LM Studio URL-format errors [sourced, comments section] — same source
- Kilo's advantages list includes privacy, offline use and no API fees. Its drawbacks include "resource-intensive", setup complexity, performance that may not match cloud models, and "Limited Features" [sourced] — [Kilo local models](https://kilo.ai/docs/advanced-usage/local-models)
- Kilo: the 10-minute default request timeout can be too short for slow local models [snippet] — [Kilo LM Studio](https://kilo.ai/docs/ai-providers/lmstudio)
- Cline's troubleshooting suggests compact prompt when responses are slow or the model gets confused. Turning it off makes responses "noticeably more nuanced" [snippet] — [Cline LM Studio docs](https://docs.cline.bot/running-models-locally/lm-studio.md); [Cline AMD blog](https://cline.bot/blog/local-models-amd)
- Roo PR #10347: dropping XML tools broke vLLM/SGLang OpenAI-compatible endpoints [snippet] — [Roo PR #10347](https://github.com/RooCodeInc/Roo-Code/pull/10347)
- A third-party guide: custom-endpoint models can silently vanish from VS Code Agent mode [snippet] — [startdebugging.net](https://startdebugging.net/2026/04/github-copilot-vs-code-byok-anthropic-ollama-foundry-local/)
- Roo Code OOM advice: preload with `ollama run` to avoid cold-start OOM, and check `ollama ps` for leftover processes [sourced] — [Roo Ollama docs](https://roocodeinc.github.io/Roo-Code/providers/ollama)

### Inferences
- [inferred] Jazari's gateway already addresses several complaints that others push onto the user:
  - The priority queue handles contention.
  - Pre-warmed llama-server avoids cold starts.
  - The response cache avoids repeated slow answers.
- Make these visible to IDE clients through early streaming, a clear 503 or queue-position message instead of silent hangs, and accurate model metadata.
- [inferred] **Abandonment risk is a selling point for Jazari's narrow approach.** It depends only on the stable OpenAI and Ollama wire formats, not on any one extension's survival. That argues for "be a great endpoint" over "build a Jazari extension".

### Gaps
- No systematic GitHub-issue sentiment pass was done (e.g. counting local-model issues in Cline or Continue). The complaints above come from docs, a review and a single PR.
- No user reports were found on Granite 4.0-H in any of these IDE tools.

## What should Jazari adopt, and where are these projects clearly better?

### Takeaway
The best adoptions are model-independent plumbing and context hygiene, not agent features. In priority order:
1. Ollama-API emulation plus accurate capability metadata, so Visual Studio and VS Code connect cleanly.
2. Glob/regex-scoped rules files, plus reading AGENTS.md.
3. Checkpoints for /edit.
4. User-extensible law book ingestion (Tabby-style).
5. A compact, tiny-toolset, grammar-constrained tool protocol, if and when a tool loop is added.
6. An optional, manually triggered FIM experiment on a separate tiny model.

### Cited Findings
- Both Microsoft IDEs accept Ollama with a custom URL, and VS Code also accepts any OpenAI-compatible endpoint [sourced] — [Visual Studio BYOM blog](https://devblogs.microsoft.com/visualstudio/unlocking-the-power-of-ai-for-every-developer-in-visual-studio-with-bring-your-own-model/); [VS Code BYOK blog](https://code.visualstudio.com/blogs/2025/10/22/bring-your-own-key)
- Continue rules: glob/regex/description/alwaysApply selective inclusion [sourced] — [Continue rules](https://docs.continue.dev/customize/deep-dives/rules)
- Tabby: user docs via REST (v0.29); MR and branch indexing [sourced/snippet] — [TabbyML/tabby README](https://github.com/TabbyML/tabby); [everydev.ai](https://www.everydev.ai/developers/tabbyml)
- Cline compact prompt, about 90% prompt reduction [snippet] — [Cline LM Studio docs](https://docs.cline.bot/running-models-locally/lm-studio.md)
- Continue system-message (XML) tools as an alternative to native tools [snippet] — [Continue model capabilities](https://docs.continue.dev/customize/deep-dives/model-capabilities)
- Continue FIM budget: 150 ms timeout, 250 ms debounce, 1,024-token prompt in the example [sourced] — [Continue autocomplete](https://docs.continue.dev/customize/deep-dives/autocomplete)

### Inferences
Adoption list (works at 1B/10 tok/s? / effort):

| # | Idea (source project) | Works at 1B active / 10 tok/s CPU? | Notes |
|---|---|---|---|
| 1 | Ollama-API emulation on the gateway: `/api/tags`, `/api/show`, `/api/chat` (VS, VS Code, Continue, Cline, Kilo all have Ollama providers) | Yes, no model cost | Highest leverage for IDE reach; must keep the API key or a localhost bind |
| 2 | Accurate capability metadata (context length, tools yes/no) in `/v1/models` and `/api/show`; strip or reject `tools` if unsupported (VS flags tool support; VS Code `customOAIModels`) | Yes | Prevents the "uneven agent" experience and keeps IDEs in chat mode |
| 3 | Glob/regex-scoped rules in `.jazari/rules/*.md`, plus reading AGENTS.md (Continue) | Yes, deterministic | Saves context; skip "description"-chosen rules (needs model judgment) |
| 4 | Checkpoints: snapshot before each /edit batch, restore to N (Cline/Roo, unverified) | Yes, no model cost | Bigger safety net for a small model that sometimes edits badly |
| 5 | Law book ingestion of user/project docs (Tabby v0.29 REST docs) | Yes, FTS5 only | Keep the "only when clearly covers" gate; that gate is an advantage |
| 6 | Compact prompt discipline and measuring prompt tokens per command (Cline) | Yes, helps directly | Prefill time is the hidden cost on CPU |
| 7 | Tool loop with 2–4 tools, one call per turn, GBNF/JSON-schema constrained, XML/text fallback kept (Continue system-message tools; lesson from Roo's XML removal) | Partly; bounded tasks only | Multi-step autonomy is not credible at 1B per every source |
| 8 | Early streaming and queue-position feedback to avoid client timeouts (Kilo 10-minute default) | Yes | At 10 tok/s long answers take minutes |
| 9 | Manual-trigger single-line FIM on a separate 0.5–1.5B FIM model (Continue/Tabby model sizes) | Marginal (about 1–2 s per request) | Competes with chat for CPU; experiment only |
| 10 | Shareable saved answers (Tabby "Pages") | Yes | Jazari /save already close; low value |

Where these projects are clearly better than Jazari:
1. **IDE-native UX.** Inline diff accept/reject and model pickers inside VS and VS Code, versus a terminal.
2. **Rules targeting.** Continue's per-file-type rules are more precise than Jazari's single per-project state notes.
3. **Docs extensibility.** Tabby ingests users' own docs and MRs, whereas Jazari's law book covers only Microsoft .NET docs.
4. **Multi-step undo.** Checkpoints (Cline) versus single undo (unverified for Cline).
5. **Tool-protocol fallbacks.** Continue's system-message tools.

Where Jazari is plausibly better or on par:
- Gated doc retrieval (avoids context pollution).
- Server-side queue and cache.
- Security-focused /review.
- Single-model tuning for one domain.
- Independence from any one extension's survival (Roo, Twinny and Void all stopped).

### Gaps
- None of the adoption ideas was prototyped or measured on the T480. The latency and viability judgments are reasoned, not benchmarked.
- The Ollama `/api/show` field set that VS and VS Code actually use for capability detection needs verification against Ollama's API docs and real client traffic (capture with a logging proxy) before implementing #1 and #2.
