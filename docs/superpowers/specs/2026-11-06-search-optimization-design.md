# Law book search optimization

Date: 2026-11-06 · Branch: `feat/search-optimization` · Source: "Knowledge classification for AI retrieval" (Eni-M's report)

## Why

Last session's benchmarks showed search is what loses answers. On the 17 hard questions, the model gets 4 right with no passage, 7 with today's search, and 15 when it's handed the right passage. The passage holds the API asked about only 6 times out of 17. Most misses find the right page and then throw it away, because a page's title and description don't mention the API names and section topics the question uses, or because the right section isn't the one sent.

## Goal and bar

The hard questions climb from 7 toward 15. Wrong pages don't go up, questions about things outside the .NET docs still get nothing, and everything stays local and free.

Measured without the model on `lawbook/dev_questions.json`:
- the API word is in a passage sent from the right page, out of 17 (today 6)
- right page, nothing, wrong page, out of 47 (today 28 / 18 / 1); a wrong page counts double
- traps refused, out of 15 (today 15); answerable `must_contain`, out of 20 (today 13)

Confirmed once on `lawbook/heldout_questions.json` (34 answerable questions by kind, plus 6 traps), which is never used for tuning. Then measured with the model: the API questions answered right, with no book, the old book and the new one.

## What the report gives us, applied here

| Report | Here |
|---|---|
| Evaluation set first, labelled by query type | The 17 hard questions get their API word back. A new held-out set is labelled plain, named, abbrev and typo. Recall@20 doesn't fit: Jazari sends at most 2 passages and a wrong one is worse than none |
| Each chunk keeps its chapter > section path | A passage's title becomes `Page > Section > Subsection` |
| Controlled vocabulary: concepts with synonyms | Two kinds, both made automatically from the docs, none by hand: (1) API names split into words (`ExecuteUpdate` is searchable as "execute update"); (2) abbreviations the docs define, such as "HTTP Strict Transport Security (HSTS)" |
| Facets as structured filters before search | Product, from the address: a question naming EF Core searches `/ef/` first, one naming ASP.NET Core or Blazor `/aspnet/` first, and the rest of the docs only when nothing there passes (C#'s own docs answer "records in ASP.NET Core"). Page kind, from the address: breaking-change, code-analysis rule and compiler-error pages are left out unless the question names an error code or asks about breaking changes or upgrades |
| Several rankings fused with RRF | Pages are ranked by what they say they're about (as today) and, separately, by their section headings and API names; the two lists are fused with RRF (k = 60) |
| Copy facets onto rows so the filter needs no join | The address already carries both facets |

## Constraints

- Local and free: no cloud model, no download, no new package. Standard-library Python for the build, `Microsoft.Data.Sqlite` in the gateway.
- Nothing is safe, a wrong page isn't: every existing rule that rules a page or passage out stays (uncovered word, half the question's words, named APIs and versions, rarest word, one page only, 2 passages, 300 tokens).
- Today's ranking columns stay as they are. New fields go in new tables, because extra columns in `pages` or `chunks` change every word's weight.
- An older book has no new tables, so it finds nothing, the same as when `pages` was added. The book isn't shipped yet (`lawBookUrl` is empty).

## Left out, and when to add it

- Search by meaning (static embeddings): tried 2026-11-05, more wrong pages. Revisit only if word search tops out.
- A reranker: needs a model download, which needs Eni-M's yes. Next in line if the right page is found but the wrong section is still sent.
- Hand-written synonyms: they would fit the test questions. Add from real misses (`eval/misses.py`) instead.
- Concept tagging by the local model: hours per build on the T480 for a gain the free signals may already give.
- `ms.topic` page types: only about 800 of 5,783 pages have one; the address gives the kinds that matter.

## Order of work

Implement everything, then build and test, then benchmark, then change what didn't work (Eni-M, 2026-11-06).

## Round 2: the report's recall-first pipeline (approved 2026-11-06)

Round 1 measured a small held-out gain (right page 5 to 7 of 34): word checks alone can't tell a right page from a near one. The report's big numbers come from the parts left out: vectors fused with keyword search by RRF, and a reranker. Eni-M approved the downloads.

**Models**, local, in `spike/models` for now (shipping them through the manifest is a later decision): BAAI `bge-small-en-v1.5` (MIT, int8 ONNX, 34 MB, 384 dimensions) for vectors, `cross-encoder/ms-marco-MiniLM-L6-v2` (Apache-2.0, int8 AVX2 ONNX, 23 MB) to rerank. Run by `Microsoft.ML.OnnxRuntime` 1.31.0 (MIT) in the gateway. The WordPiece tokenizer both need is written here, not a package.

**Book:** a `vectors(rowid, v)` table, one per passage (title and text, at most 256 tokens), int8 with a per-vector scale, about 21 MB. Made by the gateway (`embed-law-book`) after `build.py`, so the build stays standard-library Python and the vectors come from the same code that embeds questions.

**Search, when the models and vectors are there** (otherwise round 1's search runs unchanged):
1. Terms and the uncovered-word guard, as before. Facets, as before (product first, then the rest).
2. Candidates: the top 30 passages by keyword (`bm25(chunks)`, plus API-name words) and the top 30 by vector similarity to the question, fused with RRF (k = 60); the best 20 go on.
3. Each must hold every API name and version the question names, as before.
4. The reranker scores the question with each. Below the cutoff, nothing is sent. Otherwise the best passage, and the next best from the same page if it also clears the cutoff and fits (2 passages, 300 tokens).

The cutoff is set on the dev set once everything is built; the held-out set confirms it.
