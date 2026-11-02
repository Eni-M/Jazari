# Terminal UI patterns for Jazari's interactive CLI (panel, streaming, input, interrupts on Windows)

Research date: 2026-11-02. Labels used below:
- **[sourced]**: backed by the link given.
- **[code: other project]**: read directly from that project's source. The Codex CLI source was a sparse clone of `openai/codex`, commit `18c6dbe`, dated 2026-11-02. The .NET source was a sparse clone of `dotnet/runtime` `main`, taken 2026-11-02.
- **[observed in Jazari]**: read from `/home/user/myownllm` (file:line).
- **[needs testing]**: has to be checked on Eni-M's machine (Windows Terminal, conhost, PowerShell, VS Code).

Network limits in this session: learn.microsoft.com, docs.rs and steipete.me could not be fetched (the host is blocked). Claims about those pages come from search-result summaries and are marked that way. GitHub could be cloned, but not read through the API.

## 1. How mature terminal assistants keep a bottom panel with real scrollback

### Takeaway
Codex CLI does what Jazari does. On Windows Terminal it avoids scroll regions altogether: it writes history rows from the top of the live area and pushes that area down with real newlines. On other terminals it uses a DECSTBM region that starts at row 1. Microsoft's own issue tracker confirms that partial scroll regions under ConPTY don't feed scrollback. So Jazari's choice not to use margins is the right one for Windows. The remaining risks are in wrapping, width and resize bookkeeping, not in the overall technique.

### Cited Findings
- Codex states its approach directly: "Codex uses the terminal scrollback itself for finalized chat history, so inserting a history cell is an escape-sequence operation rather than a normal ratatui render." — [code: other project] `codex-rs/tui/src/insert_history.rs` lines 1-5 ([repo](https://github.com/openai/codex/blob/main/codex-rs/tui/src/insert_history.rs))
- Codex chooses an insertion strategy per terminal. `ScrollbackStrategy::detect` returns `FullScreen` when `terminal.name == WindowsTerminal || WT_SESSION is set`, `Zellij` for Zellij, and `Standard` otherwise. The `FullScreen` doc comment says it "preserves terminal-native scrollback when partial scroll regions are unreliable". — [code: other project] `codex-rs/tui/src/tui/scrollback.rs` lines 27-37 and `insert_history.rs` lines 51-54 ([repo](https://github.com/openai/codex/blob/main/codex-rs/tui/src/tui/scrollback.rs))
- How Codex's `FullScreen` mode works:
  - It clears from the viewport top down: "The existing viewport is immediately replaced in the same draw pass. Clear it before terminal scrolling can move composer contents into scrollback."
  - It writes each history line separated by `\r\n`.
  - It then prints `\r\n` + clear-to-end-of-line once for each viewport row, "so history ends immediately above the composer", and redraws the viewport lower down.
  - This is essentially Jazari's `WriteAbove` and `Show` sequence. — [code: other project] `insert_history.rs` lines 137-165
- How Codex's `Standard` mode works (non-Windows):
  - It sets `SetScrollRegion(1..viewport_top)`, moves to the last row of that region, and prints `\r\n` + line for each line, then resets the region.
  - When the viewport isn't at the bottom yet, it first pushes the viewport down with reverse index (`ESC M`) inside a region running from the viewport to the bottom.
  - A comment in `grow_viewport` explains the choice: "CSI S can discard departing rows in QTermWidget and xterm.js. Newlines at the bottom of the history region preserve native scrollback". — [code: other project] `insert_history.rs` lines 167-226; `tui/scrollback.rs` lines 66-80
- Codex pre-wraps history lines itself. It keeps URL-only lines unwrapped so terminals can still detect them as links, and it can instead let the terminal soft-wrap ("Writing raw source text through the terminal preserves its soft-wrap metadata") so that a resize reflows the text correctly. — [code: other project] `insert_history.rs` lines 122-133, 150; `HistoryLineWrapPolicy { PreWrap, Terminal }` lines 45-49
- Codex caps redraws at 120 FPS ("clamps draw notifications to a maximum of 120 FPS to avoid wasted work"). — [code: other project] `codex-rs/tui/src/tui/frame_rate_limiter.rs` lines 1-12
- microsoft/terminal #3673: scrolling inside a region that includes the top of the screen doesn't push lines into scrollback. A maintainer blamed ConPTY "not knowing anything about scrolling regions", so unless the full screen height scrolls, scrollback isn't updated. The issue was reopened and put on the backlog; the search results show no fix. — [microsoft/terminal#3673](https://github.com/microsoft/terminal/issues/3673) (from a search summary; the page itself was not fetched)
- Related ConPTY scrollback and region reports exist: an old DECSTBM status-bar corruption ([#132](https://github.com/microsoft/terminal/issues/132)), and GNU screen's scrollback not reaching Windows Terminal's ([#5284](https://github.com/microsoft/terminal/issues/5284)). — from search summaries
- Claude Code's scroll-region experiments caused real bugs:
  - A DECSTBM scroll-region experiment ("tengu_marlin_porch") was reported to corrupt the inline TUI on wide panes and with multi-line status lines. — [claudeissues #66013](https://claudeissues.com/issue/66013-decstbm-scroll-region-experiment-tengu-marlin-porch-corrupts-inline-tui-on-wide)
  - Windows Terminal rendering broke after `/statusline` because scroll margins were never reset. — [claudeissues #14716](https://claudeissues.com/issue/14716-windows-terminal-rendering-broken-after-statusline-scroll-margins-not-reset)
  - Both are third-party mirrors of GitHub issues.
- Claude Code rewrote its renderer while keeping React as the component model. Thariq Shihipar's explanation, as reported: when content exceeds the viewport, rendering happens offscreen, and Claude Code redraws its viewport dozens of times per second. He also said an alternate screen is possible but "the bar is quite high", which keeps native scrollback, selection and search. — secondary coverage via search ([steipete.me](https://steipete.me/posts/2025/signature-flicker), [AIM](https://analyticsindiamag.com/ai-news-updates/claude-code-uses-react-to-render-its-tui)); the primary post could not be fetched
- Users report Ink re-rendering the whole screen on each streamed chunk. This caused severe flicker in the VS Code/Cursor xterm.js terminal on Windows and in tmux, while one report says Windows Terminal is fine. — [claudeissues #18084](https://claudeissues.com/issue/18084-severe-screen-flickering-in-vs-code-cursor-integrated-terminal-on-windows), [#37076](https://claudeissues.com/issue/37076-terminal-rendering-causes-severe-flickering-in-tmux-and-multiplexed-terminals)
- Gemini CLI uses Ink's `<Static>` so committed history is never re-rendered. — [basehub forum](https://forums.basehub.com/google-gemini/gemini-cli/1) (secondary)
- claude-chill is a PTY proxy that intercepts Claude Code's synchronized-output blocks and emits only screen differences. This shows how much full-frame redraws cost. — [github.com/davidbeesley/claude-chill](https://github.com/davidbeesley/claude-chill)
- ratatui's `Terminal::insert_before` inserts content above an inline viewport, can do it without a full redraw using the `scrolling-regions` feature, and does nothing when the viewport isn't inline. — [ratatui-core inline.rs](https://docs.rs/crate/ratatui-core/0.1.1/source/src/terminal/inline.rs) (from a search summary)
- Textual inline mode (since 0.55.0) redraws by moving the cursor back to where the frame started. "Inline mode is not currently supported on Windows." An unmerged PR (#6673) removes a full-screen clear on resize "to preserve terminal scrollback". — [Textual blog](https://textual.textualize.io/blog/2024/04/20/behind-the-curtain-of-inline-terminal-applications/), [guide](https://textual.textualize.io/guide/app/), [PR #6673](https://github.com/Textualize/textual/pull/6673) (from search summaries)
- prompt_toolkit's `patch_stdout` proxy "will make sure that the text appears above the prompt, and that it doesn't destroy the output from the renderer". Since a November 2025 change it also redirects stderr. This is the pattern Aider relies on. — [prompt_toolkit PR #2029](https://github.com/prompt-toolkit/python-prompt-toolkit/pull/2029.diff) (from a search summary)

### Inferences
- Jazari matches Codex's Windows Terminal path: write above, clear the panel, use real newlines, redraw the panel. That is strong outside validation of the "no scroll margins" decision for Windows Terminal and ConPTY.
- On Linux and macOS, Codex shows a DECSTBM region from row 1 can feed scrollback. Jazari doesn't need it, but it could cut redraws there (no clear-and-redraw of the panel for each line that reaches it).
- The main gap between Jazari and Codex is **re-rendering on width change**. Codex keeps the source and lets either its own pre-wrap or the terminal's soft-wrap handle resize. Jazari hard-wraps once at the width the answer started with and never reflows (see section 7).
- Jazari already does the right thing that Ink-based tools lacked: it diffs rows and writes only changed panel rows, and the conversation is append-only.

### Gaps
- No primary Anthropic write-up was retrievable (blocked host), so Claude Code's current algorithm (differential renderer, how it handles Windows) stays secondhand.
- I didn't check opencode or Spectre.Console `LiveDisplay` behavior (resize, scrollback) in this pass. No sourced findings for either.
- Whether microsoft/terminal #3673 was fixed in 2026 releases is unknown.

## 2. Windows Terminal and conhost specifics

### Takeaway
Windows Terminal supports synchronized output (DEC 2026) in stable 1.24 and preview 1.25, as of March 2026. In .NET, `TreatControlCAsInput` turns off `ENABLE_PROCESSED_INPUT`, and the key-polling APIs silently drop window-resize records, so polling the window size is the only resize signal .NET offers. Character width is the biggest correctness risk: conhost (ConPTY), Windows Terminal and the app each have their own width model, and Jazari's model is very rough.

### Cited Findings
- Microsoft's Command Line blog (2026-03-05) lists Windows Terminal Preview 1.25 support for the "Synchronized Output" DEC private mode, which applications can use "to reduce flickering and tearing", and says stable 1.24 includes those features. — [devblogs: Windows Terminal Preview 1.25](https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-25-release/) (from a search summary)
- The synchronized-output spec lets apps query support with `CSI ? 2026 $ p`; a reply of `CSI ? 2026 ; 0 $ y` means unsupported. Windows Terminal's tracker is wt#8331. — [Christian Parpart's spec gist](https://gist.github.com/christianparpart/d8a62cc1ab659194337d73e399004036)
- Windows Terminal 1.22 release notes say the host now handles grapheme clusters (emoji with ZWJ, combining characters, flags) and that output from hosted apps is now translated directly from API calls to VT instead of rendering point-in-time snapshots. — via search summary of [microsoft/terminal releases](https://github.com/Microsoft/console/releases/)
- PR #16916 on width-mismatch: in a ZWJ sequence such as the rainbow flag, "most apps will expect the combination to be 3" columns, and "the conhost width calculations and the terminal width calculations need to be in sync". — [microsoft/terminal#16916](https://GitHub.com/microsoft/terminal/pull/16916)
- A third-party comparison (wtmux 4.0.0 release notes) reports that conhost, OpenConsole 1.22 and Windows Terminal 1.24 each have their own width model, while ambiguous-width characters agree across all layers. — [wtmux 4.0.0](https://newreleases.io/project/github/fukuyori/wtmux/release/4.0.0) (third-party)
- In .NET on Windows, `Console.TreatControlCAsInput` reads and writes the input handle's console mode. It returns `(mode & ENABLE_PROCESSED_INPUT) == 0`, and its setter clears or sets `ENABLE_PROCESSED_INPUT`. — [code: other project] `dotnet/runtime` `src/libraries/System.Console/src/System/ConsolePal.Windows.cs` lines 453-481
- .NET `Console.KeyAvailable` on Windows calls `PeekConsoleInput` and skips "non-significant events" (anything that isn't a key-down key event), consuming them. Window-buffer-size records are therefore thrown away, not exposed. No `ENABLE_VIRTUAL_TERMINAL_INPUT` reference was found in that file, so `ReadKey` never sees VT input such as bracketed-paste markers. — [code: other project] `ConsolePal.Windows.cs` lines 330-352 (grep of the file)
- In .NET, Ctrl+C and Ctrl+Break both reach `Console.CancelKeyPress` through the signal path, as `ConsoleSpecialKey.ControlC` or `ConsoleSpecialKey.ControlBreak`, with `args.Cancel` copied back to the signal context. — [code: other project] `System.Console/src/System/Console.cs` lines 1043-1048
- Microsoft documents `CancelKeyPress` as firing for Ctrl+C or Ctrl+Break, with `Cancel = true` keeping the process alive. — [Console.CancelKeyPress docs](https://learn.microsoft.com/pt-pt/dotNet/API/system.console.cancelkeypress?view=netcore-3.1) (from a search summary)
- On Windows, pastes often arrive as a fast stream of `Char`, `Enter` and `Tab` key events instead of one paste event. Codex runs a timing-based `PasteBurst` state machine so that Enter inside a paste is a newline and not "submit". It briefly holds the first ASCII character to avoid flicker, and it doesn't hold non-ASCII or IME characters. — [code: other project] `codex-rs/tui/src/bottom_pane/paste_burst.rs` lines 1-40
- Codex turns off its keyboard-enhancement protocol on Windows (`!cfg!(windows) && ...`). — [code: other project] `codex-rs/tui/src/tui/alternate_screen.rs` line 97
- Codex has a dedicated Windows terminal probe that reads terminal colors "without consuming startup typeahead". So reading terminal replies while the user is already typing is a known Windows hazard. — [code: other project] `codex-rs/tui/src/terminal_probe/windows.rs` line 1, `windows_replay.rs` line 1

### Inferences
- Writing `\e[?2026h/l` is safe everywhere: terminals ignore private modes they don't know. Jazari gets tear-free frames on Windows Terminal 1.24+. In conhost and older Windows Terminal builds it gets no benefit, so flicker there depends on how many separate writes a frame takes.
- Without `ENABLE_PROCESSED_INPUT`, Ctrl+C arrives as a key, which Jazari wants. Ctrl+Break still arrives as a signal in .NET. Jazari's handler only cancels the signal when an answer is running (see section 7, Ctrl+Break).
- Bracketed paste isn't available through `Console.ReadKey`. Getting it would mean dropping to `ReadConsoleInputW` with `ENABLE_VIRTUAL_TERMINAL_INPUT` and parsing VT input, which is a big change. A Codex-style timing heuristic is the cheaper fix.

### Gaps
- Whether classic conhost (not Windows Terminal) honors DEC 2026 as of late 2026: not found.
- The exact Microsoft Learn wording on DECSTBM, DECSC/DECRC and `ENABLE_VIRTUAL_TERMINAL_INPUT`: the page was blocked, so nothing is quoted here.
- VS Code's terminal on Windows (xterm.js over ConPTY): no sourced finding on 2026 support or on cursor-position accuracy. **[needs testing]**
- IME composition with `Console.ReadKey` in Windows Terminal: no source found. **[needs testing]** with Microsoft Pinyin or Japanese IME: does the candidate window follow the caret while an answer streams?

## 3. Streaming Markdown and pacing

### Takeaway
Codex's streamed Markdown commits only completed lines. It keeps the source so it can re-render at a new width, holds back a table while it might still change, and paces output by queue depth and age with an immediate catch-up gear. It doesn't hold the first words. The research says slower-than-reading delivery hurts, while faster than reading doesn't help. That argues for little or no initial hold in Jazari, especially on a 6-15 tok/s laptop.

### Cited Findings
- Codex's `MarkdownStreamCollector` buffers token deltas and "exposes a commit boundary at each newline". It "does not parse markdown in production. It only defines stable source boundaries; rendering lives in the stream controllers so width changes can re-render from one accumulated source string." — [code: other project] `codex-rs/tui/src/markdown_stream.rs` header comments
- Codex's table holdback: "Agent streams with markdown tables keep the active table as mutable tail so adding a row can reflow earlier table rows instead of committing a stale render to scrollback". A likely header row is held until its delimiter row arrives. — [code: other project] `codex-rs/tui/src/streaming/table_holdback.rs` header
- Codex's adaptive chunking:
  - Smooth mode drains one queued line per commit tick; CatchUp mode "drains queued backlog immediately so display lag converges as quickly as possible".
  - It enters CatchUp at 8 queued lines or when the oldest line is 120 ms old, and exits at 2 lines or 40 ms.
  - Hysteresis: a 250 ms exit hold and a 250 ms re-entry hold. Severe backlog is 64 lines or 300 ms.
  - The policy "depends only on queue depth and queue age". — [code: other project] `codex-rs/tui/src/streaming/chunking.rs` lines 1-60, 85-116
- Andes (QoE for LLM text streaming): delivering tokens slower than the user reads hurts experience, and delivering faster than they read doesn't improve it. — [arXiv 2404.16283](https://arxiv.org/pdf/2404.16283) (from a search summary)
- "Streaming, Fast and Slow": a crowdsourced study where participants tuned stream speed to their reading pace (start speeds of 3-8 words/s). Preferred speeds fit log-normal distributions, and adaptive streaming improved satisfaction. — [arXiv 2504.17999](https://arxiv.org/pdf/2504.17999) (from a search summary)
- Practitioner advice: a fixed typewriter interval fails both ways, so speed should depend on how far behind the display is. Revealing by word instead of by character reduces mid-token flicker. — [dev.to: smoothing streamed LLM text](https://dev.to/mzsleepyzzz/smoothing-streamed-llm-text-in-react-it-looks-like-a-typewriter-loop-it-isnt-eb3) (blog, anecdotal)
- A token-bucket shaping blog claims a 34% cut in P95 perceived latency but gives no method. Treat it as unverified. — [omr.it.com](https://omr.it.com/blog/variable-rate-shaping-llm-token-emission-control/)

### Inferences
- Jazari's `TextPacer` already speeds up as the backlog grows: each piece is the backlog divided by 16, every 30 ms. That is the backlog-proportional idea and it's sound.
- Jazari's TextPacer holds the first text until it has 60 characters or 1.5 s has passed (`TextPacer.cs:28-34`, `NextPiece` line 73). At this laptop's measured 6-15 tok/s, about 25-60 chars/s at roughly 4 chars a token, that hold adds about 1-1.5 s to the perceived time to first word, after prompt reading has already taken 10-90 s. Per Andes, delay below reading speed is what hurts. A hold of a few hundred ms, or none, is the evidence-backed choice. This needs a quick A/B with Eni-M; no study measured initial holds directly.
- The pacer emits pieces by character count (`TextPacer.cs:75-77`), so words can appear split mid-word. Snapping to the next space follows the word-reveal advice.
- Jazari's live echo of the unfinished row is more immediate than Codex's line-gated commits. The cost is that Jazari can't reflow already-printed rows on resize. That's acceptable when it's documented and resize-mid-answer is handled (see section 7).

### Gaps
- No rigorous HCI study compares artificial smoothing with raw bursts for perceived speed in terminals. The evidence above is indirect.
- How Claude Code paces text: not found in a primary source.

## 4. Flicker and performance

### Takeaway
.NET's `Console.Out` flushes on every `Write` call. Each `Write` becomes a console write, so Jazari should put each frame in one string and write it once. Jazari mostly does, except in `WriteAbove`. Diff-based row redraw plus DEC 2026 is the same recipe Claude Code and claude-chill use.

### Cited Findings
- .NET builds `Console.Out` as `TextWriter.Synchronized(new StreamWriter(...){ AutoFlush = true })`. — [code: other project] `System.Console/src/System/Console.cs` lines 237-248
- claude-chill improves Claude Code's flicker by sending only screen differences inside synchronized-output blocks. — [claude-chill](https://github.com/davidbeesley/claude-chill)
- Codex limits draws to 120 FPS. — [code: other project] `frame_rate_limiter.rs`

### Inferences
- With AutoFlush on, every `_out.Write` in `PromptEditor.WriteAbove` (`PromptEditor.cs:669, 675, 678`) and every `output.Write` in `MarkdownStream.Echo` is a separate syscall. In conhost, which has no 2026 support, each one can show up as its own paint.
- `ConversationWriter` (`PromptEditor.cs:393-409`) sends every `Write` straight to the sink. `Ui.Row` and `UserMessage` call `WriteLine` once per wrapped row, so a 30-row block means 30 full `WriteAbove` cycles: lock, cursor read, panel check, sync on and off. Grouping per call would cut that.

### Gaps
- No measurement of conhost vs Windows Terminal paint cost per write. **[needs testing]**: stream a long answer in both and look for panel flicker.

## 5. Accessibility and degraded modes

### Takeaway
Windows Terminal is the recommended host for NVDA users. Agent TUIs that move a virtual caret without moving the real cursor confuse screen readers, and streaming output should be announced in batches. Jazari handles NO_COLOR and piped output. It has no explicit plain or screen-reader mode for an interactive terminal, and it assumes a dark background.

### Cited Findings
- An NVDA user reports canvas-rendered terminals (xterm.js in some apps) hide content from the accessibility tree. Windows Terminal is suggested instead because it has good screen-reader support. The same thread says fast-streaming output costs accessibility-tree updates and should be batched, announcing key events only. — [t3code discussion #6884](https://github.com/pingdotgg/t3code/discussions/6884)
- Claude Code issue: on Windows + WSL2, what NVDA announced while arrowing through `/mcp` didn't match the focused item. The suspected cause is that the TUI's virtual cursor didn't move the real cursor; the proposed fix is an explicit cursor move on each keypress. — [claudeissues #61497](https://claudeissues.com/issue/61497-bug-mcp-tui-arrow-key-navigation-is-inaccessible-with-nvda-screen-reader-on-wind)
- Windows Terminal issue: NVDA re-read the prompt and current line on Alt+B even with dynamic content reporting turned off; later reported fixed in cmd. — [microsoft/terminal#14685](https://github.com/microsoft/terminal/issues/14685)
- The NO_COLOR convention says color is suppressed when `NO_COLOR` is present and not empty. — [no-color.org](https://no-color.org/) (not fetched this session; the convention is long-standing)

### Inferences
- Jazari moves the real cursor to the caret after every frame (`PromptEditor.cs:648, 675`), which helps screen readers. But during streaming, the cursor jumps to the conversation row and back for each write, and the panel at the bottom is repainted again and again. Under NVDA's dynamic content reporting that may be read out repeatedly. **[needs testing]** with NVDA + Windows Terminal.
- A `--plain` flag or `JAZARI_PLAIN=1` would give screen-reader and braille users a predictable stream. It could reuse the existing piped path (`TextLineReader`, no panel) while keeping the terminal.
- The palette is fixed near-white on dark (`Theme.cs:16-26` Primary `#ECECE8`; panel band `#121316`). On a light Windows Terminal scheme, or with Windows high contrast, text would be low contrast or the bands would clash. Codex probes terminal colors on Windows for this reason (section 2).

### Gaps
- No source found on how NVDA or Narrator treat DEC 2026 frames or fixed-position redraws in Windows Terminal.
- Windows high-contrast themes and Windows Terminal's "adjust indistinguishable colors" interaction with 24-bit SGR: not researched.

## 6. Piped and non-TTY mode, and color detection

### Takeaway
Jazari's detection order follows the common conventions. There are small gaps: `TERM=dumb`, `FORCE_COLOR`/`CLICOLOR_FORCE`, and `WT_SESSION` being inherited by processes that aren't actually in Windows Terminal.

### Cited Findings
- Codex also treats the presence of `WT_SESSION` as "this is Windows Terminal". — [code: other project] `tui/scrollback.rs` lines 30-33
- NO_COLOR convention. — [no-color.org](https://no-color.org/)

### Inferences
- `ColorModeDetector.Detect` (`ColorMode.cs:9-13` of the file, around line 285 of the combined listing): `WT_SESSION` forces truecolor. A shell or VS Code started *from* Windows Terminal inherits `WT_SESSION`. VS Code's terminal supports truecolor anyway, so the effect is likely harmless, but the same inheritance would wrongly enable Windows Terminal-only behavior if any were keyed on it.
- `TERM=dumb` isn't treated as no-color or no-cursor-movement. On Windows that is rare.

### Gaps
- None researched further.

## 7. Specific risks and gaps in Jazari's current code (observed, not sourced)

All items are **[observed in Jazari]**. Paths are relative to `/home/user/myownllm/src/MyOwnLLM.Cli/`. Severity is my estimate.

1. **High: the panel-collision check counts `\n` only, not soft-wrapped rows.** `WriteAbove` decides whether text reaches the panel with `_conversationRow + text.Count(c => c == '\n') + 1 >= _drawnTop` (`Terminal/PromptEditor.cs:661`). It relies on every writer pre-wrapping to `Console.WindowWidth - 1` (`Program.cs:63`). Anything that physically wraps writes over panel rows that `_drawn` still thinks are intact. That covers resize-narrower mid-answer, wrong width estimates, and the welcome or report text written raw (`InputLoop.cs:451` `ui.Output.Write(report)`). The frame diff then won't repaint those rows until the next key press or write (`Show` at :636). Codex avoids this by owning the wrap (`PreWrap`) or by letting the terminal soft-wrap in full-screen insertion.
2. **High: the answer's width is fixed when it starts.** `new MarkdownStream(sessionOutput, theme, Columns(), ScreenRows())` (`Program.cs:112`) captures the width once per answer. After a narrower resize mid-answer, every row wider than the new width wraps physically, which leads to item 1. `ClearEcho`'s `\r\e[2K` (`Terminal/MarkdownStream.cs:100`) then only clears the last physical row of the echo, leaving partial duplicate text. Windows Terminal also reflows old lines on resize, so the cached `_conversationRow` (`PromptEditor.cs:430, 633`) may no longer match the screen. **[needs testing]**: resize Windows Terminal narrower and wider while an answer streams.
3. **Medium: the width model is rough and inconsistent across components.**
   - `ConsoleText.IsWide` (`ConsoleText.cs:24-27`) counts *every* surrogate as wide: astral emoji count 2 per surrogate in some paths, and astral narrow characters count wide. BMP emoji such as ✅ and ⚠ (U+2600-27BF) count as narrow, and so do combining marks, ZWJ and VS16.
   - `Ui.Wrap` measures `string.Length` (`Terminal/Ui.cs:173-193`) and can split a surrogate pair at `w[..max]` (:184). `MarkdownStream.Split` cuts every `room` UTF-16 units and can also split pairs (`MarkdownStream.cs:164-171`).
   - `MarkdownStream.Layout` halves the whole line's room if *any* wide character appears (`MarkdownStream.cs:160, 166`). One emoji in a paragraph wraps it at half width.
   - `PromptPanel.Layout` handles surrogates but uses the same `IsWide` (`PromptEditor.cs:291`).
   - Because conhost (ConPTY) reports the cursor position from its own width model (section 2), `ReadCursor` (`PromptEditor.cs:623-627`) and Jazari's own layout can disagree on emoji or CJK text.
   - Suggested fix: a single `ConsoleText.Columns(string)` based on grapheme clusters (`StringInfo`/`Rune` + East Asian Width + emoji presentation) used by every wrapper.
4. **Medium: `StampTime` ignores the panel's height and soft wraps.** It uses `\e[{_rows}A` unless `_rows >= screenRows - 1` (`MarkdownStream.cs:54-55`). The visible conversation area is `screenRows - panelRows` (the panel is at least 3 rows; more with the live row, the menu or the queue). An answer between those two heights has its first row in scrollback; `CUU` stops at row 0 and the time lands beside the wrong row. Fix: pass the conversation area height, or let the panel report it.
5. **Medium: Ctrl+Break while idle exits without cleanup.** With `TreatControlCAsInput = true` (`PromptEditor.cs:486-487`), Ctrl+C is a key, but Ctrl+Break still raises `CancelKeyPress` (section 2). `onCancel` sets `e.Cancel = StopAnswer()` (`InputLoop.cs:473`), which is false when idle. The process is then killed without `editor.StopAsync()` (`InputLoop.cs:456`), so the panel stays on screen and the cursor may be left hidden (each frame writes `\e[?25l` ... `\e[?25h`, so a kill between those leaves it hidden). Also consider an `AppDomain.ProcessExit` and `PosixSignalRegistration` handler that restores `\e[?25h` and clears the panel.
6. **Medium: paste detection depends on `Console.KeyAvailable` being true right after Enter** (`PromptEditor.cs:547-548`, `PromptBuffer.Apply` :49). Windows pastes arrive as key-event bursts (section 2, Codex `paste_burst.rs`). If a burst is split across console reads, an embedded newline submits half the paste. Codex uses a timing window rather than "is another key queued right now". **[needs testing]**: paste a 200-line file into conhost and into Windows Terminal several times.
7. **Low-medium: large pastes are O(n²).** Every pasted character runs `Text.Insert` (an O(n) copy) and `Refresh()`, which calls `Completions.For` (`PromptEditor.cs:100-105, 129-131`). Rendering is deferred (good), but a 50 KB paste does about 50k string copies. For text starting `/read ` or `/edit `, `Completions.Paths` lists a directory per character until a space arrives.
8. **Low-medium: one conversation write is several flushes, and sync blocks nest wrongly.** `WriteAbove` writes `\e[?2026h` + text (:669), then `Render()` → `Show` writes its own `\e[?2026h ... \e[?2026l` (:649), which *ends* the outer sync early (mode 2026 doesn't nest), then the caret move (:675), then `\e[?2026l` (:678). Building one string per `WriteAbove` gives one syscall and one real synchronized frame. `ConversationWriter.Write` is per call, so `Ui.Row`'s per-row `WriteLine` runs `WriteAbove` once per row (`Ui.cs:42`).
9. **Low: input polling every 25 ms.** `PumpAsync` busy-polls `KeyAvailable` + `Task.Delay(25)` (`PromptEditor.cs:541-545`): up to 25 ms latency per key and about 40 wakeups/s even when idle (a battery cost on the T480). .NET discards resize records in `KeyAvailable` (section 2), so polling is the only resize signal through `System.Console`. A blocking `ReadKey` on a dedicated thread plus a slower size poll (100-250 ms) would cut idle wakeups. That needs care, because `ReadKey` holds the console input lock.
10. **Low (cross-platform): `Console.GetCursorPosition` on every conversation write** (`PromptEditor.cs:625`). On Windows it is a cheap `GetConsoleScreenBufferInfo` call. On Linux and macOS .NET implements it with a DSR query and reads the reply from stdin, while `PumpAsync` reads keys on another thread. That is a race the spec's goal 5 (Linux/macOS) would hit. Tracking the cursor arithmetically, as Codex does with `last_known_cursor_pos`, avoids the round trip. (I didn't check the Unix .NET implementation this session; I'm inferring from how DSR works.)
11. **Low: `TakeOverScreen` clears scrollback.** It writes `\e[3J` (`Ui.cs:31`), which wipes the user's previous shell scrollback. Claude Code and Codex keep it. This is a design choice; flag it to Eni-M.
12. **Low: Ctrl+C keeps showing the backlog.** After cancel, `FinishAsync` → `pacer.DrainAsync()` still shows everything buffered, over about 6 ticks (`Session.cs` catch block around line 202; `TextPacer.cs:12, 45-49`). It's short (about 180 ms), but "stop" should arguably drop the unshown backlog. `TextPacer` would need a `Discard()`.
13. **Low: inline styling is applied per wrapped row.** `WriteRow` runs `Link().Replace` and `Inline` on each row (`MarkdownStream.cs:144`). A `` `code` `` span or a `[text](url)` link that wraps across rows shows raw backticks or brackets. Single-asterisk italics aren't handled. Tables print nothing until the table ends (`MarkdownStream.cs:120`). That's acceptable; Codex keeps a mutable tail instead.
14. **Low: the full-width rule depends on deferred wrap.** `Rule()` prints `Width + 1` = `WindowWidth` characters (`Ui.cs:61`), relying on pending-wrap (DECAWM) behavior. **[needs testing]** in classic conhost and the VS Code terminal.
15. **Low: `Ask` blocks a thread** with `ManualResetEventSlim.Wait()` (`PromptEditor.cs:708`). This is sync-over-async inside the session; fine today, but a future async caller would deadlock on a single-threaded context.
16. **Unclear (the design spec disagrees with the code):** the spec says "no spinner, no ticking timer" and "Esc clears the current input" (`docs/superpowers/specs/2026-10-30-jazari-cli-design.md`, Conversation and keys tables). The code ticks `Thinking · 12s` every second (`ThinkingIndicator.cs`), and Esc only closes the menu (`PromptEditor.cs:60-61`). The canvas superseded parts of the spec (OPTIMIZATION-LOG "Canvas interface"); the spec should be updated to match.

### Suggested priority (inference)
1. One width function with grapheme awareness, plus re-wrap or redraw on resize (items 1-3).
2. A single buffered write per frame, with sync nesting fixed (item 8).
3. Ctrl+Break and exit cleanup (item 5).
4. Paste-burst timing (items 6-7).
5. Initial pacing hold cut to 0-300 ms and word-boundary pieces (section 3).
6. A `--plain` screen-reader mode (section 5).
7. Spec sync (item 16).
