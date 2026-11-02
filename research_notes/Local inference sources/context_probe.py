# Measures how Granite 4.0-H Tiny behaves as a conversation grows, on a temporary llama-server (port 8099).
# Uses Jazari's own server settings and this repo's own C# as the text. Prints one JSON line per request.
import glob, json, subprocess, sys, time, urllib.request

REPO = "C:/MyOwnLLM"
OUT = sys.argv[1]
SYSTEM = "You are Jazari, a coding assistant for C# and .NET. Answer directly in one to three complete sentences."
CHUNK = 4460  # about 1,000 tokens of this repo's C# (measured earlier: 4.46 chars a token)
PORT = 8099

code = "".join(open(f, encoding="utf8").read() for f in sorted(glob.glob(REPO + "/src/**/*.cs", recursive=True)))
chunks = [code[i:i + CHUNK] for i in range(0, len(code), CHUNK)]
log = open(OUT, "a", encoding="utf8")


def start(label, extra):
    server_log = open(OUT + "." + label + ".log", "w", encoding="utf8")
    p = subprocess.Popen(
        [REPO + "/spike/bin/llama-server.exe", "-m", REPO + "/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf",
         "-dev", "none", "-ngl", "0", "-t", "4", "--poll", "0", "-fa", "off", "-c", "16384", "--cache-ram", "2048",
         "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT), "-v"] + extra,
        stdout=server_log, stderr=subprocess.STDOUT)
    for _ in range(240):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=2)
            return p
        except Exception:
            time.sleep(1)
    p.kill()
    raise SystemExit("server did not start")


def ask(label, step, messages):
    body = {"messages": messages, "max_tokens": 48, "temperature": 0.2, "ignore_eos": True, "cache_prompt": True}
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    began = time.time()
    r = json.load(urllib.request.urlopen(req, timeout=1800))
    t = r["timings"]
    row = {"run": label, "step": step, "total_tokens": t["prompt_n"] + t.get("cache_n", 0), "read_tokens": t["prompt_n"],
           "from_cache": t.get("cache_n", 0), "read_s": round(t["prompt_ms"] / 1000, 1),
           "read_tok_s": round(t["prompt_per_second"], 1), "write_tok_s": round(t["predicted_per_second"], 2),
           "wall_s": round(time.time() - began, 1)}
    log.write(json.dumps(row) + "\n")
    log.flush()
    return r["choices"][0]["message"]["content"]


def question(n, size=1):
    return "Name one method in this code and what it does.\n```csharp\n" + "".join(chunks[n:n + size]) + "\n```"


def grow(label, stop):
    """Append-only conversation of about `stop` thousand tokens: the normal, cacheable case."""
    history = [{"role": "system", "content": SYSTEM}]
    for n in range(stop):
        history.append({"role": "user", "content": question(n)})
        history.append({"role": "assistant", "content": ask(label, f"grow {n + 1}k", history)})
    return history


def run(label, extra, deep):
    server = start(label, extra)
    try:
        history = grow(label, 6)
        # Same conversation, a different last question: only the tail changes.
        ask(label, "new question, same history", history + [{"role": "user", "content": "Which of those methods is longest?"}])
        ask(label, "another new question", history + [{"role": "user", "content": "Which of those methods is shortest?"}])
        # A retry after that, back on the long prefix, then append normally again.
        history.append({"role": "user", "content": question(6)})
        history.append({"role": "assistant", "content": ask(label, "append after retries", history)})
        if deep:
            # What PromptBuilder does when a conversation fills up: drop the oldest pairs, so the start changes.
            history = [history[0]] + history[3:]
            history.append({"role": "user", "content": question(7)})
            history.append({"role": "assistant", "content": ask(label, "after cutting the oldest pair", history)})
            history.append({"role": "user", "content": question(8)})
            history.append({"role": "assistant", "content": ask(label, "append after the cut", history)})
            # Deep context: does writing slow down with depth, as it did on the dense 3B (6.75 to 2.82 tok/s)?
            history.append({"role": "user", "content": question(9, 6)})
            history.append({"role": "assistant", "content": ask(label, "deep, about 13k", history)})
            history.append({"role": "user", "content": "In one sentence, what does the first method do?"})
            ask(label, "short follow-up at 13k", history)
    finally:
        server.kill()
        server.wait()
        time.sleep(5)


run("cms-default", [], True)
run("cms-1024", ["-cms", "1024"], False)
log.write(json.dumps({"done": True}) + "\n")
log.close()
