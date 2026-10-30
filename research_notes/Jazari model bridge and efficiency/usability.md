# Making a slow, small local coding model pleasant to use day to day (Jazari, Granite 4.1 3B, CPU)

Research date: 2026-10-30. Hardware facts used throughout (from the brief and the earlier notes): about 6 tok/s answers and about 28 tok/s prompt reading on an i7-8650U, 16k context. So 1k prompt tokens cost about 36 s, and a 300-token answer about 50 s.

This file avoids repeating `research_notes/Whole laptop Jazari optimization/agent_tools_and_output.md` and `repo_review.md`. Those already cover tool calling, edit formats, the path guard, diff and approval UX, `/copy` and padding, temperature, the AGENTS.md study, the deterministic review pipeline, the risk ranking, JSONL resumability, the quote gate and Aider's repo-map internals. They are cited as "earlier notes" where relevant.

Evidence labels: **[DOC]** project or vendor documentation; **[M]** measured by the source; **[C]** claimed, with no controlled data; **[2nd]** secondary or SEO-style source, weak. This was a short session of about 15 tool calls, so community-sentiment evidence is thin (see Gaps).

## Ranked feature list (summary for the report writer)

Ranked by value per hour of work on this hardware. Every item is a local-only .NET change. Sources are in the sections below.

| # | Feature | Why it matters at 6 tok/s | Effort |
|---|---|---|---|
| 1 | Prompt-reading progress bar with ETA, from llama-server `return_progress`, mirrored to the Windows Terminal tab and taskbar with OSC 9;4 | The 3-minute silent wait before the first word is the worst moment. Nielsen's rule calls for percent-done past about 10 s, and the server already sends the numbers | Small |
| 2 | Stable prompt prefix and prompt-cache discipline, plus showing "cached N / new M tokens" | It turns the 3-minute wait into seconds on follow-ups. The server reports `cache_n` | Small |
| 3 | Background and queued jobs (`/review` detached, `jazari jobs`), with a toast or taskbar flash when done | It changes how waiting feels: the user leaves and is told when it is done | Medium |
| 4 | One-shot and pipe mode: `jazari ask "..."`, `git diff \| jazari ask`, `jazari commit`, `jazari explain` (last error or clipboard) | Small, scoped inputs suit a 3B model and keep prompts tiny | Small to medium |
| 5 | PrettyPrompt ergonomics already in the dependency: persistent history, completion of `/commands` and paths, Shift+Enter multi-line, `@file` mentions | Cuts retyping and stops path typos that waste a 3-minute turn | Small |
| 6 | Context budget meter and pre-flight estimate ("this /read is 4.8k tokens, about 2 min 50 s. Continue? [y/n/outline]") | It prevents accidental long waits; the user picks an outline instead | Small |
| 7 | Symbol outline (signatures only) as the default `/read` of a folder, with full bodies on request | It makes prompts smaller; length alone hurts quality even with perfect retrieval | Medium (Roslyn or tree-sitter, partly planned in earlier notes) |
| 8 | grep/BM25-style retrieval before embeddings; skip embeddings for now | Evidence is weak or negative for hybrid retrieval with models under 7B | Small |
| 9 | Task templates that narrow scope (checklist per command, one file per question, "no findings" allowed, recite-the-evidence-first) | Narrow tasks and short inputs are where small models hold up | Small |
| 10 | Editor integration: an optional OpenAI-compatible pass-through for Continue chat only; no FIM autocomplete with Granite 3B on this CPU | Autocomplete needs sub-second latency, which this CPU cannot give a 3B model | Small, low priority |

Things to skip: inline FIM autocomplete with the 3B model, embedding indexes, multi-step agent loops with long tool chains, and speculative "self-review" passes, which double latency.

---

## Latency UX: progress, streaming, ETA, background jobs, notifications, resumable work

### Takeaway
llama-server already exposes everything needed for an honest progress bar: per-chunk `prompt_progress` (`total`, `cache`, `processed`, `time_ms`) and final `timings`. Show a percent-done bar with ETA during prompt reading, stream answer tokens, mirror progress to the Windows Terminal tab and taskbar with OSC 9;4, and push long jobs into the background with a toast on completion.

### Cited Findings
- llama-server `/completion` accepts `return_progress` (default false, streaming only). Each streamed chunk then carries `prompt_progress` with `total`, `cache`, `processed` and `time_ms`. Overall progress is `processed/total`, and timed progress excluding cached tokens is `(processed-cache)/(total-cache)` — [llama.cpp server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC, current master as of 2026-10]**.
- Responses include a `timings` object (`cache_n`, `prompt_n`, `prompt_ms`, `prompt_per_second`, `predicted_n`, `predicted_ms`, `predicted_per_second`). OpenAI-style `usage.prompt_tokens_details.cached_tokens` is also returned. `timings_per_token` adds timings to each chunk — [same README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC]**.
- Prompt-cache plumbing: `cache_prompt` defaults to true; `--cache-reuse`/`n_cache_reuse` reuses chunks via KV shifting (default 0, off); `--cache-ram` (default 8192 MiB) and `--cache-idle-slots` keep prompt caches; `--slot-save-path` plus `POST /slots/{id}?action=save|restore` persist a slot's KV cache to disk — [same README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC]**.
- Nielsen's guidance, from *Usability Engineering* (1993): use percent-done indicators for operations over about 10 seconds, and looped indicators for 2 to 10 seconds. It traces to Myers (CHI 1985) — [NN/g, Response Times: The 3 Important Limits](https://nngroup.com/articles/response-times-3-important-limits) **[DOC/classic HCI; read through search summary]**. The commonly quoted advice that waits over 10 s should offer a way to interrupt could not be verified in Nielsen's text **[2nd]**.
- Windows Terminal supports the ConEmu OSC 9;4 progress sequence, `ESC ] 9 ; 4 ; <state> ; <progress> BEL`. States: 0 hide, 1 normal, 2 error, 3 indeterminate, 4 warning. It shows as a ring in the tab header and as progress on the Windows taskbar button. Requires WT 1.6+. The C# example is `Console.Write("\x1b]9;4;1;50\x07")` — [Microsoft Learn, progress-bar sequences](https://learn.microsoft.com/en-us/windows/terminal/tutorials/progress-bar-sequences) **[DOC, updated 2025-08]**.
- Windows toast from a C# desktop app: Microsoft's quickstart uses the notifications NuGet package (`ToastContentBuilder`), which "also lets desktop apps send app notifications". Unpackaged apps cannot use remote images. Click activation needs extra setup for unpackaged apps (`ToastNotificationManagerCompat.OnActivated`). Default and maximum expiry for local notifications is 3 days. `Tag`/`Group` replace or remove a toast — [Microsoft Learn, send local toast](https://learn.microsoft.com/en-us/windows/uwp/design/shell/tiles-and-notifications/send-local-toast) **[DOC]**. Some language versions of the page still reference `Microsoft.Toolkit.Uwp.Notifications`. A third-party article says to use `CommunityToolkit.WinUI.Notifications` and warns that toasts which never appear are usually an app-registration problem — [Codemia](https://codemia.io/knowledge-hub/path/create_popup_toaster_notifications_in_windows_with_net) **[2nd]**.
- PrettyPrompt (already a Jazari dependency) returns a `CancellationToken` with each prompt result, so Ctrl+C can cancel a long-running task — [PrettyPrompt README](https://github.com/waf/PrettyPrompt) **[DOC]**.
- Earlier notes already settled resumable review (`results.jsonl` keyed by content hash, flushed per unit, sorted by risk so stopping early still gives the best results) — `repo_review.md`.

### Inferences
Implementation sketch for .NET:
- **Progress bar during prompt reading.** Send `return_progress: true`. Jazari's gateway talks to llama-server; if `/v1/chat/completions` does not pass the field through on this build, test it there, or render the prompt with `/apply-template` and call `/completion`. Compute `rate = (processed - cache) / time_ms`, then `eta = (total - processed) / rate`. Render one line that redraws with `\r`: `Reading prompt  ████████░░░░  2,410/4,980 tok  (1,200 cached)  ~1m 31s left`. Clear the line on the first answer token. Keep it out of scrollback so mouse copy stays clean (earlier notes flagged scrollback pollution).
- **Mirror to the taskbar.** Write OSC 9;4 state 1 with the percent during reading, state 3 (indeterminate) during generation (the answer length is unknown), and state 0 at the end. Write state 2 on error. Guard with `WT_SESSION` so other terminals do not show junk. This is the cheapest way to let the user switch windows and still see progress; it is about 10 lines.
- **Pre-flight ETA before sending.** Use `/tokenize` (or the local estimate) on the assembled prompt, subtract the expected cache hit, and divide by the measured `prompt_per_second` (keep a rolling average from `timings`). If the ETA is over 60 s, print it; if it is over 2 min, ask `[y] send  [o] send outline only  [n] cancel`. This is item 6 in the ranked list.
- **Generation ETA.** The answer length is unknown, so show elapsed time and live tok/s instead of a fake ETA. For `/review` parts, which have a bounded output, show `part 7/42, ~38 min left`, based on a rolling average per part.
- **End-of-turn stats line, muted:** `4,980 prompt tok (1,200 cached) in 2m 15s, 312 answer tok at 6.1 tok/s`. It teaches the user what costs time, which is the most effective long-term latency fix.
- **Background jobs.** `/review` (and later `/explain-project`) runs as a detached child process (`jazari review --job <id>`) that writes progress to `.jazari/jobs/<id>/status.json` and results to JSONL. The CLI gets `jazari jobs` (list, tail, cancel) and a status-line hint `1 job running: review 7/42`. Since llama-server handles one slot at a time on this CPU, the foreground chat must queue behind a running job or the job must pause. Simplest: a gateway-side single-slot FIFO with priority for interactive requests between job units. A job yields after each unit, so the chat waits at most one unit (about 1 to 2 min).
- **Notification on completion.** Order of preference: (1) BEL plus OSC 9;4 cleared (zero dependencies); (2) a Windows toast through the CommunityToolkit notifications package, from the job process only. Skip click-activation handling at first, because unpackaged activation needs CLSID and manifest work; a plain informational toast "Review done: 3 high, 7 medium. Report at ...\report.md" is enough. Keep it within the 3-day expiry, and use `Tag = jobId` to replace progress toasts.
- **Overnight runs.** Pair with power settings from the other research track. Jazari can request "keep awake" during a job with `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` (a Win32 API, not researched in this session; verify).
- **Cancel.** Ctrl+C must abort the HTTP stream (llama-server stops generation when the client disconnects; verify on this build) and keep the partial answer in history, marked `[cancelled]`.

### Gaps
- Whether `return_progress` is honoured on `/v1/chat/completions` (not only `/completion`) on build b11982 was not checked; the README section read describes it under `/completion`.
- No study was found on progress UX specifically for LLM prompt processing; the guidance is the general HCI rule.
- `SetThreadExecutionState` and llama-server's behaviour on client disconnect were not checked against sources in this session.

## Context selection that keeps prompts small

### Takeaway
On a 3B model at 28 tok/s, the best context strategy is the smallest one: a signature-only outline (Aider-style repo map, built with Roslyn for C#), plus grep/BM25 to pull the few exact methods needed. Embedding retrieval has weak or negative evidence for models under 7B and adds an index to maintain, so skip it for now. Shorter context also helps quality directly, not only speed.

### Cited Findings
- Aider's repo map sends "classes, methods and function signatures from everywhere in the repo", ranked by a graph algorithm. `--map-tokens` "defaults to 1k tokens" and grows when no files are in chat. The LLM can then ask for specific files — [Aider repo map docs](https://aider.chat/docs/repomap.html) **[DOC]**. The page makes no claim about small models. Ranking internals are in the earlier notes (`repo_review.md`).
- Guides on running Aider locally recommend lowering `--map-tokens` to 512 to 1,024 for small context windows and say CPU runs at 5 to 10 tok/s — [aimadetools](https://www.aimadetools.com/blog/aider-with-ollama-setup) **[2nd]**. Another says producing exact search/replace blocks "is a capability test many small models fail", and treats 8B as workable only for small single-file asks — [llmconfigurator](https://llmconfigurator.com/en/guides/coding-agents/aider-with-local-llms) **[2nd]**.
- "Context Length Alone Hurts LLM Performance Despite Perfect Retrieval" (Du et al., arXiv 2510.05381, EMNLP 2025 Findings): across five models on math, QA and coding, performance dropped 13.9% to 85% as input grew, even when the model retrieved all relevant information. The drop held with irrelevant tokens replaced by whitespace, and with evidence placed right before the question. Mitigation: ask the model to recite the relevant evidence first, which gave GPT-4o up to +4% on RULER — [arXiv 2510.05381](https://arxiv.org/abs/2510.05381v1) **[M]**.
- CodeRAG-Bench (NAACL 2025 Findings), 10 retrievers × 10 LMs: retrieving high-quality contexts helps, but retrievers often fail to fetch useful context and generators struggle to use it; effectiveness varies by task and retriever — [CodeRAG-Bench](https://github.com/code-rag-bench/code-rag-bench); [ACL Anthology](https://preview.aclanthology.org/dashboard/2025.findings-naacl.176) **[M; per-retriever numbers not extracted]**.
- WeChat code-completion RAG study (arXiv 2507.18515, 2025): combining BM25 with semantic retrieval helps larger models, but for models under 7B "the combination shows limited or even negative impact" — [arXiv 2507.18515](https://arxiv.org/pdf/2507.18515) **[M; read through search summary]**.
- A RAG design-decisions paper reports that neither BM25 nor MiniLM gives a consistent gain as context expands on code generation — [arXiv 2411.19463](https://arxiv.org/pdf/2411.19463) **[M; via search summary]**.
- Cline argues against indexing: it uses ripgrep and tree-sitter on demand, and says embeddings are a second copy of your IP; Continue deprecated `@Codebase` in favour of agent-driven search — earlier notes `repo_review.md` **[C/DOC]**.
- Earlier notes also cite few-shot and RAG giving mixed results (−9.3 to +9.1 points) on 1 to 1.3B code models — `agent_tools_and_output.md`.

### Inferences
- **Outline first, then bodies.** `/read src/Orders/` should by default send an outline: namespace, type, member signatures, attributes (`[Authorize]`, `[HttpPost("...")]`), about 5 to 10% of the file's tokens. The user (or a follow-up) pulls full bodies with `/read Orders/OrderService.cs#Pay`. For C#, use Roslyn `CSharpSyntaxTree.ParseText` (syntax only, fast, no MSBuild load) for outlines; the semantic workspace is only needed for the review pipeline. tree-sitter is for TS/React files.
- **Retrieval = grep, then rank.** For a question like "where is the discount applied?", tokenize the question, search identifiers and string literals in C# (`File.ReadLines`, already planned as the `search` tool), score with a simple BM25 over method-level chunks, and include the top 2 or 3 method bodies plus the outline. A small in-memory BM25 in C# is about 100 lines and needs no model. Show the user what was picked (`context: OrderService.Pay, DiscountRules.Apply (640 tok)`) so they can correct it with `@file`.
- **No embeddings yet.** A tiny CPU embedding model (for example a 20 to 100M-parameter model via llama-server `--embedding`) is possible. But the only small-model evidence is neutral to negative, it competes for the same CPU, and the index goes stale. Revisit only if the grep picks fail on natural-language questions in a measured eval.
- **Recite before answering.** For `/review` and questions over pasted code, ask for "first quote the lines that matter, then answer". It is the measured mitigation in Du et al., and it lines up with the quote gate in the earlier notes. It costs output tokens (about 6 s per 40 tokens), so keep the quotes short.
- **Budget meter.** Show `ctx 6.2k/16k` in the prompt line. Warn at 60% and offer `/compact` (replace old file dumps with one-line stubs, which the earlier notes already describe).

### Gaps
- No study measured outline-versus-full-file context on a 3B model. The recommendation rests on the length-hurts result and the cost model.
- CodeRAG-Bench per-retriever numbers for small generators were not extracted.

## Editor integration: is an OpenAI-compatible endpoint for VS Code worth it?

### Takeaway
Inline autocomplete (FIM) is not worth it with Granite 3B on this CPU: every reference setup for CPU uses a 0.5B to 1.5B coder model, and even then quality is called "significantly lower". An OpenAI-compatible pass-through so Continue's chat panel can use Jazari is cheap and mildly useful. Selection-based "explain or review this" actions are the editor features that fit 6 tok/s.

### Cited Findings
- llama.vscode offers FIM completion (Tab to accept, Shift+Tab first line), chat and an agent. It uses "ring" context from open and edited files and llama.cpp's smart context reuse. Presets by VRAM go down to `--fim-qwen-1.5b-default` (under 8 GB VRAM). For CPU-only it lists `Qwen2.5-Coder-1.5B-Q8_0` and `0.5B-Q8_0` with `--cache-reuse 256`, warning that quality will be "significantly lower". The agent recommends gpt-oss 20B as "the best choice for now" — [llama.vscode README](https://github.com/ggml-org/llama.vscode) **[DOC, page as of 2026]**.
- llama-server `/infill` takes `input_prefix`, `input_suffix` and `input_extra` (`[{filename,text}]`); it uses a repo-level FIM pattern if the model has `FIM_REPO`/`FIM_FILE_SEP` tokens — [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md) **[DOC]**. Whether Granite 4.1 3B has FIM tokens was not checked.
- Continue's autocomplete docs show `qwen2.5-coder:1.5b` in the autocomplete role, and Continue's own model page calls Qwen2.5-Coder its top pick — [Continue autocomplete docs](https://docs.continue.dev/advanced/model-roles/autocomplete); [Continue resources](https://resources.continue.dev/top-ollama-coding-models-q4-2025) **[DOC]**. A third-party guide says the 1.5B model "feels instant enough to leave on" — [DEV Community](https://dev.to/pavelespitia/run-qwen-coder-deepseek-locally-the-2026-free-ai-pair-programmer-setup-jo4) **[2nd]**. Debounce settings conflict between sources (150 ms vs 350 ms) and were not confirmed in official docs.
- Tabby CPU setups use StarCoder-1B for completion; one review reports StarCoder2-3B on CPU at 8 to 15 tok/s and calls CPU mode "works but barely"; another says inline latency becomes uncomfortable on CPU — [railway.com](https://railway.com/deploy/tabby); [DEV Community Tabby review 2026](https://dev.to/jovan_chan_9500711396d4e6/tabby-review-2026-self-hosted-code-completion-for-teams-3a4a); [bestllmfor](https://bestllmfor.com/guide/tabby-autocompletion-code-local-self-hosted-ollama/) **[2nd]**.

### Inferences
- **Arithmetic.** A useful completion is 20 to 60 tokens. At 6 tok/s that is 3 to 10 s, plus prompt reading of about 1 to 2k tokens of context (cache reuse helps, but the cursor moves). Autocomplete that arrives after the user has typed on is noise. A 0.5B to 1.5B FIM model as a second llama-server would be faster, but it would compete for RAM and CPU with Granite and is outside "Jazari's model". Rank: skip, or try a separate 0.5B Qwen2.5-Coder only as an experiment.
- **Minimal useful integration (if any).**
  1. The gateway exposes `/v1/chat/completions` and `/v1/models` on `127.0.0.1` with a local key. Continue's chat panel (or Visual Studio extensions that accept an OpenAI-compatible URL) then works with selection-to-chat: "explain this", "find bugs in this method". Bind to loopback only and keep the path guard and secret deny list in the gateway, because editors send file content automatically.
  2. Better value for less work: a VS Code task or Visual Studio "External Tool" that runs `jazari ask --file ${file} --lines ${selection} "review this"` in the terminal. No extension needed; reuses all of Jazari's UX (progress, `/copy`).
- **Visual Studio.** Not researched in depth this session (gap). The external-tool route works in any IDE.

### Gaps
- Whether Granite 4.1 3B ships FIM tokens, and Continue's current config keys for an OpenAI-compatible chat provider, were not verified.
- No first-hand report was found of anyone using a 3B model for FIM on an 8th-gen mobile CPU.

## CLI ergonomics

### Takeaway
Jazari already ships PrettyPrompt, which gives history, completion menus, Shift+Enter multi-line and Ctrl+C cancellation for free. The bigger wins are one-shot and pipe modes, which suit a slow model because the inputs are naturally small and scoped: commit messages from `git diff --cached`, explain-the-last-error, and `jazari ask` with stdin.

### Cited Findings
- PrettyPrompt features: history navigation with filtering like PSReadLine's `HistorySearchBackward`, optionally persisted across sessions; completion menu with documentation tooltips (Ctrl+Space); multi-line input with word-wrap and optional detection of incomplete lines so Enter becomes a soft newline (Shift+Enter); ANSI syntax highlighting; Ctrl+L clear, Ctrl+C cancel line, Ctrl+Shift+C copy; customization via `IPromptCallbacks`; a `CancellationToken` per result — [PrettyPrompt](https://github.com/waf/PrettyPrompt) **[DOC]**.
- Commit-message generation by piping is a well-worn pattern: a git alias running `git diff --cached` into Simon Willison's `llm` with a system prompt from a file, wired into a hook that fires when committing without a message — [Harper Reed, 2024-03](https://harper.blog/2024/03/11/use-an-llm-to-automagically-generate-meaningful-git-commit-messages/) **[C]**. The `llm-commit` plugin reads the staged diff, drafts a message and optionally runs `git commit -s` — [llm-commit](https://tomevault.io/tome/GNtousakis/llm-commit) **[2nd]**. A tutorial does the same against a local OpenAI-compatible server so the diff never leaves the machine — [DevToolLab](https://devtoollab.com/blog/local-llm-git-commit-messages) **[2nd]**.
- Aider has `/copy`, `/copy-context`, `/diff`, `/undo`, `/lint`, `/test` — earlier notes `agent_tools_and_output.md` **[DOC]**.

### Inferences
Sketches, in priority order:
1. **One-shot and pipe.** `jazari ask "question"` prints the answer and exits (no REPL). If stdin is redirected (`Console.IsInputRedirected`), read it as context: `git diff | jazari ask "any bugs?"`, `dotnet build 2>&1 | jazari ask "why"`. Cap stdin at the `/read` budget and say so when it is truncated (`stdin 9.1k tok, kept last 4k`). Use `System.CommandLine` for parsing. Plain-text output when stdout is redirected (no ANSI, no progress), so `jazari ask ... > notes.md` works.
2. **`jazari commit`.** `git diff --cached --stat` plus the diff truncated to about 2k tokens (largest hunks summarised by `--stat` only). Prompt: one subject line ≤ 72 characters in the repo's style (read the last 10 `git log --format=%s` lines as examples, which is a cheap, relevant few-shot), optional 1 to 3 bullets. Use a GBNF grammar or `max_tokens` 80 to stop rambling. Show it, then `[y] commit  [e] edit in $EDITOR  [n] cancel`. Never commit without the keypress. About 1k to 2.5k prompt tokens, so 40 to 90 s; fine as a background-friendly task.
3. **`jazari explain`.** Sources in order: an explicit argument; stdin; the clipboard (TextCopy is already present transitively); otherwise ask. Optionally a PowerShell profile function `jx { $out = (Invoke-Expression "$args" 2>&1 | Out-String); $out | jazari explain }` (a script for the user's own profile, not something Jazari installs silently). Strip ANSI and keep the last 60 lines of the error, which is where the stack top usually is, plus a grep for the first project file:line in the trace to include ±15 lines of that source.
4. **`@file` mentions and completion.** In the REPL, implement `IPromptCallbacks` completion: after `/` complete command names; after `/read `, `/edit `, `/review ` or `@`, complete workspace-relative paths (respecting the skip list and the secret deny list, so completion never suggests `.env`). An `@path` anywhere in a chat line expands to the outline (default) or `@path!` to full content. Show token cost in the completion tooltip (`OrderService.cs  1,840 tok ~1m05s`).
5. **History.** Turn on PrettyPrompt's persistent history at `%LOCALAPPDATA%\Jazari\history.txt`. Do not persist lines that look like secrets (simple regex for `key=`, `password`, long base64), because history is a plain file.
6. **Multi-line.** Shift+Enter is already supported; also accept a pasted block (PrettyPrompt handles paste) and a `"""` fence mode for people who prefer it.
7. **Session resume.** `jazari --continue` reloads the last conversation. If llama-server `--slot-save-path` is set, save the slot KV cache on exit and restore it on resume, so a long `/read` session does not have to re-read 5k tokens (about 3 min). Verify that restore works with Jazari's prompt shape; this is the biggest latency saver for "come back after lunch".

### Gaps
- No user study on which CLI ergonomics matter most for local assistants was found; the ranking is from cost arithmetic and the pattern being common across tools.
- PrettyPrompt's exact API names for persistent history were not on the README page read.

## Prompting small 3B models for code: what measurably helps

### Takeaway
The measurable levers are shorter inputs (length alone hurts), narrow task scoping, recite-the-evidence-first, a few in-repo examples for format-heavy tasks, and grammar-constrained output where the format matters. Direct 2025–2026 evidence on 3B models for code review is scarce; most of it is from larger models or from 1B base models.

### Cited Findings
- Length alone cuts performance by 13.9% to 85% even with perfect retrieval; recitation before solving is a model-agnostic mitigation — [Du et al., arXiv 2510.05381 (2025-10)](https://arxiv.org/abs/2510.05381v1) **[M]**.
- Few-shot for code review: GPT-3.5 with few-shot learning reached 46.38% to 659.09% higher exact match than zero-shot; the authors recommend few-shot when fine-tuning data is scarce — [Pornprasit and Tantithamthavorn, arXiv 2402.00905](https://arxiv.org/pdf/2402.00905) **[M; GPT-3.5, not a small model; 2024]**.
- Structured generation and accuracy: one blog post re-ran a study claiming that format constraints hurt LLM performance and found most metrics improved — [Dylan Castillo](https://dylancastillo.co/posts/say-what-you-mean-sometimes.html) **[C; blog analysis]**. Earlier notes add that code inside JSON raises syntax errors (Aider) and that grammars fix shape, not correctness.
- Small models can learn structured output formats via fine-tuning (sub-1B) — [Stanford CS224R 2025 report](https://cs224r.stanford.edu/projects/pdfs/CS224R_Final_Report%20(2)12.pdf) **[M; fine-tuning, student project]**.
- Small models (under 7B) gain little or lose from combined retrieval contexts — [arXiv 2507.18515](https://arxiv.org/pdf/2507.18515) **[M]**.
- Earlier notes (`agent_tools_and_output.md`, `repo_review.md`) already cover: AGENTS.md-style context files tend to hurt; temperature under 0.5 helps code; few-shot/RAG mixed on 1B models; IRIS found small models lean towards "vulnerable"; VulnGym 2B/4B scored zero on Medium; narrow checklists, max 3 findings, explicit "no findings".

### Inferences
Concrete prompt patterns for Jazari's commands (all testable on a ~20-case eval):
- **One question per call.** "Review this file" becomes 3 to 4 calls with narrow checklists ("authorization only", "input validation only"), each with a small output cap. This is slower in total but each call is short, and the batch can run in the background.
- **Order: instructions → code → short restatement of the task at the end.** A short task restatement after the code keeps the question near the generation point (consistent with the length findings; not separately measured for 3B).
- **Recite first.** "List up to 3 lines that matter, verbatim, then answer." It feeds the quote gate as well.
- **In-repo examples over generic ones.** For commit messages, use recent `git log` subjects; for edits, use one nearby method in the same style. Keep each example under about 200 tokens.
- **Output formats.** Markdown with fixed headings for human-facing answers (`### Problem`, `### Fix`, a code block); grammar-forced JSON only for machine-consumed results (review findings, commit message); search/replace for edits (earlier notes). Never JSON-wrap code.
- **Say what not to do.** "If nothing is wrong, reply `No issues found.`" reduces invented findings, which matters because small models over-flag.
- **Caps.** `max_tokens` per command (commit 80, explain 350, review part 300). A cap is the single strongest latency control at 6 tok/s.

### Gaps
- No 2025–2026 study measured prompt structure, few-shot or output format on a ~3B chat model for code review or explanation. The patterns above are inferred from larger-model studies plus the length result, and must be checked on a small local eval.
- IBM gives no prompting guidance for Granite 4.1 code tasks in the sources read (earlier notes also found no sampling recommendation).

## What users of local coding assistants actually use vs. ignore on slow hardware

### Takeaway
The evidence is weak and mostly secondary, but it points one way: on CPU-only or small-model setups, people keep cheap, scoped features (small-model autocomplete with a 1.5B model, chat about a selection, commit messages, explain-this) and drop heavy agent loops and multi-file editing, which small models fail at and slow hardware makes painful.

### Cited Findings
- CPU-only inference of a 7B model at about 5 to 15 tok/s is described as "usable for experimentation, painful for daily work" — [fungies.io 2026 guide](https://fungies.io/how-to-run-local-llms-home-setup-guide-2026/) **[2nd]**.
- In an archived r/LocalLLaMA-style thread about a 32 GB CPU-only server, a commenter said such setups can only run "smaller, older models, which are not great for coding", and others suggested skipping heavy agent frameworks on CPU-only hardware — [subreddit snapshot](https://reddit.sentinel-team.org/posts/1t3itee/snapshots/2026-05-08T23%3A26%3A23.864362Z) **[2nd, anecdotal; the snapshot site is an unofficial mirror]**.
- Aider guides: small models often fail the exact edit formats; under 16 GB, an 8B coder is suitable only for "small, single-file asks" with occasional malformed-edit retries — [llmconfigurator](https://llmconfigurator.com/en/guides/coding-agents/aider-with-local-llms) **[2nd]**.
- Continue and llama.vscode both steer users to 1.5B or smaller models for autocomplete and to much larger models (gpt-oss 20B) for agent work — [llama.vscode](https://github.com/ggml-org/llama.vscode); [Continue docs](https://docs.continue.dev/advanced/model-roles/autocomplete) **[DOC]**.
- Tabby on CPU "works but barely" — [DEV Community](https://dev.to/jovan_chan_9500711396d4e6/tabby-review-2026-self-hosted-code-completion-for-teams-3a4a) **[2nd]**.

### Inferences
- Keep and polish: `/read` + ask, `/review` in the background, `jazari ask` with pipes, commit messages, explain-error, `/copy`, `/edit` for single small changes.
- De-prioritise: multi-step autonomous tool loops (keep the 6-call cap from the earlier notes), whole-file rewrites, FIM, and any feature that adds a second model to RAM.
- Measure, do not guess: add a local, opt-in usage counter (`.jazari/usage.jsonl`: command, prompt tokens, wait seconds, accepted y/n for edits and commits). After two weeks it shows which commands are used and which waits are abandoned (Ctrl+C during prompt reading is the key signal). This stays on the laptop.

### Gaps
- No first-hand Hacker News or r/LocalLLaMA thread was retrieved in which users of Aider, Continue, Tabby, Cline or Roo report which features they use with slow local models. Searches returned SEO guides and mirrors. This is the weakest section, and its conclusions should be treated as the author's reading of indirect evidence.
- No survey data on local-assistant feature usage was found.
