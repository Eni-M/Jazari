# Jazari CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the one-shot `myllm` CLI with `jazari`: an interactive, streaming, monochrome terminal session plus one-off commands, installable as a .NET global tool.

**Architecture:** The CLI gets three layers: `Terminal/` (color mode, theme tokens, components, streaming Markdown renderer, all writing to a `TextWriter` so they're testable), `GatewayClient` (all HTTP to the gateway), and `Session` (slash commands, history, cancellation) driven by a PrettyPrompt input loop. The gateway gains `GET /v1/info`, history trimming and the "Jazari" system prompt.

**Tech Stack:** .NET 10, PrettyPrompt 6.0.5 (line editor: multiline, history, completion; the only new dependency), hand-written ANSI output (no Spectre/Markdig: 8 color tokens and a line-based Markdown renderer don't justify them), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-jazari-cli-design.md`

## Global Constraints

- Palette (truecolor / 256 / 16): primary `#F2F2F2`/255/bright white · secondary `#BBBBBB`/250/white · muted `#777777`/243/bright black · inactive `#555555`/240/bright black · code bg `#151515`/233/none · raised bg `#1C1C1C`/234/none · line `#303030`/236/bright black.
- Color mode: `NO_COLOR` set **or output redirected** → none; `COLORTERM` = `truecolor|24bit` or `WT_SESSION` set → truecolor; `TERM` contains `256` → 256; else 16.
- Glyphs: `✓ › · │` when output encoding is UTF-8, else `+ > - |`.
- Wordmark `J A Z A R I`; tagline `Intelligence, precisely applied.`; user label `YOU`; section labels uppercase muted; never color as the only signal.
- No spinners, no ticking timers, no screen clearing; one redraw allowed (replacing `THINKING` with the answer).
- All model/gateway text passes `ConsoleText.Sanitize` before output.
- Env: `JAZARI_KEY`, `JAZARI_URL` (default `http://127.0.0.1:5080`); fallback to `MYOWNLLM_KEY`/`MYOWNLLM_URL`.
- Streaming is the default for every answer.
- User-facing name is Jazari; repo, folders and C# namespaces stay `MyOwnLLM`.
- Minimum supported width 40 columns.

## Review Focus

1. **Narrow terminal (40 cols)** → welcome rows and answers wrap at the width; no line overflows. (Task 3, Task 6)
2. **Gateway down or key wrong at start** → session still opens; welcome shows `Gateway not running` / `Key rejected`; the first question prints an `ERROR` block, not an exception. (Task 5, Task 6)
3. **Ctrl+C while streaming** → answer stops, the partial text stays, a muted `stopped` line follows, and the prompt returns. (Task 5)
4. **Answer ends inside an unclosed code fence, or contains escape codes** → background/color reset at the end; no colors bleed into the prompt; escapes stripped. (Task 4)
5. **Output piped to a file** (`jazari ask "…" > out.txt`) → zero ANSI escape sequences in the file. (Task 3)

---

### Task 1: Gateway — `/v1/info`, history trimming, Jazari prompt

**Files:**
- Modify: `src/MyOwnLLM.Gateway/Prompt/PromptBuilder.cs`, `src/MyOwnLLM.Gateway/Endpoints/ChatEndpoints.cs`, `src/MyOwnLLM.Gateway/GatewayOptions.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/PromptBuilderTests.cs`, `tests/MyOwnLLM.Gateway.Tests/EndpointTests.cs`

**Interfaces:**
- Produces: `GET /v1/info` (API key required) → `{"name":"Jazari","model":"Granite 4.1 3B","version":"0.2.0"}`; values from `GatewayOptions.ModelName = "Granite 4.1 3B"` and the assembly informational version.
- Produces: `PromptBuilder.Build` drops the **oldest** history messages (in user/assistant pairs) until the estimate fits; throws `InputTooLargeException` only when system + state note + context + question alone exceed the limit.
- Produces: `PromptBuilder.SystemPrompt` starts `"You are Jazari, a concise coding assistant for ASP.NET Core, …"` (rest unchanged).

- [ ] **Step 1: Write failing tests**
  - `Build_drops_oldest_history_to_fit`: history of 10 pairs × 8,000 chars, `contextTokens 16384`, `maxOutputTokens 512` → result fits the limit, keeps the newest pair, last message is the question.
  - `Build_still_throws_when_question_alone_is_too_big` (existing 70,000-char test stays green).
  - `SystemPrompt_names_Jazari`: `Assert.StartsWith("You are Jazari", PromptBuilder.SystemPrompt)`.
  - `Info_returns_name_model_version` (200, `name == "Jazari"`, `model == "Granite 4.1 3B"`, non-empty `version`) and `Info_requires_key` (401 without key).
- [ ] **Step 2: Run** `dotnet test --filter "PromptBuilderTests|EndpointTests"`. Expected: the 4 new tests FAIL.
- [ ] **Step 3: Implement** trimming in `Build` and `MapGet("/info", …)` in `MapChatEndpoints`; set `<Version>0.2.0</Version>` in the gateway csproj.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(gateway): add /v1/info, trim history, name the model Jazari`.

### Task 2: Rename the CLI to `jazari` and settle settings/arguments

**Files:**
- Modify: `src/MyOwnLLM.Cli/MyOwnLLM.Cli.csproj` (`AssemblyName` `jazari`, `PackAsTool` true, `ToolCommandName` `jazari`, `PackageId` `Jazari`, `Version` `0.2.0`), `src/MyOwnLLM.Cli/CliArgs.cs`
- Create: `src/MyOwnLLM.Cli/Settings.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/CliArgsTests.cs`, `tests/MyOwnLLM.Gateway.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: `public sealed record CliArgs(string Command, string? Text, string? RepoPath, bool Stream, string? Id, bool? Accepted)`; `Command` ∈ `session` (no arguments), `ask`, `status`, `feedback`. `ask` accepts `--repo <path>`, `--stream` (default) and `--no-stream`. `Usage` starts with `usage: jazari`.
- Produces: `public sealed record Settings(string? Key, Uri GatewayUrl); public static Settings Settings.FromEnvironment(Func<string, string?> getEnv)` — `JAZARI_*` first, then `MYOWNLLM_*`, URL default `http://127.0.0.1:5080`.

- [ ] **Step 1: Update/write failing tests**
  - `No_arguments_means_session`: `Parse([]) == new CliArgs("session", null, null, true, null, null)` (replaces the old "no args throws" case).
  - `Ask_streams_by_default` and `Ask_no_stream`: `Parse(["ask","q"]).Stream == true`; `Parse(["ask","q","--no-stream"]).Stream == false`.
  - Existing cases updated for `Stream` defaulting to true; usage text contains `usage: jazari`.
  - `SettingsTests`: `JAZARI_KEY` wins over `MYOWNLLM_KEY`; `MYOWNLLM_KEY` used when `JAZARI_KEY` missing; URL default; `JAZARI_URL` respected.
- [ ] **Step 2: Run** `dotnet test --filter "CliArgsTests|SettingsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `Settings` and the `CliArgs` changes; csproj properties as listed. Leave `Program.cs` compiling (it's rewritten in Task 6).
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(cli): rename to jazari, stream by default, JAZARI_* settings`.

### Task 3: Terminal theme, color modes and components

**Files:**
- Create: `src/MyOwnLLM.Cli/Terminal/ColorMode.cs`, `src/MyOwnLLM.Cli/Terminal/Theme.cs`, `src/MyOwnLLM.Cli/Terminal/Ui.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/TerminalThemeTests.cs`, `tests/MyOwnLLM.Gateway.Tests/UiTests.cs`

**Interfaces:**
- Produces: `public enum ColorMode { None, Ansi16, Ansi256, TrueColor }`; `public static ColorMode ColorModeDetector.Detect(Func<string, string?> getEnv, bool outputRedirected)`.
- Produces: `public enum Tone { Primary, Secondary, Muted, Inactive, Line }`; `public sealed class Theme(ColorMode mode, bool unicode)` with `string Paint(string text, Tone tone)`, `string Code(string text)` (code background, resets at end), `string Glyph(char unicode)` (maps `✓ › · │` to fallbacks when `!unicode`).
- Produces: `public sealed class Ui(TextWriter output, Theme theme, int width)` with `Wordmark(string version)`, `Label(string text)` (uppercase, muted), `Rows(IEnumerable<(string Key, string Value)> rows)` (key column 12 wide, values wrapped to `width`), `ToolLines(string label, IEnumerable<string> doneSteps)`, `ActionRequired(string what, string command)`, `Error(string message, string? cause, string? next)`, `Metadata(string text)`, `Divider()`, `Prompt()`.

- [ ] **Step 1: Write failing tests**
  - Detection table: `NO_COLOR=1` → None; redirected → None; `COLORTERM=truecolor` → TrueColor; `WT_SESSION=x` → TrueColor; `TERM=xterm-256color` → Ansi256; nothing → Ansi16.
  - `Paint_truecolor_uses_exact_rgb`: `Paint("x", Tone.Muted)` contains `\e[38;2;119;119;119m` and ends with `\e[0m`.
  - `Paint_256_uses_index`: contains `\e[38;5;243m`.
  - `Paint_none_is_plain`: `Paint("x", Tone.Muted) == "x"`; `Code("y") == "y"`.
  - `Glyph_falls_back_without_unicode`: `✓→+`, `›→>`, `·→-`, `│→|`.
  - `Wordmark_is_spaced_capitals`: plain-mode output starts with `J A Z A R I  0.2.0`.
  - `Rows_wrap_at_40_columns`: a 100-char value → every output line ≤ 40 chars, continuation lines indented 12.
  - `Error_block_has_label_message_cause_next`: plain output lines are `ERROR`, message, `Cause: …`, next step.
  - `Redirected_output_has_no_escape_sequences`: a Ui built with `ColorModeDetector.Detect(_ => null, outputRedirected: true)` writes a full welcome + error with no `\e` character.
- [ ] **Step 2: Run** `dotnet test --filter "TerminalThemeTests|UiTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the three files. 16-color mapping: primary `97`, secondary `37`, muted/inactive/line `90`; code background omitted below 256 colors.
- [ ] **Step 4: Run** the same filter. Expected: pass.
- [ ] **Step 5: Commit** `feat(cli): add terminal theme and components`.

### Task 4: Streaming Markdown renderer

**Files:**
- Create: `src/MyOwnLLM.Cli/Terminal/MarkdownStream.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/MarkdownStreamTests.cs`

**Interfaces:**
- Consumes: `Theme` (Task 3), `ConsoleText.Sanitize`.
- Produces: `public sealed class MarkdownStream(TextWriter output, Theme theme, int width)` with `void Write(string token)` and `void Complete()`. Tokens print immediately as plain text of the current line; when a line ends, it is re-emitted styled **only if styling changes it** (heading/list/inline code/code fence) by returning to line start (`\r` + clear-line) — one redraw per line, never earlier lines. Table lines (starting `|`) are buffered and printed aligned when the table ends.

- [ ] **Step 1: Write failing tests** (plain-mode theme unless stated; feed tokens split mid-line)
  - `Heading_drops_hashes`: `"## Setup\n"` → output line `Setup`.
  - `List_items_indent_two`: `"- a\n- b\n"` → `"  - a"`, `"  - b"`.
  - `Code_fence_lines_get_code_background` (truecolor): lines between ``` fences contain `\e[48;2;21;21;21m`; fence lines themselves print the language tag muted (`csharp`) or nothing.
  - `Inline_code_is_backgrounded` (truecolor): `` "use `Foo()` here\n" `` contains the code background around `Foo()`.
  - `Table_is_aligned_after_it_ends`: `"| a | bb |\n|---|---|\n| ccc | d |\nafter\n"` → two rows with columns aligned, separator row omitted, header muted.
  - `Unclosed_fence_resets_on_complete` (truecolor): `"```\ncode"` then `Complete()` → output ends with `\e[0m`.
  - `Escape_codes_are_stripped`: token `"\e[31mred"` → no `\e[31m` in output.
  - `Wraps_long_lines_at_width_40`: a 120-char paragraph → no rendered line over 40 visible chars.
- [ ] **Step 2: Run** `dotnet test --filter MarkdownStreamTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `MarkdownStream` as a small line state machine (`Text`, `InFence`, `InTable`).
- [ ] **Step 4: Run** the same filter. Expected: pass.
- [ ] **Step 5: Commit** `feat(cli): add streaming markdown renderer`.

### Task 5: Gateway client and session commands

**Files:**
- Create: `src/MyOwnLLM.Cli/GatewayClient.cs`, `src/MyOwnLLM.Cli/Session.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/SessionTests.cs`

**Interfaces:**
- Consumes: `Settings` (Task 2), `Ui`/`MarkdownStream` (Tasks 3–4), `SseChunk`, `/v1/info` (Task 1).
- Produces: `public interface IGatewayClient { Task<GatewayInfo> InfoAsync(CancellationToken ct); IAsyncEnumerable<SseChunk> ChatAsync(IReadOnlyList<ChatTurn> history, string question, string project, CancellationToken ct); Task<(string Id, string Summary)> StatusAsync(string project, CancellationToken ct); Task<bool> FeedbackAsync(string id, bool accepted, CancellationToken ct); }`, `public sealed record GatewayInfo(string Name, string Model, string Version)`, `public sealed record ChatTurn(string Role, string Content)`, `public sealed class GatewayException(string message, string? cause) : Exception(message)` (thrown for unreachable/401/error responses with the gateway's `error` text), `GatewayClient(HttpClient http) : IGatewayClient`.
- Produces: `public sealed class Session(IGatewayClient gateway, Ui ui, Func<MarkdownStream> newRenderer, string project)` with `Task<ConnectionState> ConnectAsync(CancellationToken ct)` (`enum ConnectionState { Available, GatewayNotRunning, KeyRejected }`), `Task HandleAsync(string input, CancellationToken ct)` (a question or `/command`), `IReadOnlyList<ChatTurn> History`, `bool ExitRequested`.
- Commands: `/help`, `/status`, `/model`, `/clear`, `/retry`, `/details`, `/exit`; unknown `/x` → muted `Unknown command /x. Try /help.`

- [ ] **Step 1: Write failing tests** (fake `IGatewayClient`, plain-mode `Ui` over `StringWriter`)
  - `Question_streams_and_records_history`: fake yields chunks `"Hel"`, `"lo"`, final id `ix-3` → output contains `YOU`, `J A Z A R I`, `Hello`, `ix-3`; `History` = user + assistant turns.
  - `History_is_sent_with_next_question`.
  - `Clear_forgets_history`.
  - `Retry_resends_last_question`.
  - `Unknown_command_hints_help`.
  - `Gateway_down_prints_error_block_not_exception`: fake throws `GatewayException("model server not reachable at …", null)` → output contains `ERROR` and the message; `Details` then prints the cause/exception text.
  - `Connect_reports_states`: fake `InfoAsync` → success / `GatewayException` with 401 cause / unreachable → the three `ConnectionState` values.
  - `Cancel_mid_stream_keeps_partial_answer`: fake yields `"part"` then waits on the token; cancel → output contains `part` and a muted `stopped`; no exception escapes; `History` has the partial answer.
  - `Status_prints_summary`, `Model_prints_name_and_model`.
- [ ] **Step 2: Run** `dotnet test --filter SessionTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `GatewayClient` (moves today's HTTP code out of `Program.cs`; sends `X-Project-Path`, `stream:true`, the history as `messages`) and `Session`. `THINKING` is printed muted before the first chunk and erased with `\r` + clear-line when it arrives.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(cli): add gateway client and session commands`.

### Task 6: Interactive loop, welcome screen and one-off commands

**Files:**
- Modify: `src/MyOwnLLM.Cli/Program.cs` (rewrite), `src/MyOwnLLM.Cli/MyOwnLLM.Cli.csproj` (add `PrettyPrompt` 6.0.5)
- Create: `src/MyOwnLLM.Cli/Welcome.cs`, `src/MyOwnLLM.Cli/InputLoop.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/WelcomeTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `public static class Welcome { public static void Render(Ui ui, string version, string model, string directory, ConnectionState state); }` — exactly the spec's screen: wordmark, tagline, rows `Model / Directory / Connection / Session`, the four command rows (`/help`, `/status`, `/model`, `/clear`), then the prompt. Directory shown with the home folder as `~`.
- `InputLoop` (PrettyPrompt): prompt `› ` (glyph-aware), placeholder `Ask something...`; Enter submits; Shift+Enter, Alt+Enter and Ctrl+J insert a newline; Tab completes `/` commands; history file `%APPDATA%\Jazari\history` (`~/.config/jazari/history` elsewhere); Esc clears input; Ctrl+C while generating cancels the session's token, while idle clears input, twice in a row exits; Ctrl+D on empty input exits.
- `Program.cs`: sets UTF-8 output; `session` → connect, welcome, loop; `ask`/`status`/`feedback` → one-off through `Session`/`GatewayClient` without the welcome; missing key → exit 2 with `set JAZARI_KEY (run: dotnet run --project src/MyOwnLLM.Gateway -- add-key)`.

- [ ] **Step 1: Write failing tests**
  - `Welcome_matches_spec_layout` (plain mode, width 80): lines in order `J A Z A R I  0.2.0`, blank, `Intelligence, precisely applied.`, blank, `Model       Granite 4.1 3B · local`, `Directory   ~/EduCore` (home replaced), `Connection  Available`, `Session     New session`, blank, the four command rows.
  - `Welcome_shows_connection_problems`: `GatewayNotRunning` → `Connection  Gateway not running`; `KeyRejected` → `Connection  Key rejected`.
  - `Welcome_fits_40_columns`: no line over 40 chars.
- [ ] **Step 2: Run** `dotnet test --filter WelcomeTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `Welcome`, `InputLoop`, new `Program.cs`. Remove code from `Program.cs` that now lives in `GatewayClient`/`Session`.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Manual check** with the real gateway + llama-server: `dotnet run --project src/MyOwnLLM.Cli` in `C:\Users\enim\EduCore` shows the welcome; a question streams; Ctrl+C stops it; ↑ recalls it; `/status` answers ≤ 120 s; `$env:NO_COLOR=1` gives plain output.
- [ ] **Step 6: Commit** `feat(cli): interactive jazari session with welcome screen`.

### Task 7: Install as a global tool and rename the docs

**Files:**
- Create: `scripts/install-jazari.ps1`
- Modify: `docs/phase1-runbook.md` (rename to Jazari, `JAZARI_KEY`, install steps, recommended font/background), `README.md` (one-paragraph Jazari intro, pronunciation "jah-ZAH-ree", quick start)

- [ ] **Step 1:** `scripts/install-jazari.ps1` runs `dotnet pack src/MyOwnLLM.Cli -c Release -o artifacts` then `dotnet tool update -g Jazari --add-source artifacts` (install when absent, update otherwise).
- [ ] **Step 2: Run** the script. Expected: `jazari` resolves in a **new** terminal (`Get-Command jazari`), and `jazari ask "Reply with just: ok"` prints `ok` and an `ix-` line with the stack running.
- [ ] **Step 3:** Update the runbook and README (README replaces the current UTF-16 one-liner with UTF-8 text).
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `docs: install jazari as a global tool; rename docs to Jazari`.
