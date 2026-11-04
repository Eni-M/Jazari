import os, sys, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_lanes import median_of_three, preset_text


class MeasureLanesTests(unittest.TestCase):
    def test_preset_names_both_lanes_and_their_files(self):
        text = preset_text({"model": "a/fast.gguf", "threads": 4}, {"model": "b/deep.gguf", "threads": 3})
        self.assertIn("[fast]", text)
        self.assertIn("[deep]", text)
        self.assertIn("model = a/fast.gguf", text)
        self.assertIn("model = b/deep.gguf", text)
        self.assertIn("threads = 3", text)

    def test_median_of_three(self):
        times = iter([9.0, 1.0, 5.0])
        self.assertEqual(median_of_three(lambda: next(times)), 5.0)


if __name__ == "__main__":
    unittest.main()
