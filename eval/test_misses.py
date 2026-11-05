import os, sys, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from misses import misses, reasked


def row(id, minute, prompt, accepted=None, project="C:/p", kind="chat"):
    return (id, f"2026-11-05T10:{minute:02d}:00+00:00", project, kind, prompt, "fast", accepted)


class MissesTests(unittest.TestCase):
    def test_rated_down_is_a_miss_and_rated_up_never_is(self):
        found = misses([row(1, 0, "How do I add CORS?", accepted=0), row(2, 1, "How do I add CORS policies?", accepted=1),
                        row(3, 2, "How do I add CORS policies please?")])
        self.assertEqual([(r[0], why) for r, why in found], [(1, "rated down")])

    def test_the_same_question_soon_after_in_other_words_is_a_miss(self):
        found = misses([row(1, 0, "How do I register a scoped service?"), row(2, 3, "register scoped service in Program.cs how?")])
        self.assertEqual([(r[0], why) for r, why in found], [(1, "asked again as #2")])

    def test_much_later_or_in_another_project_or_unrelated_is_not(self):
        self.assertEqual(list(misses([row(1, 0, "register a scoped service"), row(2, 30, "register scoped service")])), [])
        self.assertEqual(list(misses([row(1, 0, "register a scoped service"), row(2, 1, "register scoped service", project="C:/q")])), [])
        self.assertFalse(reasked("register a scoped service", "what is a record struct"))
        self.assertFalse(reasked("go on", "go on"))  # the same text again is a retry, not a rephrasing

    def test_the_same_question_again_through_deep_is_a_miss(self):
        found = misses([row(1, 0, "How do I add CORS?"), row(2, 1, "How do I add CORS?", kind="deep")])
        self.assertEqual([(r[0], why) for r, why in found], [(1, "asked again as #2")])

    def test_edits_and_reviews_are_never_asked_again_but_can_be_rated_down(self):
        instructions = "Change the file as asked and answer with search and replace edits only. "
        found = misses([row(1, 0, instructions + "rename a", kind="json"), row(2, 1, instructions + "rename b", kind="json"),
                        row(3, 2, instructions + "rename c", accepted=0, kind="json")])
        self.assertEqual([(r[0], why) for r, why in found], [(3, "rated down")])


if __name__ == "__main__":
    unittest.main()
