import os, socket, sys, unittest
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from run_eval import PortInUse, run_questions, start_server, system_prompt

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


if __name__ == "__main__":
    unittest.main()
