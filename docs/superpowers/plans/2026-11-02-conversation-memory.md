# Conversation Memory and Context Fixes: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jazari stays accurate deep in a conversation and after a history cut, says when history is cut, and holds less memory.

**Architecture:** The CLI already sends the whole conversation with every request, so the gateway searches it with BM25 and puts the best old lines inside the question's message. Nothing is stored, and nothing a model wrote is pinned. A recall test in `eval/` runs through the real gateway and decides whether this ships.

**Tech Stack:** C#/.NET 10, xUnit, Python 3.12 stdlib (`py -I`, `unittest`), llama.cpp b11982.

**Spec:** `reports/Local inference sources for Jazari.md`, items 1–5 and "The design this points to". Measurements: `research_notes/Local inference sources/`.

## Global Constraints

- Never run the real Jazari app, `jazari update` or the installer from Claude Desktop. A temporary `llama-server` on port 8099 and a temporary gateway on port 5099 with its own temp data folder are allowed. Stop them when done.
- Build and test with `--disable-build-servers`.
- Keep code that touches the registry or writes executables out of Jazari and its tests: Smart App Control blocks those builds.
- Time limits: plain question at most 30 s; first answer after `/read` at most about 2 minutes; follow-up at most 30 s.
- Quality floor before speed: anything that lowers the test set score doesn't ship.
- No model-written summaries or memory. Only personal test code, never employer code. Recall facts are invented and stay out of training data.
- Base model: Granite 4.0-H Tiny Q4_K_M at `spike/models/granite-4.0-h-tiny-Q4_K_M.gguf`, with `spike/bin/llama-server.exe`.
- Start from `main` on a new branch, `feat/conversation-memory`, and first commit the research files already in the working tree (`reports/Local inference sources for Jazari.md`, `research_notes/Local inference sources/`, `docs/OPTIMIZATION-LOG.md`, this plan).

## Review Focus

1. A question that shares words with pasted code: the list should hold the fact, not fill up with code lines. Pinned in Task 2.
2. A long pasted line, like a minified file or log: skipped, with no slowdown. Pinned in Task 2.
3. Asking the same question twice: the earlier question isn't pinned as if it were an answer. Pinned in Task 2.
4. Non-English words and accents in a question or a fact: they match. Pinned in Task 2.
5. The gateway restarting mid-conversation: it uses the current note and carries on, at the cost of one re-read. Pinned in Task 3.

## Left out on purpose

Each has a reason in the report, and comes back only if the recall test or real use asks for it: a SQLite store (the CLI already holds the conversation), an earlier history cut, moving `/read` text, searching `/read` bodies or the model's answers, and a "nothing about this earlier" line.

---

### Task 1: Recall test through the real gateway, with a baseline

**Files:**
- Modify: `src/MyOwnLLM.Gateway/GatewayOptions.cs`, `src/MyOwnLLM.Gateway/Program.cs`, `.gitignore` (add `eval/results/recall/`)
- Create: `eval/recall.py`, `eval/test_recall.py`

**Interfaces:**
- Produces: `GatewayOptions.Port` (int, default 5080), and `eval/recall.py` with `FACTS`, `is_right(answer: str, want: str) -> bool`, `gateway_env(data_dir: str, llama_port: int, port: int) -> dict`, and CLI options `--label --model --threads --llama`. Writes `eval/results/recall/<label>.json`, a subfolder so `compare.py` doesn't take it for a candidate, holding `short`, `deep` and `cut`, each `{"score": int, "of": 12, "median_s": float, "answers": [...]}`, plus `deep_tokens` and `working_set_mib`.
- Consumes: from `run_eval.py`: `start_server`, `wait_until_ready`, `PortInUse`, `ServerDidNotStart`, `save_failure`.

- [ ] **Step 1: Write the failing tests in `eval/test_recall.py`**

```python
def test_there_are_twelve_facts_in_confusable_pairs(self):
    self.assertEqual(len(FACTS), 12)

def test_right_answer_and_swapped_value(self):
    self.assertTrue(is_right("Invoices are kept for 7 years.", FACTS[1].want))
    self.assertFalse(is_right("Invoices are kept for 2 years.", FACTS[1].want))

def test_gateway_is_pointed_at_the_temporary_server_and_folder(self):
    env = gateway_env("C:/tmp/x", 8099, 5099)
    self.assertEqual(env["Gateway__LlamaUrl"], "http://127.0.0.1:8099")
    self.assertEqual(env["Gateway__Port"], "5099")
    self.assertEqual(env["Gateway__DataDir"], "C:/tmp/x")
```

- [ ] **Step 2: Run them to see them fail**

Run: `py -I eval/test_recall.py`
Expected: FAIL, no module named `recall`.

- [ ] **Step 3: Add `GatewayOptions.Port = 5080`, and in `Program.cs` use `UseUrls($"http://127.0.0.1:{options.Port}")`**

- [ ] **Step 4: Write `eval/recall.py`**

Take `FACTS` from `research_notes/Local inference sources/recall_probe.py` as a named tuple of `fact, question, want`. Flow:

1. `dotnet build src/MyOwnLLM.Gateway -c Release --disable-build-servers`, run `dotnet <dll> add-key` with `gateway_env` and take the key from its first output line, then start `dotnet <dll>` in a temp folder, deleted at the end.
2. Start `llama-server` with `Tuning.ServerArgs`'s settings for a T480 CPU run (`-dev none -ngl 0 -t 4 --poll 0 -fa off -c 16384 --cache-ram 2048 -np 1 --cache-reuse 256 --no-webui`) on port 8099.
3. `short`: a facts-only conversation, then ask all 12 questions, each on top of the same history.
4. `deep`: the same with one 4,460-character chunk of this repo's C# after each fact, about 12,700 tokens. Record `deep_tokens` as characters / 4, and `working_set_mib` from `tasklist` for the server's process.
5. Stop the server and start it again with `-c 10240`. `cut`: ask all 12 on the same deep conversation, so the gateway cuts 4 pairs, taking the first 4 facts with them, as in the probe.

All requests go to the gateway as `POST /v1/chat/completions` with `max_tokens` 40 and `stream` false, with no project header. Time each question. On `PortInUse` exit with its message, and on `ServerDidNotStart` use `save_failure` with the recall folder. Always stop both processes.

- [ ] **Step 5: Run the tests**

Run: `py -I eval/test_recall.py`, then `dotnet test MyOwnLLM.slnx --disable-build-servers`
Expected: PASS.

- [ ] **Step 6: Run the baseline, about 15 minutes**

Run: `py -I eval/recall.py --label tiny-before --model spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama spike/bin/llama-server.exe`
Expected: around `short` 12/12, `deep` 9/12, `cut` 7/12, and `working_set_mib` near 9,500. Far off means the harness differs from the probe: find out why before going on. Copy `eval/results/recall/tiny-before.json` to `research_notes/Local inference sources/tiny-before-recall.json`.

- [ ] **Step 7: Commit**

`git commit -m "feat(eval): recall test through the real gateway"`

### Task 2: Search the conversation and pin the best lines

**Files:**
- Create: `src/MyOwnLLM.Gateway/Prompt/ConversationSearch.cs`, `tests/MyOwnLLM.Gateway.Tests/ConversationSearchTests.cs`
- Modify: `src/MyOwnLLM.Gateway/Prompt/PromptBuilder.cs`, `tests/MyOwnLLM.Gateway.Tests/PromptBuilderTests.cs`

**Interfaces:**
- Produces: `public static string? ConversationSearch.Pin(IReadOnlyList<ChatMessage> history, string question)`, and `PromptBuilder.Build(PromptParts parts, int contextTokens, int maxOutputTokens, out int droppedMessages)`. The existing three-argument `Build` stays and calls it.

- [ ] **Step 1: Write the failing tests**

`History(params string[] userLines)` builds user/assistant pairs, then pads with unrelated pairs so the history is over 2,000 tokens and the facts are outside the last 4 messages. `Twelve` is the 12 facts from `eval/recall.py`.

```csharp
[Theory, MemberData(nameof(Twelve))]
public void Finds_the_line_for_each_question(string fact, string question) =>
    Assert.Contains(fact, ConversationSearch.Pin(HistoryWithAllFacts(), question));

[Fact] public void Short_conversation_pins_nothing() =>
    Assert.Null(ConversationSearch.Pin([new("user", "The payment timeout is 45 seconds."), new("assistant", "ok")], "What is the payment timeout?"));

[Fact] public void The_last_two_exchanges_are_not_searched() { /* fact only in the last 4 messages: Pin is null */ }
[Fact] public void The_models_own_answers_are_not_searched() { /* fact only in an assistant message: Pin is null */ }
[Fact] public void A_question_with_no_matching_words_pins_nothing() { /* "What is dependency injection?" and "go on": both null */ }
[Fact] public void Pin_stays_inside_its_budget() { /* many matching lines: EstimateTokens(pin) <= 250 */ }
[Fact] public void Lines_come_back_in_conversation_order() { /* two matching lines, later one scores higher: earlier one first */ }
[Fact] public void Fact_beats_code_lines_that_share_its_words() { /* filler code lines holding "payment" and "timeout" once each among other words: the fact line is in the pin */ }
[Fact] public void A_very_long_line_is_skipped() { /* matching line over 300 characters: not in the pin */ }
[Fact] public void An_earlier_copy_of_the_question_is_not_pinned() { /* same question asked earlier: its line isn't in the pin */ }
[Fact] public void Accented_words_match() { /* line "Porta e bazës është 5433", question "Cila është porta e bazës?": line in the pin */ }
```

In `PromptBuilderTests`:

```csharp
[Fact] public void Pinned_lines_go_inside_the_question_message()
{
    var messages = PromptBuilder.Build(new PromptParts(null, null, HistoryWithAllFacts(), "What is the payment timeout?"), 16384, 512);
    Assert.StartsWith("From earlier in this conversation:\n", messages[^1].Content);
    Assert.Contains("45 seconds", messages[^1].Content);
    Assert.EndsWith("\n\nWhat is the payment timeout?", messages[^1].Content);
}
[Fact] public void A_line_lost_to_the_cut_is_still_pinned() { /* history over the limit, fact in a dropped turn: dropped > 0, and fact in the last message */ }
[Fact] public void Dropped_messages_are_counted() { /* Build(..., out var dropped): dropped is a multiple of 8, 0 when it fits */ }
[Fact] public void The_history_itself_is_unchanged_by_pinning() { /* every message before the last equals the same Build without the fact in the question */ }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers --filter "ConversationSearch|PromptBuilder"`
Expected: FAIL to compile, `ConversationSearch` doesn't exist.

- [ ] **Step 3: Implement `ConversationSearch`**

Constants: `MinHistoryTokens = 2000`, `RecentMessages = 4`, `MaxPinTokens = 250`, `MaxLineChars = 300`, `MaxLines = 8`. Return null when the history is under `MinHistoryTokens` by `PromptBuilder.EstimateTokens`.

- Documents: distinct lines of the `user` messages outside the last `RecentMessages`, each at most `MaxLineChars`, and not equal to the question.
- Terms: `[GeneratedRegex(@"\p{Lu}?\p{Ll}+|\p{Lu}+(?!\p{Ll})|\d+")]`, so `OrderRepository` splits. Lowercase, drop stopwords, keep the first 4 characters of longer words, so `retry` and `retries` match.
- Stopwords: `a about an and are at be can do does for from how i in is it many me much my of on or our that the this to was we what when where which who why will with you your`.
- Score: BM25 with k1 1.5 and b 0.75, over the question's distinct terms.
- Keep a line only if it matches at least half of those terms. If the question has no terms, return null.
- Take lines best first until the next one would pass `MaxPinTokens` or `MaxLines`, then return them in conversation order, joined by newlines.

Add a `ponytail:` comment naming the ceilings: `/read` text and the model's answers aren't searched; add them when a test covers them. And why: a line of its own is what the recall test measured, and a full 250 tokens re-read each turn is about 8 s, paid only when that many lines match. Eni-M accepts a little more time for accuracy.

- [ ] **Step 4: Wire it into `PromptBuilder.Build`**

Compute the pin first from `parts.History` and `parts.Question`. The question message becomes `"From earlier in this conversation:\n" + pin + "\n\n" + question`, and counts towards `required`. `droppedMessages` is the number removed by the existing `RemoveRange`. Leave `DropStep` and the fixed start unchanged.

- [ ] **Step 5: Run the tests**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers`
Expected: PASS, including the existing `Build_drops_oldest_history_to_fit`, where nothing matches "QUESTION".

- [ ] **Step 6: Commit**

`git commit -m "feat(gateway): put the matching earlier lines inside the question"`

### Task 3: Freeze the project note per conversation

**Files:**
- Create: `src/MyOwnLLM.Gateway/State/ConversationNotes.cs`
- Modify: `src/MyOwnLLM.Gateway/Endpoints/ChatEndpoints.cs` (`NoteFor`, `Chat`, `Warm`), `src/MyOwnLLM.Gateway/Program.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/EndpointTests.cs`

**Interfaces:**
- Produces: singleton `ConversationNotes` with `string? For(string project, bool newConversation, string? current)`. It stores and returns `current` when `newConversation` is true or nothing is stored for that project. Otherwise it returns what's stored.

`/v1/status` keeps reading the latest note. `CompleteAsync` sends no project header, so `/commit` and `/edit` can't refresh it. A conversation is new when its history is empty, which is also what `/clear` leaves.

- [ ] **Step 1: Write the failing tests**

Each uses a temp folder holding a `.git` directory as the project, and sets the note through `ProjectStateStore.SetAsync`.

```csharp
[Fact] public async Task A_note_updated_mid_conversation_stays_out_of_the_prompt() { /* note A; question with empty history; note B; question with history: last call's messages[1] holds A, not B */ }
[Fact] public async Task A_new_conversation_uses_the_latest_note() { /* after that, a question with empty history: holds B */ }
[Fact] public async Task After_a_restart_the_conversation_takes_the_current_note() { /* nothing stored, question with history: holds the current note */ }
[Fact] public async Task Warm_uses_the_same_note_as_the_conversation() { /* note A, question, note B, warm with history: holds A */ }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers --filter "EndpointTests"`
Expected: FAIL, the first and last tests get B.

- [ ] **Step 3: Implement `ConversationNotes` with a `ConcurrentDictionary`, keep `NoteFor` as the store read, and add `ConversationNoteFor(s, project, history.Count == 0)` for `Chat` and `Warm`**

Register the singleton in `Program.cs` and add it to `Services`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers`
Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "fix(gateway): a note update no longer forces a re-read mid-conversation"`

### Task 4: Say when history is cut

**Files:**
- Modify: `src/MyOwnLLM.Gateway/Endpoints/ChatEndpoints.cs` (streamed final event), `src/MyOwnLLM.Cli/SseChunk.cs`, `src/MyOwnLLM.Cli/Session.cs`
- Test: `tests/MyOwnLLM.Gateway.Tests/EndpointTests.cs`, `SseChunkTests.cs`, `SessionTests.cs`

**Interfaces:**
- Consumes: `PromptBuilder.Build(..., out int droppedMessages)` from Task 2.
- Produces: final SSE event field `dropped_turns` (int, `droppedMessages / 2`), and `SseChunk.DroppedTurns` (int, default 0, last parameter). A missing field means 0, so an older gateway still works.

- [ ] **Step 1: Write the failing tests**

```csharp
// EndpointTests
[Fact] public async Task Stream_final_event_says_how_many_turns_were_cut() { /* _llama.ContextTokens = 2048, 10 big pairs: final event dropped_turns > 0 */ }
// SseChunkTests
[Fact] public void Final_chunk_carries_dropped_turns() { /* "dropped_turns":4 → DroppedTurns == 4 */ }
[Fact] public void Missing_dropped_turns_means_none() { /* existing final chunk → 0 */ }
// SessionTests
[Fact] public async Task A_cut_is_mentioned_once_and_again_when_more_goes() { /* answers with 4, 4, 8: the line appears after the first and third, with "4" and "8" */ }
[Fact] public async Task Clear_starts_counting_again() { /* 4, /clear, 4: the line appears twice */ }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers --filter "SseChunk|SessionTests|EndpointTests"`
Expected: FAIL to compile, `DroppedTurns` doesn't exist.

- [ ] **Step 3: Implement**

`Session` keeps `_droppedTurns`, reset by `/clear`. After an answer, if the chunk's count is higher, show this text, as `ui.Row("cut", text, Tone.Secondary)` in a live terminal and `ui.Metadata(text)` otherwise:

`The oldest {n} question{s} no longer fit in the model's memory. Jazari can still look up what you said in them.`

- [ ] **Step 4: Run the tests**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers`
Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(cli): say when old questions no longer fit"`

### Task 5: Hold less memory

**Files:**
- Modify: `src/MyOwnLLM.Cli/Tuning.cs`, `src/MyOwnLLM.Cli/ServerLauncher.cs:39-42`, `eval/run_eval.py` (`server_args`), `eval/recall.py`
- Test: `tests/MyOwnLLM.Gateway.Tests/DeviceTunerTests.cs`, `eval/test_run_eval.py`

**Interfaces:**
- Produces: `Tuning.ServerArgs` always passes `-ctxcp 8 --cache-ram 1024`. The `CacheRamMiB` record parameter is removed. An older `tuning.json` with that field still loads, because unknown fields are ignored.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Server_args_keep_memory_small()
{
    var args = new Tuning("f", "Vulkan0", 4, 99, true, 16384).ServerArgs("m.gguf");
    Assert.Equal("8", args[Array.IndexOf(args, "-ctxcp") + 1]);
    Assert.Equal("1024", args[Array.IndexOf(args, "--cache-ram") + 1]);
}
[Fact] public void An_older_tuning_file_still_loads() { /* JSON with "cacheRamMiB": 2048: Load returns it with the other fields intact */ }
```

Update the two `Tuning.Default` tuple assertions in `DeviceTunerTests.cs:57-58` and `Server_args_carry_the_tuned_settings` for the removed parameter. Add to `eval/test_run_eval.py`: `server_args` holds `-ctxcp`, `8`, `--cache-ram`, `1024`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers --filter "DeviceTuner"`, then `py -I eval/test_run_eval.py`
Expected: FAIL.

- [ ] **Step 3: Implement**

Remove `CacheRamMiB` from the record and `Default`, add a `const int CacheRamMiB = 1024`, and change the launcher's built-in line and both eval scripts to match. Comment the reason at the constant: on H-Tiny a checkpoint is 55 MiB of Mamba state, and a saved prompt with 32 of them was 1.9 GB. The fingerprint is unchanged, so no PC re-tunes.

- [ ] **Step 4: Run the tests**

Run: `dotnet test MyOwnLLM.slnx --disable-build-servers`, then `py -I eval/test_run_eval.py`
Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "perf: fewer checkpoints and a smaller prompt cache"`

### Task 6: The gate, on the real model

**Files:**
- Modify: `docs/OPTIMIZATION-LOG.md`, `reports/Local inference sources for Jazari.md`, `README.md:5`
- Create: `research_notes/Local inference sources/tiny-after-recall.json`

- [ ] **Step 1: Run the recall test, about 15 minutes**

Run: `py -I eval/recall.py --label tiny-after --model spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama spike/bin/llama-server.exe`

Pass: `deep` at least 11/12, `cut` at least 10/12, `short` still 12/12, `median_s` at most 30, and `working_set_mib` at least 1.5 GB below the baseline.

- [ ] **Step 2: If it doesn't pass, stop and report**

Try changing `MaxPinTokens` (250, then 350) and `MinHistoryTokens` once each. If it still fails, don't ship the failing part: write down the numbers and bring them to Eni-M. Don't lower the gate.

- [ ] **Step 3: Check that nothing else got worse**

Run: `py -I eval/run_eval.py --label tiny-regress --model spike/models/granite-4.0-h-tiny-Q4_K_M.gguf --llama spike/bin/llama-server.exe --threads 4 --scenarios`, then `py -I eval/compare.py`

Pass: at least 37/45, with `/read`, follow-up, `/edit` and `/review` inside their limits. This goes straight to the server, so it checks the new server flags. Pinning can't change a question with no history. Read five of the recall answers by hand to check the model doesn't echo the "From earlier" heading.

- [ ] **Step 4: Record it**

Copy `eval/results/recall/tiny-after.json` to `research_notes/Local inference sources/tiny-after-recall.json`. Add the before and after numbers to `docs/OPTIMIZATION-LOG.md`, replace "Nothing is built yet" there, mark items 1–5 done in the report, and correct `README.md:5`, the older "hybrids can't KV-shift" notes the report lists, and the checkpoint line in `reports/Jazari model bridge and efficiency.md`.

- [ ] **Step 5: Confirm the servers are stopped, and commit**

Run: `netstat -ano | grep -E ":8099 |:5099 " | grep -c LISTENING`
Expected: `0`

`git commit -m "docs: recall before and after conversation memory"`

- [ ] **Step 6: Ask Eni-M to try it from their own terminal**

Claude Desktop can't run the real app. Eni-M runs `jazari update` and has a long conversation, watching for the `cut` row and whether answers feel right.
