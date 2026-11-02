# Test Set, Model Choice and Work-Friendly Running: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Jazari a scored C#/.NET test set, choose the base model and device mix from that score, and keep the laptop usable while it answers.

**Architecture:** A small stdlib Python tool in `eval/` starts a temporary `llama-server` on port 8099, sends the gateway's real system prompt and questions, scores the answers and saves results. Its results decide the model. The CLI's tuner then caps CPU threads at half and uses low priority if measured to be harmless.

**Tech Stack:** Python 3.12 stdlib (`py -I`, `unittest`), llama.cpp b11982, C#/.NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-02-jazari-accuracy-design.md`

**Scope:** phases 1 and 2 of the spec, plus goal 7. Phases 3 to 7 (behaviours, law book, tools, fine-tune, quantization) each get their own plan afterwards, because the chosen model shapes all of them.

## Global Constraints

- Never run the real Jazari app, `jazari update` or the installer from Claude Desktop. `llama-bench` and a temporary `llama-server` on port 8099 are allowed. Stop that server when done.
- Read install files from a Claude Desktop session only as `\\localhost\C$\Users\enim\AppData\...`; plain `%LOCALAPPDATA%` can show a stale copy. Executables run fine from the plain path.
- Time limits: plain question at most 30 s; first answer after `/read` at most about 2 minutes; follow-up after `/read` at most 30 s.
- Only personal test code. No employer code. Test questions are never used for training.
- The UHD 620 is never combined with another device. Layers are never split across two GPUs.
- Work on branch `feat/accuracy-eval`, created from `main`. Push nothing until Eni-M says so.

## Review Focus

- A model that returns no answer, or a server that dies mid-run: the question scores as failed with a reason, and the run goes on.
- A server that never becomes ready, or port 8099 already in use: a clear message and a non-zero exit, with no server left running.
- Answers that mention a right term only in order to reject it, or carry extra text: a `must_not` match fails the question even when `must_have` matched.
- A model whose follow-ups re-read everything: `pick` rejects it even with the best plain score.
- A GPU setting that can't load: that candidate is reported as failed and the comparison continues.

---

### Task 1: Scorer

**Files:**
- Create: `eval/score.py`, `eval/test_score.py`, and `eval/results/.gitkeep`
- Modify: `.gitignore` (add `eval/results/*.json`)
- Also commit: the design and this plan

**Interfaces:**
- Produces: `score(answer: str, question: dict) -> dict` returning `{"passed": bool, "missing": list[str], "forbidden": list[str]}`. A question has `must_have`, a list of groups where each group is a list of alternatives, and `must_not`, a list of strings. Matching is case-insensitive.
- Produces: `pick(results: list[dict]) -> dict | None`, where each result is `{"label", "score", "plain_p50_s", "read_first_s", "followup_s", "edit_ok", "review_ok"}`.

- [ ] **Step 1: Create the branch**

Run: `git switch -c feat/accuracy-eval`

- [ ] **Step 2: Write the failing tests**

```python
QUESTION = {"must_have": [["AddScoped", "Scoped"]], "must_not": ["AddSingleton"]}

def test_passes_when_each_group_has_a_match(self):
    self.assertTrue(score("Use addscoped here", QUESTION)["passed"])

def test_missing_group_fails_and_is_named(self):
    r = score("Use a factory", QUESTION)
    self.assertFalse(r["passed"]); self.assertEqual(r["missing"], ["AddScoped"])

def test_forbidden_text_fails_even_when_the_right_term_is_there(self):
    r = score("AddScoped or AddSingleton", QUESTION)
    self.assertFalse(r["passed"]); self.assertEqual(r["forbidden"], ["AddSingleton"])

def test_empty_answer_fails(self):
    self.assertFalse(score("", QUESTION)["passed"])

GOOD = {"label": "a", "score": 30, "plain_p50_s": 20, "read_first_s": 100, "followup_s": 25, "edit_ok": True, "review_ok": True}

def test_pick_takes_the_best_score_within_limits(self):
    better = {**GOOD, "label": "b", "score": 35}
    self.assertEqual(pick([GOOD, better])["label"], "b")

def test_pick_rejects_slow_plain_answers(self):
    self.assertEqual(pick([{**GOOD, "plain_p50_s": 31}]), None)

def test_pick_rejects_rereading_on_followups(self):
    self.assertEqual(pick([{**GOOD, "score": 99, "followup_s": 90}, GOOD])["label"], "a")

def test_pick_rejects_a_slow_first_answer_after_read(self):
    self.assertEqual(pick([{**GOOD, "read_first_s": 121}]), None)

def test_pick_rejects_a_model_where_edit_or_review_breaks(self):
    self.assertEqual(pick([{**GOOD, "edit_ok": False}]), None)
    self.assertEqual(pick([{**GOOD, "review_ok": False}]), None)

def test_pick_treats_unmeasured_scenarios_as_failing(self):
    self.assertEqual(pick([{**GOOD, "followup_s": None}]), None)
```

- [ ] **Step 3: Run them to see them fail**

Run: `cd eval; py -I -m unittest test_score -v`
Expected: FAIL, `score` isn't defined. Note that `-I` ignores the current folder, so add `sys.path.insert(0, os.path.dirname(__file__))` at the top of the test file.

- [ ] **Step 4: Implement `score` and `pick` in `eval/score.py`**

`pick` applies limits of 30, 120 and 30 seconds, requires `edit_ok` and `review_ok`, and returns the highest score, with the faster `plain_p50_s` winning a tie.

- [ ] **Step 5: Run the tests**

Expected: PASS.

- [ ] **Step 6: Commit**

`git add eval .gitignore docs/superpowers docs/OPTIMIZATION-LOG.md`, message `feat(eval): scorer and model selection rule`.

### Task 2: The questions

**Files:**
- Create: `eval/questions.json`, `eval/test_questions.py`

**Interfaces:**
- Produces: a JSON list of `{"id", "category", "question", "must_have", "must_not", "note"}`. `category` is one of `concept`, `api`, `code`, `security`, `jazari`.

- [ ] **Step 1: Write the failing test**

```python
def test_the_set_is_big_enough_and_balanced(self):
    self.assertGreaterEqual(len(QUESTIONS), 40)
    for category in ["concept", "api", "code", "security"]:
        self.assertGreaterEqual(sum(q["category"] == category for q in QUESTIONS), 8)

def test_ids_and_questions_are_unique(self): ...
def test_every_question_can_be_scored(self):  # at least one must_have group, every group non-empty
def test_the_three_known_failures_are_included(self):
    ids = {q["id"] for q in QUESTIONS}
    self.assertLessEqual({"efcore-raw-sql", "minimal-api-404", "record-kind"}, ids)
```

- [ ] **Step 2: Run it to see it fail**

Run: `py -I -m unittest eval/test_questions.py -v` from the repo root, or from `eval/` as in Task 1.
Expected: FAIL, no file.

- [ ] **Step 3: Write 45 questions**

The first three, exactly:

| id | question | must_have | must_not |
|---|---|---|---|
| `efcore-raw-sql` | How do I stop SQL injection when using EF Core raw SQL? | `[["FromSqlInterpolated", "FromSql(", "SqlParameter", "ExecuteSqlInterpolated"]]` | `[]` |
| `minimal-api-404` | How do I return a 404 from a minimal API endpoint? | `[["Results.NotFound", "TypedResults.NotFound"]]` | `["IActionResult", "[HttpGet"]` |
| `record-kind` | Is a C# record a value type or a reference type? | `[["reference type"]]` | `["value-type struct", "records are value types"]` |

Write the rest from well-known facts you can check in Microsoft's docs: DI lifetimes, `async void`, `ConfigureAwait`, `IEnumerable` vs `IQueryable`, EF Core tracking, middleware order, JWT validation, password hashing, CSRF, TLS, TCP vs UDP, and caching. Include 3 `jazari` questions (unlimited use, whom to contact, is there a subscription). Don't copy from any tuning or training text.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

Message: `feat(eval): 45 C#/.NET test questions`.

### Task 3: Runner

**Files:**
- Create: `eval/run_eval.py`, `eval/test_run_eval.py`

**Interfaces:**
- Consumes: `score` from Task 1, `questions.json` from Task 2.
- Produces: `system_prompt(source_path) -> str`, which reads `PromptBuilder.SystemPrompt` from the C# file; `run_questions(questions: list[dict], ask: Callable[[dict], str]) -> list[dict]`, one entry per question with `answer`, `seconds`, `passed` and, on failure, `error`; `start_server(args) -> Popen`, raising `PortInUse` when 8099 is taken. Produces the CLI `py -I eval/run_eval.py --label NAME --model PATH [--threads 4] [--device none] [--gpu-layers 0] [--scenarios]`, which writes `eval/results/NAME.json` shaped `{"label", "score", "total", "plain_p50_s", "plain_max_s", "answers": [...], "read_first_s", "followup_s", "edit_ok", "review_ok"}`. The last four are filled in by Task 4. `read_first_s` and `followup_s` default to `null`, and `pick` treats null as failing.

- [ ] **Step 1: Write the failing tests**

```python
def test_system_prompt_matches_the_gateway_source(self):
    prompt = system_prompt(PROMPT_BUILDER)
    self.assertTrue(prompt.startswith("You are Jazari"))
    self.assertIn("entirely on this PC", prompt)
    self.assertNotIn('" +', prompt)

def test_a_failed_request_scores_as_failed_and_the_run_goes_on(self):
    answers = run_questions([Q1, Q2], ask=lambda q: raise_or("boom", q))
    self.assertEqual(len(answers), 2); self.assertFalse(answers[0]["passed"]); self.assertIn("boom", answers[0]["error"])

def test_port_in_use_is_reported(self):  # hold 8099 with a socket; assert start_server raises PortInUse
```

- [ ] **Step 2: Run them to see them fail**

Expected: FAIL, not defined.

- [ ] **Step 3: Implement**

- Default `llama-server` path: `%LOCALAPPDATA%\Jazari\llama\llama-server.exe`; `--llama` overrides.
- Start arguments match `Tuning.ServerArgs`: `-m`, `-dev`, `-ngl`, `-t`, `--poll 0`, `-fa on`, `-c 16384`, `--cache-ram 2048`, `-np 1`, `--cache-reuse 256`, `--no-webui`, host `127.0.0.1`, port 8099.
- Each question is a request of its own, shaped like `LlamaClient`'s chat body, with `max_tokens` 512 (`DefaultChatTokens`). Time each one.
- Wait up to 180 s for `/health`. Always kill the server in `finally`. A server that doesn't come up gives a non-zero exit and a clear message, and writes no results file.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Try it on one model**

Run: `py -I eval/run_eval.py --label smoke --model spike/models/granite-4.1-3b-Q4_K_M.gguf --threads 4`
Expected: `eval/results/smoke.json` with 45 answers; `tasklist` shows no `llama-server` afterwards. Takes about 15 minutes.

- [ ] **Step 6: Commit**

Message: `feat(eval): run the test set against a model`.

### Task 4: Project scenarios

**Files:**
- Modify: `eval/run_eval.py`, `eval/test_run_eval.py`

**Interfaces:**
- Consumes: the `spike/review-test` fixture, which has known planted bugs, and `FileEdit.Schema` plus `ProjectReview`'s schema, read from their C# sources the way `system_prompt` reads its own.
- Produces: the `read_first_s`, `followup_s`, `edit_ok` and `review_ok` fields, when `--scenarios` is passed. Also `turn_times_s`, a list of 6 from the conversation test.

- [ ] **Step 1: Write the failing tests**

Against a fake `ask`:
- `read_scenario` returns the time of the first answer, then of a follow-up that repeats the same prefix with `cache_prompt` as the gateway sends it.
- `edit_scenario` is true only when the reply parses against the schema and its search text occurs exactly once in the file.
- `review_scenario` is true only when the reply parses and reports the planted hardcoded password.
- Test each with a passing and a failing reply.

- [ ] **Step 2: Run them to see them fail**

Expected: FAIL, not defined.

- [ ] **Step 3: Implement**

- `/read`: about 2,000 tokens of the fixture as a system message, as the gateway sends context, then a question, then a follow-up.
- `/edit`: one fixture file and one small change.
- `/review`: one part of the fixture.
- Conversation: 6 turns growing the history, recording each turn's time.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

Message: `feat(eval): project scenarios for /read, /edit and /review`.

### Task 5: Run the comparison and choose

**Files:**
- Create: `eval/compare.py`, `docs/superpowers/specs/2026-11-02-model-choice-results.md`

**Interfaces:**
- Consumes: `pick` from Task 1, results files.
- Produces: the winner's label, file name and settings, used by Tasks 6 and 7.

- [ ] **Step 1: Run every candidate with `--scenarios`, one at a time, with little else running**

| Label | Model in `spike/models` | Settings |
|---|---|---|
| `3b-mx150` | `granite-4.1-3b-Q4_K_M.gguf` | `--threads 4 --device Vulkan1 --gpu-layers 18` |
| `3b-cpu` | same | `--threads 4` |
| `tiny-cpu` | `granite-4.0-h-tiny-Q4_K_M.gguf` | `--threads 4` |
| `g42-cpu` | `granite-4.2-3b-Q4_K_M.gguf` | `--threads 4` |
| `qwen3-cpu` | `qwen3-4b-instruct-2507-Q4_K_M.gguf` | `--threads 4` |

Expected: five results files, or a recorded failure for a candidate that can't load. About 30 minutes each. Run them in the background.

- [ ] **Step 2: Read the saved answers by hand**

For each candidate, read every answer the scorer got wrong or right on a technicality. Fix `questions.json` where the check was at fault, not the model, and re-score from the saved answers. Never change a check because a favoured model failed it.

- [ ] **Step 3: Implement `eval/compare.py`**

It prints a table of all results and the `pick` winner. Run it.

- [ ] **Step 4: Write the results file**

The table, the winner, the licence of the winner, and anything that surprised you. Stop and show Eni-M before Task 7 if the winner isn't Apache 2.0, or if no candidate passes the limits.

- [ ] **Step 5: Commit**

Message: `docs: model comparison results`.

### Task 6: Half the CPU, low priority, and the tuner's MX150 result

**Files:**
- Modify: `src/MyOwnLLM.Cli/DeviceTuner.cs`, `src/MyOwnLLM.Cli/Tuning.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/DeviceTunerTests.cs`

**Interfaces:**
- Produces: `DeviceTuner.ThreadCounts(int logicalCores)`, returning only the half count (at least 1). `Tuning.Default` uses half too. `Tuning.ServerArgs` gains `--prio -1` if step 1 keeps it.

- [ ] **Step 1: Measure low priority**

Run `llama-bench` on the winner with `--prio -1` and without it, idle, and again with a CPU load running, like `dotnet build` in a loop.
Decision: keep `--prio -1` if idle writing speed drops less than 10%. Write the numbers in the results file.

- [ ] **Step 2: Find out why saved tuning chose CPU over the MX150**

Run the tuner's exact command: `llama-bench -m <3B> -dev Vulkan1 -ngl 18 -t 8 --poll 0 -fa 0,1 -p 512 -n 64 -r 2 -o json`. Compare it with today's 59.5 / 6.6 result. Likely causes to check: failing with flash attention on, `-t 8`, or `LayersThatFit` using free memory that changes. Use superpowers:systematic-debugging, and fix the root cause in this task with a test of its own.

- [ ] **Step 3: Write the failing tests**

```csharp
[Theory]
[InlineData(8, new[] { 4 })]
[InlineData(4, new[] { 2 })]
[InlineData(1, new[] { 1 })]
public void Threads_stay_at_half_so_the_laptop_stays_usable(int logical, int[] expected) =>
    Assert.Equal(expected, DeviceTuner.ThreadCounts(logical));
```

Also update `Default_settings_fit_the_ram` to `(4, 16384, 2048)` and `(2, 8192, 1024)`, the bench JSON tests that rely on 3 thread counts, and add a test for `--prio -1` if kept.

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test tests/MyOwnLLM.Gateway.Tests --nologo -v q --disable-build-servers --filter DeviceTuner`
Expected: FAIL.

- [ ] **Step 5: Implement, then run all tests**

Update the comment on `Tuning.Default`, which says three threads in four. Expected: all PASS.

- [ ] **Step 6: Commit**

Message: `feat(cli): half the CPU threads, so the laptop stays usable`.

### Task 7: Ship the winner

Skip this task's model steps if the winner is the current 3B; the tuner then needs no model change.

**Files:**
- Modify: `scripts/jazari-manifest.json`, `src/MyOwnLLM.Gateway/GatewayOptions.cs`, `src/MyOwnLLM.Cli/DeviceTuner.cs`, `src/MyOwnLLM.Cli/Session.cs`, `docs/OPTIMIZATION-LOG.md`
- Test: `DeviceTunerTests.cs`, `PromptEditorTests.cs`

**Interfaces:**
- Consumes: the winner from Task 5.

- [ ] **Step 1: Write the failing tests**

- `ModelFromFile("granite-4.0-h-tiny-Q4_K_M.gguf")` returns `"granite tiny"` (only if Tiny won). Today's pattern gives "the model".
- `LayersThatFit` uses the winner's layer count, read from `llama-server`'s log or a named constant.
- The panel's short model name drops the version from the new `ModelName`.

- [ ] **Step 2: Run them to see them fail**

Expected: FAIL.

- [ ] **Step 3: Update the manifest and names**

Set `modelFile`, `modelUrl` and `modelSha256` from the official IBM Hugging Face file; get the SHA with `Get-FileHash` on `spike/models` and check it against Hugging Face's listed value. Set `ModelName` in `GatewayOptions`. Fix `ModelFromFile` and the layer count.

- [ ] **Step 4: Run all tests**

Run: `dotnet test tests/MyOwnLLM.Gateway.Tests --nologo -v q --disable-build-servers`
Expected: all PASS. Run the parser check on `scripts/update-jazari.ps1`.

- [ ] **Step 5: Log and commit**

Add the results and decision to `docs/OPTIMIZATION-LOG.md`. Message: `feat: <winner> is Jazari's model, chosen by the test set`.

- [ ] **Step 6: Hand over**

Stop here. Tell Eni-M the score, the time per question, and the download size. Ask before merging to `main` or pushing. After that, Eni-M runs `jazari update` from their own terminal, and says yes to the tuning question, since a new model or new thread limit makes the old tuning out of date. Then write the plan for spec phase 3.
