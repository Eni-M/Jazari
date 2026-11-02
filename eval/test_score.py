import os, sys, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from score import score, pick

QUESTION = {"must_have": [["AddScoped", "Scoped"]], "must_not": ["AddSingleton"]}
GOOD = {"label": "a", "score": 30, "plain_p50_s": 20, "read_first_s": 100, "followup_s": 25, "edit_ok": True, "review_ok": True}


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

    def test_pick_treats_unmeasured_scenarios_as_failing(self):
        self.assertIsNone(pick([{**GOOD, "followup_s": None}]))


if __name__ == "__main__":
    unittest.main()
