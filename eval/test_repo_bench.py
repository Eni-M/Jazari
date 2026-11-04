import os, shutil, subprocess, sys, tempfile, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from repo_bench import candidates_to_tasks, closeness, first_code_block, passed, task_is_valid, test_command
from compare import pooled_report
from score import held_out

TOOL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "repo", "Blank", "bin", "Release", "net10.0", "Blank.dll")


class ScoringTests(unittest.TestCase):
    def test_passed_means_tests_when_there_are_tests(self):
        self.assertFalse(passed({"test_filter": "X"}, compiled=True, tests_passed=False))
        self.assertTrue(passed({"test_filter": "X"}, compiled=True, tests_passed=True))

    def test_passed_means_compiled_when_there_are_none(self):
        self.assertTrue(passed({"test_filter": None}, compiled=True, tests_passed=None))
        self.assertFalse(passed({"test_filter": None}, compiled=False, tests_passed=None))

    def test_a_task_whose_tests_pass_when_blanked_is_rejected(self):
        self.assertFalse(task_is_valid(tests_pass_blanked=True, tests_pass_original=True))

    def test_a_task_whose_tests_fail_on_the_original_is_rejected(self):
        self.assertFalse(task_is_valid(tests_pass_blanked=False, tests_pass_original=False))
        self.assertTrue(task_is_valid(tests_pass_blanked=False, tests_pass_original=True))

    def test_closeness_is_one_for_the_same_code_ignoring_whitespace(self):
        self.assertEqual(closeness("int  a;", "int a;"), 1.0)
        self.assertLess(closeness("int a;", "string longer = value;"), 0.5)

    def test_first_code_block_takes_the_first_fence_or_nothing(self):
        self.assertEqual(first_code_block("Here:\n```csharp\nint a;\n```\nmore\n```\nb\n```"), "int a;")
        self.assertIsNone(first_code_block("no code at all"))


class SelectionTests(unittest.TestCase):
    def test_candidates_need_a_test_naming_them_and_a_sensible_size(self):
        found = [{"file": "S.cs", "method": "Refund", "class": "S", "lines": 12}, {"file": "S.cs", "method": "Ship", "class": "S", "lines": 12},
                 {"file": "S.cs", "method": "Huge", "class": "S", "lines": 400}]
        tests = {"STests": "void T() { s.Refund(); s.Huge(); }"}
        tasks = candidates_to_tasks("repo", "C:/r", "abc", found, tests, limit=10)
        self.assertEqual([t["method"] for t in tasks], ["Refund"])
        self.assertEqual(tasks[0]["test_filter"], "FullyQualifiedName~STests")
        self.assertEqual(tasks[0]["id"], "repo-S-Refund")

    def test_without_tests_every_sized_candidate_is_build_only(self):
        found = [{"file": "S.cs", "method": "Refund", "class": "S", "lines": 12}]
        self.assertIsNone(candidates_to_tasks("repo", "C:/r", "abc", found, None, limit=10)[0]["test_filter"])

    def test_an_xunit_v3_project_runs_its_own_test_app_and_others_use_dotnet_test(self):
        v3 = test_command("T.csproj", '<PackageReference Include="xunit.v3" Version="4.0.0" />', "FullyQualifiedName~STests")
        self.assertEqual(v3[:3] + v3[-3:], ["dotnet", "run", "--project", "--", "-filterVSTest", "FullyQualifiedName~STests"])
        self.assertEqual(test_command("T.csproj", '<PackageReference Include="xunit" Version="2.9.0" />', "F")[:2], ["dotnet", "test"])


class PoolingTests(unittest.TestCase):
    @staticmethod
    def run_of(label, right, ids):
        return {"label": label, "answers": [{"id": i, "passed": i in right} for i in ids]}

    def test_pooled_pair_counts_both_sets_and_keeps_held_out_apart(self):
        ids = [f"q{i}" for i in range(60)]
        dev = [i for i in ids if not held_out(i)]
        held = [i for i in ids if held_out(i)]
        base = self.run_of("b", set(), dev[:3] + held[:2])
        cand = self.run_of("c", set(dev[:3]), dev[:3] + held[:2])
        repo_base = self.run_of("b", set(), dev[3:6])
        repo_cand = self.run_of("c", set(dev[3:6]), dev[3:6])
        report = pooled_report(base, cand, repo_base, repo_cand)
        self.assertIn("dev: 6 gained, 0 lost", report)
        self.assertIn("p = 0.03", report)
        self.assertIn("held-out: 0/2 against 0/2", report)


@unittest.skipUnless(shutil.which("dotnet") and os.path.isfile(TOOL), "build eval/repo/Blank first")
class BlankToolTests(unittest.TestCase):
    def test_blank_then_put_round_trip_and_a_missing_method_exits_2(self):
        with tempfile.TemporaryDirectory() as folder:
            file = os.path.join(folder, "A.cs")
            with open(file, "w") as f:
                f.write("class A { int F(int x) { var y = x; return y; } int G() => 1; }")
            out = subprocess.run(["dotnet", TOOL, "blank", file, "F"], capture_output=True, text=True)
            self.assertEqual(out.returncode, 0)
            self.assertIn("return y", out.stdout)
            with open(file) as f:
                self.assertIn("NotImplementedException", f.read())
            answer = os.path.join(folder, "answer.txt")
            with open(answer, "w") as f:
                f.write("int F(int x) { return x + 1; }")
            self.assertEqual(subprocess.run(["dotnet", TOOL, "put", file, "F", answer]).returncode, 0)
            with open(file) as f:
                self.assertIn("x + 1", f.read())
            self.assertEqual(subprocess.run(["dotnet", TOOL, "blank", file, "Nope"], capture_output=True).returncode, 2)


if __name__ == "__main__":
    unittest.main()
