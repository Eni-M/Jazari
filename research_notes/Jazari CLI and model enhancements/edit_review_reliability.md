# Reliable /edit and /review output from small (1B to 8B) local models

Research date: 2026-11-02. This builds on `research_notes/Whole laptop Jazari optimization/agent_tools_and_output.md` (edit formats, native tools versus GBNF, a live 3B test), `repo_review.md` (review prompt, grammar facts, quote gates) and the 2026-11-02 accuracy design. Those findings are not repeated here; they are referred to as "earlier notes".

How the code works today (read from the repo, 2026-11-02):
- `/edit` (`src/MyOwnLLM.Cli/FileEdit.cs`): the server enforces a `json_schema` `{"edits":[{"search","replace"}]}` with 1 to 8 edits and no `maxLength`. Applying an edit tries an exact unique match first, then a line match with trimmed line ends and an indentation shift. Any failed edit cancels the whole set. Nothing is written until the user says yes.
- `/review` (`src/MyOwnLLM.Cli/ProjectReview.cs`): the server enforces a `json_schema` with at most 5 findings, each with fields `file, quote, problem, severity, fix` and `maxLength` caps. Each part is about 2,000 tokens and the answer about 500. A finding is "Verified" when its whitespace-squeezed quote is a **substring** of some line in the part.
- `LlamaClient.cs`: temperature is 0.2 for everything, and `json_schema` goes on `/v1/chat/completions` with no streaming. No `grammar`, `top_k` or `min_p` is set, so the server defaults (top_k 40, top_p 0.95, min_p 0.05, per earlier notes) apply.

Labels: **[SOURCED]** means a linked source states it. **[INFERRED]** is my reasoning from sourced facts. **[MEASURE]** means it must be tested on Jazari's own `eval/`. Older items are dated. aider.chat, huggingface.co and ibm.com could not be reached from this session (DNS blocked). Aider data was read from its GitHub data files, and some Hugging Face and IBM facts come only from search snippets. Those are marked "(snippet)".

## Constrained decoding in llama.cpp: json_schema, GBNF, streaming, CPU cost, pitfalls, hybrid Granite

### Takeaway
Keep `json_schema`; Jazari already uses it correctly. The fixes are cheap. Add `maxLength` to the edit strings so a runaway string can't fill `max_tokens`. Keep schemas flat and bounded, because grammar cost grows with parser stack count. Never enable thinking together with `json_schema`, since the grammar applies from the first token. No source shows a Granite-4.0-H-specific grammar problem, so the hybrid only needs a regression test, not a redesign.

### Cited Findings
- llama-server's `response_format` accepts `{"type":"json_object"}` or `{"type":"json_schema","schema":...}`, the same idea as other OpenAI-style providers. `json_schema` is also a native field on `/completion` — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[SOURCED]**.
- The schema-to-grammar converter's whitespace rule is `space ::= | " " | "\n"{1,2} [ \t]{0,20}`. Whitespace between JSON tokens is therefore bounded at each position (at most 2 newlines and 20 indent characters), so the classic "infinite whitespace" loop cannot happen inside one gap — [json-schema-to-grammar.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/json-schema-to-grammar.cpp) **[SOURCED]**. String contents, however, are only bounded by `maxLength`. The README example compiles `maxLength: 100` to `char{1,100}` — [grammars README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/grammars/README.md) **[SOURCED]**.
- Performance pitfalls the grammars README states directly: "Grammars currently have performance gotchas (see #4218)". Writing `x? x? x? ...` N times "may result in extremely slow sampling", so use `x{0,N}`. Unsupported features include `patternProperties`, and patterns must start with `^` and end with `$` — [grammars README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/grammars/README.md) **[SOURCED]**. In issue #4218 (2023, older) the reporter found that the number of grammar stacks, up to about 800K, drove the per-token slowdown, and it grew with output length — [issue #4218](https://github.com/ggerganov/llama.cpp/issues/4218) **[SOURCED, older]**.
- LLGuidance is an optional backend, built with `-DLLAMA_LLGUIDANCE=ON` and needing Rust. When it is built in, JSON Schema requests go to it. It computes a token mask in about 50 µs on average per token on a 128k vocabulary (p99 0.5 ms, p100 20 ms, its own benchmark). It allows any whitespace, defaults `additionalProperties` to true, keeps the order properties are declared in, and returns an error for unsupported schemas rather than silently ignoring them. The built-in grammars always put required properties first, and they skip unsupported keywords silently — [llguidance.md](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/docs/llguidance.md) **[SOURCED, vendor benchmark]**.
- Large grammars can be rejected when the grammar is built. Issue #25895 hit this with many tool schemas — [issue #25895](https://github.com/ggml-org/llama.cpp/issues/25895) **[SOURCED, snippet]**. `json_schema` and `grammar` cannot be sent together — [issue #11847](https://github.com/ggml-org/llama.cpp/issues/11847) **[SOURCED]**. Earlier notes add that tools and grammar cannot be mixed either, and that `grammar_lazy` and `grammar_triggers` are stripped from requests.
- Thinking models: llama-server applies a `json_schema` grammar from the first token, so the model cannot think first. Ollama 0.34.4 instead lets free text through until `</think>` — [Towards AI analysis](https://pub.towardsai.net/ollama-0-34-4-lets-qwen-think-before-json-then-blocks-the-tool-call-5-of-6-parsers-tested-8313c8dfb089) **[SOURCED, secondary blog]**. Vendored llama.cpp parser code (Qwen3-Coder) has a `has_response_format` branch that places the schema-constrained JSON parser after the reasoning block — [llama-cpp-sys-4 vendored qwen3-coder.cpp](https://docs.rs/crate/llama-cpp-sys-4/0.7.0/source/llama.cpp/common/parsers/qwen3-coder.cpp) **[SOURCED, third-party copy; whether mainline does this for Qwen3-4B was not confirmed]**. The server has `chat_template_kwargs: {"enable_thinking": false}`, `reasoning_effort: "none"` and `--reasoning-budget N` — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[SOURCED]**.
- Format restrictions and quality: Tam et al. (EMNLP 2024 Industry, older) found that reasoning accuracy falls under format restrictions, and falls more the stricter they are. JSON mode was worst on GSM8K, Last Letter and Shuffled Objects. "NL-to-Format" (answer freely, then convert) did better. A secondary summary says strict formats can help classification — [arXiv 2408.02442](https://arxiv.org/html/2408.02442v1) **[SOURCED; the classification claim is secondary]**. Aider found code inside JSON gets more syntax errors (2024, cloud models), per earlier notes. The dottxt rebuttal ("Say What You Mean") could not be found in this session's search, so it is not cited.
- Hybrid Granite 4.0-H: the only Granite-4-hybrid bug found is template detection breaking tool calls (#16415, 2025, closed per earlier notes) — [issue #16415](https://github.com/ggml-org/llama.cpp/issues/16415) **[SOURCED]**. I found no report of grammar or json_schema failures specific to hybrid or Mamba models **[SOURCED as absence]**.

### Inferences
- Grammar sampling works on logits and does not depend on the architecture, so Granite 4.0-H should behave like the 3B. The risk is tokenizer handling of the schema's literal strings, not the Mamba layers **[INFERRED]**. Regression test: run the `/edit` and `/review` schemas on H-Tiny with code containing `"`, `\`, `\u`, tabs and CRLF **[MEASURE]**.
- **Fix 1 (bug-class):** `FileEdit.Schema` has no `maxLength` on `search`/`replace`. A small model in a repetition loop inside a JSON string runs until `max_tokens`, and the closing `"}]}` never comes, so `Parse` returns null after the slowest possible failure. Add `maxLength` (for example search 1,500 and replace 3,000 characters) and `maxItems` as today **[INFERRED]**. Whether a bounded `char{1,3000}` costs noticeable CPU per token on this build has to be measured; `x{0,N}` is the cheap form per the README **[MEASURE]**.
- **Fix 2:** schema order already puts `quote` before `problem` in the review, which is evidence before claim. The built-in converter keeps that order only because every field is required. Keep all fields required, or the converter reorders them **[INFERRED from the llguidance.md note on ordering]**.
- **Fix 3 (Qwen3-4B, or any thinking model):** send `chat_template_kwargs: {"enable_thinking": false}` (or `reasoning_effort: "none"`) on every `json_schema` call. If thinking ever helps review quality, do it as two calls: a free reasoning pass, then a constrained "convert to findings" pass. That is Tam et al.'s NL-to-Format, at about double the cost **[INFERRED; MEASURE whether it pays at 6 to 10 tok/s]**.
- Streaming: Jazari doesn't stream constrained calls, and needn't. At 6 to 10 tok/s a 500-token review answer takes 50 to 80 s. Show progress (tokens so far) rather than partial JSON **[INFERRED]**.
- LLGuidance would make Jazari's installer require Rust in llama.cpp builds. Jazari's schemas are small and flat, so it isn't worth it unless measured grammar overhead exceeds about 5% of the time per token **[INFERRED; MEASURE grammar on versus off with `llama-server` timings]**.

### Gaps
- No measured CPU cost per token of `json_schema` on a 4-core CPU for a schema like Jazari's. Measure it with the server's `timings` field, with and without the schema, on the same prompt.
- The mainline llama.cpp behaviour of `response_format` with thinking enabled for Qwen3-4B at build b11982 was not confirmed.
- No study of how JSON-mode quality loss scales for 1 to 8B code models specifically; Tam et al. used general reasoning tasks.

## Edit formats: which ones small models succeed with

### Takeaway
On Aider's own data, every small model was benchmarked in **whole-file** format and stayed 97 to 100% well formed. A 32B model fell from 99.6% (whole) to 71.6% (diff) on the harder polyglot set. So format compliance, not just correctness, is the small-model bottleneck for marker formats. Jazari sidesteps compliance with a grammar, which Aider doesn't use. Keep **grammar-forced search/replace**, add **whole-file rewrite for small files** as a fallback, and don't adopt unified diff, line numbers or a separate apply model.

### Cited Findings
Aider data, from its leaderboard YAML files on GitHub ([edit_leaderboard.yml](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/website/_data/edit_leaderboard.yml), [polyglot_leaderboard.yml](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/website/_data/polyglot_leaderboard.yml)) **[SOURCED; 2024 to 2025 runs, older models; Python Exercism for "edit", 225 polyglot exercises in 6 languages]**:

| Model (date) | Benchmark | Format | Pass rate (2 tries) | Well-formed |
|---|---|---|---|---|
| Qwen2.5-Coder-0.5B (2024-10) | edit | whole | 14.3 | 100.0 |
| Qwen2.5-Coder-1.5B (2024-11) | edit | whole | 31.6 | 100.0 |
| Qwen2.5-Coder-3B (2024-11) | edit | whole | 39.1 | 100.0 |
| llama3.2 3B fp16 (2024-09) | edit | whole | 26.3 | 97.0 |
| Qwen2.5-Coder-7B (2024-10) | edit | whole | 57.9 | 100.0 |
| Llama-3.1-8B (2024-07) | edit | whole | 37.6 | 100.0 |
| **granite3-dense 8B, Ollama (2024-11)** | edit | whole | **20.3** | **78.9** |
| Qwen2.5-Coder-14B (2024-10) | edit | whole | 69.2 | 100.0 |
| Qwen2.5-Coder-32B (2024-11) | edit | diff | 71.4 | 94.7 |
| Qwen2.5-Coder-32B via OpenRouter (2024-11) | edit | diff | 65.4 | 84.2 |
| Qwen2.5-Coder-32B (2024-12) | polyglot | diff | 8.0 | 71.6 (148 malformed responses) |
| Qwen2.5-Coder-32B (2024-12) | polyglot | whole | 16.4 | 99.6 |
| gemma-3-27b (2025-03) | polyglot | whole | 4.9 | 100.0 |
| Qwen3 32B (2025-05) | polyglot | diff | 40.0 | 83.6 |

- No model under 8B on Aider's boards was run in `diff` (search/replace) format, and the polyglot board has no small model at all. Its last entries are from 2025-10-03 [polyglot YAML] **[SOURCED as absence]**.
- Granite 3 dense 8B was the only small model below 91% well formed even in whole format. That is an older Granite generation, not 4.x [edit YAML] **[SOURCED, older]**.
- A secondary guide says Aider rewards models of about 22B and up, that smaller models "drift out of" diff and search/replace formats, and suggests whole format — [llmconfigurator](https://llmconfigurator.com/en/guides/coding-agents/aider-with-local-llms) **[SOURCED, secondary, no data]**.
- Aider's search/replace applier tries: an exact match or one differing only in leading whitespace; then the same after dropping a spurious leading blank line; then `...` elision handling. Its edit-distance fuzzy match (`SequenceMatcher`, threshold 0.8) sits after an unconditional `return`, so it is **switched off**. On failure Aider replies with "Did you mean to match some of these actual lines", using the nearest lines at a 0.6 similarity — [editblock_coder.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/coders/editblock_coder.py) **[SOURCED, code]**.
- Fast apply: Kortix FastApply-1.5B v1.0 is Apache-2.0, Qwen2-based, with a 986 MB Q4_K_M GGUF and a 7B sibling — [Kortix HF](https://huggingface.co/Kortix/FastApply-1.5B-v1.0_GGUF/tree/2003d279f3890ae038f8ec58c8a181e13a97c259) **[SOURCED, snippet]**. Morph claims 98% merge accuracy at 10,500+ tok/s, against 86% for Claude 4 Sonnet search/replace and 80% for Aider. Its own pages are inconsistent (96% vs 98%; "2x faster" vs "about 35% faster end-to-end") and give no method — [Morph docs](https://docs.morphllm.com/models/apply), [Morph comparisons](https://www.morphllm.com/comparisons) **[SOURCED, vendor claims]**. Relace says it reaches 10k+ tok/s with Apply 3 — [aggregator](https://feedbagel.com/post/relace-open-sources-fast-apply-model-training-methods-after-reaching-10k-tokenss) **[SOURCED, secondary]**.
- Earlier notes already cover: line numbers corrupt 99.1% of files under a one-line shift; search/replace beat unified diff 53% to 30% on Llama 3.1 repair; Roo uses a fuzzy threshold; on the live 3B test native `edit_file` passed 3/3 and GBNF text passed 1/3.

### Inferences
- A fast-apply model is the wrong tool here. These models re-emit the whole file, and their speed comes from GPUs. At 6 to 10 tok/s a 300-line C# file (about 3,300 tokens at about 11 tokens a line, per earlier notes) takes 5.5 to 9 minutes, plus a second ~1 GB model in RAM **[INFERRED]**. Reject it.
- Aider's whole-format result is about **compliance**, which Jazari's grammar already forces. What the grammar can't force is that `search` is real file text. So Jazari's failure mode is "no match", not "malformed". Track the two separately in eval: `parse_fail`, `no_match`, `ambiguous`, `applied_but_wrong`, `applied_and_builds` **[INFERRED; MEASURE]**.
- **Whole-file fallback:** for a file under about 60 lines, or after two failed search/replace retries, ask for the whole file under a grammar of the form `"```csharp\n" body "```"` (not JSON, to avoid escaping). Show the diff as usual. Earlier notes put about 60 lines at about 110 s at 6 tok/s; under H-Tiny's measured 10 tok/s it's about 65 s **[INFERRED from the design doc speeds]**. llama-server has lookup decoding (`--lookup-cache-static`/`--lookup-cache-dynamic`), and in principle the original file could serve as a draft to speed up a whole-file echo. Whether it does on this build, and whether it helps a hybrid model, is unknown — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[MEASURE]**.
- **JSON versus marker text for search/replace:** the only head-to-head on Jazari's own model (earlier notes, 3B, n=3) favoured JSON-shaped native calls, but it never exercised multi-line code with quotes and backslashes. That is exactly where Aider's code-in-JSON evidence and Tam et al. predict JSON hurts. Measure an A/B on eval: (a) the current `json_schema` edits, against (b) a GBNF grammar for Aider-style `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` blocks with raw text, on at least 20 multi-line C# edits with string literals, `\`, `@""` and raw strings, over 3 seeds **[MEASURE]**.
- **Architect/editor split** (one model plans in prose, another writes edits) costs two generations. On one 3 to 7B model at 6 to 10 tok/s it likely doubles time for little gain. The polyglot board's one architect row (QwQ-32B plus Qwen2.5-Coder, 26.2%, 100% well formed) is a 32B pair **[INFERRED]**. A cheap version is to let `/fix` write its prose explanation first, then call the constrained edit with that explanation in context. That's the NL-to-Format pattern, and `/fix` already produces the explanation **[INFERRED]**.

### Gaps
- No public edit-format benchmark on any 2025 to 2026 model under 8B (Granite 4.x, Qwen3-4B) was found. Aider's small-model rows are 2024 and whole-format only.
- I couldn't fetch Cline's `replace_in_file` or Continue's apply source (the GitHub paths have moved), so their formats are not cited here.
- No independent accuracy benchmark exists for Morph, Relace or Kortix FastApply.

## Robust application: matching ladder, retry on mismatch, compile before offering

### Takeaway
Jazari's ladder (exact, then line-trimmed with an indent shift, then refuse) already matches what Aider does once you see that Aider's fuzzy match is switched off. Add three things: a **"did you mean" retry** that quotes the nearest real lines, a **leading-line-number strip**, and a **compile gate** (Roslyn parse first, `dotnet build` when the user agrees) before the diff is offered as "builds".

### Cited Findings
- Aider: exact or whitespace-tolerant match, blank-line and `...` handling, with fuzzy matching disabled in code. Its mismatch message lists similar actual lines and says "The SEARCH section must exactly match an existing block of lines including all white space" — [editblock_coder.py](https://raw.githubusercontent.com/Aider-AI/aider/main/aider/coders/editblock_coder.py) **[SOURCED]**. Aider allows `max_reflections = 3` (earlier notes).
- Fix-guided verification, which runs a proposed fix against tests and checks, reduced reviewer misjudgement in a 2026 study — [arXiv 2603.00539](https://arxiv.org/pdf/2603.00539) **[SOURCED, abstract level]**.
- Aider has `/lint` and `/test` loops, and the design doc already plans `dotnet build` with at most 2 retries for `/fix` (earlier notes; [design doc](../../docs/superpowers/specs/2026-11-02-jazari-accuracy-design.md)) **[SOURCED]**.

### Inferences
- Concrete applier changes to `FileEdit.Apply` **[INFERRED]**:
  1. On "this text isn't in the file", find the 1 to 3 windows of `wanted.Length` lines with the highest line-set similarity (≥0.6, as Aider does). Send them back with "Your search text was not found. The closest real lines are: …. Copy search exactly from them." Retry at most 2 times, as the design doc's `/fix` rule does.
  2. On "appears more than once", reply with the count and the line numbers of each match, and ask for one more line of context.
  3. Before matching, strip a leading `^\s*\d+[:|]\s` from every search line if all lines have it. Earlier notes saw that prefix leak once on the 3B.
  4. Keep "any failed edit cancels the set". A partly applied edit is a worse state.
- Compile gate, cheapest first **[INFERRED]**:
  - A syntax check with Roslyn (`CSharpSyntaxTree.ParseText(...).GetDiagnostics()`), in process, in milliseconds, with no running of project code. Run it every time on `.cs` files. If the original file parsed clean and the edited one doesn't, retry automatically, since no project code runs. Roslyn as an extra NuGet dependency adds about 10+ MB to the CLI, which needs checking **[MEASURE size]**.
  - `dotnet build --no-restore` on the edit applied to a temporary copy, or in place with an undo, only after a yes, because it runs MSBuild targets (as the design doc says). Show "builds" or "build fails" next to the diff.
- Count only `applied_and_builds` as success in eval for `/edit` and `/fix`. "Applied" alone overstates quality **[INFERRED]**.

### Gaps
- No timing for `dotnet build --no-restore` on Eni-M's projects on the T480. The retry loop budget depends on it **[MEASURE]**.
- No source measured how often "did you mean" retries succeed for small models. Aider's figure is for cloud models and isn't published per model.

## Review output: findings schema, self-verification, cutting made-up findings

### Takeaway
Jazari's schema is already the right shape: quote first, bounded, at most 5 findings. The weak spot is the check. Substring-of-a-line lets trivial quotes like `}` or `return null;` "verify" a finding about something else. Tighten the quote gate, compute the line from the quote, stop asking for a fix in the same call, and add an optional cheap yes/no re-check pass for high-severity findings.

### Cited Findings
- LLM reviewers systematically **overcorrect**, rejecting correct code. More elaborate prompts, "especially those asking for explanations and fixes", increase misjudgements. Some models also accept buggy code. Fix-guided verification is the proposed filter — [arXiv 2603.00539](https://arxiv.org/pdf/2603.00539), [html](https://arxiv.org/html/2603.00539v1) **[SOURCED; 2026, larger models]**. Over-critique of correct code is also reported in [arXiv 2508.12358](https://arxiv.org/html/2508.12358v1) **[SOURCED]**.
- HalluJudge (Atlassian, 2026) detects review comments not grounded in the code, without a reference, and agrees with developer preference about 67% of the time in production — [arXiv 2601.19072](https://arxiv.org/abs/2601.19072v1) **[SOURCED, abstract]**.
- Format restriction hurts reasoning but may help classification — [arXiv 2408.02442](https://arxiv.org/html/2408.02442v1) **[SOURCED, partly secondary]**. A yes/no "is this finding real?" check is a classification task.
- Earlier notes already cover: run-to-run variation (3, 6, then 11 findings), false-positive cutting to 6.3% with agents, and code models over-flagging patched code (Codellama 7B 63.7% vs 36.3%).

### Inferences
Schema changes to `ProjectReview.Schema` **[INFERRED]**:
- Order: `quote` (one whole line, `minLength` about 12), then `problem`, then `severity`. Make `fix` optional or drop it from the first pass, since asking for fixes raises misjudgements per 2603.00539. Keep `file` but don't trust it; the code already searches the other files.
- Optionally add `"kind"` as an enum of the checklist items (`hardcoded-secret`, `sql-injection`, `missing-authz`, `idor`, `unchecked-input`, `path-traversal`, `upload`, `weak-crypto`, `swallowed-error`, `bug`). An enum forces the model to name a category from the checklist, which stops "style" findings and gives eval a per-category precision.

Stronger quote gate in `Check` **[INFERRED]**:
- The squeezed quote must equal a squeezed **whole line**, or be at least 80% of it, not just a substring. It must contain at least one identifier character run of 3 or more letters and be at least 12 characters long. Today `Contains` passes `{` or `return;`.
- If the quote matches more than one line in the part, mark it "ambiguous" rather than taking the first. The line number is then unknown.
- Kind-specific sanity checks, all deterministic and cheap: `sql-injection` needs `+`, `$"` or `string.Format` near SQL keywords on the quoted line; `hardcoded-secret` needs a string literal on the line; `missing-authz` needs a `Map*`/`[Http*]` within a few lines. A failed check downgrades the finding to "unverified", it doesn't drop it.

Re-check pass for high severity only **[INFERRED; MEASURE]**:
- For each verified high-severity finding, make a second constrained call: the 20 lines around the quoted line, the claim, and the schema `{"real": boolean}` (or the enum `yes/no/unsure`), with `max_tokens` about 5. Reading 20 lines (~250 tokens) at 35 to 56 tok/s takes about 5 to 7 s per finding on this laptop, with almost no writing. Keep only `yes`. This is the cheap version of the AEGIS/verifier idea from earlier notes.
- Self-consistency (run the part twice with different seeds, keep findings whose quote appears both times) doubles review time. Use it only for risky files, and only if eval shows precision gains **[MEASURE]**.
- Report precision per category on a planted-bug set, for example `eval/` files with known bugs plus clean twins, because 2603.00539 says the clean-twin false-alarm rate is the main risk **[MEASURE]**.

### Gaps
- No study measures quote-gating or yes/no re-check passes on 1 to 8B models for code review. The re-check's value is engineering judgement until eval shows it.
- HalluJudge's method details (model size, prompts) were not extracted.

## Granite 4.x and Qwen3-4B specifics: prompt format, sampling, IBM guidance

### Takeaway
IBM publishes no code-editing or sampling guidance that I could find. Qwen publishes explicit sampling settings and warns against greedy decoding in thinking mode. For constrained `/edit` and `/review`, keep a low temperature on Granite, and on Qwen3-4B turn thinking off and use Qwen's non-thinking settings or a slightly cooler version. Test both on eval.

### Cited Findings
- IBM's Granite prompt engineering page highlights native JSON output, tool calling, RAG and FIM. The chat template puts tools in the system prompt and asks for each call as JSON inside XML tags, with tools listed before documents — [IBM prompt engineering](https://www.ibm.com/granite/docs/use-cases/prompt-engineering) **[SOURCED, snippet; page not fetchable here]**. No recommended temperature for code or JSON was found. Hosting defaults differ: Replicate lists 0 and 0.6 for Granite 4.0 H Small versions, and Cloudflare lists 0.6 for H Micro — [Replicate](https://replicate.com/ibm-granite/granite-4.0-h-small/versions/79cd422742e4dbd543569aae06db8d5c55bc932153501c2f1bd6213e78cd427d/api), [Cloudflare](https://docs-durable-objects-instance-replaced-errors.previews.developers.cloudflare.com/workers-ai/models/granite-4.0-h-micro/index.md) **[SOURCED; platform defaults, not IBM advice]**.
- Qwen3-4B, non-thinking mode: temperature 0.7, top_p 0.8, top_k 20, min_p 0. Thinking mode: "DO NOT use greedy decoding, as it can lead to performance degradation and endless repetitions". For endless repetition, set `presence_penalty` to 1.5. Qwen3-4B-2507 Instruct is non-thinking only — [unsloth/Qwen3-4b card](https://huggingface.co/unsloth/Qwen3-4b), [LM Studio qwen3-4b-2507](https://www.lmstudio.ai/qwen/qwen3-4b-2507) **[SOURCED, snippet]**.
- Earlier notes: Granite 4.1 uses `<tool_call>` JSON in the Hermes style; the template has no default system prompt; IBM gives no sampling recommendation.

### Inferences
- **Granite (4.1 3B, 4.0-H Tiny):** keep temperature 0.2. Set `top_k` 20 to 40 and `min_p` 0.05 explicitly, not by server default, so a server upgrade can't change behaviour. Put the file before the instruction, and the instruction last (Jazari already does this by passing the context and then `Instructions + Request`) **[INFERRED]**.
- **Qwen3-4B, if eval picks it:** use the 2507 Instruct (non-thinking) build, or send `enable_thinking: false` on every constrained call. Sample at Qwen's non-thinking settings or cooler (0.2 to 0.7), and A/B it on eval. Qwen's greedy warning is stated for thinking mode, so low temperature isn't forbidden in non-thinking mode, but it is untested. If loops appear inside JSON strings, the `maxLength` fix above bounds them, and `presence_penalty` 1.5 is Qwen's own remedy **[INFERRED; MEASURE]**.
- Keep one sampling profile per model in the model catalog (`scripts/jazari-manifest.json`) rather than a single constant `CodeTemperature`, because the two families disagree **[INFERRED]**.

### Gaps
- No IBM guidance specific to code editing or diffs for Granite 4.x was found, and the IBM docs couldn't be fetched to confirm there isn't any.
- No Granite 4.x row exists on any public edit-format leaderboard.
