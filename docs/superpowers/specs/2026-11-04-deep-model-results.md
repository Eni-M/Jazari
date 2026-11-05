# Deep model results (Task 9, 2026-11-05)

Run on the T480, plugged in, CPU only. The questions come from `eval/run_eval.py --deep` and the repo tasks from `eval/repo_bench.py`, both through the gateway, compared by `eval/compare.py --pair tiny-regress deep-qwen3 --with-repo`. Only counts and timings are recorded here: the repos are private.

**Status: decided. `/deep` doesn't ship with Qwen3 4B, and the manifest stays empty.**

## Candidates

- **Qwen3 4B Instruct 2507, Q4_K_M, thinking off:** the only candidate on disk. This file has no thinking mode. Thinking on needs the separate Qwen3 4B Thinking 2507 file, which is a download (see "Follow-ups").
- **Granite 4.1 8B and Mellum2.1 12B-A2.5B:** not measured. They need Eni-M's yes before downloading.
- **LFM2.5-8B-A1B:** left out until Eni-M accepts its licence.

## The rule (written before the results)

The winner is the best pooled score among candidates within the time and RAM limits. It ships only if all of these hold:

- pooled dev McNemar p < 0.05;
- held-out doesn't drop against the fast lane;
- typical answer ≤ 90 s and longest ≤ 3 min, or a stage says why;
- working set ≤ 8 GB.

## Results

| Run | Questions | Typical | Longest | Repo passed | Compiled | Closeness | Repo median |
|---|---|---|---|---|---|---|---|
| Fast lane, H-Tiny (`tiny-regress`) | 39/45 | 8.5 s | 40.5 s | 6/27 | 14 | 0.601 | 38 s (longest 144 s) |
| `/deep`, Qwen3 4B (`deep-qwen3`) | 39/45 | 60.9 s | 137.2 s | 7/27 | 14 | 0.580 | 110 s (longest 337 s) |
| Fast lane through the router (`fast-router`) | 38/45 | 12.2 s | 57.4 s | n/a | n/a | n/a | n/a |

- **Working set:** Qwen3 6,483 MB, within 8 GB (`2026-11-04-deep-measurements.md`).
- **Repo tasks:** 27 valid of 90 chosen. 17 are tested EduCore tasks (the plan asked for 20) and 10 are WareFlow tasks scored on the build alone. 13 of the 27 are held out. Most EduCore methods were dropped because their tests still passed with the method blanked out.
- **Repo tasks by kind:** on the 17 tested EduCore tasks, the fast lane passed 3 and `/deep` passed 4, and both compiled 11. On the 10 WareFlow tasks, both passed 3.
- **Questions:** 3 gained (`record-kind`, `minimal-api-404`, `xss-razor`) and 3 lost (`scoped-register`, `options-snapshot`, `switch-expression`).
- **Pooled comparison (questions and repo tasks):** on dev, 3 gained and 3 lost, exact McNemar p = 1.00. On held-out, 14/25 against 13/25.
- **`/deep` repo timings:** 17 of 27 tasks took over 90 s, and 5 took over 3 min. No request failed.

## What the first runs got wrong

- **`dotnet test` refused EduCore's tests.** .NET 10 refuses an xunit.v3 project that opts into Microsoft.Testing.Platform, so every tested task failed validation, and the first run had only WareFlow's 10. Fixed in `82e0a6d`: such a project's test app now runs itself with `-filterVSTest`. With Docker running (Testcontainers), all 535 EduCore tests pass in 78 s.
- **The benchmark asked before the model had loaded.** 7 of the first 10 baseline answers failed at once and counted as misses. Fixed in `7064a83`. Earlier repo results are void.

## Decision

**`/deep` doesn't ship.** Against the rule:

| Condition | Result | Pass? |
|---|---|---|
| Pooled dev McNemar p < 0.05 | p = 1.00 | no |
| Held-out doesn't drop | 14/25 against 13/25 | yes |
| Typical ≤ 90 s, longest ≤ 3 min | questions 61 s and 137 s; repo tasks 110 s and 337 s | no, on repo tasks |
| Working set ≤ 8 GB | 6.5 GB | yes |

`scripts/jazari-manifest.json` keeps its deep fields empty, so `/deep` reports itself unavailable. A1 and the fast-lane hint work without it.

### Reading it

- **The deep pipeline scored below plain Qwen3.** The same file asked directly scored 41/45 at a 17 s median (`qwen3-think-off`), against 39/45 at 61 s through `/deep`. So the 400-token plan call and the extra steps cost about 45 s an answer and gained nothing on the questions. Try `/deep` as a plain call with the A1 check and one repair before trying a bigger model.
- **The repo benchmark is small.** 27 tasks, of which 17 are tested. A real gain of one or two tasks can't show at this size. More EduCore methods with tests that notice a blanked method would help: most chosen methods' tests still passed with the method blanked.

## Second round: /deep without the plan call (2026-11-05)

After the first decision `/deep` lost its plan call, gained one fresh answer at temperature 0.8 when the repair still fails, and runs on the fast model when there's no smart one (`a1f406c`, `ab2c0f9`).

| Run | Questions | Typical | Longest | Repo passed | Compiled | Closeness | Repo median (longest) |
|---|---|---|---|---|---|---|---|
| Fast lane, H-Tiny (`tiny-regress`) | 39/45 | 8.5 s | 40.5 s | 6/27 | 14 | 0.601 | 38 s (144 s) |
| `/deep` on H-Tiny (`deep-tiny`) | 37/45 | 9.7 s | 41.3 s | 7/27 | 16 | 0.620 | 21 s (76 s) |
| `/deep` on Qwen3, no plan (`deep-qwen3-noplan`) | not run | | | 7/27 | 13 | 0.586 | 79 s |

- **`deep-tiny` against the fast lane, pooled:** dev 1 gained, 3 lost, p = 0.62; held-out 14/25 against 13/25. On the questions alone: 0 gained, 2 lost (`ienumerable-iqueryable`, `connection-string`), p = 0.50.
- **By the rule it doesn't ship either:** no significant gain. It meets every time limit, and more of its repo answers compile (16 against 14), which is what the checks are for, but passing tests didn't move.
- **Decision (Eni-M):** off. Without a smart model `/deep` says it isn't installed, as before; the plan's removal, the warmer second sample and the fallback for a smart model that won't load stay.
- **Dropping the plan made Qwen3 32 s faster per repo task** (110 to 79 s) at the same 7/27.

## Follow-ups for Eni-M

- Download Qwen3 4B Thinking 2507, or Granite 4.1 8B, to measure a second candidate.
- Check `/deep /edit` on EduCore, and try the install steps in your own terminal.
