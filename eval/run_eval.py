"""Runs the test set against a model through a temporary llama-server on port 8099, and saves the scored answers."""
import argparse, json, os, re, socket, statistics, subprocess, sys, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from score import score

PORT = 8099
PROMPT_BUILDER = os.path.join(HERE, "..", "src", "MyOwnLLM.Gateway", "Prompt", "PromptBuilder.cs")
MAX_TOKENS = 512  # the gateway's DefaultChatTokens
TEMPERATURE = 0.2  # the gateway's CodeTemperature


class PortInUse(Exception):
    pass


class ServerDidNotStart(Exception):
    pass


def system_prompt(source_path=PROMPT_BUILDER):
    """PromptBuilder.SystemPrompt: its string literals joined, without the comments above them."""
    with open(source_path, encoding="utf-8") as f:
        source = f.read()
    start = re.search(r"const string SystemPrompt\s*=", source).end()
    parts = []
    for line in source[start:].splitlines():
        text = line.strip()
        if not text or text.startswith("//"):
            continue
        parts.append(text)
        if text.endswith(";"):
            break
    return "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', " ".join(parts)))


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
            "-c", "16384", "--cache-ram", "2048", "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--device", default="none")
    parser.add_argument("--gpu-layers", type=int, default=0)
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
    finally:
        server.kill()
        server.wait()
    os.makedirs(os.path.join(HERE, "results"), exist_ok=True)
    with open(os.path.join(HERE, "results", f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: {result["score"]}/{result["total"]}, median {result["plain_p50_s"]} s, longest {result["plain_max_s"]} s')


if __name__ == "__main__":
    main()
