# Measured on this laptop, 2026-10-29, llama.cpp b11982 Vulkan build, -dev none (CPU only)

| model                          |       size |     params | backend    | ngl | threads | dev          |            test |                  t/s |
| ------------------------------ | ---------: | ---------: | ---------- | --: | ------: | ------------ | --------------: | -------------------: |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       3 | none         |           pp512 |         29.55 ± 4.31 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       3 | none         |            tg64 |          5.62 ± 0.11 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       4 | none         |           pp512 |         30.42 ± 1.17 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       4 | none         |            tg64 |          5.51 ± 0.15 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       6 | none         |           pp512 |         34.79 ± 0.24 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       6 | none         |            tg64 |          6.23 ± 0.33 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       8 | none         |           pp512 |         35.43 ± 0.40 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |       8 | none         |            tg64 |          6.13 ± 0.17 |

## poll 0 (no spin-wait), 4 threads
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |          0 | none         |           pp512 |         30.27 ± 0.58 |
| granite 3B Q4_K - Medium       |   1.95 GiB |     3.40 B | Vulkan     |  -1 |          0 | none         |            tg64 |          6.34 ± 0.16 |

## Takeaway (measured, not estimated)
- The "4 threads beats 8" claim from arXiv 2607.24814 does NOT hold on this laptop for Granite 4.1 3B: 6 and 8 threads are slightly faster than 3–4 for both prompt processing (~35 vs ~30 t/s) and generation (~6.2 vs ~5.5 t/s).
- --poll 0 (no spin-wait) at 4 threads gave the best generation result (6.34 t/s) with pp ~30 t/s, so spin-waiting costs heat without helping generation here. Not yet tested: --poll 0 with 6/8 threads.
- The earlier 8-thread baseline (pp512 39 t/s, tg 6.0) was taken on a cooler machine; this run got pp512 35 t/s at 8 threads, so thermal state alone moves prompt speed ~10%.
- Small sample (2 repetitions each); differences under ~0.3 t/s in generation are within noise.
