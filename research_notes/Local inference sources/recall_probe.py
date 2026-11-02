# Does Granite 4.0-H Tiny still recall facts deep in a conversation? Temporary llama-server on port 8099, Jazari's real
# system prompt, this repo's own C# as filler, invented project facts as the things to recall. One run each: direction, not proof.
import glob, json, re, subprocess, sys, time, urllib.request

REPO = "C:/MyOwnLLM"
OUT = sys.argv[1]
PORT = 8099
CHUNK = 4460
src = open(REPO + "/src/MyOwnLLM.Gateway/Prompt/PromptBuilder.cs", encoding="utf8").read()
SYSTEM = re.search(r"public const string SystemPrompt =(.*?);", src, re.S).group(1)
SYSTEM = "".join(re.findall(r'"([^"]*)"', SYSTEM))
code = "".join(open(f, encoding="utf8").read() for f in sorted(glob.glob(REPO + "/src/**/*.cs", recursive=True)))
chunks = [code[i:i + CHUNK] for i in range(0, len(code), CHUNK)]

# Six confusable pairs: the same kind of value twice, so a model that half-remembers picks the wrong one.
FACTS = [
    ("The refund service retries 3 times before giving up.", "refund service retries", "How many times does the refund service retry?", r"\b(3|three)\b"),
    ("Invoices are kept for 7 years.", "invoices kept", "How long are invoices kept?", r"\b(7|seven)\b"),
    ("The staging database listens on port 5433.", "staging database port", "Which port does the staging database listen on?", r"5433"),
    ("Order ids start with the prefix ORD-.", "order id prefix", "What prefix do order ids start with?", r"ORD"),
    ("The payment timeout is 45 seconds.", "payment timeout", "What is the payment timeout?", r"\b45\b"),
    ("Deployments are frozen on Fridays.", "deployment freeze day", "On which day are deployments frozen?", r"Friday"),
    ("The email service retries 5 times before giving up.", "email service retries", "How many times does the email service retry?", r"\b(5|five)\b"),
    ("Audit logs are kept for 2 years.", "audit logs kept", "How long are audit logs kept?", r"\b(2|two)\b"),
    ("The production database listens on port 6432.", "production database port", "Which port does the production database listen on?", r"6432"),
    ("Customer ids start with the prefix CUS-.", "customer id prefix", "What prefix do customer ids start with?", r"CUS"),
    ("The login timeout is 20 seconds.", "login timeout", "What is the login timeout?", r"\b20\b"),
    ("Backups run on Sundays.", "backup day", "On which day do backups run?", r"Sunday"),
]
log = open(OUT, "a", encoding="utf8")


def write(row):
    log.write(json.dumps(row, ensure_ascii=False) + "\n")
    log.flush()


def start(label, extra):
    p = subprocess.Popen(
        [REPO + "/spike/bin/llama-server.exe", "-m", REPO + "/spike/models/granite-4.0-h-tiny-Q4_K_M.gguf",
         "-dev", "none", "-ngl", "0", "-t", "4", "--poll", "0", "-fa", "off", "-c", "16384", "--cache-ram", "2048",
         "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(PORT), "-v"] + extra,
        stdout=open(OUT + "." + label + ".log", "w", encoding="utf8"), stderr=subprocess.STDOUT)
    for _ in range(240):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=2)
            return p
        except Exception:
            time.sleep(1)
    p.kill()
    raise SystemExit("server did not start")


def ask(messages, tokens=40):
    body = {"messages": messages, "max_tokens": tokens, "temperature": 0.2, "seed": 7, "cache_prompt": True}
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req, timeout=1800))
    return r["choices"][0]["message"]["content"], r["timings"]


def memory(pid):
    out = subprocess.run(["tasklist", "/fi", f"PID eq {pid}", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    m = re.search(r'"([\d,.\s]+) K"', out)
    return int(re.sub(r"\D", "", m.group(1))) // 1024 if m else None


def exam(label, messages, suffix=None, facts=FACTS):
    right = 0
    for n, (_, _, q, want) in enumerate(facts):
        tail = ([suffix] if suffix else []) + [{"role": "user", "content": q}]
        answer, t = ask(messages + tail)
        ok = bool(re.search(want, answer, re.I))
        right += ok
        write({"exam": label, "fact": n, "ok": ok, "answer": answer[:160], "read": t["prompt_n"], "cached": t.get("cache_n", 0),
               "read_s": round(t["prompt_ms"] / 1000, 1), "write_tok_s": round(t["predicted_per_second"], 1)})
    write({"exam": label, "score": right, "of": len(facts)})


def conversation(filler):
    """One fact per turn, oldest first, each with `filler` chunks of real code around it."""
    history = [{"role": "system", "content": SYSTEM}]
    for n, (fact, _, _, _) in enumerate(FACTS):
        text = fact
        if filler:
            text += "\nAlso look at this code and name one method in it.\n```csharp\n" + "".join(chunks[n:n + filler]) + "\n```"
        history.append({"role": "user", "content": text})
        history.append({"role": "assistant", "content": ask(history, 24)[0]})
    return history


server = start("short", [])
try:
    exam("A short conversation, facts only", conversation(0))
finally:
    server.kill(); server.wait(); time.sleep(5)

server = start("deep", [])
try:
    deep = conversation(1)
    _, t = ask(deep, 1)
    write({"note": "deep conversation built", "tokens": t["prompt_n"] + t.get("cache_n", 0), "working_set_mib": memory(server.pid)})
    exam("B deep conversation, nothing pinned", deep)
    pinned = "Project facts:\n" + "\n".join(f"{label}: {re.search(r'[0-9]+|ORD-|CUS-|Fridays|Sundays', fact).group(0)}" for fact, label, _, _ in FACTS)
    exam("C deep, label: value list pinned before the question", deep, {"role": "user", "content": pinned})
    exam("D deep, same facts as prose pinned before the question", deep, {"role": "user", "content": "Project facts: " + " ".join(f[0] for f in FACTS)})
    # What PromptBuilder does when full: the oldest four question/answer pairs go, here taking facts 0-3 with them.
    cut = [deep[0]] + deep[9:]
    exam("E after cutting four pairs, kept facts", cut, facts=FACTS[4:])
    exam("F after cutting four pairs, cut facts", cut, facts=FACTS[:4])
    write({"note": "peak", "working_set_mib": memory(server.pid)})
finally:
    server.kill(); server.wait(); time.sleep(5)

# The same cut text read fresh, with nothing cached: tells whether the cheap cut above changes what the model knows.
server = start("fresh", [])
try:
    exam("G cut conversation read fresh, kept facts", cut, facts=FACTS[4:])
    exam("H cut conversation read fresh, cut facts", cut, facts=FACTS[:4])
finally:
    server.kill(); server.wait(); time.sleep(5)

# Fewer checkpoints: does the cheap "new question, same history" still work, and how much memory comes back?
server = start("ctxcp8", ["-ctxcp", "8"])
try:
    small = [deep[0]] + deep[1:13]
    ask(small, 8)
    write({"note": "ctxcp 8 after build", "working_set_mib": memory(server.pid)})
    for q in ("Which of those methods is longest?", "Which of those methods is shortest?"):
        _, t = ask(small + [{"role": "user", "content": q}], 8)
        write({"note": "ctxcp 8 new question", "read": t["prompt_n"], "cached": t.get("cache_n", 0), "read_s": round(t["prompt_ms"] / 1000, 1), "working_set_mib": memory(server.pid)})
finally:
    server.kill(); server.wait()
write({"done": True})
log.close()
