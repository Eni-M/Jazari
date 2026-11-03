"""Runs the dev questions against a law book, the way the gateway searches it.

    py -I lawbook/try_dev.py [--book lawbook/out/law-book.db] [--check-overlap] [-v]

Pass: at least 16 of 20 answerable questions find a passage holding their `must_contain`, and at least 14 of 15 traps
get nothing. --check-overlap compares the dev questions with eval/questions.json by content-word overlap and prints only the
dev questions that share too much, never the test questions, so tuning never sees them.
"""
import argparse, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJECT = os.path.join(ROOT, "src", "MyOwnLLM.Gateway")
DLL = os.path.join(PROJECT, "bin", "Debug", "net10.0", "MyOwnLLM.Gateway.dll")  # what main's `dotnet build` makes
NEED_FOUND, NEED_REFUSED = 16, 14


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


def search(book, question):
    # The built DLL itself: `dotnet run` evaluates the whole project again for each of the 35 questions.
    out = subprocess.run(["dotnet", DLL, "search-law", question, f"--Gateway:LawBookFile={os.path.abspath(book)}"],
                         check=True, capture_output=True, text=True, encoding="utf-8").stdout
    return json.loads(out.strip().splitlines()[-1])


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--book", default=os.path.join(HERE, "out", "law-book.db"))
    p.add_argument("--check-overlap", action="store_true")
    p.add_argument("-v", "--verbose", action="store_true")
    a = p.parse_args(argv)
    with open(os.path.join(HERE, "dev_questions.json"), encoding="utf-8") as f:
        dev = json.load(f)
    questions = [("answerable", q["question"], q["must_contain"]) for q in dev["answerable"]] + \
                [("trap", q["question"], None) for q in dev["traps"]]

    if a.check_overlap:
        with open(os.path.join(ROOT, "eval", "questions.json"), encoding="utf-8") as f:
            test = [q["question"] for q in json.load(f)]
        close = too_close([q for _, q, _ in questions], test)
        print(f"{len(close)} dev questions share more than half their words with a test question")
        for q in close:
            print("  " + q)
        return 1 if close else 0

    subprocess.run(["dotnet", "build", PROJECT, "--disable-build-servers", "-v", "q", "-nologo"], check=True, capture_output=True)
    results = []
    for kind, question, must in questions:
        passages = search(a.book, question)
        results.append((kind, passages, must))
        ok = found(passages, must) if kind == "answerable" else not passages
        print(f"{'ok  ' if ok else 'MISS'} {kind:10} {question}")
        if a.verbose or not ok:
            for x in passages:
                print(f"       {x['title']}  {x['url']}")
    hit, n_ans, refused, n_traps = score(results)
    passed = hit >= NEED_FOUND and refused >= NEED_REFUSED
    print(f"\nanswerable found {hit}/{n_ans} (need {NEED_FOUND}), traps refused {refused}/{n_traps} (need {NEED_REFUSED}): {'PASS' if passed else 'FAIL'}")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
