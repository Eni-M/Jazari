"""Runs the dev questions against a law book, the way the gateway searches it.

    py -I lawbook/try_dev.py [--book lawbook/out/law-book.db] [--check-overlap] [-v]

Pass: of the page-checked questions, at least 25 of 47 find the right page and at most 3 a wrong one; at least 14 of 15 traps
get nothing. The 20 `must_contain` questions are printed but don't decide: a passage from the wrong page can hold the word. --check-overlap compares the dev questions with eval/questions.json by content-word overlap and prints only the
dev questions that share too much, never the test questions, so tuning never sees them.
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
    every = [q for _, q, _ in questions] + [q["question"] for q in dev["pages"]]

    if a.check_overlap:
        with open(os.path.join(ROOT, "eval", "questions.json"), encoding="utf-8") as f:
            test = [q["question"] for q in json.load(f)]
        close = too_close(every, test)
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
    pages = {"right": 0, "none": 0, "wrong": 0}
    for q in dev["pages"]:
        passages = search(a.book, q["question"])
        outcome = page_outcome(passages, q["page"])
        pages[outcome] += 1
        print(f"{outcome:5} page       {q['question']}")
        if a.verbose or outcome == "wrong":
            for x in passages:
                print(f"       {x['title']}  {x['url']}")
    print("\n" + f"pages: right {pages['right']}, nothing {pages['none']}, wrong {pages['wrong']} of {len(dev['pages'])}")
    hit, n_ans, refused, n_traps = score(results)
    passed = pages["right"] >= NEED_RIGHT and pages["wrong"] <= MAX_WRONG and refused >= NEED_REFUSED
    print(f"\nright page {pages['right']} (need {NEED_RIGHT}), wrong page {pages['wrong']} (at most {MAX_WRONG}), traps refused {refused}/{n_traps} (need {NEED_REFUSED}), must_contain found {hit}/{n_ans}: {'PASS' if passed else 'FAIL'}")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
