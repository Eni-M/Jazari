"""Recall test: twelve invented facts asked back in a short conversation, deep in a long one, and after the gateway cuts
history, all through the real gateway (port 5099, its own temporary data folder) and a temporary llama-server (port
8099). Writes eval/results/recall/<label>.json. The facts are invented and stay out of training data."""
import argparse, collections, json, os, re, shutil, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import (stamp, GATEWAY_DLL, GATEWAY_PORT, LLAMA_PORT, REPO, PortInUse, ServerDidNotStart, add_key, build_gateway, gateway_chat, gateway_env,
                     launch, stop, wait_for_gateway)
from run_eval import save_failure, wait_until_ready

RESULTS = os.path.join(HERE, "results", "recall")  # a subfolder, so compare.py doesn't take a result for a candidate
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
    # The prefixes are matched as capitals: ignoring case, "ORD" was found in any answer saying "order" (task 6 handoff).
    Fact("Order ids start with the prefix ORD-.", "What prefix do order ids start with?", r"(?-i:\bORD\b)"),
    Fact("The payment timeout is 45 seconds.", "What is the payment timeout?", r"\b45\b"),
    Fact("Deployments are frozen on Fridays.", "On which day are deployments frozen?", r"Friday"),
    Fact("The email service retries 5 times before giving up.", "How many times does the email service retry?", r"\b(5|five)\b"),
    Fact("Audit logs are kept for 2 years.", "How long are audit logs kept?", r"\b(2|two)\b"),
    Fact("The production database listens on port 6432.", "Which port does the production database listen on?", r"6432"),
    Fact("Customer ids start with the prefix CUS-.", "What prefix do customer ids start with?", r"(?-i:\bCUS\b)"),
    Fact("The login timeout is 20 seconds.", "What is the login timeout?", r"\b20\b"),
    Fact("Backups run on Sundays.", "On which day do backups run?", r"Sunday"),
]


def is_right(answer, want):
    return bool(re.search(want, answer, re.I))


def llama_args(llama, model, threads, context, cache_ram=1024, ctxcp=8):
    # Tuning.ServerArgs's settings for a T480 CPU run. The server path is made absolute: on Windows a relative one with
    # forward slashes, like spike/bin/llama-server.exe, fails to start with WinError 2. cache_ram and ctxcp can be set
    # back to the old 2048 and 32, to measure what the flags alone change, on one commit.
    return [os.path.abspath(llama), "-m", model, "-dev", "none", "-ngl", "0", "-t", str(threads), "--poll", "0", "-fa", "off", "-c", str(context),
            "--cache-ram", str(cache_ram), "-ctxcp", str(ctxcp), "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(LLAMA_PORT)]


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


def memory_mib(pid):
    """Windows' own numbers for a process, in MiB, or {} where they can't be read. The working set holds the resident
    pages of the memory-mapped model file (4.2 GB for H-Tiny), which Windows fills and trims as it likes; private bytes
    are what the server allocated itself (prompt cache, checkpoints, buffers), which is what the cache flags change.
    Both peaks cover the server's whole life so far."""
    if os.name != "nt":
        return {}
    import ctypes
    from ctypes import wintypes

    class Counters(ctypes.Structure):  # PROCESS_MEMORY_COUNTERS_EX
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
            "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel32.OpenProcess(0x1000 | 0x0010, False, pid)  # query limited information, read memory
    if not handle:
        return {}
    try:
        counters = Counters(cb=ctypes.sizeof(Counters))
        if not kernel32.K32GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return {}
    finally:
        kernel32.CloseHandle(handle)
    mib = lambda n: n // (1 << 20)
    return {"working_set": mib(counters.WorkingSetSize), "peak_working_set": mib(counters.PeakWorkingSetSize),
            "private": mib(counters.PrivateUsage), "peak_private": mib(counters.PeakPagefileUsage)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--llama", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "Jazari", "llama", "llama-server.exe"))
    parser.add_argument("--cache-ram", type=int, default=1024, help="MiB of prompt cache; the shipped 1024, or the old 2048 to compare")
    parser.add_argument("--ctxcp", type=int, default=8, help="checkpoints per conversation; the shipped 8, or llama-server's old default 32")
    a = parser.parse_args()
    os.makedirs(RESULTS, exist_ok=True)
    log = os.path.join(RESULTS, f"{a.label}-llama.log")  # both servers' output, the deep one first
    if os.path.exists(log):
        os.remove(log)

    try:
        build_gateway()
    except ServerDidNotStart as e:
        sys.exit(str(e))
    folder = tempfile.mkdtemp(prefix="jazari-recall-")
    env = gateway_env(folder, LLAMA_PORT, GATEWAY_PORT)
    running = []

    def start(context):
        llama = launch(llama_args(a.llama, a.model, a.threads, context, a.cache_ram, a.ctxcp), LLAMA_PORT, log)
        running.append(llama)
        gateway = launch(["dotnet", GATEWAY_DLL], GATEWAY_PORT, env=env, cwd=folder)
        running.append(gateway)
        wait_until_ready(llama, LLAMA_PORT)
        wait_for_gateway(gateway, key)
        return llama

    try:
        key = add_key(env, folder)
        ask = lambda messages: gateway_chat(key, messages, MAX_TOKENS)
        llama = start(DEEP_CONTEXT)
        print("short: facts only", flush=True)
        short = exam(ask, conversation(ask))
        print("deep: a chunk of code after each fact", flush=True)
        deep_history = conversation(ask, filler())
        deep = exam(ask, deep_history)
        memory = working_set_mib(llama.pid)  # after the deep questions, when the server holds the most
        counters = memory_mib(llama.pid)
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

    result = {"label": a.label, "model": os.path.basename(a.model), "threads": a.threads, "cache_ram": a.cache_ram, "ctxcp": a.ctxcp, **stamp(a.llama, a.model),
              "short": short, "deep": deep, "cut": cut, "deep_tokens": estimated_tokens(deep_history), "working_set_mib": memory,
              "deep_server_mib": counters, "llama_log": log}
    with open(os.path.join(RESULTS, f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: short {short["score"]}/12, deep {deep["score"]}/12, cut {cut["score"]}/12, median {deep["median_s"]} s deep, '
          f'{result["deep_tokens"]} tokens deep, server {memory} MiB; private {counters.get("private")} MiB, '
          f'peak private {counters.get("peak_private")} MiB, peak working set {counters.get("peak_working_set")} MiB')


if __name__ == "__main__":
    main()
