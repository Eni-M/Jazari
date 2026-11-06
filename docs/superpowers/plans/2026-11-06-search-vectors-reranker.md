# Law book search, round 2: vectors and a reranker

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Search gathers candidates by keyword and by meaning, and a reranker picks the passage or sends nothing.

**Architecture:** `WordPiece` tokenizes for both models; `OnnxSearchModels` (behind `ISearchModels`, so tests use a fake) embeds and reranks; `embed-law-book` writes `vectors` into the book; `LawBook` runs the round 2 search when models and vectors are present and round 1's otherwise.

**Tech Stack:** C# .NET 10, Microsoft.ML.OnnxRuntime 1.31.0, Microsoft.Data.Sqlite; Python standard library for the eval scripts.

**Spec:** `docs/superpowers/specs/2026-11-06-search-optimization-design.md`, "Round 2"

## Global Constraints

- Local and free. Only the approved models and package.
- Round 1's search stays as the fallback when the models or `vectors` are missing; its tests keep passing.
- Every guard stays: uncovered word, named API and version, one page, 2 passages, 300 tokens.
- Implement everything before measuring (Eni-M, 2026-11-06).

## Review Focus

- Models or vectors missing, or a model file damaged: round 1's search, no crash. Pinned by `Without_models_the_word_search_runs`.
- A trap question: the uncovered-word guard still refuses it before any model runs. Pinned by `A_trap_is_refused_before_the_models_run`.
- Passages from two pages never go together. Pinned by `The_second_passage_comes_from_the_same_page`.
- Long passages and questions: tokens are cut to the model's limit. Pinned by `WordPiece_cuts_to_the_limit`.
- Non-English characters, accents and symbols in a question: tokenized, never thrown. Pinned by `WordPiece_handles_accents_symbols_and_unknown_words`.

---

### Task 1: WordPiece and the models

**Files:** Create `src/MyOwnLLM.Gateway/LawBook/WordPiece.cs`, `src/MyOwnLLM.Gateway/LawBook/SearchModels.cs`; modify `MyOwnLLM.Gateway.csproj`, `GatewayOptions.cs`. Test: `tests/MyOwnLLM.Gateway.Tests/WordPieceTests.cs`.

**Interfaces (produced):**
- `sealed class WordPiece(IReadOnlyDictionary<string, int> vocab)`, `static WordPiece Load(string vocabFile)`, `(long[] Ids, long[] TypeIds) Encode(string first, string? second, int maxTokens)`.
- `interface ISearchModels { float[][] Embed(IReadOnlyList<string> texts); float[] Rerank(string question, IReadOnlyList<string> passages); }`
- `sealed class OnnxSearchModels : ISearchModels, IDisposable`, `static OnnxSearchModels? Load(string dir)`: null when a file is missing or won't load.
- `GatewayOptions.SearchModelsDir`, default `DataDir/search-models`, holding `bge-small-en-v1.5/` and `ms-marco-MiniLM-L6-v2/` as in `spike/models`.

- [ ] Tests: `WordPiece_splits_words_into_known_pieces` ("unaffable" with vocab un, ##aff, ##able → [CLS] un ##aff ##able [SEP]); `WordPiece_lowercases_and_splits_punctuation`; `WordPiece_handles_accents_symbols_and_unknown_words`; `WordPiece_cuts_to_the_limit` (both texts, second cut first, ends with [SEP]); `A_pair_marks_the_second_text_with_type_1`.
- [ ] Implement. Embeddings: CLS pooling, L2-normalized; questions prefixed "Represent this sentence for searching relevant passages: ". Rerank: the logit per pair. Batches padded to their longest.

### Task 2: Vectors in the book

**Files:** Create `src/MyOwnLLM.Gateway/LawBook/BookVectors.cs`; modify `Program.cs` (`embed-law-book`), `lawbook/build.py` (prints the next step).

**Interfaces (produced):** `static class BookVectors { byte[] Pack(float[] v); static void Write(SqliteConnection db, ISearchModels models, int batch = 32); }` and a loaded index `sealed class VectorIndex(long[] RowIds, string[] Urls, sbyte[] Values, float[] Scales)` with `IEnumerable<long> Nearest(float[] query, int count, Func<string, bool> allowed)`.

- [ ] Tests: `Packed_vectors_keep_their_direction` (cosine of unpacked and original > 0.99); `Nearest_finds_the_closest_allowed_passages`.
- [ ] Implement: passages in length order, batches of 32, progress printed; `vectors(rowid INTEGER PRIMARY KEY, v BLOB)`.

### Task 3: Round 2 search

**Files:** Modify `LawBook.cs`, `try_dev.py` (`--models`, default `spike/models` when present), `eval/gateway.py` (`Gateway__SearchModelsDir`). Test: `LawBookTests.cs`.

- [ ] Tests with a fake `ISearchModels`: `The_reranker_picks_the_passage`; `Below_the_cutoff_nothing_is_sent`; `Vectors_find_a_passage_words_miss`; `The_second_passage_comes_from_the_same_page`; `A_trap_is_refused_before_the_models_run`; `Without_models_the_word_search_runs`.
- [ ] Implement as the spec's steps 1–4; `Cutoff` a constant, set during testing.

### Task 4: Build, test, embed, benchmark

- [ ] Build and all tests. Rebuild the book, `embed-law-book`, `try_dev.py` dev and held-out. Set the cutoff on dev, confirm on held-out. Hand the model runs to Eni-M.
