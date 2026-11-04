# How open-source and self-hostable AI coding tools build codebase context and do AI code review, and what Jazari can adopt

Research date: 2026-11-03. Evidence labels used throughout:
- **[primary]**: read in full from the project's own repo, docs or blog in this session.
- **[vendor]**: primary, but a vendor's claim about its own product, not independently checked.
- **[snippet-only]**: taken from a search-result summary; the page itself was not fetched.
- **[third-party]**: from a source that is not the project's own.
- **Inferences** sections hold my reasoning about Jazari. Nothing there is sourced.
- **OUTDATED?**: the source is from before 2025.

Jazari baseline used for comparison: Granite 4.0-H Tiny (~1B active), llama.cpp on CPU at ~10 tok/s, 16k/8k context.
- **/read:** about 5k tokens of files, chosen by filename risk heuristics.
- **/review:** a whole-project walk in about 2k-token parts. The model returns JSON findings with an exact quoted line. A finding is "verified" only if that line occurs exactly once. Results are cached per part by content hash and written to a Markdown report. Large projects take hours.
- **Law book:** SQLite FTS5/BM25 over Microsoft .NET docs, with strict gates on API names, versions, and 75% term coverage.

---

## Q1. How do they select context under tight token budgets, and what works without embeddings or a GPU?

### Takeaway
Since 2024 the field has moved away from embedding RAG for code context. Sourcegraph Cody dropped embeddings for BM25-style search. Continue deprecated its embeddings-based @Codebase. Cline and Claude Code use grep plus AST tools. Aider never used embeddings: it ranks a tree-sitter symbol graph with personalized PageRank to fit a fixed token budget. That last design is the best fit for a CPU-only, ~1B model, because selection is deterministic and costs no LLM tokens. Agentic search (Amp, Cline, Claude Code) assumes a strong model making many tool calls, which a 1B model at 10 tok/s cannot do well.

### Cited Findings

**Aider repo map (tree-sitter + PageRank).** Most directly reusable. Code read from `main` in 2026; the blog post is from 2023 (OUTDATED? for the concept only).
- The map lists the important classes and functions with types and call signatures, sent with every request. `--map-tokens` defaults to 1k tokens. The map grows significantly when no files are in the chat. [primary] — [Aider docs: repo map](https://aider.chat/docs/repomap.html)
- Tree-sitter replaced ctags. It gives full call signatures and needs no separate `universal-ctags` install. Tree-sitter extracts where identifiers are defined and where they are referenced. Each file is a graph node, and edges are dependencies. The 2023 post reports no measured results. [primary, 2023] — [Aider blog, Oct 2023](https://aider.chat/2023/10/22/repomap.html)
- Edge-weight constants in `get_ranked_tags`. Every identifier starts at weight 1.0, then:
  - mentioned in the chat: ×10
  - snake/kebab/camelCase and ≥8 characters long: ×10
  - name starts with `_`: ×0.1
  - defined in more than 5 files: ×0.1
  - referenced from a file in the chat: ×50
  - reference counts are damped by `sqrt(num_refs)`
  - definitions with no references get a self-edge of 0.1

  [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- Personalization:
  - The base value is `100/len(fnames)`. Chat files and mentioned files get it.
  - A file whose path component or basename matches a mentioned identifier also gets it.
  - The ranking call is `nx.pagerank(G, weight="weight", personalization=..., dangling=...)`. On `ZeroDivisionError` it retries unpersonalized.
  - Rank is split across a node's out-edges and summed per (file, identifier).

  [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- Budget fitting is a binary search over the number of ranked tags:
  - The first guess is `min(max_map_tokens // 25, num_tags)`.
  - It accepts a result within 15% of the budget (`ok_err = 0.15`).
  - Token counting samples about 100 lines of long texts and extrapolates.
  - Defaults: `map_tokens=1024`, `map_mul_no_files=8`. With no chat files the target is `min(max_map_tokens*8, max_context_window-4096)`.

  [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- Caching: tags live in a `diskcache` SQLite store (`.aider.tags.cache.v3/v4`) keyed by file mtime and are re-parsed only when the mtime changes. On error it falls back to an in-memory dict. Refresh modes are `auto` (cache only if building took more than 1 s), `files`, `always` and `manual`. [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- C# (`.cs`) has repo-map and linter support. A language needs a tree-sitter `tags.scm` to get a repo map. Parsers come from `tree-sitter-language-pack`. [primary] — [Aider languages](https://aider.chat/docs/languages.html)

**Continue (IDE extension).**
- The @Codebase context provider is **deprecated**. The docs say it was replaced by giving Agent mode codebase awareness, for "more selective context picking, and better performance". @Docs was deprecated in the same change. [snippet-only] — [Continue: @Codebase (Deprecated)](https://docs.continue.dev/reference/deprecated-codebase)
- How the legacy design worked:
  - embeddings (local transformers.js, stored in `~/.continue/index`) plus keyword search
  - `nRetrieve` = 25 candidates, then an LLM reranker (`useReranking` = true) picks `nFinal` = 5
  - respects `.gitignore` and `.continueignore`
  - the docs said it is not useful for refactors or when the LLM must see every file

  [snippet-only] — [Continue: @Codebase (Deprecated)](https://docs.continue.dev/reference/deprecated-codebase)
- Community mirrors from mid-2026 tie the change to an MCP-based route for custom code RAG. [snippet-only, third-party mirror] — [gitea mirror of Continue docs](https://gitea.varghacsongor.hu/GithubMirror/continue/src/commit/d861a72c6f11a0b4f0f17e53fd0db9e8796a9954/docs/reference/deprecated-codebase.mdx)

**Tabby (self-hosted completion and chat server).**
- 2023 design (OUTDATED?):
  - A tree-sitter tags query (e.g. type declarations) extracts definition snippets into "an efficient token reverse index". Each completion request runs a BM25 search over it.
  - Snippets are inserted as line comments with `// Path: ...` headers, so they don't break the syntax of the prompt.
  - The budget was about 2,000 tokens of context. Ranking was "relatively simple". No quantitative gains were reported.

  [primary, Oct 2023] — [TabbyML blog: Repository context](https://www.tabbyml.com/blog/repository-context-for-code-completion)
- Current docs: context providers cover git repositories and developer docs. Repositories are parsed to an AST and indexed, and local `file://` repos are allowed. Developer-docs indexing is Beta. [snippet-only] — [Tabby docs: context](https://tabby.tabbyml.com/docs/administration/context)
- Third-party descriptions of current internals:
  - tree-sitter tags stored in Tantivy, per-repo snapshots for incremental rebuilds
  - hybrid BM25 plus vector retrieval fused with reciprocal rank fusion
  - multi-branch indexing in v0.32.0

  [snippet-only, third-party] — [pyshine walkthrough](https://pyshine.com/tabby/); [yespress](https://yespress.io/products/tabby.md)

**Sourcegraph Cody and Amp.**
- Cody retrieval in two stages:
  1. Retrieval: remote Sourcegraph search over up to 10 selected repos, plus snippets from open IDE files. Autocomplete uses only local signals: the active file, open tabs and recently closed tabs.
  2. Ranking: a global ranking using "an adapted form of BM25" plus learned, task-tuned signals. The top N snippets go into the prompt, with N depending on snippet length.

  [primary, Feb 2024, OUTDATED?] — [Sourcegraph: How Cody understands your codebase](https://sourcegraph.com/blog/how-cody-understands-your-codebase)
- Cody **dropped embeddings** (OpenAI ada-002) for three reasons: code had to go to a third party, admins had to maintain the index, and vector search scaled badly across many repos. [primary, Feb 2024] — [Sourcegraph blog](https://sourcegraph.com/blog/how-cody-understands-your-codebase)
- Older Cody docs describe 20–30-line chunks for embeddings, with keyword search as a zero-setup fallback. A later docs version makes the Sourcegraph search engine the primary context provider for Enterprise. [snippet-only] — [Cody keyword search docs](https://5.5.sourcegraph.com/cody/core-concepts/keyword-search); [Cody context docs](https://5.5.sourcegraph.com/cody/core-concepts/context)
- Status:
  - Cody Free and Pro ended (signups stopped 2025-06-25; access ended 2025-07-23). Cody is now enterprise-only. [snippet-only] — [Sourcegraph: changes to Cody plans](https://sourcegraph.com/blog/changes-to-cody-free-pro-and-enterprise-starter-plans)
  - Amp was spun out of Sourcegraph on 2025-12-02. Sources disagree on the company name. [snippet-only, third-party] — [HackerNoon](https://sia.hackernoon.com/sourcegraph-spins-out-amp-to-chase-the-ai-coding-frontier)
- Amp internals are closed:
  - It has a Search subagent with its own context window, and a "Librarian" that searches public and private GitHub code. [snippet-only] — [Amp docs: modes and subagents](https://ampcode.com/docs/models-and-subagents); [Amp: The Librarian](https://ampcode.com/news/librarian)
  - A third-party guide says the search subagent uses grep. [third-party, snippet-only] — [DEV guide](https://dev.to/matt_tanner_f9c36595644ad/how-to-use-subagents-in-ai-coding-198c)

**Cline (agent extension).**
- Cline deliberately keeps **no index**. It traverses files, parses ASTs with tree-sitter, and searches with ripgrep, then follows imports. The reasons given: an index must be kept fresh, embeddings are a second copy of the code, and reading files locally is fast. [snippet-only, primary blog] — [Cline blog: Why Cline doesn't index your codebase](https://cline.bot/blog/why-cline-doesnt-index-your-codebase-and-why-thats-a-good-thing)

**Serena (LSP tools for agents, MCP server).**
- Serena offers symbol-level tools: find symbol, find referencing symbols, symbol overview (file outline), insert after symbol, replace symbol body, and rename. Its default backend is language servers, with over 40 languages including C#. A paid JetBrains backend adds move, inline and safe delete. [primary] — [oraios/serena README](https://raw.githubusercontent.com/oraios/serena/main/README.md)
- The README claims symbolic editing is "much more token-efficient". Its evidence is self-reported testimonials from about 20 internal tasks. Clients are frontier-agent hosts (Claude Code, Codex, OpenCode, Cursor and others). Local models are not mentioned. v2 has a REPL-based interface. [primary, vendor claims] — [Serena README](https://raw.githubusercontent.com/oraios/serena/main/README.md)

**PR-Agent (context for diff review).**
- Asymmetric context: "the context preceding a code change is typically more crucial". The settings and their meaning:
  - `patch_extra_lines_before = 5` and `patch_extra_lines_after = 1`, each added to the 3 default diff lines
  - `allow_dynamic_context = true` extends upward to the **enclosing function or class**, up to `max_extra_lines_before_dynamic_context = 10` extra lines
  - `.md` and `.txt` files are skipped

  The page gives these values as appendix examples; it does not state formal defaults. [primary] — [PR-Agent dynamic_context.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/core-abilities/dynamic_context.md)
- Compression when the PR is larger than the budget:
  - Drop binary and non-code files.
  - Order files by the repo's language mix, then by token count with the largest first.
  - Prefer additions over deletions and remove deletion-only hunks.
  - Pack patches up to a buffer below the maximum.
  - List the overflow as "other modified files" and then "deleted files", with a hard stop at the token limit.

  [primary] — [PR-Agent compression_strategy.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/core-abilities/compression_strategy.md)

**CodeRabbit (commercial; methods published).**
- Each review clones the repo into a sandbox and **rebuilds a code dependency graph on every review**. It also uses past PRs, linked issues (Jira, Linear, GitHub, GitLab), path filters and path instructions, imported rules files (Cursor, Copilot, Cline, Windsurf), and learnings from chat. It aims for a **1:1 ratio of code to context** in prompts. [vendor, Jul 2025] — [CodeRabbit: context engineering](https://coderabbit.ai/blog/context-engineering-ai-code-reviews)
- "Codegraph" maps definitions and references and mines commit history for co-changed files, so it can flag bugs outside the diff. A semantic (embedding) index covers functions, classes, tests and prior PRs. [vendor, Sep 2025] — [CodeRabbit on DEV](https://dev.to/coderabbitai/how-coderabbit-delivers-accurate-ai-code-reviews-on-massive-codebases-46eo)

**GitHub Copilot Autofix (design reference).**
- Context sent to the LLM:
  - the CodeQL alert as SARIF
  - "short snippets around each source location, sink location, and any location referenced in the alert message or flow path"
  - "the first ~10 lines from each file involved"
  - the CodeQL query help text

  If the context would be truncated (a very large file or repo), it **does not attempt a fix**. [primary] — [GitHub Docs: responsible use of Copilot Autofix](https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/responsible-use-autofix-code-scanning)

### Inferences
- **Aider's design maps directly onto Jazari's /read.**
  - Replace "riskiest by filename" with a symbol graph built from C# syntax.
  - Seed personalization with identifiers and file names taken from the user's question, plus any files already read.
  - Keep a risk prior (auth, crypto, SQL, deserialization files) as an extra personalization term, not as the whole ranking.
  - Roslyn syntax-only parsing (`CSharpSyntaxTree.ParseText`, no MSBuild or compilation) or tree-sitter C# with Aider's `tags.scm` both give definitions and references in milliseconds per file on CPU. PageRank over a few thousand nodes is negligible.
  - Cost per query: zero LLM tokens.
  - The Aider constants (×10 mentioned, ×50 from chat files, sqrt damping, ×0.1 for generic names) are a tested starting point.
- **Two separate uses for a repo map:**
  - Choosing which files fill the 5k-token /read budget. This works with any model size.
  - Showing the model a signatures-only map. Value is uncertain with a 1B model, and 1k tokens is 6–12% of an 8–16k window.

  Start with the first. Make the second optional and measure it.
- **Jazari can reuse its own FTS5 for code.** It already has SQLite FTS5/BM25 for the law book. Index C# declarations (symbol name split on camelCase, signature, file, doc comment) in the same engine. Code BM25 then needs no new dependency. Tabby (2023) and Cody (2024) both ran BM25-style code retrieval in production.
- **Skip embeddings and LLM reranking for code context.** Continue's 25→5 LLM reranking would cost thousands of prompt tokens per query at CPU prompt-processing speed. Jazari's prompt-processing speed is not measured in these notes, so it should be measured before considering any reranking step.
- **Agentic search is a poor fit for Jazari's default path.** The Amp, Cline and Claude Code style needs many reliable tool calls. A 1B model at 10 tok/s would be slow and error-prone. Deterministic selection fits better.
- **Two cheap context tricks to copy:**
  - Copilot Autofix's "first ~10 lines of each involved file" corresponds to C# `using` directives and the namespace. It is cheap and stops the model inventing imports.
  - PR-Agent's "extend to the enclosing method/class, capped" is the right unit for C# snippets. Roslyn makes it exact: walk up to the enclosing `MethodDeclarationSyntax` or `TypeDeclarationSyntax`.

### Gaps
- No public evaluation of Aider's repo map, such as a recall@k or SWE-bench ablation, was found. The 2023 post has no numbers.
- Tabby's current retrieval internals (Tantivy BM25 plus vector RRF) are confirmed only by third-party pages.
- Amp's search internals are not documented, and whether it uses embeddings could not be confirmed.
- Continue's replacement design (agent tools plus MCP) was not fetched in detail. Qodo's own docs site (`qodo-merge-docs.qodo.ai`) now redirects to `docs.qodo.ai/code-review`, so PR-Agent repo docs were used instead.
- No benchmark comparing BM25, repo map and embeddings for C# specifically was found.

---

## Q2. How do AI review tools reduce false positives, and are there published precision/recall numbers?

### Takeaway
The tools that publish credible noise numbers ground the LLM in something deterministic: static-analysis findings (Copilot Autofix, Semgrep Assistant, the Tencent study, ZeroFalse), anchored lines (PR-Agent), or generated grep/ast-grep checks (CodeRabbit). They then add a second filtering pass: self-reflection scoring, a conservative verdict bias, or user "memories". Even frontier review tools reach only about 22–76% precision on real PRs, and the best measured F1 on real bug-fix PRs in an April 2026 study was 0.066. Review quality collapses as diff size grows. So for a ~1B model, deterministic grounding and verification are mandatory, and LLM self-scoring is weak.

### Cited Findings

**PR-Agent (open source; donated by Qodo to the community).**
- Self-reflection is a second call that sees all suggestions together:
  - It scores each one 0–10 with a reason and re-ranks them.
  - A score of 0 means incorrect, and the suggestion is removed.
  - `suggestions_score_threshold` (default 1) drops anything lower.
  - If reflection fails it tries the next model, reusing the original suggestions.

  [primary] — [PR-Agent self_reflection.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/core-abilities/self_reflection.md)
- If reflection fails entirely, every suggestion gets `score_on_reflection_failure` (default **7**) and the note "Self-reflection unavailable; score not model-assigned". A score that cannot be parsed means the suggestion is dropped. [primary] — [pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)
- Line anchoring with `_validate_suggestion`:
  - `existing_code` is compared with the lines at `relevant_lines_start..end` in the head file or patch, after `textwrap.dedent` and stripping trailing spaces and tabs. The comparison is exact line by line, with no fuzzy matching.
  - **No valid anchor:** the suggestion goes to a plain-text fallback comment with a `Location:` line.
  - **Text mismatch:** the suggestion is still published, but as a non-committable "Proposed code" block.

  [primary] — [pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)
- Other deterministic filters:
  - `python_replacement_compiles`: a Python fix that does not compile is made non-committable.
  - `MAX_CODE_SUGGESTION_LENGTH`: truncated code is made non-committable.
  - Deduplication by `one_sentence_summary` plus file, but only within one response, not across chunks.
  - A hard-coded drop of "use const instead of let" suggestions.
  - "critical" is relabelled to "possible issue" when `focus_only_on_problems` is set.
  - `max_suggestions_per_file` (default 0, off) keeps the top-scored suggestions per file.
  - `num_code_suggestions_per_chunk` (default 3) is enforced only through the prompt.

  [primary] — [pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)
- Truncation safety: "a truncated diff must never be treated as complete". `finish_reason == "length"` raises `FallbackEligibleError`. [primary] — [pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)
- No precision/recall numbers for PR-Agent itself were found. [search, no result] — [AI code review benchmark search results](https://composio.dev/blog/best-ai-powered-code-review-tools-in-2026)

**CodeRabbit.**
- Each suggestion is verified after generation for hallucination, accuracy and guideline conformance. Scripts generated in the sandbox test LLM comments, and low-value feedback is filtered out. 40+ linters and SAST tools run, and a verification agent reduces their noise. [vendor, Jul 2025] — [CodeRabbit: context engineering](https://coderabbit.ai/blog/context-engineering-ai-code-reviews)
- When a claim needs confirmation, it generates shell or Python checks (grep, ast-grep) "to test assumptions against the codebase before posting". Learnings from developer replies suppress future false positives. [vendor, Sep 2025] — [CodeRabbit on DEV](https://dev.to/coderabbitai/how-coderabbit-delivers-accurate-ai-code-reviews-on-massive-codebases-46eo)
- A separate "judge model" that scores each finding is described only by third parties. [third-party, snippet-only] — [The AI Engineer: How CodeRabbit actually works](https://theaiengineer.substack.com/p/how-coderabbit-actually-works)

**Kodus (open source under AGPLv3, self-hosted on Docker).**
- Hybrid design: a "deterministic, AST-based rule engine" gives the LLM precise, structured context. The post claims a "dramatically reduced noise rate" but gives no numbers, and names neither the parser nor the filtering mechanism. [vendor, Apr 2025] — [Kodus on DEV](https://dev.to/kodus/kodus-an-open-source-ai-code-review-engine-ast-and-llw-less-noise-3726)
- Kody Rules are plain-language review rules scoped to an org, repo or path. Findings carry a severity and a suggested fix. BYOK covers any OpenAI-compatible endpoint. The minimum self-hosted machine is 2 CPU, 8 GB RAM and 60 GB disk. [primary] — [kodus-ai README](https://raw.githubusercontent.com/kodustech/kodus-ai/main/README.md)
- No independent benchmark of Kodus exists. [third-party, snippet-only] — [aicoolies review](https://aicoolies.com/reviews/kodus-review)

**Semgrep Assistant (LLM triage of SAST findings).**
- Context for each finding: rule metadata, the rule's **confidence** value, earlier triage decisions for the same rule, examples, relevant Memories, several dozen lines around the finding, and extra lines at each dataflow step. [vendor, Jan 2025] — [Semgrep: AppSec AI that researchers agree with 96%](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)
- The verdict is binary ("likely TP" or "likely FP") with reasoning, deliberately **conservative** to minimize false negatives. A useful prompt idea was asking "what would someone triaging this issue consider?" [vendor] — [Semgrep blog](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)
- Benchmark and results:

  | Stage | Overall agreement | TP agreement | FP agreement |
  |---|---|---|---|
  | Initial | ~55% | 91% | 25% |
  | Final | — | 96% | 41% |

  - Dataset: more than 2,000 researcher-triaged findings, run through promptfoo.
  - gpt-4o was the best trade-off among the models tested.
  - Adding rule confidence helped: the model distrusts low-confidence rules.
  - Gains in false-positive detection often cost true-positive detection.

  [vendor] — [Semgrep blog](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)
- Later figures:
  - Customer audits show a 96% "human-agree" rate, and the average reduction in findings was 60% (Aug 2025). [snippet-only] — [Semgrep Assistant metrics](https://semgrep.dev/docs/semgrep-assistant/metrics)
  - About 60% of new SAST findings triaged so far in 2025. [snippet-only] — [Semgrep blog Sep 2025](https://semgrep.dev/blog/2025/semgrep-is-confidently-handling-60-of-all-triage-for-users-without-reducing-coverage)
  - At the Jan 2025 launch, triage work fell about 20% and up to 40% after a week. [snippet-only] — [Semgrep: noise filtering and memories](https://ai.semgrep.dev/blog/2025/announcing-ai-noise-filtering-and-triage-memories)

**GitHub Copilot Autofix (CodeQL + LLM).**
- Suggestions that "fail internal testing are not displayed". The evaluation harness (over 2,300 alerts from public repos that have tests) merges each fix unedited and checks four things: is the alert fixed, did new alerts appear, are there syntax errors code scanning can detect, and did test results change. The docs do not say whether every production fix gets all of these checks. [primary] — [GitHub Docs: responsible use](https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/responsible-use-autofix-code-scanning)
- At the 2024 launch, fixes covered more than 90% of alert types for JS, TS, Java and Python, and addressed more than two-thirds of found vulnerabilities "with little or no editing". [vendor, Mar 2024, updated Apr 2025, OUTDATED?] — [GitHub blog: Found means fixed](https://github.blog/news-insights/product-news/found-means-fixed-introducing-code-scanning-autofix-powered-by-github-copilot-and-codeql/)
- Current language list includes C#. Listed limitations: fixes may be non-deterministic, syntactically invalid, partial or semantically wrong, and may invent dependencies. [primary] — [GitHub Docs](https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/responsible-use-autofix-code-scanning)

**Research on LLM triage of static-analysis alerts.**
- **ZeroFalse (Oct 2025).**
  - Input: CodeQL SARIF alerts with the source-to-sink trace, enclosing methods and signatures, and annotations on each step (taint introduced, propagated, mitigation incomplete).
  - A **CWE micro-rubric of 10–20 declarative rules** per CWE.
  - A strict JSON schema with a High/Medium/Low confidence field, at temperature 0.
  - F1 on the OWASP Java Benchmark: grok-4 0.912, gpt-oss-20b (3.6B active) 0.884, mixtral-8x7b 0.217.
  - F1 on OpenVuln: gpt-5 0.955, gpt-oss-20b 0.904, qwen3-235b 0.793, deepseek-r1-distill-70b 0.655.
  - Conclusion: "Effective FP reduction depends on robust reasoning, not … parameter count".
  - The smallest model tested has 3.6B active parameters.

  [primary, academic] — [ZeroFalse, arXiv 2510.02534](https://arxiv.org/html/2510.02534v1)
- **Tencent industrial study (Jan 2026, ICSE 2026 SEIP).** 433 alarms (328 FP, 105 TP). Hybrid LLM plus static analysis removed **94–98% of false positives** while keeping high recall. Cost was $0.0011–$0.12 and 2.1–109.5 s per alarm, against 10–20 minutes of manual review. [primary abstract] — [arXiv 2601.18844](https://arxiv.org/abs/2601.18844v1)
- **"Bigger Isn't Always Better" (arXiv 2606.15689; the page says submitted 9 Apr 2026, which conflicts with the 2606 ID).**
  - Haiku 4.5 scored higher F1 than Sonnet 4.6 (0.365 vs 0.343).
  - The best F1 on **real** bug-fix PRs was **0.066**, against 0.847 on synthetic mutations.
  - F1 fell from **0.657 on diffs under 10 lines to 0.043 on diffs over 150 lines**.
  - Recall on performance bugs was near zero.

  [primary abstract] — [arXiv 2606.15689](https://arxiv.org/abs/2606.15689)

**Benchmarks of commercial review tools (mostly vendor-run; treat with care).**
- Martian Code Review Bench, offline track:
  - 50 PRs from Sentry, Grafana, Cal.com, Discourse and Keycloak, scored against human "golden comments" by an LLM judge.
  - The golden-comment count is reported as 136, 137 or 173 depending on the source.
  - One re-scoring blog reports cubic-v2 at P 56.8 / R 67.2 / F1 61.5 and Augment at P 47.4 / R 59.1 / F1 52.6.
  - The paper above lists Haiku 4.5 at P 32.6 / R 41.2 / F1 36.4.

  [snippet-only] — [ithome blog](https://ithelp.ithome.com.tw/articles/10404348); [Qodo glossary](https://www.qodo.ai/glossary/what-is-ai-code-review-benchmarks/); [arXiv 2606.15689](https://arxiv.org/pdf/2606.15689)
- The leaderboard page rendered empty when fetched. [primary, failed] — [codereview.withmartian.com](https://codereview.withmartian.com/)
- Vendor and third-party figures:
  - Augment's Dec 2025 test: Claude Code about 22% precision and 50% recall; CodeRabbit 35% and 45%. [vendor, snippet-only] — [Augment benchmark](https://augmentcode.com/blog/we-benchmarked-7-ai-code-review-tools-on-real-world-prs-here-are-the-results)
  - Greptile at P 66.2 / R 40.4 against Copilot at 56.5 / 36.7. [third-party, snippet-only] — [morphllm](https://www.morphllm.com/comparisons/greptile-vs-copilot)
  - CodeRabbit #1 at F1 60.8% / P 76.2% / R 50.6% on the 2026-07-30 leaderboard. [third-party, snippet-only; the tool name in the snippet was ambiguous] — [Composio](https://composio.dev/blog/best-ai-powered-code-review-tools-in-2026)

### Inferences
- **Small review units suit Jazari.** The diff-size finding (F1 0.657 under 10 lines, 0.043 over 150 lines) supports small units for a 1B model. Review one method or one hunk plus its enclosing member, not 2,000-token mixed parts. For whole-file parts, ask for at most 1–3 findings per unit (PR-Agent's default of 3 per chunk).
- **Jazari's exact-quote check is stricter than PR-Agent's anchor check**, which is a good thing. It should adopt PR-Agent's normalization so formatting does not cause spurious "unverified" results: dedent, strip trailing whitespace, and possibly collapse internal whitespace. When a quote matches more than once, disambiguate with the reported line number rather than discarding the finding.
- **Self-reflection is a weak filter at 1B scale.** ZeroFalse shows triage quality depends on reasoning, and the smallest model it tested has 3.6B active parameters. A ~1B non-reasoning model scoring its own findings is unproven. If Jazari tries it, use it to re-rank only, never to delete. Follow Semgrep's conservative bias of keeping anything uncertain.
- **Deterministic checks are cheap, CPU-only, and work at any model size:**
  - parse the suggested fix with Roslyn and mark it non-applicable if it does not parse (the C# version of PR-Agent's `python_replacement_compiles`)
  - flag cut-off generations (`finish_reason == length`, or JSON that does not parse) and re-split rather than accept a partial result
  - deduplicate findings on (file, normalized quoted line, problem summary)
  - drop a small blocklist of known-noisy suggestion types
  - cap findings per file
- **Static-analysis grounding is the biggest gap to close.** Copilot Autofix, Semgrep, CodeRabbit, the Tencent study and ZeroFalse all start from analyzer findings. For Jazari that is the earlier recommendation: run Roslyn analyzers, SecurityCodeScan, Semgrep or Opengrep, and gitleaks first, then give the LLM one finding at a time with (a) rule metadata and confidence, (b) the enclosing method, (c) the file's `using` lines, and (d) a small CWE rubric. This plays to the model's narrowest task: a binary or 3-way verdict in short JSON.
- **Feedback memory is cheap.** Store user dismissals as deterministic suppressions keyed by rule ID or pattern plus path glob, in the style of Semgrep Memories, CodeRabbit learnings and Kody Rules. This needs no LLM at all.

### Gaps
- No precision/recall numbers exist for PR-Agent, Kodus or Tabby, and none for any ~1B model on code review or SAST triage.
- The Martian leaderboard could not be read directly. Its numbers above are second-hand and conflict.
- CodeRabbit's judge model and verification internals are vendor or third-party descriptions, with no numbers.
- Copilot Autofix has no current numeric fix rate. The two-thirds figure is from 2024.

---

## Q3. How do they present review results, and how do they make reruns incremental?

### Takeaway
Hosted reviewers post inline PR comments, with a fallback to a summary comment when a finding cannot be anchored. They edit one persistent summary comment instead of adding new ones, and they track the state of each finding across runs (new, still open, resolved). Static-analysis-centred tools use SARIF as the interchange format. Incremental work happens at three levels: per-file parse caches keyed by mtime (Aider), per-repo index snapshots (Tabby), and finding-state tracking with earlier findings fed back to the model (PR-Agent, Kodus). Jazari has the first level (a content-hash cache per part) but not the third.

### Cited Findings
- **PR-Agent /review output:**
  - `persistent_comment` edits the previous review comment instead of posting a new one.
  - Labels: "Possible security concern" and "review effort [x/5]".
  - `key_issues_to_review` can be published inline (`inline_key_issues`), and findings that cannot be anchored stay in the summary.
  - `require_merge_recommendation` gives `no_concerns_found`, `needs_review` or `changes_required`.
  - `num_max_findings` caps the findings.

  [primary] — [PR-Agent review.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/tools/review.md)
- **PR-Agent incremental behaviour:**
  - `persistent_finding_state` (default true) tracks findings across complete review runs. Incremental or partial reviews **do not resolve** findings that are absent from them.
  - `max_previous_findings_chars` (default 8,000) budgets earlier findings that are fed back.
  - `enable_large_pr_chunking` (default false) works with `max_number_of_calls` (default 3).

  [primary] — [PR-Agent review.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/tools/review.md)
- **PR-Agent coverage footer:** reports `failed_chunk_count`, `remaining_files_list` (skipped because of the token budget or call limit) and `partial_files_list` (clipped patches). Code suggestions are committable inline, or non-committable "Proposed code" blocks when the anchor or syntax check fails. [primary] — [pr_code_suggestions.py](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/pr_agent/tools/pr_code_suggestions.py)
- **PR-Agent platforms and models:** GitHub, GitLab, Bitbucket, Azure DevOps and Gitea; any LiteLLM model, including Ollama. Releases from 0.34.2 on are published as `pragent/pr-agent`. [primary] — [PR-Agent README](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/README.md)
- **Kodus:** findings appear in PRs with severity and a suggested fix. "Kody Issues" track unimplemented suggestions and **auto-resolve when a later PR contains the fix**. There is a CLI (`kodus review`) for local and CI use. [primary] — [kodus-ai README](https://raw.githubusercontent.com/kodustech/kodus-ai/main/README.md)
- **CodeRabbit:** comments cite the definitions used, related earlier PRs, and linter rule plus line. The code graph is rebuilt on every review. Caching or indexing can be enabled to speed up repeat reviews. [vendor] — [CodeRabbit blog](https://coderabbit.ai/blog/context-engineering-ai-code-reviews); [CodeRabbit on DEV](https://dev.to/coderabbitai/how-coderabbit-delivers-accurate-ai-code-reviews-on-massive-codebases-46eo)
- **Copilot Autofix:** consumes CodeQL alerts as **SARIF**. Each fix is shown inline with a natural-language explanation and code preview (accept, edit or dismiss). Fixes may span several files and list dependencies to add. [primary] — [GitHub Docs](https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/responsible-use-autofix-code-scanning); [GitHub blog](https://github.blog/news-insights/product-news/found-means-fixed-introducing-code-scanning-autofix-powered-by-github-copilot-and-codeql/)
- **Semgrep Assistant:** decides whether a finding should interrupt the developer (a PR comment) or stay in the AppSec backlog, and tags likely false positives. [vendor] — [Semgrep blog](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)
- **Aider:** tags are cached by mtime. The rendered map is cached by (file, lines of interest, mtime). [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- **Tabby:** per-repository snapshots make rebuilds incremental. [snippet-only, third-party] — [pyshine](https://pyshine.com/tabby/)

### Inferences
- **Make finding state the main incremental feature of /review.**
  - Key each finding on (path, normalized quoted-line hash, rule or category).
  - On each rerun, label every finding new, still present, or resolved (the quoted line is gone or the part is now clean). This is the PR-Agent and Kodus model, and it costs nothing at inference time.
  - Keep PR-Agent's rule: a partial run never marks findings resolved.
- **Add a diff mode as a fast path:** `/review --changed`, or a default when inside a git repo. Review only changed hunks, each with its enclosing member, using PR-Agent's asymmetric context (about 8 lines before, about 4 after, or up to the enclosing method). Seconds to minutes instead of hours, and the diff-size finding says quality is better on small units.
- **Emit SARIF 2.1.0 next to the Markdown report.** It is a plain JSON file and fully offline. VS Code's SARIF viewer and GitHub code scanning can both import it (inferred; not verified this session). Use the same schema for static-analysis findings and LLM triage verdicts.
- **Add a coverage section to the Markdown report:** parts not reviewed (time limit, failed or truncated generations), parts served from the cache, files skipped as secrets. This is PR-Agent's coverage footer, and it fixes the silent-gap problem in long runs.

### Gaps
- CodeRabbit's incremental-commit review behaviour is not described in the sources fetched.
- How PR-Agent tracks "last reviewed commit" for an `-i` incremental review is not documented in the fetched page.
- SARIF consumption by VS Code and GitHub was not re-verified this session.

---

## Q4. How do they handle docs retrieval (official docs, version awareness)?

### Takeaway
Docs retrieval in 2025–2026 has moved to MCP servers backed by hosted indexes: Context7 (community library docs, version picked from the prompt) and the Microsoft Learn MCP server (semantic search plus fetch plus a separate code-sample search). Continue's crawled-and-embedded @Docs and Tabby's docs crawler are deprecated or beta. None of the sources found offers strict version gating or abstention like Jazari's law book. What Jazari can borrow: a separate code-sample index, search-then-fetch, and per-response token budgets.

### Cited Findings
- **Context7:**
  - Two MCP tools: `resolve-library-id` (library name to ID) and `query-docs` (library ID plus query, with an optional `language` that ranks examples in that language first).
  - The version is matched automatically when named in the prompt (e.g. "Next.js 16"). Library IDs such as `/vercel/next.js` carry no version segment.
  - Libraries are community-contributed.
  - The MCP server is MIT-licensed, but the "API backend, parsing engine, and crawling engine" are private, so it cannot be fully self-hosted or used offline.

  [primary] — [upstash/context7 README](https://raw.githubusercontent.com/upstash/context7/master/README.md)
- **Microsoft Learn MCP server:**
  - Remote endpoint `https://learn.microsoft.com/api/mcp`, no authentication.
  - Tools: `microsoft_docs_search` (semantic search of official docs), `microsoft_docs_fetch` (a page as Markdown) and `microsoft_code_sample_search` (official code snippets, optional `language` filter).
  - An experimental `maxTokenBudget` parameter (e.g. 2000) truncates search responses.
  - **No version or moniker parameter is documented.**

  [primary] — [MicrosoftDocs/mcp README](https://raw.githubusercontent.com/MicrosoftDocs/mcp/main/README.md)
- **Continue:** @Docs was deprecated along with @Codebase, and docs configuration moved to `config.yaml`. [snippet-only] — [Continue deprecated codebase/docs](https://docs.continue.dev/reference/deprecated-codebase)
- **Tabby:** "developer docs" is a context provider in Beta. [snippet-only] — [Tabby docs: context](https://tabby.tabbyml.com/docs/administration/context)
- **CodeRabbit:** when the model's knowledge may be stale, it runs a live web query for release notes or docs. The example corrected a Go version from 1.23.6 to 1.24.1. [vendor] — [CodeRabbit blog](https://coderabbit.ai/blog/context-engineering-ai-code-reviews)
- **Copilot Autofix:** includes the CodeQL **query help text** in the prompt. This is a form of docs retrieval keyed by rule ID, not by free-text search. [primary] — [GitHub Docs](https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/responsible-use-autofix-code-scanning)
- **Semgrep:** includes rule metadata, examples of what the rule should and should not catch, and the rule's confidence. [vendor] — [Semgrep blog](https://semgrep.dev/blog/2025/building-an-appsec-ai-that-security-researchers-agree-with-96-of-the-time/)

### Inferences
- **Jazari's law book is unusual: offline, BM25, strictly gated.** None of the projects above publishes abstention gates. The strictness is an advantage for a 1B model, which is easily misled by weak matches.
- **Ideas to borrow:**
  - **A separate code-sample index.** Index fenced code blocks from the .NET docs as their own FTS5 table with an API-name column. Route "how do I" questions there first. This is Microsoft's `microsoft_code_sample_search` split.
  - **Docs keyed by rule ID**, as Copilot Autofix and Semgrep do. When /review or the static pre-pass produces a finding with a rule ID (CA2100, SCS0002, a Semgrep rule ID), insert that rule's documentation page directly, with no search or gate needed. This is the cheapest high-precision docs retrieval available.
  - **Version from the project.** Context7 matches a version named in the prompt. Jazari can do better offline by reading `<TargetFramework>` from the `.csproj` and filtering law-book chunks by version metadata. Microsoft Learn URLs carry `?view=aspnetcore-N.0` style monikers that could be stored at indexing time (inferred from general knowledge; not verified this session).
  - **An explicit token budget per insertion** (like `maxTokenBudget`), so a docs chunk never crowds out code in an 8k context.
- **Do not add an online fallback by default** (Context7, the Microsoft Learn MCP server, a web query). It conflicts with the private and offline premise. At most, offer it opt-in as a law-book refresh source, not as per-query retrieval.

### Gaps
- How Microsoft Learn MCP handles versions is undocumented. Context7's index coverage of Microsoft .NET or ASP.NET Core docs was not checked.
- Embedded and hybrid docs retrieval was not compared with Jazari's BM25 gates on .NET docs; no such benchmark was found.

---

## Q5. Which ideas are cheap enough for a CPU laptop, and where do these projects clearly beat Jazari?

### Takeaway
The highest-value adoptions use no extra LLM calls at all:
- Aider-style graph ranking for /read
- PR-Agent-style diff and enclosing-member review units
- Static-analyzer-first review with the LLM limited to short verdicts
- Finding-state tracking across runs, plus a coverage report
- Roslyn parse checks on suggested fixes
- Rule-ID-keyed docs insertion
- Deterministic suppression memories

LLM-heavy techniques (self-reflection, LLM reranking, agentic search, generated verification scripts) are either unproven at ~1B active parameters or too slow at 10 tok/s.

### Cited Findings
- Aider's repo map is built entirely from tree-sitter, networkx PageRank and a disk cache, with no embeddings or LLM calls. [primary] — [aider/repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/repomap.py)
- Cody dropped embeddings in favour of BM25-style search, partly for cost, maintenance and privacy. [primary, 2024] — [Sourcegraph blog](https://sourcegraph.com/blog/how-cody-understands-your-codebase)
- Cline keeps no index and uses tree-sitter plus ripgrep. [snippet-only] — [Cline blog](https://cline.bot/blog/why-cline-doesnt-index-your-codebase-and-why-thats-a-good-thing)
- In the Tencent study, hybrid LLM plus static analysis cost 2.1–109.5 s per alarm and removed 94–98% of false positives. [primary abstract] — [arXiv 2601.18844](https://arxiv.org/abs/2601.18844v1)
- In ZeroFalse, the smallest model that worked well was gpt-oss-20b (3.6B active parameters, a reasoning model); mixtral-8x7b failed (F1 0.217). [primary] — [ZeroFalse](https://arxiv.org/html/2510.02534v1)
- Review F1 collapses on large diffs (0.657 for under 10 lines, 0.043 for over 150). [primary abstract] — [arXiv 2606.15689](https://arxiv.org/abs/2606.15689)
- Kodus's self-hosted minimum is 2 CPU, 8 GB RAM and 60 GB disk, plus an external or self-hosted LLM. It is a server stack, not a laptop CLI. [primary] — [kodus-ai README](https://raw.githubusercontent.com/kodustech/kodus-ai/main/README.md)

### Inferences

**Adoption list for Jazari.** Cost is on a CPU at ~10 tok/s; fit is for ~1B active, CPU-only, offline use.

| # | Technique | Source project | Fits 1B / CPU / offline? | Cost | Priority |
|---|---|---|---|---|---|
| 1 | Symbol graph plus personalized PageRank to choose /read files and snippets. Seed from identifiers in the question, files already read, and risk-prior files. Binary-search the 5k budget. | Aider | Yes / yes / yes | Parsing is ms per file; ranking is ms; 0 LLM tokens | High |
| 2 | Context unit = enclosing method or type plus the file's `using` lines, extended upward by preference (asymmetric before/after) | PR-Agent dynamic context; Copilot Autofix "first ~10 lines" | Yes | Roslyn syntax walk only | High |
| 3 | Diff mode for /review: only changed hunks, each with its enclosing member. Deleted-only hunks dropped. Largest files first within the budget, with an explicit "not reviewed" list. | PR-Agent compression | Yes | Minutes, not hours | High |
| 4 | Static pre-pass (Roslyn and NetAnalyzers, SecurityCodeScan, Semgrep or Opengrep, gitleaks); the LLM only gives a verdict per finding (TP / FP / unsure plus a one-line reason) using rule metadata and confidence, enclosing method, `using` lines and a 10–20-rule CWE rubric | Copilot Autofix, Semgrep Assistant, ZeroFalse, Tencent | Pre-pass yes. LLM verdict quality at 1B is unproven, so default to keep-if-unsure (Semgrep's conservative bias). | About 100–300 output tokens per finding, so roughly 10–30 s generation plus prompt processing | High (pre-pass); Medium (LLM triage) |
| 5 | Finding-state tracking across runs (new / still present / resolved), keyed on path, normalized-quote hash and category. Partial runs never resolve findings. | PR-Agent `persistent_finding_state`; Kodus Issues | Yes | Trivial | High |
| 6 | Coverage section in the report: failed or truncated parts, cache hits, skipped files | PR-Agent coverage footer | Yes | Trivial | High |
| 7 | Normalize before quote verification (dedent, trailing whitespace). Break ties on duplicate quotes with the reported line number. | PR-Agent `_validate_suggestion` | Yes | Trivial | High |
| 8 | Roslyn `ParseText` on the suggested fix and on code in answers; label a fix "not applicable" if it does not parse. Optionally do a full compile later. | PR-Agent `python_replacement_compiles`; Copilot Autofix harness | Yes | ms (parse); seconds (compile) | High |
| 9 | Truncation guard: if the generation hit the length limit or the JSON does not parse, split the part and retry; never accept partial output as complete | PR-Agent `FallbackEligibleError` | Yes | Trivial | High |
| 10 | Insert docs by rule ID (the CA / SCS / Semgrep rule doc page) next to a finding; no search or gate needed | Copilot Autofix query help; Semgrep rule metadata | Yes | One FTS lookup | Medium |
| 11 | Separate FTS5 table for docs code samples, plus `<TargetFramework>`-based version filtering | Microsoft Learn MCP split; Context7 version matching | Yes | Indexing-time only | Medium |
| 12 | Suppression memory: a user-dismissed finding becomes a rule (rule ID or pattern plus path glob) that filters future runs deterministically | Semgrep Memories; CodeRabbit learnings; Kody Rules | Yes | Trivial | Medium |
| 13 | Cap and deduplicate: at most 3 findings per unit, dedupe on (file, quote, summary), blocklist of noisy categories | PR-Agent | Yes | Trivial | Medium |
| 14 | SARIF 2.1.0 output next to the Markdown report | Copilot Autofix (SARIF input), CodeQL ecosystem | Yes | Small | Medium |
| 15 | BM25 index of C# declarations (camelCase-split names, signatures, XML doc comments) in the existing FTS5 engine, as a second retrieval source for /read and questions | Tabby 2023; Cody 2024 | Yes | Index once; ms queries | Medium |
| 16 | Self-reflection second pass scoring 0–10 | PR-Agent | Doubtful at 1B; re-rank only, never delete | Doubles generation time | Low |
| 17 | LLM reranking of 25 candidates down to 5 | Continue (legacy) | Poor: prompt-processing cost on CPU | High | Low / skip |
| 18 | Agentic search subagent, or LSP tool loop (Serena-style) driven by the model | Amp, Cline, Serena | Poor: needs many reliable tool calls. Roslyn can produce the same symbol data deterministically. | High | Low / skip |
| 19 | Embedding index (local) or hybrid RRF | Tabby (third-party report), CodeRabbit | Possible (small CPU embedders exist), but the 2024–2025 trend is away from it and it adds a second copy of the code | Medium | Low |
| 20 | Generated grep or ast-grep verification scripts per claim | CodeRabbit | Script generation by a 1B model is unreliable. A fixed, deterministic check (does the identifier exist? is the call reachable?) is feasible. | Varies | Low (LLM-generated); Medium (fixed checks) |

**Where these projects clearly beat Jazari today:**
1. **Relevance of /read selection.** Aider ranks by the symbols the question touches and the files already in play. Jazari ranks by filename risk only, which ignores the question.
2. **Review scope and speed.** PR-Agent and CodeRabbit review diffs with enclosing-member context in one pass. Jazari's first full-project walk takes hours, and evidence says quality is worse on big, mixed units.
3. **Grounding.** Copilot Autofix, Semgrep, CodeRabbit's 40+ linters and the Tencent hybrid results all start from deterministic analyzer findings. Jazari's /review asks the LLM to find issues unaided, which is exactly the weakest task for a 1B model.
4. **Lifecycle of findings.** PR-Agent and Kodus track findings across runs and mark them resolved. Semgrep, CodeRabbit and Kodus learn suppressions from users. Jazari caches results but has no finding state and no feedback memory.
5. **Honest coverage reporting.** PR-Agent reports failed, skipped and clipped chunks and refuses to treat truncated input as complete.
6. **Fix validity.** PR-Agent syntax-checks replacements, and Copilot Autofix tests fixes by re-running CodeQL and the test suite. Jazari verifies the quoted line but not the proposed fix.

**Where Jazari is already equal or ahead:**
- Exact-once quote verification is stricter than PR-Agent's anchored-range check.
- Content-hash caching per part survives file moves and touches, unlike mtime caching (Aider), and is incremental, unlike CodeRabbit's rebuild on every review.
- The law book's version and term-coverage gates with abstention have no counterpart in Context7, Microsoft Learn MCP or Continue.
- Secret-path exclusion matches tools that respect `.gitignore` and `.continueignore`.
- Everything runs offline. Context7's backend and Microsoft Learn MCP are hosted, and Kodus needs a server stack.

### Gaps
- No source measures any of these techniques with a ~1B-active model, so every 1B fit judgment above is inferred. Jazari's own eval harness (`eval/`) would be needed to confirm items 4 and 16 in particular.
- CPU prompt-processing throughput for Granite 4.0-H Tiny on the target laptop was not measured here. The cost estimates in the table count generation time only.
- No published comparison of Roslyn syntax-only versus tree-sitter for C# symbol extraction speed or accuracy was found. Both are expected to be fast, but this is not verified.
