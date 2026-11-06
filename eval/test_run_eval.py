import os, re, socket, sys, unittest
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import argparse, json, tempfile
from compare import by_category, mcnemar_p, pair_report, paired, table
from run_eval import PortInUse, law_questions, conversation, plain_through_gateway, with_passages, csharp_int, csharp_string, edit_scenario, read_context, read_scenario, review_scenario, run_questions, save_failure, server_args, start_server, system_prompt

PROMPT_BUILDER = os.path.join(HERE, "..", "src", "MyOwnLLM.Gateway", "Prompt", "PromptBuilder.cs")
Q1 = {"id": "a", "category": "api", "question": "boom", "must_have": [["x"]], "must_not": []}
Q2 = {"id": "b", "category": "api", "question": "fine", "must_have": [["x"]], "must_not": []}


def raising_on_boom(q):
    if q["question"] == "boom":
        raise RuntimeError("boom")
    return "x"


class RunnerTests(unittest.TestCase):
    def test_system_prompt_matches_the_gateway_source(self):
        prompt = system_prompt(PROMPT_BUILDER)
        self.assertTrue(prompt.startswith("You are Jazari"))
        self.assertIn("entirely on this PC", prompt)
        self.assertTrue(prompt.endswith("products."))
        self.assertNotIn('" +', prompt)
        self.assertNotIn("Tried on the real model", prompt)  # comments aren't part of it

    def test_a_failed_request_scores_as_failed_and_the_run_goes_on(self):
        answers = run_questions([Q1, Q2], raising_on_boom)
        self.assertEqual(len(answers), 2)
        self.assertFalse(answers[0]["passed"])
        self.assertIn("boom", answers[0]["error"])
        self.assertTrue(answers[1]["passed"])
        self.assertGreaterEqual(answers[1]["seconds"], 0)

    def test_port_in_use_is_reported(self):
        with socket.socket() as held:
            held.bind(("127.0.0.1", 0))
            held.listen()
            with self.assertRaises(PortInUse):
                start_server(["unused.exe"], port=held.getsockname()[1])


def replying(*replies):
    """A fake model: answers in turn, and records what it was sent."""
    calls = []
    def ask(messages, max_tokens=512, json_schema=None):
        calls.append({"messages": list(messages), "max_tokens": max_tokens, "json_schema": json_schema})
        return replies[min(len(calls) - 1, len(replies) - 1)]
    return ask, calls


class FailureTests(unittest.TestCase):
    def test_a_setup_that_cannot_load_replaces_its_old_result(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "x.json")
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"label": "x", "score": 45}, f)
            save_failure("x", "would not load", folder)
            with open(path, encoding="utf-8") as f:
                self.assertEqual(json.load(f), {"label": "x", "failed": "would not load"})

    def test_the_comparison_lists_a_failed_setup(self):
        rows = table([{"label": "broken", "failed": "would not load"}])
        self.assertIn("broken", rows)
        self.assertIn("would not load", rows)
        self.assertEqual(by_category([{"label": "broken", "failed": "would not load"}]), "")


class ServerArgsTests(unittest.TestCase):
    def args(self, **changes):
        settings = {"llama": "l.exe", "model": "m.gguf", "device": "none", "gpu_layers": 0, "threads": 4, "reasoning": "auto", **changes}
        return server_args(argparse.Namespace(**settings))

    def test_thinking_can_be_turned_off(self):
        args = self.args(reasoning="off")
        self.assertEqual(args[args.index("--reasoning") + 1], "off")

    def test_thinking_is_left_to_the_model_by_default(self):
        self.assertNotIn("--reasoning", self.args())

    def test_memory_is_kept_small_as_in_jazari(self):
        args = self.args()
        self.assertEqual(args[args.index("-ctxcp") + 1], "8")
        self.assertEqual(args[args.index("--cache-ram") + 1], "1024")

    def test_a_router_preset_replaces_the_model_and_keeps_one_loaded(self):
        args = self.args(router_preset="lanes.ini")
        self.assertEqual(args[args.index("--models-preset") + 1], "lanes.ini")
        self.assertEqual(args[args.index("--models-max") + 1], "1")
        self.assertNotIn("-m", args)


class ScenarioTests(unittest.TestCase):
    def test_read_context_is_about_2000_tokens_of_real_code(self):
        context = read_context()
        self.assertGreater(len(context), 7000)
        self.assertLessEqual(len(context), 8000)
        self.assertIn("--- ", context)

    def test_edit_and_review_constants_come_from_the_cli_source(self):
        self.assertIn("Answer only with edits", csharp_string("FileEdit.cs", "Instructions"))
        self.assertIn('"edits"', csharp_string("FileEdit.cs", "Schema"))
        self.assertIn('"findings"', csharp_string("ProjectReview.cs", "Schema"))
        self.assertIn("senior security-minded", csharp_string("ProjectReview.cs", "Instructions"))

    def test_the_table_shows_review_found_and_false_alarms(self):
        result = {"label": "a", "score": 1, "total": 1, "plain_p50_s": 1, "plain_max_s": 1, "read_first_s": 1, "followup_s": 1, "edit_ok": True,
                  "review_ok": True, "review_found": 2, "review_false_alarms": 7, "turn_times_s": []}
        header, row = table([result]).splitlines()
        self.assertIn("found", header)
        self.assertIn("false", header)
        self.assertRegex(row, r"\b2\s+7\b")

    def test_an_older_result_without_those_numbers_still_prints(self):
        older = {"label": "a", "score": 1, "total": 1, "plain_p50_s": 1, "plain_max_s": 1, "read_first_s": 1, "followup_s": 1, "edit_ok": True, "review_ok": True, "turn_times_s": []}
        self.assertIn("None", table([older]).splitlines()[1])

    def test_a_followup_repeats_the_first_turn_so_the_cache_can_serve_it(self):
        ask, calls = replying("first answer", "second answer")
        first_s, followup_s = read_scenario(ask, "SYSTEM", "CODE")
        self.assertEqual(calls[1]["messages"][:len(calls[0]["messages"])], calls[0]["messages"])
        self.assertEqual(calls[1]["messages"][len(calls[0]["messages"])], {"role": "assistant", "content": "first answer"})
        self.assertTrue(any("CODE" in m["content"] for m in calls[0]["messages"]))
        self.assertGreaterEqual(first_s, 0)
        self.assertGreaterEqual(followup_s, 0)

    def test_an_edit_counts_when_its_search_text_occurs_exactly_once(self):
        ask, calls = replying(json.dumps({"edits": [{"search": "limit = 16", "replace": "limit = 32"}]}))
        self.assertTrue(edit_scenario(ask, "SYSTEM", "int limit = 16;"))
        self.assertIsNotNone(calls[0]["json_schema"])

    def test_an_edit_fails_on_text_that_appears_twice(self):
        ask, _ = replying(json.dumps({"edits": [{"search": "a", "replace": "b"}]}))
        self.assertFalse(edit_scenario(ask, "SYSTEM", "a a"))

    def test_an_edit_fails_on_text_that_is_not_there(self):
        ask, _ = replying(json.dumps({"edits": [{"search": "nope", "replace": "b"}]}))
        self.assertFalse(edit_scenario(ask, "SYSTEM", "int limit = 16;"))

    def test_an_edit_fails_on_a_reply_that_is_not_json(self):
        ask, _ = replying("Sure! Change it to 32.")
        self.assertFalse(edit_scenario(ask, "SYSTEM", "int limit = 16;"))

    def finding(self, file, problem):
        return {"file": file, "quote": "x", "problem": problem, "severity": "low"}

    def reviewing(self, mixed, clean=()):
        ask, calls = replying(json.dumps({"findings": list(mixed)}), json.dumps({"findings": list(clean)}))
        return review_scenario(ask, "SYSTEM"), calls

    def test_a_review_counts_when_it_reports_the_planted_password(self):
        result, _ = self.reviewing([self.finding("AuthService.cs", "Hardcoded password in the code")])
        self.assertTrue(result["review_ok"])

    def test_a_review_fails_when_it_misses_the_password(self):
        result, _ = self.reviewing([self.finding("Order.cs", "Weak naming")])
        self.assertFalse(result["review_ok"])

    def test_a_review_fails_on_a_reply_that_is_not_json(self):
        ask, _ = replying("Looks fine")
        self.assertEqual(review_scenario(ask, "SYSTEM"), {"review_ok": False, "review_found": None, "review_false_alarms": None})

    def test_a_review_counts_each_planted_bug_it_found(self):
        result, _ = self.reviewing([self.finding("AuthService.cs", "Hardcoded password"), self.finding("OrderRepository.cs", "SQL injection from joined text"),
                                    self.finding("RefundController.cs", "Anyone can refund an order they don't own")])
        self.assertEqual(result["review_found"], 3)

    def test_a_planted_bug_counts_only_on_its_own_file(self):
        result, _ = self.reviewing([self.finding("Program.cs", "Anyone can refund an order they don't own")])
        self.assertEqual(result["review_found"], 0)

    def test_findings_on_clean_code_are_false_alarms(self):
        result, _ = self.reviewing([self.finding("Order.cs", "Weak naming"), self.finding("AuthService.cs", "Hardcoded password")],
                                   [self.finding("StoreDbContext.cs", "No authorization check"), self.finding("StoreDbContext.cs", "Possible SQL injection")])
        self.assertEqual(result["review_false_alarms"], 3)

    def test_a_good_review_of_clean_code_has_no_false_alarms(self):
        result, _ = self.reviewing([self.finding("AuthService.cs", "Hardcoded password")])
        self.assertEqual(result["review_false_alarms"], 0)

    def test_the_second_review_part_holds_only_the_clean_schema_file(self):
        _, calls = self.reviewing([])
        code = " ".join(m["content"] for m in calls[1]["messages"])
        self.assertIn("StoreDbContext", code)
        self.assertNotIn("AuthService", code)

    def test_the_longest_review_reply_the_schema_allows_fits_the_answer_limit(self):
        schema = csharp_string("ProjectReview.cs", "Schema")
        items = int(re.search(r'"maxItems":(\d+)', schema).group(1))
        longest = items * (sum(int(n) for n in re.findall(r'"maxLength":(\d+)', schema)) + 80)  # 80: the keys and quotes of one finding
        self.assertLessEqual(longest / 4, csharp_int("ProjectReview.cs", "AnswerTokens"))  # the replay ran at about 4 characters a token

    def test_a_review_is_allowed_the_answer_length_the_cli_asks_for(self):
        _, calls = self.reviewing([])
        self.assertEqual(calls[0]["max_tokens"], csharp_int("ProjectReview.cs", "AnswerTokens"))

    def test_a_conversation_times_six_turns_with_a_growing_history(self):
        ask, calls = replying("reply")
        times = conversation(ask, "SYSTEM")
        self.assertEqual(len(times), 6)
        self.assertEqual(len(calls), 6)
        self.assertGreater(len(calls[5]["messages"]), len(calls[0]["messages"]) + 8)



class LawBookRunTests(unittest.TestCase):
    def test_gateway_env_carries_the_law_book(self):
        from gateway import gateway_env
        self.assertEqual(gateway_env("dir", 8099, 5099, law_book="x.db")["Gateway__LawBookFile"], os.path.abspath("x.db"))

    def test_without_a_law_book_the_gateway_gets_no_setting(self):
        from gateway import gateway_env
        self.assertNotIn("Gateway__LawBookFile", gateway_env("dir", 8099, 5099))

    def test_each_answer_records_the_passages_it_was_given(self):
        answers = [{"question": "covered?"}, {"question": "trap?"}]
        found = {"covered?": [{"title": "T", "url": "https://learn.microsoft.com/a#b", "text": "x"}], "trap?": []}
        with_passages(answers, found.get)
        self.assertEqual(answers[0]["passages"], ["https://learn.microsoft.com/a#b"])
        self.assertEqual(answers[1]["passages"], [])

    def test_a_failed_lookup_is_noted_not_fatal(self):
        def broken(_): raise RuntimeError("no dotnet")
        answers = [{"question": "q"}]
        with_passages(answers, broken)
        self.assertIsNone(answers[0]["passages"])

    def test_the_plain_question_goes_to_the_gateway_alone(self):
        sent = []
        ask = plain_through_gateway(lambda messages: sent.append(messages) or "ok")
        self.assertEqual(ask({"question": "Q?"}), "ok")
        self.assertEqual(sent, [[{"role": "user", "content": "Q?"}]])  # the gateway adds the system prompt itself



def _run(label, passed, passages=None):
    return {"label": label, "answers": [{"id": i, "passed": ok, **({"passages": passages.get(i, [])} if passages is not None else {})}
                                        for i, ok in passed.items()]}


class PairedTests(unittest.TestCase):
    def test_exact_mcnemar_p_matches_the_binomial(self):
        self.assertAlmostEqual(mcnemar_p(3, 0), 0.25)
        self.assertAlmostEqual(mcnemar_p(6, 0), 0.03125)
        self.assertAlmostEqual(mcnemar_p(5, 1), 0.21875)
        self.assertEqual(mcnemar_p(0, 0), 1.0)
        self.assertEqual(mcnemar_p(2, 2), 1.0)

    def test_questions_are_paired_by_id(self):
        base = _run("base", {"a": True, "b": False, "c": True, "d": False})
        cand = _run("cand", {"a": True, "b": True, "c": False, "d": False, "e": True})
        p = paired(base, cand)
        self.assertEqual((p["gained"], p["lost"], p["both"], p["neither"]), (["b"], ["c"], 1, 1))
        self.assertEqual(p["missing"], ["e"])  # in one run only: not counted

    def test_the_report_names_what_was_lost_and_whether_a_passage_was_given(self):
        base = _run("tiny-regress", {"a": True, "b": True, "c": False})
        cand = _run("tiny-law", {"a": False, "b": False, "c": True}, passages={"a": ["https://learn.microsoft.com/x"]})
        text = pair_report(base, cand)
        self.assertIn("1 gained, 2 lost", text)
        self.assertIn("a (given a passage)", text)
        self.assertIn("b (no passage)", text)
        self.assertIn("p = 1.00", text)

    def test_a_small_gain_is_called_noise(self):
        base = _run("base", {str(i): i >= 3 for i in range(45)})
        cand = _run("cand", {str(i): True for i in range(45)})
        self.assertIn("could be chance", pair_report(base, cand))

    def test_runs_from_different_setups_say_so(self):
        base = {**_run("base", {"a": True}), "llama_build": "11982", "git_commit": "aaa"}
        cand = {**_run("cand", {"a": True}), "llama_build": "12069", "git_commit": "aaa"}
        self.assertIn("the runs also differ in: llama_build 11982 vs 12069", pair_report(base, cand))
        self.assertNotIn("differ in", pair_report(base, {**cand, "llama_build": "11982"}))
        self.assertNotIn("differ in", pair_report(_run("old", {"a": True}), cand))  # an unstamped run can't be compared


class LawQuestionTests(unittest.TestCase):
    def test_law_questions_are_the_api_questions(self):
        qs = law_questions()
        self.assertEqual(sum(q["category"] == "law-dev" for q in qs), 17)
        self.assertEqual(sum(q["category"] == "law-held-out" for q in qs), 34)
        self.assertEqual(len({q["id"] for q in qs}), len(qs))
        self.assertTrue(all(len(q["must_have"]) == 1 and len(q["must_have"][0]) == 1 and q["must_not"] == [] for q in qs))


if __name__ == "__main__":
    unittest.main()
