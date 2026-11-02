"""Scores an answer against a test question, and picks the best model from a comparison."""

PLAIN_LIMIT_S = 30
READ_FIRST_LIMIT_S = 120
FOLLOWUP_LIMIT_S = 30


def score(answer, question):
    """must_have is a list of groups, each passed by any one alternative. A must_not match fails the question."""
    text = (answer or "").lower()
    missing = [group[0] for group in question["must_have"] if not any(alt.lower() in text for alt in group)]
    forbidden = [bad for bad in question.get("must_not", []) if bad.lower() in text]
    return {"passed": bool(text.strip()) and not missing and not forbidden, "missing": missing, "forbidden": forbidden}


def _within_limits(result):
    timed = [(result.get("plain_p50_s"), PLAIN_LIMIT_S), (result.get("read_first_s"), READ_FIRST_LIMIT_S), (result.get("followup_s"), FOLLOWUP_LIMIT_S)]
    return all(seconds is not None and seconds <= limit for seconds, limit in timed) and result.get("edit_ok") and result.get("review_ok")


def pick(results):
    """The highest score that meets every limit; the faster one wins a tie. None when nothing qualifies."""
    fit = [r for r in results if _within_limits(r)]
    return min(fit, key=lambda r: (-r["score"], r["plain_p50_s"])) if fit else None
