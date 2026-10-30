# Whole-repo review with a 16k-context local model at 30 tok/s on CPU

Evidence labels used throughout:
- **[M]** measured: a benchmark in a paper, or a measurement on this laptop.
- **[C]** claimed: an author or vendor statement with no independent check.
- **[V]** vendor comparison: a measurement run by a party that sells the winner.
- **[2nd]** read through a secondary summary, not the primary source.

Research date: 2026-10-30. Several 2026 arXiv items are preprints.

## Sizing: what the "46 hours" really is (measured on this laptop today)

### Takeaway
EduCore's 46-hour figure is almost entirely non-code. Its hand-written C# is about 363k tokens, or 3.4 hours of reading at 30 tok/s, so a full read fits in one night. The tight limit is **answer speed (6.2 tok/s)**, not reading speed.

### Cited Findings
- [M] Size scan of `C:\Users\enim\EduCore` on 2026-10-30, using file sizes only at 4 bytes per token. No file contents were opened. bin, obj, node_modules, .git and .vs were excluded.

| Part | Files | Approx. tokens |
|---|---|---|
| All matched text files | 721 | 5,672k |
| `.sql` | 4 | 3,909k |
| `.xml` | 12 | 674k |
| `.cs` (all) | 696 | 992k |
| of which under `Migrations\` (any file type matched) | 65 | 629k |
| **`.cs` outside Migrations, not `.Designer.cs` or `.g.cs`** | **631** | **363k (3.4 h at 30 tok/s)** |
| Hot-path file names (Controller, Auth, Middleware, Program, Startup, Payment, Upload, Security, Identity) | 23 | 25k (about 14 min) |

- [M] Size distribution of the 631 C# files: p50 189 tokens, p90 1,530, p99 4,224, max 12,187. Seven files exceed 4k tokens and none exceed 13k. So nearly every file fits a 16k window whole, and most are tiny.
- [M] Granite 4.1 3B Q4_K_M on CPU reads at 28.8 tok/s and answers at 6.2 tok/s — [docs/OPTIMIZATION-LOG.md](file:///C:/MyOwnLLM/docs/OPTIMIZATION-LOG.md).
- [M] EduCoreFront is about 233k tokens, or about 2.2 h — same log.

### Inferences
- The 200k-5M range in the brief should be restated as 200k-1M after skipping generated files and data dumps. The four `.sql` files are likely dumps or seed data, but I did not open them. They should go to gitleaks and a pattern scan, not the model.
- Answer tokens cost about 5 times read tokens. One call per file with a 120-token answer costs 631 × 19 s = 3.4 h, the same as reading everything. So **batch small files into units of 6k-10k tokens**, and let a clean unit answer `{"findings":[]}` in a few tokens.
- One night of 8 h gives about 860k read tokens with no answers, or about 700k with answers (about 150 tokens per 4k-token unit).

### Gaps
- 4 bytes per token is an estimate. Real tokens for C# need checking with the model's own tokenizer through `llama-server /tokenize`.

## How do real tools handle big repos?

### Takeaway
Nobody feeds a whole repo to a model. Tools rank (Aider, PR-Agent), retrieve (Cody, Roo, Greptile), or let the agent search on demand (Cline, Continue). The security-focused ones then add a filter stage after detection. Aider's ranked signature map and CodeRabbit's "linters plus verification" are the parts worth copying.

### Cited Findings
| Tool | Unit and context | Ranking | Cache and index | Source |
|---|---|---|---|---|
| Aider | tree-sitter def and ref tags; shows definition lines only, each cut to 100 characters | PageRank on a file graph; edge weight ×10 for mentioned identifiers, ×10 for long names, ×0.1 for `_private`, ×0.1 if defined in over 5 files, ×50 from chat files, times sqrt(ref count) | diskcache keyed by path, valid only if mtime matches; binary search fits the map to the budget, default 1k tokens, ×8 when no files are in chat | [repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/master/aider/repomap.py) [M: read from source] |
| PR-Agent (Qodo) | Diff hunks plus 3 lines of context | Orders files by how common the language is in the repo, then largest first; drops deletion-only hunks; leftovers become an "other modified files" list | None; token-counted packing against a limit | [compression_strategy.md](https://raw.githubusercontent.com/qodo-ai/pr-agent/main/docs/docs/core-abilities/compression_strategy.md) |
| Cline | Whole files, on demand | ripgrep and tree-sitter on demand; argues chunking breaks logic and embeddings are a second copy of your IP | No index | [Cline blog](https://cline.bot/blog/why-cline-doesnt-index-your-codebase-and-why-thats-a-good-thing) [C] |
| Continue | Built-in read and search tools, rules files, MCP | Agent-driven | `@Codebase` is deprecated | [Continue docs](https://docs.continue.dev/guides/codebase-documentation-awareness) |
| Roo Code | Tree-sitter blocks of 100-1,000 characters; line fallback | Vector search | Qdrant plus an embedding provider; experimental | [Roo docs](https://docs.roocode.com/features/codebase-indexing) |
| Sourcegraph Cody | Snippets | Adapted BM25 plus learned signals; one global ranking across providers | Sourcegraph search index; older docs also describe embeddings | [Sourcegraph blog](https://sourcegraph.com/blog/how-cody-understands-your-codebase) [C] |
| Greptile | Every function, class and file, with call relations | Graph query | Whole-repo graph, updated incrementally per [2nd] summaries | [Greptile docs](https://www.greptile.com/docs/how-greptile-works/graph-based-codebase-context) [C] |
| CodeRabbit | PR plus layers: code graph, linters and static analyzers, verification scripts; claims a 1:1 code-to-context ratio | Dependency graph rebuilt per review | LanceDB for PR and issue memory | [CodeRabbit blog](https://coderabbit.ai/blog/context-engineering-ai-code-reviews) [C]; [LanceDB](https://lancedb.com/blog/case-study-coderabbit) [C] |
| Claude Code security review | PR diff plus file search | 3 phases: learn project patterns, compare, assess; report only above 80% confidence | Stage 1 regex hard exclusions, stage 2 per-finding LLM re-check | [prompts.py](https://raw.githubusercontent.com/anthropics/claude-code-security-review/main/claudecode/prompts.py); [findings_filter.py](https://raw.githubusercontent.com/anthropics/claude-code-security-review/main/claudecode/findings_filter.py) |
| Vulnhuntr | Per-file pass, then LLM requests related symbols to build a call chain from input to output | README summary in the system prompt | Python only; AGPL-3.0; authors report no success getting structured output from open models through Ollama | [Vulnhuntr](https://github.com/protectai/vulnhuntr) [C] |
| OpenAnt | Function units with dependencies inlined to a set depth (3 levels per a [2nd] write-up) | Reachability filter first, no LLM; then detection, then adversarial attacker pass, then dynamic exploit check | Apache 2.0; claims up to 97% surface cut, e.g. OpenSSL 15,232 functions to 390 units [2nd] | [OpenAnt paper](https://arxiv.org/pdf/2606.19149) [C]; [secrss write-up](https://www.secrss.com/articles/91627) |

- Accuracy claims conflict and should not guide design. CodeRabbit and Greptile figures vary widely across comparison posts, and one source flags Greptile's 82% catch rate as much lower on an independent leaderboard — [Morph comparison](https://www.morphllm.com/comparisons/coderabbit-vs-greptile) [2nd].
- The Claude Code action states it is not hardened against prompt injection and should review only trusted PRs — [README](https://github.com/anthropics/claude-code-security-review).

### Inferences
- Copy from Aider: a ranked, signature-only map. Personalize PageRank to entry points and sinks rather than chat files.
- Copy from Claude Code security review: a repo-context card before file review, hard exclusions, and a separate per-finding re-check.
- Copy from OpenAnt: reachability filtering and inlined dependencies.
- Skip embeddings for a first version. They add a second copy of the code and a dependency, and review order comes from risk ranking, not query similarity.
- Use a content hash rather than Aider's mtime, because a git checkout changes mtime without changing content.

### Gaps
- No tool publishes a chunking or ranking evaluation on a 16k window. Greptile's chunking is undocumented in what I found.

## Map-reduce and hierarchical summarization

### Takeaway
It raises coverage, but evidence is thin and mostly qualitative. It fails on cross-file behaviour, and that weakness is measured directly in authorization review. Summaries should be used for orientation only, never as proof that a check exists or is missing.

### Cited Findings
- [C] Syntax-guided hierarchical summaries (function to file to package) with local LLMs improved coverage, and grounding them in the business domain improved relevance, on one telecom system. The abstract gives no numbers or model names — [Dhulshette et al.](https://arxiv.org/abs/2501.07857v1).
- [C] Agent4cs reports an 8% gain in semantic similarity for folder summaries over structured-prompt baselines, and says cross-file dependencies are scattered and underused — [Agent4cs](https://www.alphaxiv.org/abs/2607.01425). This is a preprint and I did not verify it against the full text.
- [M] In Semgrep's IDOR study, Claude found 11 of 16 cases with no authorization and 3 of 6 with protection in one function or file, but **0 of 19** with checks spread across files and **0 of 18** with authorization applied implicitly by middleware or framework. The reason given is that reasoning stayed within one function or file — [Semgrep](https://semgrep.dev/blog/2025/can-llms-detect-idors-understanding-the-boundaries-of-ai-reasoning).
- [M] VulnGym, on hard tasks, attributes 56.5% of 347 failures to never reaching the vulnerable file or failing line-level localization (26.8% no endpoint file, 29.7% endpoint file only) — [VulnGym](https://arxiv.org/html/2608.02001).
- [C] VulnAgent-R2 argues that isolated function classifiers are fragile because bugs depend on cross-file flow and runtime guards — [papers summary](https://awesomepapers.io/papers/2603.13384) [2nd].

### Inferences
- The failure mode is specific: a summary of a file loses the fact that a guard exists elsewhere. Facts about guards must come from static analysis and travel with each unit as text.
- Summaries lose details that matter to security, such as one missing `await` or one wrong operator. So the model must read actual code for review, and use summaries only for "what is this module".

### Gaps
- I found no paper measuring vulnerability recall with versus without hierarchical summaries, and none using a model near 3B parameters.

## Deterministic first pass

### Takeaway
Run analyzers before any model. Use Roslyn NetAnalyzers, Opengrep with a small custom rule pack, gitleaks and osv-scanner. All have free, offline-capable Windows paths. CodeQL is ruled out for private code by its licence.

### Cited Findings
| Tool | Licence | Windows size | Offline? | Notes |
|---|---|---|---|---|
| NetAnalyzers (.NET SDK) | Open source repo | In SDK | Yes | CA3001 is taint-based for SQL sinks but disabled by default — [source](https://source.dot.net/Microsoft.CodeAnalysis.NetAnalyzers/Microsoft.NetCore.Analyzers/Security/ReviewCodeForSqlInjectionVulnerabilities.cs.html) |
| SecurityCodeScan `security-scan` tool | Not checked | Not checked | Likely | v5.6.7, last updated 2022-09-05, targets net5.0 and netcoreapp3.1 — [NuGet](https://www.nuget.org/packages/security-scan). Effectively stale. |
| Opengrep | LGPL-2.1 | exe 53.7 MB, v1.33.1, 2026-10-29 | Local rules | `--taint-intrafile`, inter-method taint in one file, C# 12-14 guide, runs existing Semgrep rules — [repo](https://github.com/opengrep/opengrep); [release](https://api.github.com/repos/opengrep/opengrep/releases/latest) |
| Semgrep CE | Engine open source; rules under a separate licence | Python install | Local rules | Native Windows since Nov 2025, docs still say beta — [release notes](https://ai.semgrep.dev/blog/2025/semgrep-community-edition-fall-release-2025). C#: single-file constant propagation, single-function taint; framework Pro rules may return nothing on CE — [C# docs](https://semgrep.dev/docs/languages/csharp) |
| gitleaks | MIT | zip 8.4 MB, v8.30.1 | Built-in config | [repo](https://github.com/gitleaks/gitleaks); [release](https://api.github.com/repos/gitleaks/gitleaks/releases/latest) |
| osv-scanner | Not checked | exe 59.0 MB, v2.7.0 | Yes, after one DB download | Reads `packages.lock.json`, `packages.config`, `deps.json`, `package-lock.json`, `yarn.lock`, `pnpm-lock.yaml` — [lockfiles](https://google.github.io/osv-scanner/supported-languages-and-lockfiles/); [offline](https://google.github.io/osv-scanner/usage/offline-mode/); [release](https://api.github.com/repos/google/osv-scanner/releases/latest) |
| CodeQL | Proprietary free | Not checked | N/A | Terms bar running it on code not under an OSI licence, except academic research or demonstration — [LICENSE](https://github.com/github/codeql-cli-binaries/blob/9c56568817722bcf4eccc8e04a358bbd5e29a0ff/LICENSE.md). **Prior art only.** |

- Semgrep rules are under the Semgrep Rules License v1.0: internal use is allowed, but it does not allow distributing the rules or offering them as a service — [licence](https://semgrep.dev/legal/rules-license).
- `opengrep-rules` was archived on 2025-11-28, is based on rules from December 2024, and is described as for research, testing and benchmarking — [repo](https://github.com/opengrep/opengrep-rules).
- Commands confirmed by the docs read:
  - `dotnet build ... -p:ErrorLog=out.sarif%2cversion=2.1` — [Meziantou](https://www.meziantou.net/how-to-output-a-sarif-file-from-a-dotnet-project.htm)
  - `dotnet tool install --global security-scan --version 5.6.7` — [NuGet](https://www.nuget.org/packages/security-scan)
  - `gitleaks dir -v <path>`, `gitleaks git -v <repo>`, built-in sarif format, `--redact` (default 100%), `--baseline-path`, `.gitleaksignore` — [repo](https://github.com/gitleaks/gitleaks)
  - `osv-scanner --offline-vulnerabilities --download-offline-databases <dir>` once, then `osv-scanner --offline <dir>` — [offline docs](https://google.github.io/osv-scanner/usage/offline-mode/)
  - `irm https://raw.githubusercontent.com/opengrep/opengrep/main/install.ps1 | iex`, or download the exe directly — [repo](https://github.com/opengrep/opengrep)
- `dotnet list package --vulnerable` needs `--include-transitive` to see transitive packages, and audit sources do not feed this command — [NuGet audit docs](https://learn.microsoft.com/en-us/NuGet/concepts/auditing-packages).

**Evidence that LLM triage helps:**
- [M] ZeroFalse on CodeQL alerts, with the SARIF flow trace, code slices and a CWE micro-rubric of 10-20 rules: F1 0.912 (grok-4) on 1,974 OWASP cases and 0.955 (gpt-5) on 58 real alerts. Open-weight gpt-oss-20b scored 0.884 and 0.904. Mixtral 8x7b scored 0.217 on OWASP with recall 0.135. Gemini fell from 0.910 to 0.372 between the two sets, which suggests fitting to synthetic benchmarks. Java only — [ZeroFalse](https://arxiv.org/html/2510.02534v1). The model list uses non-standard names, so check them against the PDF.
- [M] IRIS on 120 Java CVEs: CodeQL found 27, IRIS with GPT-4 found 55, with F1 0.177 against 0.076. Smaller models found many but with higher false discovery: Llama 3 8B 41 at 95.55% FDR, DeepSeekCoder 7B 52 at 95.40%, against 90.03% for CodeQL. **Contextual filtering helped larger models but hurt the smaller ones, which lean towards saying "vulnerable"** — [IRIS](https://arxiv.org/html/2405.17238v2).
- [M] Sifting the Noise: agents cut a false-positive rate above 92% to as low as 6.3% on OWASP in the best configuration, strongly dependent on model and CWE — [abstract](https://arxiv.org/abs/2601.22952v1). One unverified third-party entry says real Vul4J vulnerabilities were suppressed — [promptfoo](https://promptfoo.dev/lm-security-db/vuln/llm-agent-false-negative-f1372ad9/).
- [M] Memoir on 865 CodeQL alerts reports FP-F1 0.9882 against ZeroFalse 0.8919 and IRIS 0.5436, **but TP recall is only 0.5, so half of true vulnerabilities were suppressed** — [Memoir](https://arxiv.org/html/2608.09181). Its evolution section uses hypothetical wording in places.
- [C] Datadog says its LLM filter is useful but publishes no numbers, and notes that prompts tuned for true positives misclassify false positives and vice versa — [Datadog](https://www.datadoghq.com/blog/using-llms-to-filter-out-false-positives.md).
- [M, 2nd] ChatGPT static-analysis triage reached up to 93.88% precision for null dereference and 63.33% for resource leaks — [AIware 2024](https://www.eecs.yorku.ca/~wangsong/papers/aiware24.pdf).
- LLM4SA has a naming conflict. Search shows it as Li et al., FSE 2023 ([paper](https://www.cs.ucr.edu/~zhiyunq/pub/fseivr23_static_chatgpt.pdf)), while a ZeroFalse summary attributes it to Wen et al., 2024.

### Inferences
Recommended order, which should finish in minutes, not hours (commands marked * need a test run here):
1. `dotnet restore`, then `dotnet build X.sln --disable-build-servers -p:ErrorLog=roslyn.sarif%2cversion=2.1 -p:AnalysisMode=All`*, with CA3001-style taint rules set to warning in a generated `.editorconfig`*. The `--disable-build-servers` flag comes from the Traps section of the optimization log.
2. `opengrep scan --taint-intrafile --sarif -o og.sarif --config rules\ .`*
3. `gitleaks dir --redact -f sarif -r gl.sarif .`* plus `gitleaks git` for history. Scan the `.sql` and `.xml` files here too.
4. `osv-scanner --offline .` against `packages.lock.json` or `*.deps.json` and `package-lock.json`.

Further points:
- Treat `dotnet list package --vulnerable` and `npm audit` as optional online extras. They send package names to a registry, though no source code. osv-scanner covers the same need offline, after a public database download.
- Do not ship Semgrep's rules inside Jazari. Have the user fetch them, or write a pack of 20-30 of your own ASP.NET Core rules. Semgrep itself recommends codifying access-control patterns as deterministic rules.
- Feed each alert in ZeroFalse's format: rule, CWE, message, trace, slice, micro-rubric, JSON verdict. Give ±5 lines plus the enclosing function for source and sink, as IRIS does.
- **Given the IRIS result, do not let a 3B model drop anything silently.** Its verdict should only sort the report. Rejected alerts stay in a visible section.

### Gaps
- No measured data for LLM triage on C#, TypeScript, or any model around 3B. All strong results are Java and mostly CodeQL.
- Whether `security-scan` runs on .NET 10 solutions is unknown. A trial is needed.
- `AnalysisMode=All`, the exact Opengrep CLI flags, and whether `dotnet list package` fails or warns offline were not confirmed in sources read.
- No source found for which CA rules beyond CA3001 use taint analysis, or for Semgrep CE's and CodeQL's install size.
- Whether Opengrep taint crosses files is not stated.

## Risk ranking and pulling in data flow

### Takeaway
Rank by reachability from HTTP entry points, sink presence and analyzer hits. Use Roslyn to attach the path around each sink, and skip generated code by rule. On EduCore, names alone isolate about 25k tokens of hot paths, which can be read in 14 minutes.

### Cited Findings
- [C] OpenAnt puts reachability filtering before any LLM and reports up to a 97% surface cut — [OpenAnt](https://arxiv.org/pdf/2606.19149).
- [M] VulnGym: the most common hard failures are never finding the right file, so ranking is a recall issue, not only a speed issue — [VulnGym](https://arxiv.org/html/2608.02001).
- Roslyn provides `SymbolFinder.FindCallersAsync` for caller lookup — [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.findsymbols.symbolfinder.findcallersasync?view=roslyn-dotnet-5.3.0).
- OWASP's .NET guidance names the sinks and guards to look for: `FromSqlRaw`, `BinaryFormatter`, `IgnoreAntiforgeryToken`, `IAuthorizationService.AuthorizeAsync`, `[AllowAnonymous]`, `LocalRedirect`, `Url.IsLocalUrl`. It also warns that `[Authorize]` and the fallback policy do not perform resource-level checks — [OWASP cheat sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html).
- [M] EduCore's Migrations folder holds 629k tokens — size scan above.

### Inferences
Suggested score (all weights need tuning on own repos):
```
score = 10*is_entrypoint + 8*has_sink + 6*analyzer_hits(capped 3)
      + 5*name_match(auth|login|token|payment|invoice|upload|tenant|role|password|admin)
      + 4*reachable_from_entrypoint(depth<=3) + 3*personalized_pagerank_pct
      + 2*recent_churn(git log) - 8*test_file
```
- **Sinks for C#:** `FromSqlRaw`, `ExecuteSqlRaw`, `SqlCommand.CommandText`, `Process.Start`, `File.*`/`Path.Combine` with request data, `IFormFile`, `HttpClient` with a request URL (SSRF), `Redirect`, `Html.Raw`, `BinaryFormatter`, `TypeNameHandling`, `JsonSerializerSettings`, JWT validation parameters, CORS setup.
- **Sinks for TS/React:** `dangerouslySetInnerHTML`, `eval`, `innerHTML`, `window.location =`, `localStorage` holding tokens, `postMessage` handlers. Add route guards, as client checks prove nothing without server checks.
- **Data-flow pull:** from a sink, walk callers to the entry point to depth 3 with `FindCallersAsync`; for each hop, include the full body of the two nearest methods and signatures only for the rest. Stop when the unit reaches 10k tokens.
- **Skip list:** `Migrations\**`, `*.Designer.cs`, `*ModelSnapshot.cs`, `*.g.cs`, `*.g.i.cs`, files with an `<auto-generated>` header or `[GeneratedCode]`, `bin`, `obj`, `node_modules`, `dist`, `wwwroot\lib`, `*.min.js`, lock files, `*.xml` docs, `.sql` dumps over 50k tokens.
- **Do not fully ignore skipped files:** run gitleaks on all of them, and grep Migrations for `migrationBuilder.Sql(` and for seed data.
- **Tiny DTOs and entities (p50 189 tokens):** keep them as signatures only. The model can reference them through the map instead of reading them.

### Gaps
- No published evidence for specific risk weights. Measure recall per hour on seeded bugs.
- Performance of `FindCallersAsync` across a solution of this size is unmeasured here.

## Per-file prompt format and hallucination control

### Takeaway
Use one narrow checklist per unit, a grammar-forced JSON output, and a required verbatim quote that code checks by string match. Confidence should be computed by the tool from checks passed, not trusted from the model.

### Cited Findings
- [M] ZeroFalse's prompt has five segments: role and JSON-only guardrails, scope and evidence constraints, CWE micro-rubric, interpretation checklist, strict schema. Temperature 0, confidence High/Medium/Low — [ZeroFalse](https://arxiv.org/html/2510.02534v1).
- [M] IRIS has the model write an explanation before the verdict, and on a false verdict say whether the source or sink is wrong so related paths can be pruned — [IRIS](https://arxiv.org/html/2405.17238v2).
- Claude Code's schema fields are `file`, `line`, `severity`, `category`, `description`, `exploit_scenario`, `recommendation`, `confidence`. Its hard filters drop DoS, rate limiting, resource leaks, open redirect, regex DoS, and memory-safety claims on non-C/C++ files. Open redirect matters for ASP.NET, so review that exclusion rather than copying it — [prompts.py](https://raw.githubusercontent.com/anthropics/claude-code-security-review/main/claudecode/prompts.py); [findings_filter.py](https://raw.githubusercontent.com/anthropics/claude-code-security-review/main/claudecode/findings_filter.py).
- llama.cpp grammar facts: `json_schema` on completions and `response_format` on chat; **the schema is not injected into the prompt**, so describe it yourself; `additionalProperties` defaults to false, which is faster with fewer hallucinations; many `x?` repeats are slow, use `x{0,N}`; nested `$ref` is broken; `properties` cannot mix with `anyOf` in one type; unsupported features are skipped silently — [grammars README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/grammars/README.md).
- [M] HalluJudge checks review comments for grounding without a reference, reaching precision, recall and F1 of 0.85; in production 67% of its judgments matched developer preference — [HalluJudge](https://arxiv.org/abs/2601.19072v1) [2nd].
- [M] Overcorrection: LLMs often flag correct code as failing — [arXiv 2603.00539](https://arxiv.org/pdf/2603.00539). AEGIS proposes a verifier and an independent audit agent with veto over claims, and OpenAnt an adversarial attacker pass — [AEGIS](https://papers.cool/arxiv/2603.20637) [C]; [OpenAnt](https://arxiv.org/pdf/2606.19149) [C].
- [M] Non-determinism: one app gave 3, then 6, then 11 distinct findings from the same prompt — [Semgrep summary](https://semgrep.dev/blog/2025/can-llms-detect-idors-understanding-the-boundaries-of-ai-reasoning) [2nd]. Security-review hallucination and run-to-run consistency are also studied in [Yu et al.](https://arxiv.org/pdf/2401.16310), but I could not extract its numbers.

### Inferences
**16k budget per call:** system and rules 800, repo card 600, micro-checklist 600, facts block 1,000, code up to 10,000, output cap 600, with 3,000 spare for token-count error.

**Checklist:** pick by unit type, 6-10 lines each, never the whole OWASP list.
- Controller: A01 access control including resource ownership, mass assignment, CSRF, redirects.
- Data layer: raw SQL, tenant filter, secrets.
- Upload: extension and content checks, path traversal, size.
- Auth: token validation, password hashing, cookies.
- Program.cs: fallback policy, middleware order, CORS, HTTPS.
- React: XSS sinks, token storage.

**Schema:**
```json
{"type":"object","required":["findings"],"properties":{"findings":{"type":"array","maxItems":3,
 "items":{"type":"object",
  "required":["quote","why","cwe","severity","needs_context"],
  "properties":{
   "quote":{"type":"string","maxLength":200},
   "why":{"type":"string","maxLength":240},
   "cwe":{"type":"string","pattern":"^CWE-[0-9]{1,4}$"},
   "severity":{"enum":["high","medium","low"]},
   "needs_context":{"type":"string","maxLength":80}}}}}}
```
`quote` and `why` come before the verdict fields, following IRIS. `maxLength` support needs testing. The schema must also be described in the prompt text.

**Gates, all done by code, in order:**
1. **Quote gate:** after normalizing whitespace, the quote must exist in the unit. If not, drop it to the rejected list. Compute line numbers from the match, never from the model.
2. **Symbol gate:** any identifier the finding relies on must exist in the Roslyn or tree-sitter index.
3. **Absence gate:** "missing authorization" or "missing validation" findings are compared with the facts block. If an effective guard is listed, reject.
4. **Exclusion gate:** drop DoS and rate-limit style noise at a chosen level.
5. **Re-check call:** new prompt with only that finding plus the data path, asking the opposite question: "List what makes this safe. Then state whether it stays reachable." Keep `why` out of it so the model does not just agree.

**Tool-computed confidence:**
- High: quote passes and a static tool hit is on the same lines, or the re-check confirms with the path shown.
- Medium: quote passes and the re-check confirms.
- Low: quote passes but the re-check is unsure.
Low-confidence findings are shown as questions to look at, not as findings.

Other rules:
- Make "no findings" an explicit and acceptable answer in the prompt.
- Allow at most 3 findings per unit.
- Wrap code in clear delimiters and state that comments in it are data. Local review with no tools limits what prompt injection can do.
- For high-value units only, run twice with a different unit split, because of the non-determinism above.

### Gaps
- No study found tests quote-matching or repeated sampling for small-model vulnerability review. The gates are engineering judgment supported by the general hallucination work, and they check grounding, not correctness.

## Business-flow review

### Takeaway
This is the weakest area. Even frontier models scored 0 of 37 on cross-file authorization. A small model cannot derive flows, but it can answer narrow questions about flow facts that code has already extracted. The endpoint table alone is valuable with no model at all.

### Cited Findings
- [M] Semgrep's numbers are above. Intentionally public data was often flagged, and models kept to one function or file — [Semgrep](https://semgrep.dev/blog/2025/can-llms-detect-idors-understanding-the-boundaries-of-ai-reasoning).
- [M] Claude Code TP rate on IDOR: 13 of 59 (22%), so 78% false positives. Another Semgrep post gives 88% false positives, which conflicts — [AI-powered detection post](https://ai.semgrep.dev/blog/2025/ai-powered-detection-with-semgrep).
- [V, 2nd] Hybrid detection claimed recall 59.9% and F1 57.1% on 275 labelled IDORs, against Mythos at 13.9% recall and Codex Security at 11.3% — [plushcap copy](https://www.plushcap.com/content/semgrep/blog/semgrep-semgrep-multimodal-goes-beyond-authentication-what-we-learned-from-comparing-it-with-mythos). Vendor result, but it supports starting from routes plus deterministic analysis.
- [M] VulnGym: 2B and 4B models scored zero on Medium; Qwen3.5-9B reached 6.36% advisory recall on Easy, 3.23% on Medium, 0 on Hard; Qwen3.5-27B 12.90% on Hard; best frontier agent 22.58% on Hard. Even given oracle endpoints, traces were "incomplete and noisy" (best 36.37%) — [VulnGym](https://arxiv.org/html/2608.02001).
- [2nd] A blog says 71.2% of VulnGym advisories are business-logic flaws — [Vaughan](https://codex.danielvaughan.com/2026/08/07/vulngym-repository-level-vulnerability-detection-coding-agents-codex-cli-security-scanning-business-logic-flaws/). I could not confirm that in the paper. The blog also says 38 projects while the abstract says 23 repositories.
- OWASP notes that authentication does not imply permission, and that `IAuthorizationService.AuthorizeAsync` is needed for resource-level checks — [OWASP](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html).
- Business-context grounding improved summary relevance in one local-LLM study — [Dhulshette et al.](https://arxiv.org/abs/2501.07857v1) [C].

### Inferences
**Stage A: Roslyn extracts one entry-point card per endpoint, no LLM:**
```
POST /api/invoices/{id}/pay  InvoicesController.Pay
EFFECTIVE AUTH: fallback=authenticated; class=[Authorize(Roles="Staff")]; method=none
RESOURCE CHECK: none found (no AuthorizeAsync; no tenant/owner filter in query)
INPUT: id (route), PayDto{Amount, Method}   <- client-supplied Amount
CALLS: InvoiceService.Pay > PaymentGateway.Charge > ctx.SaveChanges
WRITES: Invoice.Status, Payment
EXTERNAL: PaymentGateway  TRANSACTION: no
```
`EFFECTIVE AUTH` must walk global filters, base classes, minimal-API `RequireAuthorization`, fallback policy and EF query filters. This handles the two categories that scored zero, because the tool computes them and the model only reads the result.

**Stage B: deterministic matrix.** Endpoint × policy × resource check × state written. Flag anonymous writes, state changes with no resource check, and mismatched roles between sibling endpoints. This is report material already.

**Stage C: group by entity.** Build one state table per entity that lists which endpoints set which status values. Each group is about 3k-6k tokens of cards, which fits the window.

**Stage D: narrow questions per group,** one at a time, each with the same quote gate:
- Can any step be skipped? (status written without checking the previous status)
- Is a price, amount or role taken from the client?
- What happens on a double submit or retry? (idempotency)
- Is the external call done before the commit, and what if one fails?
- Does any endpoint permit what a sibling endpoint forbids?
- Do tenant or owner checks differ between read and write paths?

**Stage E:** the user supplies a short `business-rules.md` ("students may only see own grades"). This is the one input the code cannot contain, and it is the Memoir-style memory that stays verifiable.

Present these as "questions worth checking", not findings.

### Gaps
- No paper tests entry-point cards with a small model, and no benchmark exists for this on ASP.NET Core. This section is a design proposal, not a proven method. A seeded test on own repos must prove it before any claim of quality is made.

## Tree-sitter or Roslyn from .NET?

### Takeaway
Use both: Roslyn for C#, because the tool needs semantics, and tree-sitter for TypeScript/React and as a fallback. The stack is available on .NET 10.

### Cited Findings
- `Microsoft.CodeAnalysis.Workspaces.MSBuild` is at 5.9.0 stable (2026-08-17), targeting .NET 10.0 and .NET Framework 4.7.2 — [NuGet](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild).
- Call `MSBuildLocator.RegisterDefaults()` first, before workspace use, and restore packages before opening the solution — [Meziantou](https://www.meziantou.net/using-roslyn-to-analyze-and-rewrite-code-in-a-solution.htm).
- `TreeSitter.DotNet` 1.3.0 (2026-01-22): MIT, netstandard2.0 with net10.0 computed, 28+ grammars with C#, TypeScript and Razor named, win-x64 natives included, supports predicate queries — [NuGet](https://www.nuget.org/packages/TreeSitter.DotNet); [repo](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings).
- Alternatives: `CycoDevTreeSitter` 1.4.0 and `TreeSitter.Bindings` 0.4.0 (net8.0) — [NuGet](https://www.nuget.org/packages/CycoDevTreeSitter); [NuGet](https://www.nuget.org/packages/TreeSitter.Bindings/).
- CWM.RoslynNavigator requires the .NET 10 SDK and uses MSBuildLocator, which suggests the stack works there — [NuGet](https://packages.nuget.org/packages/CWM.RoslynNavigator).

### Inferences
| Need | C# | TS/React |
|---|---|---|
| Chunk by method or class | Roslyn syntax tree | tree-sitter |
| Who calls this, effective attributes, types | Roslyn semantic model | tree-sitter queries plus import resolving; weak |
| Fallback if the solution will not load | `CSharpSyntaxTree.ParseText`, no semantics | Line-based |

- Do not pass C# through tree-sitter. It would need a second implementation of what Roslyn already gives, and it cannot resolve the inherited `[Authorize]`.
- Put Roslyn in a separate helper process with no trimming, writing a JSON index. This keeps MSBuild loading away from the main app and matches the existing build-server trap in the log.
- For better TS analysis later, run a small Node script with the TypeScript compiler, since such a repo needs Node anyway. Do this only if tree-sitter's cross-file gaps show up in testing.

### Gaps
- TSX grammar in `TreeSitter.DotNet` is not confirmed. It must be tested first, because React code depends on it.
- No benchmark of Roslyn load time or memory on EduCore. With 16 GB RAM shared with the model, run indexing **before** loading the model.

## Resumable batch and report format

### Takeaway
Use an append-only JSONL results file keyed by content hash and prompt version, flushed after every unit. Then a stop, crash or reboot loses one unit at most, and a re-run reads only what changed. Add SQLite only if JSONL scanning becomes slow.

### Cited Findings
- Aider's cache checks mtime per file and falls back when the cache errors — [repomap.py](https://raw.githubusercontent.com/Aider-AI/aider/master/aider/repomap.py).
- Greptile describes incremental graph updates — [Greptile docs](https://www.greptile.com/docs/how-greptile-works/graph-based-codebase-context) [C].
- SARIF is the "central interface" between analyzer and LLM in ZeroFalse — [ZeroFalse](https://arxiv.org/html/2510.02534v1).
- Claude Code's filter records `filter_stage` and keeps excluded findings in the output, and keeps a finding when the API call fails — [findings_filter.py](https://raw.githubusercontent.com/anthropics/claude-code-security-review/main/claudecode/findings_filter.py).
- gitleaks baselines report only new issues — [repo](https://github.com/gitleaks/gitleaks).

### Inferences
**Cache key:** `sha256(unit_text + context_facts) + prompt_version + checklist_version + model_file_hash`. Including `context_facts` means a file re-runs when a guard changes elsewhere, which matters because the guard is usually in another file.

**Layout** (`.jazari/review/`, gitignored, local only):
```
manifest.json     repo commit, tool versions, skip rules
index.json        symbols, cards, unit list with scores
results.jsonl     one line per finished unit: key, status, ms, tokens, findings
sarif/            raw tool outputs
report.md         regenerated from results at any time
```

**Run loop:** build the unit list sorted by score, skip keys already in `results.jsonl`, run, then append and flush. Timeouts and bad JSON are recorded as `status: failed` and retried once at the end. Run-to-run new findings can be compared against a baseline.

**Order of work on EduCore, estimated:**

| Stage | Reads | Time |
|---|---|---|
| Deterministic + index | none | minutes (not measured) |
| Triage static hits | per alert, 1k-2k tokens | 1-2 min each |
| Hot paths | 25k tokens | under 30 min |
| Remaining ranked code | 340k tokens | about 3.5-4.5 h |
| Business questions | 6 questions × groups | 1-2 h |
| Re-checks | 2-4 min per finding | depends on count |

Because units are sorted by score, stopping early still gives the highest-risk results. Each stage should report as soon as it ends, with the first useful result within minutes.

**Report sections:**
1. Coverage first: units read, skipped with reason, failed. State clearly what was not read.
2. Top findings, by computed confidence then severity, each with file, lines, quote, why, re-check result, and which tools agreed.
3. Endpoint matrix.
4. Questions worth checking (business flow).
5. Better practices, kept separate so they do not bury security items.
6. Dependencies and secrets (secrets always redacted).
7. Rejected by gates, collapsed, with reasons.

### Gaps
- `llama-server` prompt caching (shared-prefix reuse so fixed prompts are not re-read per unit) should be tested. At 28.8 tok/s, 2k of fixed prompt costs 70 s per call without it. I did not read a source for this in this session.
- Keeping Windows awake overnight and keeping the laptop usable alongside it belongs to the other research tracks.

## Objective: the buildable pattern

### Takeaway
**Tools find, code proves, the model explains.** The small model should be a triage and question-answering step inside a deterministic pipeline, not an auditor reading alone. This gives a useful one-night result on a real project, but its quality with a 3B model is unproven and must be measured before any claim is made.

### Cited Findings
- [M] Model size is the main risk: 2B and 4B models scored zero on VulnGym Medium ([VulnGym](https://arxiv.org/html/2608.02001)), smaller models get worse with contextual filtering ([IRIS](https://arxiv.org/html/2405.17238v2)), and Mixtral had recall 0.135 where gpt-oss-20b had 0.884 in the same prompt ([ZeroFalse](https://arxiv.org/html/2510.02534v1)). None of these used a 3B model on this task with this pipeline, so they warn but do not predict.
- [C] Vulnhuntr authors did not get usable structured output from open models — [Vulnhuntr](https://github.com/protectai/vulnhuntr). Grammars should address this, but not correctness — [llama.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/grammars/README.md).

### Inferences
**Pipeline:**
```
0 Inventory   hash files, apply skip rules, measure tokens
1 Tools       Roslyn analyzers, Opengrep+own rules, gitleaks, osv-scanner -> SARIF
2 Index       Roslyn + tree-sitter: symbols, call graph, entry cards, effective auth
3 Rank        score units; make repo map and 600-token repo card
4 Triage      each static alert: slice + trace + rubric -> verdict (sort only, no delete)
5 Review      ranked units <=10k tokens, narrow checklist, grammar JSON, gates
6 Flows       entity groups + narrow questions
7 Re-check    opposite-question pass on survivors
8 Report      regenerated any time from results.jsonl
```
Steps 0-3 use no model and already give findings, secrets, vulnerable packages and the endpoint matrix.

**Lazy first version:** steps 0, 1 and 8 with the endpoint matrix, then the review step on hot paths. Stop there and test. Add flows and re-check once the quote gate works.

**Prove it before claiming quality** (the log's quality floor rule). On Eni-M's own repos only, never General-CRM:
- Seed about 20 known bugs into a branch of EduCore and EduCoreFront: removed `[Authorize]`, an owner check removed in a service, `FromSqlRaw` concat, client-supplied amount, and so on.
- Measure bugs found per hour, false findings per true finding, and time-to-first-result for tools only, versus tools plus model.
- If the model adds no recall over tools alone, ship it as triage and explanation only, and tell the user so.
- Compare with a real cloud review of EduCore if one exists, as a recall reference.

**User expectation:** Eni-M got a cloud AI's review of EduCore. This design can match the mechanical part (secrets, packages, injection, missing attributes) and read every file. It is unlikely to match a frontier model's cross-file judgment on subtle flows. The report should say that plainly.

### Gaps
- A larger model (7B-20B) may run better overnight despite slower speed. The 16 GB RAM and 6.2 tok/s trade-off belongs to model-selection work and was not tested here.
- No end-to-end offline system with this design and hardware was found, so all timing above is estimated from measured rates.
