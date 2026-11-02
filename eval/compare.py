"""Prints every candidate's results and the model the selection rule picks."""
import glob, json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from score import pick, rescore


def load():
    results = []
    for path in sorted(glob.glob(os.path.join(HERE, "results", "*.json"))):
        with open(path, encoding="utf-8") as f:
            results.append(json.load(f))
    return results


def table(results):
    rows = [f'{"setup":<12}{"score":>7}{"median":>8}{"longest":>9}{"/read":>7}{"follow":>8}  {"edit":<6}{"review":<7}later turns']
    for r in sorted(results, key=lambda r: -r.get("score", -1)):
        if "failed" in r:
            rows.append(f'{r["label"]:<12}failed: {r["failed"]}')
            continue
        turns = r.get("turn_times_s") or []
        rows.append(f'{r["label"]:<12}{r["score"]:>4}/{r["total"]:<2}{r["plain_p50_s"]:>7.0f}s{r["plain_max_s"]:>8.0f}s'
                    f'{str(r["read_first_s"]):>7}{str(r["followup_s"]):>8}  {str(r["edit_ok"]):<6}{str(r["review_ok"]):<7}{turns}')
    return "\n".join(rows)


def by_category(results):
    rows = []
    for r in results:
        if "failed" in r:
            continue
        scores = {}
        for a in r["answers"]:
            passed, total = scores.get(a["category"], (0, 0))
            scores[a["category"]] = (passed + a["passed"], total + 1)
        rows.append(f'{r["label"]:<12}' + "  ".join(f"{c} {p}/{t}" for c, (p, t) in sorted(scores.items())))
    return "\n".join(rows)


if __name__ == "__main__":
    with open(os.path.join(HERE, "questions.json"), encoding="utf-8") as f:
        questions = json.load(f)
    all_results = [rescore(r, questions) for r in load()]
    print(table(all_results))
    print()
    print(by_category(all_results))
    winner = pick(all_results)
    print()
    print(f'Winner: {winner["label"]}' if winner else "No candidate meets every limit")
