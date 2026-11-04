"""Runs the snippet checker over answers already saved, with no model running, and prints every flagged block for reading by
hand, plus how long the checks took. A flagged block in a correct answer is a false alarm: at most 10% of flags may be.

    py -I eval/check_saved.py eval/results/tiny-regress.json eval/results/qwen3-cpu.json
"""
import argparse, json, os, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import GATEWAY_DLL, build_gateway


def flagged(results, answers):
    """(question id, block number, errors) for each block that didn't compile."""
    return [(a["id"], c["block"], c["errors"]) for a, r in zip(answers, results) for c in r["checks"] if c["state"] == "DoesNotCompile"]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("runs", nargs="+")
    a = parser.parse_args()
    build_gateway()
    for path in a.runs:
        with open(path, encoding="utf-8") as f:
            answers = json.load(f)["answers"]
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f:
            json.dump([x["answer"] for x in answers], f)
        started = time.time()
        with tempfile.TemporaryDirectory() as folder:
            out = subprocess.run(["dotnet", GATEWAY_DLL, "check-answer", f.name], capture_output=True, text=True, encoding="utf-8", cwd=folder,
                                 env={**os.environ, "Gateway__DataDir": folder})
        os.unlink(f.name)
        if out.returncode != 0:
            sys.exit(out.stderr.strip() or f"check-answer exit {out.returncode}")
        results = json.loads(out.stdout.strip().splitlines()[-1])
        times = [r["ms"] for r in results if r["checks"]]
        print(f"{os.path.basename(path)}: {sum(len(r['checks']) for r in results)} blocks, first {times[0] if times else 0} ms, "
              f"then median {statistics.median(times[1:]) if len(times) > 1 else 0} ms, whole run {time.time() - started:.1f} s")
        for question, block, errors in flagged(results, answers):
            print(f"  {question} block {block}: {errors}")


if __name__ == "__main__":
    main()
