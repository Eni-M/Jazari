# Granite 4.0-H Tiny vs Granite 4.1 3B — measured on this laptop (2026-10-30)

llama.cpp b11982 Vulkan build, CPU only (`-dev none -t 8 --poll 0`), Q4_K_M, runs alternated to even out heat.

| Model | Run | pp512 t/s | tg64 t/s |
|---|---|---|---|
| Granite 4.1 3B (dense, 1.95 GiB) | 1 | 36.9 ± 3.5 | 6.02 |
| Granite 4.0-H Tiny (7B MoE, ~1B active, 3.94 GiB) | 1 | 69.5 ± 0.6 | 10.35 |
| Granite 4.1 3B | 2 | 42.7 | 6.44 |
| Granite 4.0-H Tiny | 2 | 58.1 ± 8.2 | 7.63 ± 1.92 (throttling) |

FIM test (`spike/fim_test.py`, llama-server `/infill`):
- Granite 4.1 3B: 5/6 pass, avg 8.8 s.
- Granite 4.0-H Tiny: 0/6 pass, avg 66 s. The first function body was correct in all 6 cases, but the model never stopped:
  it ran on into markdown fences, explanations and extra functions until the 160-token cap. No FIM tokens were reported in the
  server log, so `/infill` likely fell back to plain prompting — this GGUF/model doesn't behave as a FIM model out of the box.
- Server runs were far slower per token than llama-bench (unexplained; the server defaulted to 4 slots — retest with `-np 1`).

Takeaway: H-Tiny is ~1.5–1.9x faster at reading prompts and up to ~1.7x faster at generating, at 2x the RAM, but it is unusable
for autocomplete as-is. It is a candidate for the chat role only, pending a chat-quality check and an `-np 1` server retest.
