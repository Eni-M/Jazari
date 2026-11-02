import os, sys, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from score import rescore, score, pick

QUESTION = {"must_have": [["AddScoped", "Scoped"]], "must_not": ["AddSingleton"]}
GOOD = {"label": "a", "score": 30, "plain_p50_s": 20, "read_first_s": 100, "followup_s": 25, "edit_ok": True, "review_ok": True, "turn_times_s": [10, 10, 10, 10, 10, 10]}


class ScoreTests(unittest.TestCase):
    def test_passes_when_each_group_has_a_match(self):
        self.assertTrue(score("Use addscoped here", QUESTION)["passed"])

    def test_missing_group_fails_and_is_named(self):
        r = score("Use a factory", QUESTION)
        self.assertFalse(r["passed"])
        self.assertEqual(r["missing"], ["AddScoped"])

    def test_forbidden_text_fails_even_when_the_right_term_is_there(self):
        r = score("AddScoped or AddSingleton", QUESTION)
        self.assertFalse(r["passed"])
        self.assertEqual(r["forbidden"], ["AddSingleton"])

    def test_empty_answer_fails(self):
        self.assertFalse(score("", QUESTION)["passed"])


class RescoreTests(unittest.TestCase):
    def test_a_saved_answer_is_scored_against_the_current_checks(self):
        result = {"score": 0, "answers": [{"id": "q", "answer": "use scoped", "passed": False, "missing": ["x"], "forbidden": []},
                                          {"id": "gone", "answer": "kept", "passed": True, "missing": [], "forbidden": []}]}
        rescored = rescore(result, [{"id": "q", **QUESTION}])
        self.assertEqual(rescored["answers"][0]["passed"], True)
        self.assertEqual(rescored["answers"][0]["missing"], [])
        self.assertEqual(rescored["score"], 2)  # a question no longer in the set keeps its result


class PickTests(unittest.TestCase):
    def test_pick_takes_the_best_score_within_limits(self):
        better = {**GOOD, "label": "b", "score": 35}
        self.assertEqual(pick([GOOD, better])["label"], "b")

    def test_a_tie_goes_to_the_faster_one(self):
        faster = {**GOOD, "label": "b", "plain_p50_s": 10}
        self.assertEqual(pick([GOOD, faster])["label"], "b")

    def test_pick_rejects_slow_plain_answers(self):
        self.assertIsNone(pick([{**GOOD, "plain_p50_s": 31}]))

    def test_pick_rejects_rereading_on_followups(self):
        self.assertEqual(pick([{**GOOD, "score": 99, "followup_s": 90}, GOOD])["label"], "a")

    def test_pick_rejects_a_slow_first_answer_after_read(self):
        self.assertIsNone(pick([{**GOOD, "read_first_s": 121}]))

    def test_pick_rejects_a_model_where_edit_or_review_breaks(self):
        self.assertIsNone(pick([{**GOOD, "edit_ok": False}]))
        self.assertIsNone(pick([{**GOOD, "review_ok": False}]))

    def test_pick_rejects_a_model_that_slows_down_over_a_conversation(self):
        self.assertIsNone(pick([{**GOOD, "turn_times_s": [7.3, 40.5, 45.1, 83.0, 128.6, 143.7]}]))

    def test_pick_rejects_an_unfinished_conversation(self):
        self.assertIsNone(pick([{**GOOD, "turn_times_s": [10, 10]}]))

    def test_pick_skips_a_setup_that_failed_to_load(self):
        self.assertEqual(pick([{"label": "broken", "failed": "would not load"}, GOOD])["label"], "a")

    def test_rescore_leaves_a_failed_setup_alone(self):
        self.assertEqual(rescore({"label": "broken", "failed": "would not load"}, [])["failed"], "would not load")

    def test_pick_treats_unmeasured_scenarios_as_failing(self):
        self.assertIsNone(pick([{**GOOD, "followup_s": None}]))


if __name__ == "__main__":
    unittest.main()
