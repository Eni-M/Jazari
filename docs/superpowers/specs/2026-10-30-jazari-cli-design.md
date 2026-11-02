# Jazari CLI — design

Date: 2026-10-30 · Owner: Eni-M · Status: draft for approval
Builds on: `2026-10-30-myownllm-design.md` (Phase 1 gateway + `myllm` CLI, merged to main).

## Intent

Turn the one-shot `myllm` command into **Jazari**: a terminal environment you open by typing `jazari`, which feels like a carefully engineered instrument rather than a chatbot. Minimal, focused, noise-free, monochrome. The interface must never compete with the work being done.

Reference for interaction *quality* only: Claude Code. Jazari must not imitate its branding, colors, layout or logo.

## Goals

1. `jazari` opens an interactive session in the current folder; `jazari ask "…"` stays for one-offs.
2. One reusable terminal design system (components, not per-screen styling) that every screen uses.
3. Answers **stream by default** (Eni-M, 2026-10-30), rendered as restrained Markdown.
4. Installable as a `jazari` command on this PC and other PCs.
5. Works in Windows Terminal, PowerShell, and Linux/macOS terminals, degrading gracefully.

## Non-goals (this design)

- Implementing tools (reading files, running commands) or the approval flow. The **look** of those components is specified here so they're consistent when tools arrive; building them is a later feature.
- `/memory`, web search, remote/API models, multi-user use.
- Syntax highlighting (optional per the source spec; deferred).

## Anti-goals

- Colorful accents, boxes around everything, large ASCII logos, spinners, decorative animation, constant status refreshes, stack traces by default, cluttered welcome screen.
- Anything that looks like Claude Code.
- Readability sacrificed for minimalism.

## Decided by Eni-M

- **Name:** Jazari. **Wordmark:** spaced capitals, `J A Z A R I` (rendered `JAZARI` with letter spacing, i.e. `J A Z A R I` in the terminal).
- **Visual identity (superseded 2026-11-02 by the Jazari CLI Interface canvas, see docs/OPTIMIZATION-LOG.md):** Eni-M's spec (pasted 2026-10-30) was the starting point: monochrome palette, monospaced type, labels instead of colors, `›` input marker, `✓`/`!` in gray, `ERROR` label, indented muted tool activity, no boxes. Eni-M called it a suggestion: details may be refined for what looks best, as long as the anti-goals hold.
- **Streaming by default.** This supersedes the main design's "buffered by default"; the gateway keeps buffered as its API default, the CLI always asks for streaming.
- The mockup shown on 2026-10-30 (welcome → question → inspecting → running → action required → error) is approved as the look.

## Constraints

- A CLI cannot set the terminal's **font** or **background color**. Jazari recommends JetBrains Mono (fallback Cascadia Code, Consolas) and background `#0B0B0B` in the README/runbook; it only colors its own text and code-block backgrounds.
- Prompt reading is slow on this laptop (~30–47 tok/s): the first token can take 10–90 s. The waiting indicator must be honest without becoming a constant refresh.
- Model output is untrusted: all of it passes through the existing `ConsoleText.Sanitize` before reaching the terminal.

## Design system

### Palette and fallbacks

| Token | Truecolor | 256-color | 16-color | `NO_COLOR` |
|---|---|---|---|---|
| `text.primary` | `#F2F2F2` | 255 | bright white | default |
| `text.secondary` | `#BBBBBB` | 250 | white | default |
| `text.muted` | `#777777` | 243 | bright black | default |
| `text.inactive` | `#555555` | 240 | bright black | default |
| `surface.code` | `#151515` bg | 233 bg | none | none |
| `surface.raised` | `#1C1C1C` bg | 234 bg | none | none |
| `line` | `#303030` | 236 | bright black | `-` characters |

Capability detection order: `NO_COLOR` set → no color; `COLORTERM=truecolor|24bit` or Windows Terminal (`WT_SESSION`) → truecolor; `TERM` contains `256` → 256; else 16. Because every state is also a word (`ERROR`, `ACTION REQUIRED`, `YOU`), meaning survives with no color at all.

Unicode: `✓ › ·` are used when the console encoding is UTF-8 (set by Jazari on Windows); fallbacks `+ > -` otherwise.

### Components

| Component | Rendering |
|---|---|
| Wordmark | `J A Z A R I` in `text.primary`, followed by version in `text.muted` |
| User label | `YOU`, muted, small caps feel (uppercase, letter-spaced) |
| Assistant label | `J A Z A R I`, `text.primary` |
| Message body | `text.primary`, wrapped to terminal width, one blank line between messages |
| Section label (tools, states) | uppercase muted word: `INSPECTING`, `RUNNING`, `THINKING` |
| Tool line | indented 2, muted, `✓` per finished step |
| Approval | `ACTION REQUIRED` label, a `│` rule in `text.secondary` on the left (the only "border" in the system, used so approvals can't be mistaken for answers), options `[y] Allow once  [n] Deny  [a] Always allow` |
| Error | `ERROR` label, one-line message, muted `Cause:` line, muted next step, optional `/retry` |
| Warning | `!` muted prefix |
| Divider | thin line in `line`, only between welcome and conversation |
| Code block | `surface.code` background, full width, no frame; language tag muted on the first line |
| Inline code | `surface.code` background |
| Metadata | muted, one line after an answer: `ix-12 · 41.2 s` |
| Input | `›` in `text.secondary`, placeholder `Ask something...` in `text.inactive` |

### Markdown (restrained)

Headings → `text.primary` with a blank line before (no `#` characters, no underline). Lists → `- ` / `1. ` indented 2. Tables → aligned columns, header muted, rows separated by spacing only. Links → text then muted URL. Bold → `text.primary` (only weight/brightness, never color). Code blocks and inline code as above.

**Streaming render rule:** text is printed as tokens arrive; styling is applied when a line completes (heading, list item, inline code). Inside a fenced code block, each completed line gets the code background. Tables are buffered until the table ends, then printed aligned. This keeps streaming immediate without redrawing earlier output.

## Screens

**Welcome** (printed once at start; scrolls away naturally, never cleared):
```
J A Z A R I  0.2.0

Intelligence, precisely applied.

Model       Granite 4.1 3B · local
Directory   ~/EduCore
Connection  Available
Session     New session

/help       Available commands
/status     Where are we left at?
/model      Show the model
/clear      Clear conversation

› Ask something...
```
`Connection` is checked once at start (gateway reachable and key accepted): `Available`, `Gateway not running`, or `Key rejected`.

**Conversation:** `YOU` block, then `J A Z A R I` block streaming, then metadata. While waiting for the first token one muted line `THINKING` is shown and replaced by the answer (a single redraw, no spinner, no ticking timer). Elapsed time appears in the metadata line afterwards.

**Errors:** concise by default; `/details` prints the technical detail of the last error.

## Commands and keys

| Input | Action |
|---|---|
| `/help` | list commands |
| `/status` | "Where are we left at?" for the current folder (existing `/v1/status`) |
| `/model` | show model and gateway (read-only until more models exist) |
| `/clear` | forget the conversation (local history); welcome is not reprinted |
| `/retry` | resend the last question |
| `/details` | technical detail of the last error |
| `/exit` | quit (also Ctrl+D on an empty line) |
| Enter | send · Shift+Enter: new line |
| ↑ / ↓ | history (persisted per user) |
| Tab | complete `/` commands |
| Esc | clear the current input |
| Ctrl+C | while generating: stop the answer (gateway cancels the model). While idle: clear input; twice in a row: exit |

## Session behaviour

- Project = the current folder (`X-Project-Path`), so `/status` and the state note work without flags.
- Conversation history is kept in memory and sent with each question; when it would exceed the context budget the oldest exchanges are dropped first (the gateway's `PromptBuilder` already accepts history).
- One-off mode: `jazari ask "…" [--repo] [--stream|--no-stream]` and `jazari status`, `jazari feedback` keep today's behaviour, with streaming now the default.

## Install and other PCs

- Packaged as a .NET global tool: `dotnet tool install -g Jazari --add-source <folder with the .nupkg>`; command `jazari`. Branding (name, tagline, version) lives in one place so it stays configurable.
- **Other PCs:** the CLI installs anywhere, but answers need a gateway + model. Each PC runs its own llama-server + gateway (2 GB model, ~5 GB RAM). Reaching this laptop's gateway over the network is out of scope (API keys are personal and localhost-only).

## Calls made by Claude (Eni-M to check)

- **Small gateway changes are in scope:** a new `GET /v1/info` returns `{ "name": "Jazari", "model": "Granite 4.1 3B", "version" }` behind the API key. It is instant, so the welcome screen's `Connection` line uses it (200 → Available, 401 → Key rejected, no connection → Gateway not running), and `/model` reads it.
- **The gateway trims history, not the CLI:** when the prompt would exceed the context budget, `PromptBuilder` drops the oldest history messages until it fits, and only throws 413 if the question plus state note alone are too big. The budget stays a gateway setting.
- **Project folder:** both the session and the one-off commands default to the current folder; `--repo <path>` overrides it. No repo code search is added (that's the later context-building plan).
- **Settings:** `JAZARI_KEY` and `JAZARI_URL` (default `http://127.0.0.1:5080`); the old `MYOWNLLM_*` names are still read as a fallback. "Key rejected" tells the user to run `jazari-gateway add-key`.
- **New line:** Shift+Enter where the terminal reports it; Alt+Enter and Ctrl+J always work as fallbacks. `/help` lists them.
- **Rename scope:** everything user-facing becomes Jazari: the CLI command, the system prompt ("You are Jazari, …"), the README and runbook. The repository, folders and C# namespaces stay `MyOwnLLM` to avoid churn.

## What's left to the builder

- Library choice (e.g. Spectre.Console for styling/capability detection; a line editor library for multiline input, history and completion; Markdig for Markdown parsing), kept to the fewest dependencies that meet the key table.
- Exact letter-spacing/wrapping maths, history file location, color-detection edge cases.
- Renaming the `myllm` project/assembly to `jazari` without breaking the existing tests.
