"""Recall test: twelve invented facts asked back in a short conversation, deep in a long one, and after the gateway cuts
history, all through the real gateway (port 5099, its own temporary data folder) and a temporary llama-server (port
8099). Writes eval/results/recall/<label>.json. The facts are invented and stay out of training data."""
import argparse, collections, json, os, re, shutil, statistics, subprocess, sys, tempfile, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from run_eval import PortInUse, ServerDidNotStart, save_failure, start_server, wait_until_ready

REPO = os.path.join(HERE, "..")
GATEWAY_PROJECT = os.path.join(REPO, "src", "MyOwnLLM.Gateway")
GATEWAY_DLL = os.path.join(GATEWAY_PROJECT, "bin", "Release", "net10.0", "MyOwnLLM.Gateway.dll")
RESULTS = os.path.join(HERE, "results", "recall")  # a subfolder, so compare.py doesn't take a result for a candidate
LLAMA_PORT, GATEWAY_PORT = 8099, 5099
CHUNK = 4460  # characters of this repo's C# after each fact in the deep conversation: about 12,700 tokens in all
MAX_TOKENS = 40
# At 16384 the deep conversation fits whole. At 10240 the gateway cuts 4 question/answer pairs, taking facts 1-4 with them.
DEEP_CONTEXT, CUT_CONTEXT = 16384, 10240

Fact = collections.namedtuple("Fact", "fact question want")
# From research_notes/Local inference sources/recall_probe.py. Six confusable pairs: the same kind of value twice, so a
# model that half-remembers picks the wrong one.
FACTS = [
    Fact("The refund service retries 3 times before giving up.", "How many times does the refund service retry?", r"\b(3|three)\b"),
    Fact("Invoices are kept for 7 years.", "How long are invoices kept?", r"\b(7|seven)\b"),
    Fact("The staging database listens on port 5433.", "Which port does the staging database listen on?", r"5433"),
    Fact("Order ids start with the prefix ORD-.", "What prefix do order ids start with?", r"ORD"),
    Fact("The payment timeout is 45 seconds.", "What is the payment timeout?", r"\b45\b"),
    Fact("Deployments are frozen on Fridays.", "On which day are deployments frozen?", r"Friday"),
    Fact("The email service retries 5 times before giving up.", "How many times does the email service retry?", r"\b(5|five)\b"),
    Fact("Audit logs are kept for 2 years.", "How long are audit logs kept?", r"\b(2|two)\b"),
    Fact("The production database listens on port 6432.", "Which port does the production database listen on?", r"6432"),
    Fact("Customer ids start with the prefix CUS-.", "What prefix do customer ids start with?", r"CUS"),
    Fact("The login timeout is 20 seconds.", "What is the login timeout?", r"\b20\b"),
    Fact("Backups run on Sundays.", "On which day do backups run?", r"Sunday"),
]


def is_right(answer, want):
    return bool(re.search(want, answer, re.I))


def gateway_env(data_dir, llama_port, port):
    """The gateway's settings, pointed at the temporary server and folder, so the installed Jazari's data is never touched."""
    return {**os.environ, "Gateway__LlamaUrl": f"http://127.0.0.1:{llama_port}", "Gateway__Port": str(port), "Gateway__DataDir": data_dir}


def llama_args(llama, model, threads, context):
    # Tuning.ServerArgs's settings for a T480 CPU run. The server path is made absolute: on Windows a relative one with
    # forward slashes, like spike/bin/llama-server.exe, fails to start with WinError 2.
    return [os.path.abspath(llama), "-m", model, "-dev", "none", "-ngl", "0", "-t", str(threads), "--poll", "0", "-fa", "off", "-c", str(context),
            "--cache-ram", "1024", "-ctxcp", "8", "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(LLAMA_PORT)]


def filler(chunk=CHUNK):
    """This repo's own C# in CHUNK-sized pieces, in the same order on every PC, leaving out what builds generate in bin/ and obj/."""
    paths = []
    for root, dirs, files in os.walk(os.path.join(REPO, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        paths += [os.path.relpath(os.path.join(root, f), REPO).replace(os.sep, "/") for f in files if f.endswith(".cs")]
    parts = []
    for path in sorted(paths):
        with open(os.path.join(REPO, path), encoding="utf-8") as f:
            parts.append(f.read())
    code = "".join(parts)
    return [code[i:i + chunk] for i in range(0, len(code), chunk)]


def conversation(ask, chunks=None):
    """One fact per question, oldest first, each followed by a chunk of real code when `chunks` is given. No system
    message: the gateway adds its own, as it does for the CLI."""
    history = []
    for n, f in enumerate(FACTS):
        text = f.fact
        if chunks:
            text += "\nAlso look at this code and name one method in it.\n```csharp\n" + chunks[n] + "\n```"
        history.append({"role": "user", "content": text})
        history.append({"role": "assistant", "content": ask(history)})
    return history


def exam(ask, history):
    """All twelve questions, each on top of the same history. A failed request counts as wrong, and the rest still run."""
    answers = []
    for f in FACTS:
        started = time.time()
        try:
            answer, error = ask(history + [{"role": "user", "content": f.question}]), None
        except Exception as e:
            answer, error = "", str(e)
        row = {"question": f.question, "answer": answer, "right": is_right(answer, f.want), "seconds": round(time.time() - started, 1)}
        if error:
            row["error"] = error
        answers.append(row)
        print(f'{"RIGHT" if row["right"] else "WRONG"} {row["seconds"]:5.1f}s {f.question} {answer[:80]!r}', flush=True)
    return {"score": sum(a["right"] for a in answers), "of": len(FACTS), "median_s": statistics.median(a["seconds"] for a in answers), "answers": answers}


def estimated_tokens(history):
    """Characters / 4, the gateway's own estimate."""
    return sum(len(m["content"]) for m in history) // 4


def parse_working_set(tasklist_csv):
    """The memory column of `tasklist /fo csv /nh` in MiB. Its thousands separator follows the PC's language."""
    m = re.search(r'"([^"]+) K"', tasklist_csv)
    return int(re.sub(r"\D", "", m.group(1))) // 1024 if m else None


def working_set_mib(pid):
    """What Windows reports for the process; None where tasklist doesn't exist."""
    try:
        out = subprocess.run(["tasklist", "/fi", f"PID eq {pid}", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    except OSError:
        return None
    return parse_working_set(out)


def launch(args, port, **popen):
    """start_server, with a missing or blocked program reported like a server that didn't start."""
    try:
        return start_server(args, port, **popen)
    except OSError as e:
        raise ServerDidNotStart(f"couldn't start {args[0]}: {e}")


def add_key(env, folder):
    """A key in the temporary folder's own keys.json, from the first line add-key prints."""
    out = subprocess.run(["dotnet", GATEWAY_DLL, "add-key"], env=env, cwd=folder, capture_output=True, text=True)
    if out.returncode != 0 or not out.stdout.strip():
        raise ServerDidNotStart(f"the gateway's add-key failed: {out.stderr.strip() or out.returncode}")
    return out.stdout.splitlines()[0].strip()


def wait_for_gateway(gateway, key, seconds=60):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if gateway.poll() is not None:
            raise ServerDidNotStart(f"the gateway stopped with exit code {gateway.returncode}")
        try:
            request = urllib.request.Request(f"http://127.0.0.1:{GATEWAY_PORT}/v1/info", headers={"Authorization": f"Bearer {key}"})
            urllib.request.urlopen(request, timeout=2).close()
            return
        except urllib.error.HTTPError as e:
            raise ServerDidNotStart(f"the gateway answered {e.code} to its own key")
        except (urllib.error.URLError, OSError):
            time.sleep(1)
    raise ServerDidNotStart(f"the gateway wasn't ready after {seconds} s")


def gateway_chat(key, messages):
    """What the CLI sends, without a project folder: no project note goes into the prompt."""
    body = {"messages": messages, "max_tokens": MAX_TOKENS, "stream": False}
    request = urllib.request.Request(f"http://127.0.0.1:{GATEWAY_PORT}/v1/chat/completions", json.dumps(body).encode(),
                                     {"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(request, timeout=1800) as response:
        return json.load(response)["choices"][0]["message"]["content"] or ""


def stop(processes):
    for p in processes:
        if p.poll() is None:
            p.kill()
        p.wait()
    processes.clear()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--llama", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "Jazari", "llama", "llama-server.exe"))
    a = parser.parse_args()

    if subprocess.run(["dotnet", "build", GATEWAY_PROJECT, "-c", "Release", "--disable-build-servers"]).returncode != 0:
        sys.exit("the gateway didn't build")
    folder = tempfile.mkdtemp(prefix="jazari-recall-")
    env = gateway_env(folder, LLAMA_PORT, GATEWAY_PORT)
    running = []

    def start(context):
        llama = launch(llama_args(a.llama, a.model, a.threads, context), LLAMA_PORT)
        running.append(llama)
        gateway = launch(["dotnet", GATEWAY_DLL], GATEWAY_PORT, env=env, cwd=folder)
        running.append(gateway)
        wait_until_ready(llama, LLAMA_PORT)
        wait_for_gateway(gateway, key)
        return llama

    try:
        key = add_key(env, folder)
        ask = lambda messages: gateway_chat(key, messages)
        llama = start(DEEP_CONTEXT)
        print("short: facts only", flush=True)
        short = exam(ask, conversation(ask))
        print("deep: a chunk of code after each fact", flush=True)
        deep_history = conversation(ask, filler())
        deep = exam(ask, deep_history)
        memory = working_set_mib(llama.pid)  # after the deep questions, when the server holds the most
        # The gateway restarts too: it asks llama-server for the context size only once a minute.
        stop(running)
        time.sleep(5)
        start(CUT_CONTEXT)
        print("cut: the same deep conversation in a smaller context", flush=True)
        cut = exam(ask, deep_history)
    except PortInUse as e:
        sys.exit(str(e))
    except ServerDidNotStart as e:
        save_failure(a.label, str(e), RESULTS)
        sys.exit(f"{a.label}: {e}")
    finally:
        stop(running)
        shutil.rmtree(folder, ignore_errors=True)

    result = {"label": a.label, "model": os.path.basename(a.model), "threads": a.threads, "short": short, "deep": deep, "cut": cut,
              "deep_tokens": estimated_tokens(deep_history), "working_set_mib": memory}
    os.makedirs(RESULTS, exist_ok=True)
    with open(os.path.join(RESULTS, f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: short {short["score"]}/12, deep {deep["score"]}/12, cut {cut["score"]}/12, median {deep["median_s"]} s deep, '
          f'{result["deep_tokens"]} tokens deep, server {memory} MiB')


if __name__ == "__main__":
    main()
