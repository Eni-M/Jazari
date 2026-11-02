"""Runs the test set against a model through a temporary llama-server on port 8099, and saves the scored answers."""
import argparse, json, os, re, socket, statistics, subprocess, sys, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from score import score

PORT = 8099
REPO = os.path.join(HERE, "..")
PROMPT_BUILDER = os.path.join(REPO, "src", "MyOwnLLM.Gateway", "Prompt", "PromptBuilder.cs")
MAX_TOKENS = 512  # the gateway's DefaultChatTokens
TEMPERATURE = 0.2  # the gateway's CodeTemperature


class PortInUse(Exception):
    pass


class ServerDidNotStart(Exception):
    pass


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


def csharp_string(cli_file, name):
    return _constant(os.path.join(REPO, "src", "MyOwnLLM.Cli", cli_file), name)


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
    messages = [{"role": "system", "content": system}, {"role": "system", "content": "Relevant code:\n" + code},
                {"role": "user", "content": "What does this code do when the queue is full?"}]
    answer, first_s = _timed(lambda: ask(messages))
    messages = messages + [{"role": "assistant", "content": answer}, {"role": "user", "content": "Which class should I change to raise that limit?"}]
    _, followup_s = _timed(lambda: ask(messages))
    return first_s, followup_s


def edit_scenario(ask, system, file_text, request='Change the user name it compares against from "admin" to "root".'):
    messages = [{"role": "system", "content": system}, {"role": "system", "content": f"Relevant code:\n--- file ---\n{file_text}"},
                {"role": "user", "content": csharp_string("FileEdit.cs", "Instructions") + request}]
    reply = ask(messages, 1000, csharp_string("FileEdit.cs", "Schema"))
    try:
        edits = json.loads(reply)["edits"]
        return bool(edits) and all(file_text.count(e["search"]) == 1 for e in edits)
    except (ValueError, KeyError, TypeError):
        return False


def review_scenario(ask, system):
    """One /review part: the fixture with planted bugs. It must at least report the hardcoded password."""
    fixture = [f"spike/review-test/{name}.cs" for name in ["AuthService", "EmailSender", "Order", "OrderRepository", "Program", "RefundController"]]
    messages = [{"role": "system", "content": system}, {"role": "system", "content": "Relevant code:\n" + _files(fixture)},
                {"role": "user", "content": csharp_string("ProjectReview.cs", "Instructions")}]
    reply = ask(messages, 500, csharp_string("ProjectReview.cs", "Schema"))
    try:
        return any("password" in f"{f['problem']} {f['quote']}".lower() for f in json.loads(reply)["findings"])
    except (ValueError, KeyError, TypeError):
        return False


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
            "review_ok": attempt("review", lambda: review_scenario(ask, system), False), "turn_times_s": attempt("conversation", lambda: conversation(ask, system), [])}


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


def start_server(args, port=PORT):
    with socket.socket() as probe:
        probe.settimeout(1)
        if probe.connect_ex(("127.0.0.1", port)) == 0:
            raise PortInUse(f"port {port} is in use: stop whatever is on it, then run this again")
    return subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


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


def chat(messages, max_tokens=MAX_TOKENS, json_schema=None, port=PORT):
    body = {"messages": messages, "max_tokens": max_tokens, "cache_prompt": True, "stream": False, "temperature": TEMPERATURE}
    if json_schema:
        body["json_schema"] = json.loads(json_schema)
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=600) as response:
        return json.load(response)["choices"][0]["message"]["content"] or ""


def server_args(a):
    # The same settings as Tuning.ServerArgs.
    return [a.llama, "-m", a.model, "-dev", a.device, "-ngl", str(a.gpu_layers), "-t", str(a.threads), "--poll", "0", "-fa", "on",
            "-c", "16384", "--cache-ram", "2048", "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT)] + ([] if a.reasoning == "auto" else ["--reasoning", a.reasoning])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--device", default="none")
    parser.add_argument("--gpu-layers", type=int, default=0)
    parser.add_argument("--reasoning", default="auto", choices=["auto", "on", "off"], help="a model's own thinking; off leaves its answer alone within the token cap")
    parser.add_argument("--scenarios", action="store_true", help="also time /read, /edit, /review and a conversation")
    parser.add_argument("--limit", type=int, help="only the first N questions, for a quick check")
    parser.add_argument("--llama", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "Jazari", "llama", "llama-server.exe"))
    a = parser.parse_args()

    with open(os.path.join(HERE, "questions.json"), encoding="utf-8") as f:
        questions = json.load(f)[: a.limit]
    system = system_prompt()

    try:
        server = start_server(server_args(a))
    except PortInUse as e:
        sys.exit(str(e))
    try:
        try:
            wait_until_ready(server)
        except ServerDidNotStart as e:
            sys.exit(f"{a.label}: {e}")
        answers = run_questions(questions, lambda q: chat([{"role": "system", "content": system}, {"role": "user", "content": q["question"]}]))
        times = [x["seconds"] for x in answers]
        result = {"label": a.label, "model": os.path.basename(a.model), "threads": a.threads, "device": a.device, "gpu_layers": a.gpu_layers,
                  "score": sum(x["passed"] for x in answers), "total": len(answers), "plain_p50_s": statistics.median(times), "plain_max_s": max(times),
                  "read_first_s": None, "followup_s": None, "edit_ok": None, "review_ok": None, "answers": answers}
        if a.scenarios:
            result.update(run_scenarios(chat, system))
    finally:
        server.kill()
        server.wait()
    os.makedirs(os.path.join(HERE, "results"), exist_ok=True)
    with open(os.path.join(HERE, "results", f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: {result["score"]}/{result["total"]}, median {result["plain_p50_s"]} s, longest {result["plain_max_s"]} s')


if __name__ == "__main__":
    main()
