"""Scores an answer against a test question, and picks the best model from a comparison."""
import hashlib

PLAIN_LIMIT_S = 30
READ_FIRST_LIMIT_S = 120
FOLLOWUP_LIMIT_S = 30
CONVERSATION_TURNS = 6
TURN_LIMIT_S = 60  # hard turns may take longer than 30 s; a model that keeps slowing down over a conversation may not


def score(answer, question):
    """must_have is a list of groups, each passed by any one alternative. A must_not match fails the question."""
    text = (answer or "").lower()
    missing = [group[0] for group in question["must_have"] if not any(alt.lower() in text for alt in group)]
    forbidden = [bad for bad in question.get("must_not", []) if bad.lower() in text]
    return {"passed": bool(text.strip()) and not missing and not forbidden, "missing": missing, "forbidden": forbidden}


def _within_limits(result):
    timed = [(result.get("plain_p50_s"), PLAIN_LIMIT_S), (result.get("read_first_s"), READ_FIRST_LIMIT_S), (result.get("followup_s"), FOLLOWUP_LIMIT_S)]
    turns = result.get("turn_times_s") or []
    steady = len(turns) == CONVERSATION_TURNS and all(seconds <= TURN_LIMIT_S for seconds in turns)
    return all(seconds is not None and seconds <= limit for seconds, limit in timed) and steady and result.get("edit_ok") and result.get("review_ok")


def pick(results):
    """The highest score that meets every limit; the faster one wins a tie. None when nothing qualifies."""
    fit = [r for r in results if _within_limits(r)]
    return min(fit, key=lambda r: (-r["score"], r["plain_p50_s"])) if fit else None


def held_out(question_id):
    """About a third of the questions, kept for confirming a change rather than tuning one: a prompt tweaked until the dev
    questions pass has to win on these too. Chosen by a hash of the id, so a question added later never moves another."""
    return hashlib.sha256(question_id.encode()).digest()[0] % 3 == 0


def rescore(result, questions):
    """Scores saved answers again after a check was corrected, without running the model."""
    if "failed" in result:
        return result
    by_id = {q["id"]: q for q in questions}
    for answer in result["answers"]:
        if answer["id"] in by_id:
            answer.update(score(answer["answer"], by_id[answer["id"]]))
    result["score"] = sum(a["passed"] for a in result["answers"])
    held = [a for a in result["answers"] if held_out(a["id"])]
    result["held_out"] = f'{sum(a["passed"] for a in held)}/{len(held)}'
    return result
