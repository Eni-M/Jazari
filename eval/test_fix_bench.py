"""Checks the benchmark's seeds, with the compiler and no model. Run: py -I eval/test_fix_bench.py"""
import os, sys, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import fix_bench


class SeedTests(unittest.TestCase):
    def test_every_seed_breaks_with_its_code(self):
        with tempfile.TemporaryDirectory(prefix="fix-seed") as folder:
            project = fix_bench.fresh_copy(folder)
            self.assertEqual(fix_bench.build_errors(project), [], "the clean project must build")
            for seed in fix_bench.seeds():
                with self.subTest(seed["id"]):
                    fix_bench.apply_seed(project, seed)
                    self.assertEqual({e["code"] for e in fix_bench.build_errors(project)}, {seed["code"]})
                    fix_bench.restore_seed(project, seed)

    def test_a_seed_that_does_not_match_is_refused(self):
        with tempfile.TemporaryDirectory(prefix="fix-seed") as folder:
            project = fix_bench.fresh_copy(folder)
            with self.assertRaises(ValueError):
                fix_bench.apply_seed(project, {"file": "Order.cs", "search": "not in the file", "replace": ""})

    def test_results_decide_by_the_ship_rule(self):
        row = lambda fixed, worse=False, seconds=60: {"fixed": fixed, "worse": worse, "seconds": seconds}
        self.assertTrue(fix_bench.ships([row(True)] * 7 + [row(False)] * 5))
        self.assertFalse(fix_bench.ships([row(True)] * 6 + [row(False)] * 6))
        self.assertFalse(fix_bench.ships([row(True)] * 11 + [row(False, worse=True)]))
        self.assertFalse(fix_bench.ships([row(True, seconds=121)] * 12))


if __name__ == "__main__":
    unittest.main()
