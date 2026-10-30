# Granite 4.0-H Tiny vs Granite 4.1 3B (measured, 2026-10-30)

llama.cpp b11982 Vulkan build, CPU only (`-dev none -t 8 --poll 0`), both Q4_K_M, runs alternated to even out heat.

| Model | File | pp512 t/s | tg64 t/s |
|---|---|---|---|
| Granite 4.1 3B (dense) | 1.95 GiB | 32–47 | 5.0–7.1 |
| Granite 4.0-H Tiny (7B MoE, ~1B active, Mamba-2 hybrid) | 3.94 GiB | 49–62 | 7.0–12.1 |
| Granite 4.0-H Tiny, pp2048 | | 45 | |
| Granite 4.1 3B, pp2048 | | 30 | |

- Tiny is ~1.5x faster at reading prompts and ~1.4–1.8x faster at writing, at twice the RAM (~4 GB).
- Run-to-run spread is large (heat); one early session also had stray concurrent requests on the server, so those runs were discarded and redone on a single slot.

## Autocomplete (fim_test.py, 6 tasks)

| Model | Passed | Note |
|---|---|---|
| Granite 4.1 3B | 5/6 | stops cleanly at the hole |
| Granite 4.0-H Tiny | 0/6 | writes the right body, then keeps going (repeats the suffix, markdown, prose) until the 160-token cap; no FIM tokens detected in its GGUF |

## Takeaway
Tiny is a faster **chat** model on this laptop but is not usable for **fill-in-the-middle**. If chosen, pair it with Qwen2.5-Coder-1.5B on the MX150 for autocomplete. Not yet compared: chat answer quality on .NET questions, and prompt-cache reuse (hybrids can't KV-shift).
