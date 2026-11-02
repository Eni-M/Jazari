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
| Regression, 37/45 and limits | | | | **Not run** |

The raw results are in `research_notes/Local inference sources/tiny-before-recall.json` and `tiny-after-recall.json`.

The recall fix works. The memory change, `-ctxcp 8 --cache-ram 1024`, saved only 56 MiB where the gate wants 1,536, and the cause hasn't been looked into. Both flags are in `eval/recall.py`'s server arguments. One lead, not checked: the model file is 4.2 GB and memory-mapped, and the 9.9 GB the first probe saw may have needed more turns than this test runs.

## Still to do

1. **Find out why memory barely moved** (see above), and decide what the gate should measure on purpose. Don't lower it just to pass.

2. **Run the regression check.** It didn't fit before the deadline, so nothing has checked the new server flags against the 37/45 quality floor:

   ```bash
   py -I eval/run_eval.py --label tiny-regress --model C:/MyOwnLLM/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama C:/MyOwnLLM/spike/bin/llama-server.exe --threads 4 --scenarios
   ```

   ```bash
   py -I eval/compare.py
   ```

   Pass: at least 37/45, first answer after `/read` about 2 minutes at most, follow-up 30 s at most, `/edit` and `/review` inside their limits.

3. **Check the servers are stopped.** This must print nothing (PowerShell):

   ```powershell
   netstat -ano | Select-String ":8099 |:5099 " | Select-String LISTENING
   ```

4. **Update the docs with the result:** the regression line in `docs/OPTIMIZATION-LOG.md`, under "Context and outside sources (2026-11-02)".

5. **Your own last step:** run `jazari update` from your own terminal, have a long conversation, and watch for the `cut` row.

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
- **A stray `find.exe` may still be running.** `find / -iname *granite*4.1*.gguf`, running since 2026-10-30 and using a full core. It was running during these measurements. End it before timing anything.

## Known caveats

- **Weak scoring on the two prefix facts.** The patterns `ORD` and `CUS` with ignore-case also match "order" and "customer", so an answer that only repeats the question scores as right. Before and after are still comparable, since the original probe scored the same way.
- **`scripts/start-llama.ps1` (the manual start)** still passes `--cache-ram 2048` without `-ctxcp 8`. It wasn't in the plan and was left as is.
- **Prefix scoring in these runs:** one baseline `deep` answer ("prefix CUS-" for order ids) scored right only because of the weak pattern, so the true baseline `deep` is 8/12. No after answer depends on it.
- **New in the after `short` run:** 5 of 12 answers add an invented "in an ASP.NET Core application". The baseline's don't. They're still scored right, and the cause isn't known. Short conversations are under the 2,000-token pin limit, so look at more than the pin.
- **Conversation size:** `deep_tokens` was 14,063 and 14,261, not the plan's 12,700.
- **The filler code differs slightly between runs.** The deep conversation uses this repo's own C#, and the baseline's commit has different source files from the branch, so the chunks aren't identical.

## Other branches, not part of this

- `research/jazari-enhancements`: not finished, for later.
- `claude/festive-cori-6q3i6t`: CI changes.
- `jazari-update-wip`: keeps Jazari working when an update or install fails part-way.
