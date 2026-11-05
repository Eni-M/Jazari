"""Likely misses from Jazari's own log, for Eni-M to turn the real ones into dev-set items by hand (never held-out ones).

    py -I eval/misses.py [--db %LOCALAPPDATA%/MyOwnLLM/myownllm.db] [--days 30]

A likely miss is an answer rated down (jazari feedback, an /edit turned down or undone) or a question asked again soon after,
in other words. It prints the questions, which can hold private code: read it here, and don't paste it anywhere.
"""
import argparse, datetime, os, re, sqlite3, sys

STOPWORDS = set("""a about an and are at be can do does for from how i in is it many me much my of on or our that the this to
was we what when where which who why will with you your""".split())
REASK_MINUTES, REASK_OVERLAP = 10, 0.5


def words(text):
    return set(re.findall(r"[a-z0-9]+", text.lower())) - STOPWORDS


def reasked(prompt, later, kind="chat", later_kind="chat"):
    """Whether a later question asks mostly the same thing: half of the first one's words. The same text again counts only
    through /deep after a fast answer; otherwise it's a retry after an error, or "go on"."""
    if prompt.strip() == later.strip():
        return kind == "chat" and later_kind == "deep"
    first, second = words(prompt), words(later)
    return bool(first) and len(first & second) / len(first) >= REASK_OVERLAP


def misses(rows):
    """rows: (id, created_utc, project, kind, prompt, model, accepted), oldest first. Yields (row, why). Any kind can be rated
    down; only questions (chat, deep) can be asked again: /edit, /review and /commit (json) share long fixed instructions."""
    questions = [r for r in rows if r[3] in ("chat", "deep")]
    for row in rows:
        if row[6] == 0:
            yield row, "rated down"
            continue
        if row[6] == 1 or row[3] not in ("chat", "deep"):
            continue
        when = datetime.datetime.fromisoformat(row[1])
        for later in questions[questions.index(row) + 1:]:
            if datetime.datetime.fromisoformat(later[1]) - when > datetime.timedelta(minutes=REASK_MINUTES):
                break
            if later[2] == row[2] and reasked(row[4], later[4], row[3], later[3]):
                yield row, f"asked again as #{later[0]}"
                break


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--db", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "MyOwnLLM", "myownllm.db"))
    p.add_argument("--days", type=int, default=30)
    a = p.parse_args(argv)
    if not os.path.exists(a.db):
        sys.exit(f"no log at {a.db}")
    since = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=a.days)).isoformat()
    db = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    has_model = any(c[1] == "model" for c in db.execute("PRAGMA table_info(interactions)"))
    rows = db.execute(f"SELECT id, created_utc, project, kind, prompt, {'model' if has_model else 'NULL'}, accepted FROM interactions "
                      "WHERE created_utc >= ? ORDER BY id", (since,)).fetchall()
    found = list(misses(rows))
    for row, why in found:
        question = " ".join(row[4].split())
        print(f"#{row[0]}  {row[1][:16]}  {row[3]}  {row[5] or '?'}  {why}\n    {question[:160]}")
    print(f"{len(found)} likely misses in {len(rows)} answers over {a.days} days")


if __name__ == "__main__":
    main()
