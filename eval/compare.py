"""Prints every candidate's results and the model the selection rule picks.

    py -I eval/compare.py                              # every saved result
    py -I eval/compare.py --pair tiny-regress tiny-law # also: question by question, what the second run won and lost

"held" is the held-out third (score.held_out): tune a prompt on the rest, and trust a gain only if it shows there too.
"""
import argparse, glob, json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from score import held_out, pick, rescore


def load():
    results = []
    for path in sorted(glob.glob(os.path.join(HERE, "results", "*.json"))):
        with open(path, encoding="utf-8") as f:
            results.append(json.load(f))
    return results


def table(results):
    rows = [f'{"setup":<12}{"score":>7}{"held":>7}{"median":>8}{"longest":>9}{"/read":>7}{"follow":>8}  {"edit":<6}{"review":<7}{"found":>6}{"false":>6}  later turns']
    for r in sorted(results, key=lambda r: -r.get("score", -1)):
        if "failed" in r:
            rows.append(f'{r["label"]:<12}failed: {r["failed"]}')
            continue
        turns = r.get("turn_times_s") or []
        rows.append(f'{r["label"]:<12}{r["score"]:>4}/{r["total"]:<2}{r.get("held_out", "-"):>7}{r["plain_p50_s"]:>7.0f}s{r["plain_max_s"]:>8.0f}s'
                    f'{str(r["read_first_s"]):>7}{str(r["followup_s"]):>8}  {str(r["edit_ok"]):<6}{str(r["review_ok"]):<7}'
                    # planted bugs found (of 3) and findings on clean code; older results have neither: None
                    f'{str(r.get("review_found")):>6}{str(r.get("review_false_alarms")):>6}  {turns}')
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


def mcnemar_p(wins, losses):
    """Exact two-sided McNemar p: how likely a split at least this uneven is when neither run is better. Only the
    questions the two runs disagree on count."""
    n = wins + losses
    if n == 0:
        return 1.0
    tail = sum(math.comb(n, i) for i in range(min(wins, losses) + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def paired(base, candidate):
    """The two runs question by question, matched by id. A question in only one run isn't counted."""
    b = {a["id"]: a for a in base["answers"]}
    c = {a["id"]: a for a in candidate["answers"]}
    common = [i for i in b if i in c]
    return {
        "gained": [i for i in common if c[i]["passed"] and not b[i]["passed"]],
        "lost": [i for i in common if b[i]["passed"] and not c[i]["passed"]],
        "both": sum(b[i]["passed"] and c[i]["passed"] for i in common),
        "neither": sum(not b[i]["passed"] and not c[i]["passed"] for i in common),
        "missing": sorted(set(b) ^ set(c)),
        "passages": {i: c[i].get("passages") for i in common},
    }


def pair_report(base, candidate):
    """What the candidate won and lost against the base. On 45 questions a few wins can't be told from chance: +3 with no
    losses gives p = 0.25, and it takes 6 wins and 0 losses to get under 0.05 (reports/Jazari enhancements after law book.md)."""
    p = paired(base, candidate)
    wins, losses = len(p["gained"]), len(p["lost"])
    value = mcnemar_p(wins, losses)

    def note(i):
        given = p["passages"].get(i)
        return i if given is None else f'{i} ({"given a passage" if given else "no passage"})'

    lines = [f'{candidate["label"]} against {base["label"]}: {wins} gained, {losses} lost, {p["both"]} right in both, {p["neither"]} wrong in both',
             f"exact McNemar p = {value:.2f}: " + ("unlikely to be chance" if value < 0.05 else "could be chance; a gain this size can't be told from noise")]
    if p["gained"]:
        lines.append("gained: " + ", ".join(note(i) for i in p["gained"]))
    if p["lost"]:
        lines.append("lost (right before, wrong now): " + ", ".join(note(i) for i in p["lost"]))
    if p["missing"]:
        lines.append("in one run only, not counted: " + ", ".join(p["missing"]))
    # Stamped runs (gateway.stamp): a difference the comparison didn't mean to test is named, so it isn't mistaken for the change's effect.
    differ = [f"{key} {base[key]} vs {candidate[key]}" for key in ("git_commit", "git_dirty", "llama_build", "model_fingerprint", "temperature")
              if key in base and key in candidate and base[key] != candidate[key]]
    if differ:
        lines.append("the runs also differ in: " + "; ".join(differ))
    return "\n".join(lines)


def pooled_report(base, candidate, repo_base, repo_candidate):
    """The ship rule for /deep: the questions and repo tasks together on dev, then held-out on its own. At 39/45 a run can gain
    at most 6 questions, so the questions alone could pass only at exactly 6 to 0."""
    pool = lambda run, repo: {"label": run["label"], "answers": run["answers"] + repo["answers"]}
    def part(run, keep):
        return {"answers": [a for a in run["answers"] if keep(a["id"])]}
    both_base, both_candidate = pool(base, repo_base), pool(candidate, repo_candidate)
    dev = paired(part(both_base, lambda i: not held_out(i)), part(both_candidate, lambda i: not held_out(i)))
    wins, losses = len(dev["gained"]), len(dev["lost"])
    right = lambda run: f'{sum(a["passed"] for a in run["answers"])}/{len(run["answers"])}'
    held_base, held_candidate = part(both_base, held_out), part(both_candidate, held_out)
    return "\n".join([f'{candidate["label"]} against {base["label"]}, questions and repo tasks together',
                      f"dev: {wins} gained, {losses} lost, exact McNemar p = {mcnemar_p(wins, losses):.2f}",
                      f"held-out: {right(held_candidate)} against {right(held_base)}"])


def load_repo(label):
    path = os.path.join(HERE, "results", "repo", f"{label}.json")
    if not os.path.exists(path):
        sys.exit(f"no repo benchmark run named {label} in eval/results/repo")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--pair", nargs=2, metavar=("BASE", "CANDIDATE"), help="labels of two saved runs to compare question by question")
    parser.add_argument("--with-repo", action="store_true", help="with --pair: also pool the repo benchmark's runs of the same labels")
    args = parser.parse_args()
    with open(os.path.join(HERE, "questions.json"), encoding="utf-8") as f:
        questions = json.load(f)
    all_results = [rescore(r, questions) for r in load()]
    if args.pair:
        found = {r["label"]: r for r in all_results if "failed" not in r}
        missing = [label for label in args.pair if label not in found]
        if missing:
            sys.exit(f"no saved run named {', '.join(missing)} in eval/results")
        print(pair_report(found[args.pair[0]], found[args.pair[1]]))
        if args.with_repo:
            print(pooled_report(found[args.pair[0]], found[args.pair[1]], load_repo(args.pair[0]), load_repo(args.pair[1])))
        print()
    print(table(all_results))
    print()
    print(by_category(all_results))
    winner = pick(all_results)
    print()
    print(f'Winner: {winner["label"]}' if winner else "No candidate meets every limit")
