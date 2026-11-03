# Pick up here: Task 6, conversation memory

Written 2026-11-02, under a 17:00 deadline. The plan is `docs/superpowers/plans/2026-11-02-conversation-memory.md`. Rules and Traps are in `docs/OPTIMIZATION-LOG.md`.

## Where it stands

Everything is on `main`, pushed before the deadline on Eni-M's instruction. **Not everything passed.**

Recall test, H-Tiny Q4_K_M on the T480, one run each. Before is `1f72651`, after is `6de629b`:

| | Before | After | Gate | Result |
|---|---|---|---|---|
| `short` | 12/12 | 12/12 | 12/12 | Pass |
| `deep` | 9/12 | 12/12 | at least 11/12 | Pass |
| `cut` | 8/12 | 12/12 | at least 10/12 | Pass |
| `median_s` short / deep / cut | 2.05 / 1.95 / 1.8 | 2.25 / 2.9 / 2.25 | at most 30 | Pass |
| `working_set_mib` | 8,767 | 8,711 | at most 7,231 | **Fail** |
| Regression, 37/45 and limits | 37/45 | 39/45 | at least 37/45 | Pass |

The raw results are in `research_notes/Local inference sources/tiny-before-recall.json`, `tiny-after-recall.json` and `tiny-regress.json`. The regression ran on 2026-11-03; what it does and doesn't show is in `docs/OPTIMIZATION-LOG.md`, under "Context and outside sources (2026-11-02)".

The recall fix works. The memory change, `-ctxcp 8 --cache-ram 1024`, saved only 56 MiB where the gate wants 1,536.

## Checked on 2026-11-03

**Why memory barely moved: the 1,536 MiB target was never a measurement of the flags.** It comes from the first probe (`research_notes/Local inference sources/recall_probe.py` and `.jsonl`). The 9,565 MiB reading there was a 12,752-token conversation after 13 requests, and the 7,315 MiB "with `-ctxcp 8`" reading was a different conversation, `deep[1:13]`, of 6,567 tokens after 1 request. Half the conversation and a fifth of the requests explain the 2.2 GB gap at least as well as the flag does. The flags' own saving has never been measured.

The recall test couldn't show it either:
- It compared two commits (`1f72651` and `6de629b`) that differ in the gateway's code as well as in the flags.
- It sent llama-server's output nowhere, so the checkpoints and cached prompts it made weren't recorded.
- It took one `tasklist` reading, the working set. That includes the resident pages of the 4.2 GB memory-mapped model file, which Windows fills and trims as it likes. The checkpoints and prompt cache are private bytes, which weren't recorded.

**Changed so the next run can tell** (`eval/recall.py`, `eval/gateway.py`):
- `--cache-ram` and `--ctxcp` options (defaults: the shipped 1024 and 8), so the old and new flags can run on one commit.
- llama-server's output is kept in `eval/results/recall/<label>-llama.log`.
- `deep_server_mib` in the result: Windows' working set, peak working set, private bytes and peak private bytes for the deep server, read with `GetProcessMemoryInfo`. `working_set_mib` from `tasklist` stays, for comparison with the old runs.
- The `ORD` and `CUS` facts now match only the capitals, so "order" and "customer" no longer count. Rescoring the saved answers: before `deep` is 8/12, not 9/12; after is unchanged at 12/12, 12/12, 12/12. The recall gates still pass.

## Still to do

1. **Measure the flags alone**, from your own terminal on the T480, laptop otherwise idle, port 8099 free. Same commit, only the flags differ:

   ```bash
   py -I eval/recall.py --label flags-old --cache-ram 2048 --ctxcp 32 --model C:/MyOwnLLM/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama C:/MyOwnLLM/spike/bin/llama-server.exe
   py -I eval/recall.py --label flags-new --model C:/MyOwnLLM/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama C:/MyOwnLLM/spike/bin/llama-server.exe
   ```

   Then compare `deep_server_mib.peak_private` in the two results, and count the checkpoint lines in each log: `Select-String "checkpoint" eval/results/recall/flags-old-llama.log`.

2. **Decide the gate, on purpose.** The flags exist to cap what llama-server allocates in a long conversation, so the gate should measure peak private bytes, old flags against new, on one commit. A proposal, yours to accept or change: the new flags' peak private bytes must be lower than the old flags', by at least the checkpoints the old log shows beyond 8, at about 55 MiB each. The log then says how much the flags could save, and the A/B says whether they did. If the old log shows fewer than 9 checkpoints, this test is too short to reach the cap, and the conversation needs more requests: the first probe's 9.9 GB came after about 73. Don't lower the gate just to pass.

3. **Your own last step:** run `jazari update` from your own terminal, have a long conversation, and watch for the `cut` row.

## If you change the code

The plan's Step 2 knobs, `MaxPinTokens` and `MinHistoryTokens` in `src/MyOwnLLM.Gateway/Prompt/ConversationSearch.cs`, tune recall, which already passes. They won't fix memory. Re-run the gate after any change, on the same PC as the baseline:

```bash
py -I eval/recall.py --label tiny-after --model C:/MyOwnLLM/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama C:/MyOwnLLM/spike/bin/llama-server.exe
```

Pass: `deep` at least 11/12, `cut` at least 10/12, `short` 12/12, every `median_s` at most 30, and `working_set_mib` at least 1,536 MiB below the baseline, so at most 7,231.

## Things to know

- **Compare on the same PC.** The baseline was measured on the T480. Timings and memory from another PC can't be compared with it, so a different PC needs its own baseline: check out `1f72651` and run `eval/recall.py` with `--label tiny-before`.
- **Models and results don't travel with git.** `spike/` and `eval/results/` are ignored. Only the copies in `research_notes/Local inference sources/` are committed.
- **Run the checks with the laptop otherwise idle.** The recall test uses about 8.8 GB, and a second eval on the same PC spoils both runs' timing and memory. Check first that port 8099 is free. Another session was running a model comparison there on 2026-11-02.
- **That comparison looks unfinished.** The loop ended at 16:25 with only `gsq-qwen3-q2` saved. `gsq-qwen35-q2xl`, `gsq-qwen3-q3` and `tiny-q3` have no results.
- **Never run the real Jazari app, `jazari update` or the installer from a Claude Desktop session.** See Traps in the log.

## Known caveats

- **A stray `find.exe` ran during the recall measurements**, using a full core, both before and after. It was stopped on 2026-11-03, so later timings aren't strictly comparable with them.
- **Weak scoring on the two prefix facts** (fixed 2026-11-03). The patterns `ORD` and `CUS` with ignore-case also matched "order" and "customer". They now match only the capitals. Saved runs scored the old way.
- **`scripts/start-llama.ps1` (the manual start)** passed `--cache-ram 2048` without `-ctxcp 8`. Fixed on 2026-11-03: it now matches `Tuning.ServerArgs`.
- **Prefix scoring in these runs:** one baseline `deep` answer ("prefix CUS-" for order ids) scored right only because of the weak pattern, so the true baseline `deep` is 8/12. No after answer depends on it.
- **New in the after `short` run:** 5 of 12 answers add an invented "in an ASP.NET Core application". The baseline's don't. They're still scored right, and the cause isn't known. Short conversations are under the 2,000-token pin limit, so look at more than the pin.
- **Conversation size:** `deep_tokens` was 14,063 and 14,261, not the plan's 12,700.
- **The filler code differs slightly between runs.** The deep conversation uses this repo's own C#, and the baseline's commit has different source files from the branch, so the chunks aren't identical.

## Other branches, not part of this

Checked on 2026-11-03: all three are on `main` now, so none holds unfinished work.

- `research/jazari-enhancements` and `jazari-update-wip`: every commit is on `main`.
- `claude/festive-cori-6q3i6t`: every commit is on `main` in equivalent form, apart from one change and its own revert, which cancel out. Safe to delete when you like.
