# Model choice results (2026-11-02)

Run on the T480 with `eval/run_eval.py`: 45 questions plus project scenarios, one run each, using Jazari's system prompt and server settings. The automatic checks look for key terms, then Claude read the answers. Other work was running on the laptop for part of this, so treat small timing differences as noise.

| Setup | Score | Typical answer | Longest | First answer after `/read` | Follow-up | `/edit` | `/review` |
|---|---|---|---|---|---|---|---|
| Qwen3 4B, CPU, 4 threads | 41/45 | 23 s | 59 s | 153 s | 41 s | works | works |
| **Granite 4.0-H Tiny, CPU, 4 threads** | **37/45** | **8 s** | 43 s | **47 s** | **12 s** | works | works |
| Granite 4.1 3B, CPU + MX150 18 layers | 36/45 | 8 s | 31 s | 36 s | 8 s | works | works |
| Granite 4.2 3B, CPU, thinking off | 36/45 | 12 s | 56 s | 110 s | 14 s | works | works |
| Granite 4.1 3B, CPU, 4 threads | 33/45 | 10 s | 36 s | 74 s | 5 s | works | works |

**Winner by the agreed rule: Granite 4.0-H Tiny.** It's Apache 2.0 licensed and has the best score among setups that meet every limit.

## Reading the numbers

- **Qwen3 4B is the most accurate here**, and reading its answers agrees with the score. It missed the limits: 153 s against about 2 minutes after `/read`, 41 s against 30 s on a follow-up, and 12 of 45 answers took over 30 s. It's the choice to revisit if Eni-M will wait longer. Its answers do have wrong parts the checks missed, like inventing `AddSqlQuery`.
- **H-Tiny is about three times faster than Qwen3** on typical answers. That spare time can pay for law book passages, which address its main weakness: it got `record` kinds, the minimal API 404, and EF Core raw SQL wrong.
- **Qwen3 and H-Tiny should be compared again once the law book exists**, since the 30-second budget has to cover reading passages too.
- **Granite 4.2 slows down over a conversation:** 7 s on the first turn, 144 s on the sixth. Its thinking mode also ran into the token cap on plain questions. Left out.
- **Three checks were corrected for every model** after reading the answers: LINQ query syntax, "escape" for "encode", and ASP.NET Identity for password hashing. This raised Qwen3 from 40 to 41, H-Tiny from 36 to 37, and the 3B on CPU + MX150 from 35 to 36.

## Device findings

- **Qwen3 + MX150:** the server wouldn't start at 16k context with 14 layers. A short bench at 14 layers read faster (43 vs 26 tok/s) but wrote slower (4.8 vs 5.3), and writing is what makes it miss the limits, so it wasn't rerun.
- **Why saved tuning skipped the MX150:** the tuner measured it at 8 threads (47 tok/s reading, 5.2 writing) and not at 4 (60 and 6.6). At 8 threads the turn estimate is nearly tied with the CPU, so noise can pick either. Capping threads at half removes that.
- **Low priority (`--prio -1`) rejected:** idle writing fell 21% (9.4 to 7.5 tok/s), over the 10% allowed, and other work gained little.

| While H-Tiny answers | Other work's speed (4 busy processes) | Model writing |
|---|---|---|
| Nothing running | 100% | n/a |
| Normal priority, 4 threads | 80% | 10.6 tok/s |
| Low priority, 4 threads | 87% | 8.9 tok/s |
