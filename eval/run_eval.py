"""Runs the test set against a model through a temporary llama-server on port 8099, and saves the scored answers."""
import argparse, json, os, re, shutil, statistics, sys, tempfile, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from score import score
from gateway import (stamp, GATEWAY_DLL, GATEWAY_PORT, PortInUse, ServerDidNotStart, add_key, build_gateway, gateway_chat, gateway_env, launch,
                     search_law, start_server, stop, wait_for_gateway)

PORT = 8099
REPO = os.path.join(HERE, "..")
PROMPT_BUILDER = os.path.join(REPO, "src", "MyOwnLLM.Gateway", "Prompt", "PromptBuilder.cs")
MAX_TOKENS = 512  # the gateway's DefaultChatTokens
TEMPERATURE = 0.2  # the gateway's CodeTemperature


def _constant(source_path, name):
    """A C# string constant: a raw \"\"\" string, or string literals joined with +, without comments between them."""
    with open(source_path, encoding="utf-8") as f:
        source = f.read()
    raw = re.search(rf'const string {name}\s*=\s*"""(.*?)"""', source, re.S)
    if raw:
        return raw.group(1).strip()
    start = re.search(rf"const string {name}\s*=", source).end()
    parts = []
    for line in source[start:].splitlines():
        text = line.strip()
        if not text or text.startswith("//"):
            continue
        parts.append(text)
        if text.endswith(";"):
            break
    return "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', " ".join(parts))).replace("\\n", "\n").replace('\\"', '"')


def system_prompt(source_path=PROMPT_BUILDER):
    return _constant(source_path, "SystemPrompt")


def relevant_code(text, source_path=PROMPT_BUILDER):
    """Shared files as the gateway puts them in the prompt: marked as data, not instructions."""
    return _constant(source_path, "FilesStart") + text + _constant(source_path, "FilesEnd")


def csharp_string(cli_file, name):
    return _constant(os.path.join(REPO, "src", "MyOwnLLM.Cli", cli_file), name)


def csharp_int(cli_file, name):
    """A C# int constant, as `const int Name = 500;` or one of several on a line (`const int A = 1, Name = 500;`)."""
    with open(os.path.join(REPO, "src", "MyOwnLLM.Cli", cli_file), encoding="utf-8-sig") as f:
        source = f.read()
    return int(re.search(rf"const int [^;]*\b{name}\s*=\s*(\d+)", source).group(1))


def _files(paths):
    parts = []
    for path in paths:
        with open(os.path.join(REPO, path), encoding="utf-8-sig") as f:
            parts.append(f"--- {os.path.basename(path)} ---\n{f.read()}\n")
    return "".join(parts)


def read_context(limit=8000):
    """About 2,000 tokens of real code, cut the way /read stops at a limit. Jazari's own code, so nothing private is involved."""
    return _files(["src/MyOwnLLM.Gateway/Queue/RequestQueue.cs", "src/MyOwnLLM.Gateway/State/StateNoteUpdater.cs"])[:limit]


def _timed(call):
    started = time.time()
    return call(), round(time.time() - started, 1)


def read_scenario(ask, system, code):
    """What /read then two questions cost. The gateway sends the code as a system message after the system prompt."""
    messages = [{"role": "system", "content": system}, {"role": "system", "content": relevant_code(code)},
                {"role": "user", "content": "What does this code do when the queue is full?"}]
    answer, first_s = _timed(lambda: ask(messages))
    messages = messages + [{"role": "assistant", "content": answer}, {"role": "user", "content": "Which class should I change to raise that limit?"}]
    _, followup_s = _timed(lambda: ask(messages))
    return first_s, followup_s


def edit_scenario(ask, system, file_text, request='Change the user name it compares against from "admin" to "root".'):
    messages = [{"role": "system", "content": system}, {"role": "system", "content": relevant_code(f"--- file ---\n{file_text}")},
                {"role": "user", "content": csharp_string("FileEdit.cs", "Instructions") + request}]
    reply = ask(messages, 1000, csharp_string("FileEdit.cs", "Schema"))
    try:
        edits = json.loads(reply)["edits"]
        return bool(edits) and all(file_text.count(e["search"]) == 1 for e in edits)
    except (ValueError, KeyError, TypeError):
        return False


# The fixture's three planted bugs (spike/review-test/README.md): the file each is in, and words that name it.
PLANTED = {"AuthService.cs": ("password",), "OrderRepository.cs": ("sql", "injection"), "RefundController.cs": ("own", "authoriz")}


def _review_part(ask, system, files):
    """The findings for one /review part, sent as the CLI sends it; None when the reply isn't the expected JSON."""
    messages = [{"role": "system", "content": system}, {"role": "system", "content": relevant_code(_files(files))},
                {"role": "user", "content": csharp_string("ProjectReview.cs", "Instructions")}]
    reply = ask(messages, csharp_int("ProjectReview.cs", "AnswerTokens"), csharp_string("ProjectReview.cs", "Schema"))
    try:
        findings = json.loads(reply)["findings"]
        return [(os.path.basename(str(f["file"])), f"{f['problem']} {f.get('quote', '')}".lower()) for f in findings]
    except (ValueError, KeyError, TypeError):
        return None


def review_scenario(ask, system):
    """Two /review parts. The fixture with three planted bugs: each counts as found once, and only on its own file; it must
    at least report the hardcoded password. Then StoreDbContext.cs, which is clean but full of security words (encryption,
    passwords, tokens), alone. Every finding that names no planted bug is a false alarm."""
    mixed = _review_part(ask, system, [f"spike/review-test/{name}.cs" for name in ["AuthService", "EmailSender", "Order", "OrderRepository", "Program", "RefundController"]])
    if mixed is None:
        return {"review_ok": False, "review_found": None, "review_false_alarms": None}
    hits = {file for file, text in mixed if any(word in text for word in PLANTED.get(file, ()))}
    clean = _review_part(ask, system, ["spike/review-test/StoreDbContext.cs"])
    false_alarms = None if clean is None else len(clean) + sum(not any(word in text for word in PLANTED.get(file, ())) for file, text in mixed)
    return {"review_ok": "AuthService.cs" in hits, "review_found": len(hits), "review_false_alarms": false_alarms}


def conversation(ask, system):
    """Six turns on one topic; the times show whether later turns get slower."""
    turns = ["What is dependency injection?", "How do I register a service for it in ASP.NET Core?", "What does scoped mean there?",
             "Can a singleton use a scoped service?", "How do I get a scoped service inside a BackgroundService?", "Show a short example of that."]
    messages = [{"role": "system", "content": system}]
    times = []
    for turn in turns:
        messages = messages + [{"role": "user", "content": turn}]
        answer, seconds = _timed(lambda: ask(messages))
        messages = messages + [{"role": "assistant", "content": answer}]
        times.append(seconds)
    return times


def run_scenarios(ask, system):
    """Each scenario on its own: one that breaks is recorded as failed and the rest still run."""
    def attempt(name, call, failed):
        print(f"scenario: {name}", flush=True)
        try:
            return call()
        except Exception as e:
            print(f"scenario {name} failed: {e}", flush=True)
            return failed
    first_s, followup_s = attempt("read", lambda: read_scenario(ask, system, read_context()), (None, None))
    with open(os.path.join(REPO, "spike", "review-test", "AuthService.cs"), encoding="utf-8-sig") as f:
        auth = f.read()
    return {"read_first_s": first_s, "followup_s": followup_s, "edit_ok": attempt("edit", lambda: edit_scenario(ask, system, auth), False),
            **attempt("review", lambda: review_scenario(ask, system), {"review_ok": False, "review_found": None, "review_false_alarms": None}),
            "turn_times_s": attempt("conversation", lambda: conversation(ask, system), [])}


def run_questions(questions, ask):
    answers = []
    for q in questions:
        started = time.time()
        try:
            text, error = ask(q), None
        except Exception as e:  # a dead server or a bad reply fails this question, not the whole run
            text, error = "", str(e)
        result = {"id": q["id"], "category": q["category"], "question": q["question"], "answer": text, "seconds": round(time.time() - started, 1), **score(text, q)}
        if error:
            result["error"] = error
        answers.append(result)
        print(f'{"PASS" if result["passed"] else "FAIL"} {result["seconds"]:5.0f}s {q["id"]}', flush=True)
    return answers


def plain_through_gateway(gateway_ask):
    """A question as the CLI sends it: the user's words alone. The gateway adds the system prompt, and the law book's passage."""
    return lambda q: gateway_ask([{"role": "user", "content": q["question"]}])


def with_passages(answers, find):
    """Notes the law book addresses each question was given, so a wrong answer can be traced to its passage. None: unknown."""
    for answer in answers:
        try:
            answer["passages"] = [p["url"] for p in find(answer["question"])]
        except Exception:  # the lookup is a note on the run, not part of it
            answer["passages"] = None
    return answers


def wait_until_ready(server, port=PORT, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if server.poll() is not None:
            raise ServerDidNotStart(f"llama-server stopped with exit code {server.returncode}: these settings may not load")
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2)
            return
        except (urllib.error.URLError, OSError):
            time.sleep(1)
    raise ServerDidNotStart(f"llama-server wasn't ready after {seconds} s")


def save_failure(label, reason, folder=os.path.join(HERE, "results")):
    """A setup that can't load is reported as failed, and an older result can't stand in for it."""
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, f"{label}.json"), "w", encoding="utf-8") as f:
        json.dump({"label": label, "failed": reason}, f, indent=1)


def chat(messages, max_tokens=MAX_TOKENS, json_schema=None, port=PORT, model=None):
    body = {"messages": messages, "max_tokens": max_tokens, "cache_prompt": True, "stream": False, "temperature": TEMPERATURE}
    if model:  # which lane, when the server is a router
        body["model"] = model
    if json_schema:
        body["json_schema"] = json.loads(json_schema)
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=600) as response:
        return json.load(response)["choices"][0]["message"]["content"] or ""


def server_args(a):
    # The same settings as Tuning.ServerArgs. With a router preset the models and their settings come from the preset instead.
    if getattr(a, "router_preset", None):
        return [a.llama, "--models-preset", a.router_preset, "--models-max", "1", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT)]
    return [a.llama, "-m", a.model, "-dev", a.device, "-ngl", str(a.gpu_layers), "-t", str(a.threads), "--poll", "0", "-fa", "on",
            "-c", "16384", "--cache-ram", "1024", "-ctxcp", "8", "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT)] + ([] if a.reasoning == "auto" else ["--reasoning", a.reasoning])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--device", default="none")
    parser.add_argument("--gpu-layers", type=int, default=0)
    parser.add_argument("--reasoning", default="auto", choices=["auto", "on", "off"], help="a model's own thinking; off leaves its answer alone within the token cap")
    parser.add_argument("--router-preset", help="run llama-server in router mode on this preset file (see measure_lanes.preset_text), asking for --model-name")
    parser.add_argument("--model-name", help="the preset section to ask, with --router-preset")
    parser.add_argument("--scenarios", action="store_true", help="also time /read, /edit, /review and a conversation")
    parser.add_argument("--limit", type=int, help="only the first N questions, for a quick check")
    parser.add_argument("--llama", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "Jazari", "llama", "llama-server.exe"))
    parser.add_argument("--law-book", help="send the plain questions through a temporary gateway (port 5099, its own data folder) "
                        "that searches this law book file; the scenarios stay as they are")
    a = parser.parse_args()
    if bool(a.router_preset) != bool(a.model_name):
        sys.exit("--router-preset and --model-name go together")
    if a.law_book:
        if not os.path.isfile(a.law_book):
            sys.exit(f"no law book at {a.law_book}: build it with py -I lawbook/build.py")
        try:
            build_gateway()
        except ServerDidNotStart as e:
            sys.exit(str(e))

    with open(os.path.join(HERE, "questions.json"), encoding="utf-8") as f:
        questions = json.load(f)[: a.limit]
    system = system_prompt()

    try:
        server = start_server(server_args(a), PORT)
    except PortInUse as e:
        sys.exit(str(e))
    gateway, folder = [], None
    try:
        try:
            ask = lambda q: chat([{"role": "system", "content": system}, {"role": "user", "content": q["question"]}], model=a.model_name)
            if a.law_book:
                folder = tempfile.mkdtemp(prefix="jazari-eval-")
                env = gateway_env(folder, PORT, GATEWAY_PORT, a.law_book)
                key = add_key(env, folder)
                gateway.append(launch(["dotnet", GATEWAY_DLL], GATEWAY_PORT, env=env, cwd=folder))
                ask = plain_through_gateway(lambda messages: gateway_chat(key, messages, MAX_TOKENS))
            wait_until_ready(server)
            if gateway:
                wait_for_gateway(gateway[0], key)
        except PortInUse as e:
            sys.exit(str(e))
        except ServerDidNotStart as e:
            save_failure(a.label, str(e))
            sys.exit(f"{a.label}: {e}")
        answers = run_questions(questions, ask)
        if a.law_book:
            with_passages(answers, lambda question: search_law(env, folder, question))
        times = [x["seconds"] for x in answers]
        result = {"label": a.label, "model": os.path.basename(a.model), "threads": a.threads, "device": a.device, "gpu_layers": a.gpu_layers, "temperature": TEMPERATURE, **stamp(a.llama, a.model),
                  "law_book": os.path.abspath(a.law_book) if a.law_book else None,
                  "score": sum(x["passed"] for x in answers), "total": len(answers), "plain_p50_s": statistics.median(times), "plain_max_s": max(times),
                  "read_first_s": None, "followup_s": None, "edit_ok": None, "review_ok": None, "review_found": None, "review_false_alarms": None, "answers": answers}
        if a.scenarios:
            result.update(run_scenarios(chat, system))
    finally:
        stop(gateway)
        server.kill()
        server.wait()
        if folder:
            shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(os.path.join(HERE, "results"), exist_ok=True)
    with open(os.path.join(HERE, "results", f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: {result["score"]}/{result["total"]}, median {result["plain_p50_s"]} s, longest {result["plain_max_s"]} s')
    if a.law_book:
        used = [x for x in answers if x.get("passages")]
        print(f'{len(used)} questions were given a passage; {sum(not x["passed"] for x in used)} of them still failed: '
              + (", ".join(x["id"] for x in used if not x["passed"]) or "none"))


if __name__ == "__main__":
    main()
