import os, socket, sys, unittest
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import argparse, json, tempfile
from compare import by_category, table
from run_eval import PortInUse, conversation, csharp_string, edit_scenario, read_context, read_scenario, review_scenario, run_questions, save_failure, server_args, start_server, system_prompt

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

    def test_a_review_counts_when_it_reports_the_planted_password(self):
        reply = {"findings": [{"file": "AuthService.cs", "quote": "x", "problem": "Hardcoded password in the code", "severity": "high", "fix": "y"}]}
        ask, _ = replying(json.dumps(reply))
        self.assertTrue(review_scenario(ask, "SYSTEM"))

    def test_a_review_fails_when_it_misses_the_password(self):
        reply = {"findings": [{"file": "Order.cs", "quote": "x", "problem": "Weak naming", "severity": "low", "fix": "y"}]}
        ask, _ = replying(json.dumps(reply))
        self.assertFalse(review_scenario(ask, "SYSTEM"))

    def test_a_review_fails_on_a_reply_that_is_not_json(self):
        ask, _ = replying("Looks fine")
        self.assertFalse(review_scenario(ask, "SYSTEM"))

    def test_a_conversation_times_six_turns_with_a_growing_history(self):
        ask, calls = replying("reply")
        times = conversation(ask, "SYSTEM")
        self.assertEqual(len(times), 6)
        self.assertEqual(len(calls), 6)
        self.assertGreater(len(calls[5]["messages"]), len(calls[0]["messages"]) + 8)


if __name__ == "__main__":
    unittest.main()
