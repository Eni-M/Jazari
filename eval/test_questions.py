import json, os, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, "questions.json"), encoding="utf-8") as f:
    QUESTIONS = json.load(f)


class QuestionTests(unittest.TestCase):
    def test_the_set_is_big_enough_and_balanced(self):
        self.assertGreaterEqual(len(QUESTIONS), 40)
        for category in ["concept", "api", "code", "security"]:
            self.assertGreaterEqual(sum(q["category"] == category for q in QUESTIONS), 8, category)

    def test_ids_and_questions_are_unique(self):
        self.assertEqual(len({q["id"] for q in QUESTIONS}), len(QUESTIONS))
        self.assertEqual(len({q["question"] for q in QUESTIONS}), len(QUESTIONS))

    def test_every_question_can_be_scored(self):
        for q in QUESTIONS:
            self.assertTrue(q["must_have"], q["id"])
            self.assertTrue(all(group and all(alt.strip() for alt in group) for group in q["must_have"]), q["id"])

    def test_the_three_known_failures_are_included(self):
        ids = {q["id"] for q in QUESTIONS}
        self.assertLessEqual({"efcore-raw-sql", "minimal-api-404", "record-kind"}, ids)


if __name__ == "__main__":
    unittest.main()
