# `/deep`: a smarter answer when it's worth the wait

Date: 2026-11-04 · Owner: Eni-M · Status: draft for approval

## Why

Jazari runs Granite 4.0-H Tiny because it's fast: about 8 s for a typical answer. It was picked over Qwen3 4B, which scored higher (41/45 against 37/45) but takes about 23 s. That's the right trade for simple questions, and the wrong one for hard ones. Eni-M (2026-11-04): "I want it as smart but also usable. I cannot wait 20 seconds for a simple question."

So there are two speeds. Plain questions stay on the fast model. `/deep` uses a smarter model and works harder on the answer, and Eni-M decides per question when it's worth waiting.

## What the user sees

```
> how do I register a scoped service?          fast lane, ~8 s, as today
> /deep why does this deadlock under load?      smart lane, up to ~3 min
> /deep                                         asks the last question again, in the smart lane
```

- **`/deep <question>`** asks one question in the smart lane. The next question goes back to the fast lane.
- **`/deep` alone** asks the last question again in the smart lane: "that wasn't good enough, try harder". The fast answer stays above it, so the two can be compared.
- **Progress shows by stage,** so a long wait never looks frozen: `loading smart model · reading · thinking · checking the code · answer`.
- **Every deep answer ends with a check row:** `checked: code compiles`, `checked: 1 error fixed`, `not checked: no code`, or `could not fix: <error>`.
- **The fast lane may suggest `/deep`,** but never switches by itself. When a fast answer's code fails the compiler check (A1 in `2026-11-04-what-next.md`), Jazari shows `the code doesn't compile · press D for /deep`.
- `jazari ask --deep "..."` does the same for scripts.

## What `/deep` does

Each step is there to make the answer better. The order matters.

1. **A smarter model.** Qwen3 4B by default, the best model measured so far. The repo benchmark (G1) picks the final choice from the candidates below, so this can change without a code change. The choice is set in `jazari-manifest.json` and by `jazari tune`.
2. **More context.** Up to two law-book passages instead of one, the `/read` files, and (once B1 exists) the repo map entries for the names in the question. The fast lane keeps its smaller budget.
3. **Think first, with a cap.** The model writes a short plan before the answer, using its own thinking mode if it has one, capped at about 400 tokens. Granite 4.2's thinking ran into the token cap on plain questions, so the cap stays and is measured.
4. **Check the code.** Every C# block is compiled with Roslyn (A1). For `/deep` on `/edit`, the semantic check (A2) runs too.
5. **One repair round.** If the code doesn't compile, the model gets the error lines and the block, and tries once. If it still fails, the answer is shown with the error. One round only: the evidence says more doesn't pay at this size.

Not in `/deep`: sampling several answers and picking one (it multiplies time), a model judging its own answer (unreliable at this size), and open-ended agent loops.

## Models

Only one model is loaded at a time. That's safest for a 16 GB laptop: H-Tiny's server already uses about 8.7 GB with its caches.

- **Switching:** the gateway stops the fast model's llama-server and starts the smart one, with the same key and settings. After `/deep` it stays loaded for 2 minutes (a follow-up is likely), then the fast model comes back. Both switches are shown as a stage, never as silence.
- **Cost to measure first:** the time to swap models on the T480. If it's over about 10 s, test keeping both loaded with a smaller `--cache-ram` before deciding.
- **Candidates for the smart lane,** all scored by G1 and the 45 questions:
  - Qwen3 4B, thinking off and on (Apache 2.0, already measured: 41/45, 23 s typical)
  - Granite 4.1 8B (dense, Apache 2.0)
  - Mellum2.1 12B-A2.5B, if it fits the RAM limit (about 8 GB)
  - LFM2.5-8B-A1B, only if its licence is acceptable

## Limits

| | Fast lane | `/deep` |
|---|---|---|
| Typical answer | ≤ 10 s (today about 8 s) | ≤ 90 s |
| Longest | ≤ 45 s | ≤ 3 min, or a stage says why |
| RAM | as today | ≤ 8 GB working set |
| Quality | no worse than today (37/45) | clearly better than the fast lane on G1 and the 45 questions, by the McNemar rule in `compare.py` |

**If `/deep` isn't clearly better than the fast lane, it doesn't ship.** A slower answer that isn't measurably better is the failure this design exists to avoid.

## Build order

1. **Measure:** model swap time, and Qwen3 4B thinking on and off for time and score. (T480, about an evening.)
2. **G1, the repo benchmark,** so "smarter" means "writes code that builds and passes Eni-M's tests", not only "says the key terms".
3. **Snippet compile check (A1).** `/deep` and the fast lane's "press D" hint both need it.
4. **`/deep` itself:** gateway lane and model swap, the CLI command and stages, the repair round, `ask --deep`.
5. **Pick the smart model** from the candidates by G1, and record the result next to `2026-11-02-model-choice-results.md`.

## Open questions

- Is 3 minutes the right ceiling for `/deep`, or should a long one carry on in the background while Eni-M keeps working?
- Should `/edit` and `/review` use the smart lane by default, since they are where wrong answers cost the most? Overnight review already wants the bigger model.
