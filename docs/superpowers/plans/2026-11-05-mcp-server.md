# `jazari mcp`: the editor connection (C1)

Spec: section C1 of `docs/superpowers/specs/2026-11-04-what-next.md`. C2 is out of scope.

**Goal:** VS Code, or any other MCP client, starts `jazari mcp` and talks JSON-RPC over stdin and stdout. No port is opened.

## Tools

The three the spec names, each with one required text argument.

| Tool | Argument | What it does |
|---|---|---|
| `law_book_search` | `query` | Passages of the .NET docs, each with its page. No model. |
| `jazari_ask` | `question` | One answer from the fast model, outside any conversation. Named project code goes with it, as in `jazari ask`. |
| `review_file` | `path` | The `/review` report for one file of the project. |

## Decisions

- **No MCP library in the server.** The protocol this needs is under 300 lines of `System.Text.Json`. The official SDK brings a hosting stack into every `jazari` start. It's used in the tests only, as a client written by someone else, the way the `OpenAI` package checks the gateway.
- **The project is the working directory.** VS Code starts a server in the workspace folder and the launcher passes the folder on, so there's no `--repo`.
- **`jazari mcp` never makes or replaces a key.** An editor starts it without anyone watching, so a key problem comes back as a tool error that says what to run. It's also why it can be started from any client safely.
- **Servers start on the first tool call, not on connect.** VS Code can autostart MCP servers, and opening a folder mustn't load the model. `law_book_search` needs no model, so it only starts the servers when the gateway doesn't answer.
- **`review_file` is stricter than `/review`.** The caller is another model, not the user. The path must be inside the project, reached through no link, a kind of file `/read` takes from a folder, and not in `.jazariignore`. Naming a file doesn't override those rules here.
- **One model tool at a time.** The model has one lane and two reviews of a file would write the same store. `law_book_search` doesn't wait for this.
- **Not read-only in the tool list.** No `readOnlyHint`: clients may use it to skip asking, and these tools send project code to whichever model is chatting. The README says so.

## Slow tools

The spec asks how clients treat a tool taking 30 s or more. What could be found without running a client:

- VS Code's `mcpServerRequestHandler.ts` sets no timer on `tools/call` and cancels with `notifications/cancelled`. It doesn't ask for progress either. Its RPC layer wasn't read.
- The TypeScript SDK defaults to a 60 s limit, and progress only resets it if the client opted in. Sources disagree on that default.
- A Claude Code issue reports a stdio server dropped after a response when progress notifications were in play. That's one user's report.

So the server:

1. Answers `ping` and reads cancels while a tool runs.
2. Sends `notifications/progress` every 10 s, only when the request has a `progressToken`, and never after its response.
3. On `notifications/cancelled` stops the work and sends no response, as the spec says.
4. Keeps finished review parts, so a review cut off by a client carries on at the next call.

A client with a hard limit below one review part (about 70 s) can't finish a review. A job-and-poll tool would fix that; it waits until a real client needs it.

## Tasks

1. `tests/.../McpServerTests.cs`: protocol, tools, slow calls and cancels, in-process over pipes with `FakeGateway`.
2. `src/MyOwnLLM.Cli/McpServer.cs`, `CliArgs`, `Program`.
3. `tests/.../McpProcessTests.cs`: the built CLI over real stdio, the official SDK as client, an HTTP fake as gateway. It only calls `law_book_search`, so no real server can start.
4. README: connecting VS Code, and where the tools' output goes.

## Only the user can check

- A real VS Code session, following the README.
- How long VS Code really waits on a slow tool.
