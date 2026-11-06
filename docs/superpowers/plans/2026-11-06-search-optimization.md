# Law book search optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The law book sends the passage that holds the answer far more often, without sending more wrong pages.

**Architecture:** `lawbook/build.py` adds section paths to passage titles and three new tables made from the docs themselves: `page_terms` (a page's headings and API names), `chunk_names` (each passage's API names split into words) and `aliases` (abbreviations the docs define). `LawBook.cs` expands abbreviations, filters pages by product and kind from the address, fuses two page rankings with RRF, and lets the new tables count wherever words are checked. The eval tools measure the API word in the passage, a held-out set, and the model on the API questions.

**Tech Stack:** Python 3 standard library (build, eval), C# .NET 10 with Microsoft.Data.Sqlite FTS5 (gateway), xunit.

**Spec:** `docs/superpowers/specs/2026-11-06-search-optimization-design.md`

## Global Constraints

- No new package, no download, no cloud call.
- Existing tables keep their columns: `chunks(title, text, url UNINDEXED)`, `pages(title, description, slug, url UNINDEXED)`, tokenizer `porter unicode61`.
- Every rule that rules a page or passage out stays. Limits stay: 3 pages tried, 40 passages read, 2 passages, 300 tokens.
- Implement every task before running any benchmark (Eni-M, 2026-11-06). Unit tests are written with each task and run at the end.

## Review Focus

- A trap question about another stack or an invented API must still get nothing: the new tables must not make an invented name "covered". Pinned by the existing trap tests and `A_made_up_name_split_into_real_words_is_still_not_covered` (Task 3).
- `/fix` asks about compiler errors by code: a question holding `CS0103` must still reach compiler-error pages. Pinned by `An_error_code_reaches_the_error_pages` (Task 3).
- A book from before this change: no passages, no crash. Pinned by `A_book_without_the_new_tables_means_no_passages` (Task 3).
- Search syntax and huge questions in the alias lookup: pinned by the existing `Search_syntax_in_a_question_is_harmless` and `A_huge_question_stays_quick`.
- A question naming two products filters by neither. Pinned in `A_named_product_keeps_the_search_to_its_docs` (Task 3).

---

### Task 1: Measure the API word and a held-out set

**Files:**
- Modify: `lawbook/dev_questions.json` (`api` on the 17 hard page questions, done), `lawbook/try_dev.py`, `eval/run_eval.py`
- Create: `lawbook/heldout_questions.json` (done)
- Test: `eval/test_run_eval.py`

**Interfaces:**
- Produces: `try_dev.py --held-out`; `run_eval.py --law-questions`; `run_eval.law_questions() -> list[dict]` with `id`, `category` (`law-dev` or `law-held-out`), `question`, `must_have: [[api]]`, `must_not: []`.

- [ ] `try_dev.py`: for page questions with `api`, count it right when the page is right and a passage's title or text holds `api` (any case); print `API in the passage: n/17`. `--held-out` runs `heldout_questions.json` pages and traps and prints right/nothing/wrong/API per kind and traps refused. `--check-overlap` also checks held-out questions.
- [ ] `run_eval.py --law-questions`: asks `law_questions()` instead of `questions.json` and prints the score per category.
- [ ] Test `test_law_questions_are_the_api_questions`: every item has `must_have == [[q["api"]]]`, 17 are `law-dev`, 34 are `law-held-out`, ids are unique.

### Task 2: Build the new book

**Files:**
- Modify: `lawbook/build.py`
- Test: `lawbook/test_build.py`

**Interfaces:**
- Produces, in the book: `chunks.title` = `Page > Section > Subsection`; `page_terms(headings, names, url UNINDEXED)`; `chunk_names(names)` with `rowid` = the passage's `chunks.rowid`; `aliases(alias TEXT PRIMARY KEY, expansion TEXT NOT NULL)`.
- Produces, in Python: `api_names(text) -> list[str]`, `name_words(names) -> list[str]`, `abbreviations(text) -> list[tuple[str, str]]`, `page_terms(title, headings, texts, url) -> tuple[str, str, str]`.

- [ ] Tests:
  - `test_api_names_have_an_inner_capital_or_a_dot`: `api_names("Call AddKeyedSingleton, then Results.NotFound; Hello and ASP stay out. AddKeyedSingleton again.") == ["AddKeyedSingleton", "Results.NotFound"]`
  - `test_name_words_split_humps_and_dots`: `name_words(["IExceptionHandler", "Results.NotFound", "XMLHttpRequest"]) == ["exception", "handler", "results", "not", "found", "xml", "http", "request"]`
  - `test_a_passage_title_is_its_section_path`: titles of `chunks("Records", "## Equality\na\n### Positional\nb\n## Other\nc")` are `["Records > Equality", "Records > Equality > Positional", "Records > Other"]`; the existing title tests change `: ` to ` > `
  - `test_abbreviations_the_docs_define`: `abbreviations("Use HTTP Strict Transport Security (HSTS), ahead-of-time (AOT) and OpenTelemetry (OTel), but not the thing (WRONG).") == [("hsts", "http strict transport security"), ("aot", "ahead of time"), ("otel", "opentelemetry")]`
  - `test_page_terms_hold_headings_and_api_names`: `page_terms("ExecuteUpdate and ExecuteDelete", ["Basic usage"], ["ctx.Blogs.ExecuteUpdate(x)"], "u") == ("Basic usage | execute update delete", "ExecuteUpdate ExecuteDelete ctx.Blogs.ExecuteUpdate", "u")`
- [ ] Implement. Abbreviations: the long form is up to 7 words before `(ABBR)`; the shortest run of last words whose initials (of words, or of their humps) spell the abbreviation's capitals is the expansion, lower case, hyphens as spaces. Each abbreviation keeps its most frequent expansion.

### Task 3: Search with the new tables

**Files:**
- Modify: `src/MyOwnLLM.Gateway/LawBook/LawBook.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/LawBookTests.cs`

**Interfaces:**
- Consumes: the tables from Task 2.
- Produces: `LawBook.Search(string question, int maxTokens = 300)` unchanged; `LawBook.Write` test helper gains `pageTerms`, `chunkNames` and `aliases` arguments and makes the new tables (empty when not given).

Search, in order:
1. Terms as today; each becomes a concept: the word, plus its expansion as a phrase when `aliases` has it.
2. Uncovered: a concept in no passage text, title or `chunk_names` means nothing.
3. Facets from the question: EF Core or Entity Framework → `/ef/` only; ASP.NET or Blazor → `/aspnet/` only; both or neither → no filter. Addresses holding `/compatibility/`, `/code-analysis/`, `/syslib-diagnostics/`, `/aspnet/core/diagnostics/` or `/compiler-messages/` are left out unless the question holds an error code (`CS0103`, `CA1869`, `ASP0021`, `SYSLIB0001`...) or says breaking, break, upgrade, migrate, obsolete or deprecated.
4. Candidates: the top 10 pages by `bm25(pages, 1, 1, 3)` and the top 10 by `bm25(page_terms, 1, 1)`, facet-filtered, fused by RRF (`1/(60 + rank)`), best 3 tried.
5. Half the concepts must be in the page's row in `pages` or `page_terms`.
6. Passages must hold the rarest concept in their title, text or `chunk_names`; they're read by `bm25(chunks, 1, 1)` and then by `bm25(chunk_names)`, and sections whose path names the question come first.

- [ ] Tests (each builds its own small book):
  - `Plain_words_find_a_passage_by_the_API_names_in_its_code`: "How do I update many blogs at once?" finds the passage holding `ExecuteUpdate` when its `chunk_names` row holds "execute update", and nothing without it.
  - `A_page_whose_code_names_the_API_is_found`: "How do I use AddKeyedSingleton?" finds a page whose title and description never name it but whose `page_terms` names do, and nothing without them. (Changed after measuring: headings bring pages in to be tried, only API names count towards the half rule.)
  - `An_abbreviation_the_docs_spell_out_finds_its_long_form`: "How do I turn on HSTS?" finds the passage titled `Enforce HTTPS > HTTP Strict Transport Security` with the alias, and nothing without it.
  - `A_named_product_keeps_the_search_to_its_docs`: "How do I add an index in EF Core?" returns only `/ef/` passages though a `/dotnet/` page matches more words; naming both EF Core and ASP.NET Core filters neither.
  - `Breaking_change_pages_are_left_out_unless_asked`: "How do I make invalid options fail at startup?" gets the options page, never the `/compatibility/` one; "What breaking change did options validation get?" may get it.
  - `An_error_code_reaches_the_error_pages`: "What does CS0103 mean?" finds a `/compiler-messages/` passage.
  - `A_made_up_name_split_into_real_words_is_still_not_covered`: "How do I call SaveEverythingAsync?" gets nothing, though `chunk_names` holds "save", "everything" and "async".
  - `A_book_without_the_new_tables_means_no_passages`.
  - The existing tests keep passing, with titles written as `Page > Section`.

### Task 4: Build, test, benchmark (after Tasks 1–3 are all done)

- [ ] `dotnet build MyOwnLLM.slnx --disable-build-servers`, then `dotnet test` and `py -I -m unittest` in `lawbook` and `eval`. Expected: all pass.
- [ ] Rebuild the book: `py -I lawbook/build.py --out lawbook/out/law-book.db --work C:/MyOwnLLM/lawbook/out/work`.
- [ ] `py -I lawbook/try_dev.py --book lawbook/out/law-book.db`, old book and new, then `--held-out` on both.
- [ ] With the model, from Eni-M's terminal or a temporary server (never the installed Jazari): `run_eval.py --law-questions` with no book, the old book and the new book.
- [ ] Then change what didn't work, and log the results in `docs/OPTIMIZATION-LOG.md`.
