# `/deep` measurements (2026-11-04)

On the T480: i7-8650U, 16 GB, CPU only, plugged in, Jazari's servers stopped. Medians of three unless stated. Made with `eval/measure_lanes.py`; the two models are Granite 4.0-H Tiny Q4_K_M and Qwen3 4B Q4_K_M.

## Switching models

| | Stop one server, start the other | Router (`--models-max 1`) |
|---|---|---|
| Fast to smart, until first token | 17.0 s | 9.8 s |
| Smart to fast, until first token | 12.2 s | 13.8 s |

- **Router mode is used.** It isn't slower, it adds 53 MB when idle, and the port stays up, so a second `jazari` window can't start another server.
- **One model at a time stays.** Going back takes a little over the 10 s the design allows, but both loaded would take about 13.9 GB of working set on a 16 GB laptop (Tiny 7,390 MB, Qwen3 6,483 MB), so the alternative doesn't fit.

## How the router behaves at b11982

Checked by hand with two small models.

- `--api-key-file` applies to the router. `/health` needs no key; `/models` and `/props` do.
- `prompt_progress` comes through the proxy when streaming.
- `/props` on the router has `role: router` and `n_ctx: 0`. `/props?model=<name>` gives the model's real values.
- With `--no-models-autoload`, a request for a model that isn't loaded gets 400 `model is not loaded`, so nothing loads by surprise.
- `/models/load` returns at once. `/models` then shows `loading`, then `loaded`, and loading another model unloads the first.
- A model that fails to load goes back to `unloaded`, and the router keeps running. The launcher can't tell from the process list that the tuned settings failed, so it reads `/models`.

## Thinking

`--reasoning-budget N` exists, but it's set per server and only works for a model with a thinking mode. `/deep` uses a separate short plan call capped at 400 tokens instead, which works with every candidate and is switched off by a code change alone. Qwen3 with thinking on wasn't scored: the attempted run failed on a path mistake and wasn't repeated, because this choice doesn't depend on it. Task 9 judges the whole pipeline.

## Snippet check

Run over saved answers with `eval/check_saved.py`, 68 blocks from `tiny-regress` and `qwen3-cpu`.

| | Result | Limit |
|---|---|---|
| First check, loading the packs | 1.6 s | run during `/v1/warm` |
| Later checks, median | about 45 ms | 500 ms |
| Peak working set of the process | 102 MB | 300 MB added |
| Blocks flagged | 2 of 68 | |
| False alarms | 1 of 2, `AddDefaultUI`, then added to the package list | 10% |

The true catch was `HttpContext.ValidateToken`. The first version flagged 22 blocks, nearly all false alarms: example types such as `ISomeService`, and real types whose `using` the answer left out. So every namespace in the packs is now imported, and an unknown plain type name no longer counts.
