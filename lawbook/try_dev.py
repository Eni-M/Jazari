"""Runs the dev questions against a law book, the way the gateway searches it.

    py -I lawbook/try_dev.py [--book lawbook/out/law-book.db] [--models spike/models|none] [--held-out] [--check-overlap] [-v]

Pass: of the page-checked questions, at least 25 of 47 find the right page and at most 3 a wrong one; at least 14 of 15 traps
get nothing. The 20 `must_contain` questions are printed but don't decide: a passage from the wrong page can hold the word.
The 17 page questions with an `api` also count how often a passage from the right page holds that word: the measure that
moved answers last time (7 of 17 right with today's passages, 15 with the right one). --held-out runs
heldout_questions.json instead, by kind; it's for confirming a change, never for tuning one. --check-overlap compares the dev
and held-out questions with eval/questions.json by content-word overlap and prints only ours that share too much, never the
test questions, so tuning never sees them.
"""
import argparse, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJECT = os.path.join(ROOT, "src", "MyOwnLLM.Gateway")
DLL = os.path.join(PROJECT, "bin", "Debug", "net10.0", "MyOwnLLM.Gateway.dll")  # what main's `dotnet build` makes
NEED_RIGHT, MAX_WRONG, NEED_REFUSED = 25, 3, 14


# ConversationSearch's stopwords: "how do I ... in" is in every question and says nothing about its topic.
STOPWORDS = set("""a about an and are at be can do does for from how i in is it many me much my of on or our that the this to
was we what when where which who why will with you your""".split())


def words(text):
    return set(re.findall(r"[a-z0-9]+", text.lower())) - STOPWORDS


def overlap(dev, test):
    """Share of the dev question's words that the test question also has."""
    d = words(dev)
    return len(d & words(test)) / max(1, len(d))


def too_close(dev_questions, test_questions, limit=0.5):
    return [q for q in dev_questions if any(overlap(q, t) > limit for t in test_questions)]


def found(passages, must_contain):
    return any(must_contain.lower() in (p["title"] + "\n" + p["text"]).lower() for p in passages)


def score(results):
    """results: (kind, passages, must_contain) per question. Returns (found, answerable, refused, traps)."""
    answerable = [r for r in results if r[0] == "answerable"]
    traps = [r for r in results if r[0] == "trap"]
    return (sum(found(p, m) for _, p, m in answerable), len(answerable),
            sum(not p for _, p, _ in traps), len(traps))


def page_outcome(passages, page):
    """'right' if any passage comes from the page, 'none' if nothing came back, else 'wrong'."""
    if not passages:
        return "none"
    return "right" if any(page in p["url"] for p in passages) else "wrong"


def run_pages(book, questions, verbose):
    """Right, nothing, wrong and API-in-the-passage per kind (one kind, "all", when the questions have none)."""
    tally = {}
    for q in questions:
        passages = search(book, q["question"])
        outcome = page_outcome(passages, q["page"])
        t = tally.setdefault(q.get("kind", "all"), {"right": 0, "none": 0, "wrong": 0, "api": 0, "apis": 0, "n": 0})
        t[outcome] += 1; t["n"] += 1
        api_ok = None
        if q.get("api"):
            t["apis"] += 1
            api_ok = outcome == "right" and found(passages, q["api"])
            t["api"] += api_ok
        print(f"{outcome:5} {'' if api_ok is None else ('api ' if api_ok else 'NOAPI'):5} {q['question']}")
        if verbose or outcome == "wrong" or api_ok is False:
            for x in passages:
                print(f"       {x['title']}  {x['url']}")
    return tally


def print_tally(tally):
    for kind, t in tally.items():
        print(f"{kind:8} right {t['right']}, nothing {t['none']}, wrong {t['wrong']} of {t['n']}; API in the passage {t['api']}/{t['apis']}")
    total = {k: sum(t[k] for t in tally.values()) for k in ("right", "none", "wrong", "api", "apis", "n")}
    if len(tally) > 1:
        print(f"{'total':8} right {total['right']}, nothing {total['none']}, wrong {total['wrong']} of {total['n']}; API in the passage {total['api']}/{total['apis']}")
    return total


def held_out(book, verbose):
    with open(os.path.join(HERE, "heldout_questions.json"), encoding="utf-8") as f:
        held = json.load(f)
    total = print_tally(run_pages(book, held["pages"], verbose))
    refused = 0
    for q in held["traps"]:
        passages = search(book, q["question"])
        refused += not passages
        print(f"{'ok  ' if not passages else 'MISS'} trap {q['question']}")
        for x in passages:
            print(f"       {x['title']}  {x['url']}")
    print(f"\nheld out: right page {total['right']}, wrong page {total['wrong']} of {total['n']}, API in the passage {total['api']}/{total['apis']}, traps refused {refused}/{len(held['traps'])}")
    return 0


MODELS = os.path.join(ROOT, "spike", "models")  # the search's two models, as the gateway finds them in Gateway:SearchModelsDir
models = MODELS


def search(book, question):
    # The built DLL itself: `dotnet run` evaluates the whole project again for each of the 35 questions.
    # A models folder that doesn't exist means the search by words alone.
    out = subprocess.run(["dotnet", DLL, "search-law", question, f"--Gateway:LawBookFile={os.path.abspath(book)}",
                          f"--Gateway:SearchModelsDir={os.path.abspath(models) if models != 'none' else os.path.join(HERE, 'no-models')}"],
                         check=True, capture_output=True, text=True, encoding="utf-8").stdout
    return json.loads(out.strip().splitlines()[-1])


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--book", default=os.path.join(HERE, "out", "law-book.db"))
    p.add_argument("--models", default=MODELS, help="folder with the search's two models (default spike/models); 'none' for words alone")
    p.add_argument("--held-out", action="store_true", help="run heldout_questions.json, to confirm a change; never tune on it")
    p.add_argument("--check-overlap", action="store_true")
    p.add_argument("-v", "--verbose", action="store_true")
    a = p.parse_args(argv)
    global models
    models = a.models
    with open(os.path.join(HERE, "dev_questions.json"), encoding="utf-8") as f:
        dev = json.load(f)
    with open(os.path.join(HERE, "heldout_questions.json"), encoding="utf-8") as f:
        held = json.load(f)
    questions = [("answerable", q["question"], q["must_contain"]) for q in dev["answerable"]] + \
                [("trap", q["question"], None) for q in dev["traps"]]
    every = [q for _, q, _ in questions] + [q["question"] for q in dev["pages"]] + \
            [q["question"] for q in held["pages"] + held["traps"]]

    if a.check_overlap:
        with open(os.path.join(ROOT, "eval", "questions.json"), encoding="utf-8") as f:
            test = [q["question"] for q in json.load(f)]
        close = too_close(every, test)
        print(f"{len(close)} dev questions share more than half their words with a test question")
        for q in close:
            print("  " + q)
        return 1 if close else 0

    subprocess.run(["dotnet", "build", PROJECT, "--disable-build-servers", "-v", "q", "-nologo"], check=True, capture_output=True)
    if a.held_out:
        return held_out(a.book, a.verbose)
    results = []
    for kind, question, must in questions:
        passages = search(a.book, question)
        results.append((kind, passages, must))
        ok = found(passages, must) if kind == "answerable" else not passages
        print(f"{'ok  ' if ok else 'MISS'} {kind:10} {question}")
        if a.verbose or not ok:
            for x in passages:
                print(f"       {x['title']}  {x['url']}")
    print()
    pages = print_tally(run_pages(a.book, dev["pages"], a.verbose))
    hit, n_ans, refused, n_traps = score(results)
    passed = pages["right"] >= NEED_RIGHT and pages["wrong"] <= MAX_WRONG and refused >= NEED_REFUSED
    print(f"\nright page {pages['right']} (need {NEED_RIGHT}), wrong page {pages['wrong']} (at most {MAX_WRONG}), traps refused {refused}/{n_traps} (need {NEED_REFUSED}), "
          f"must_contain found {hit}/{n_ans}, API in the passage {pages['api']}/{pages['apis']}: {'PASS' if passed else 'FAIL'}")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
