"""Times the two ways of switching between the fast and the deep model on this PC, a plain stop-and-start and llama-server's
router mode, and measures each lane's memory. Port 8099 only; stop the real Jazari and other heavy work first.

    py -I eval/measure_lanes.py [--threads]       # prints the numbers as JSON
"""
import argparse, json, os, statistics, subprocess, sys, tempfile, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import LLAMA_PORT, kill_tree, start_server
from run_eval import chat, server_args, wait_until_ready

LLAMA = os.path.join(HERE, "..", "spike", "bin", "llama-server.exe")
MODELS = os.path.join(HERE, "..", "spike", "models")
FAST = {"model": os.path.join(MODELS, "granite-4.0-h-tiny-Q4_K_M.gguf"), "threads": 4}
DEEP = {"model": os.path.join(MODELS, "qwen3-4b-instruct-2507-Q4_K_M.gguf"), "threads": 4}
KEY = "measure-key"
# The same settings as run_eval.server_args, as the preset keys the router reads.
SHARED = {"c": 16384, "flash-attn": "on", "cache-ram": 1024, "ctx-checkpoints": 8, "parallel": 1, "cache-reuse": 256, "poll": 0,
          "device": "none", "n-gpu-layers": 0}


def preset_text(fast, deep, shared=SHARED):
    """The router's INI: one [section] per lane, named as the request's "model"; `shared` goes under [*]."""
    def section(name, values):
        return f"[{name}]\n" + "".join(f"{k} = {v}\n" for k, v in values.items()) + "\n"
    return "version = 1\n\n" + section("*", shared) + section("fast", fast) + section("deep", deep)


def median_of_three(measure):
    """The middle of three timed runs; one slow disk read can't decide it."""
    return statistics.median(measure() for _ in range(3))


def plain_args(lane):
    return server_args(argparse.Namespace(llama=LLAMA, model=lane["model"], threads=lane["threads"], device="none", gpu_layers=0, reasoning="auto"))


def memory_mb():
    """(working set, peak working set) of every llama-server process, in MB, summed: the router and its child count together."""
    out = subprocess.run(["powershell", "-NoProfile", "-c", "$p = Get-Process llama-server -ErrorAction SilentlyContinue; "
                          "'{0} {1}' -f [int](($p | measure WorkingSet64 -Sum).Sum / 1MB), [int](($p | measure PeakWorkingSet64 -Sum).Sum / 1MB)"],
                         capture_output=True, text=True).stdout.split()
    return int(out[0]), int(out[1])


def first_token(model=None, headers=None):
    """Seconds for one one-token answer: a request that has to wait for the model it names to load counts all of that."""
    body = {"messages": [{"role": "user", "content": "Say hi"}], "max_tokens": 1, "stream": False}
    if model:
        body["model"] = model
    started = time.time()
    request = urllib.request.Request(f"http://127.0.0.1:{LLAMA_PORT}/v1/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json", **(headers or {})})
    urllib.request.urlopen(request, timeout=600).read()
    return time.time() - started


def stop(server):
    kill_tree(server)


def plain_swap(a, b):
    """Stop lane a's server, start lane b's, and time it until b's first token."""
    server = start_server(plain_args(a), LLAMA_PORT)
    try:
        wait_until_ready(server)
        first_token()
        started = time.time()
        stop(server)
        server = start_server(plain_args(b), LLAMA_PORT)
        wait_until_ready(server)
        first_token()
        return time.time() - started
    finally:
        stop(server)


def router_swap(a, b, runs):
    """One router with --models-max 1: ask for lane a, then time asking for lane b. Alternates, so every run after the first is a swap."""
    with tempfile.TemporaryDirectory() as folder:
        preset = os.path.join(folder, "lanes.ini")
        with open(preset, "w") as f:
            f.write(preset_text(FAST, DEEP))
        router = start_server([LLAMA, "--models-preset", preset, "--models-max", "1", "--no-webui", "--host", "127.0.0.1", "--port", str(LLAMA_PORT)],
                              LLAMA_PORT, env={**os.environ, "LLAMA_API_KEY": KEY})
        try:
            wait_until_ready(router)
            idle = memory_mb()[0]
            headers = {"Authorization": f"Bearer {KEY}"}
            times = []
            for _ in range(runs):
                first_token("fast" if a is FAST else "deep", headers)
                times.append(first_token("deep" if b is DEEP else "fast", headers))
            return times, idle
        finally:
            stop(router)


def plain_lane(lane):
    server = start_server(plain_args(lane), LLAMA_PORT)
    try:
        wait_until_ready(server)
        started = time.time()
        chat([{"role": "user", "content": "Write a C# method that reverses a string, with a short explanation."}], 64)
        seconds = time.time() - started
        return {"working_set_mb": memory_mb()[0], "peak_mb": memory_mb()[1], "seconds_for_64_tokens": round(seconds, 1)}
    finally:
        stop(server)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--threads", action="store_true", help="also time the deep lane at 2, 3 and 4 threads")
    a = parser.parse_args()
    swaps = {"plain_fast_to_deep_s": [plain_swap(FAST, DEEP) for _ in range(3)], "plain_deep_to_fast_s": [plain_swap(DEEP, FAST) for _ in range(3)]}
    swaps["router_fast_to_deep_s"], idle = router_swap(FAST, DEEP, 3)
    swaps["router_deep_to_fast_s"], _ = router_swap(DEEP, FAST, 3)
    result = {k: {"runs": [round(x, 1) for x in v], "median": round(median_of_three(iter(v).__next__), 1)} for k, v in swaps.items()}
    result.update(router_idle_mb=idle, fast_lane=plain_lane(FAST), deep_lane=plain_lane(DEEP))
    if a.threads:
        result["deep_threads"] = {t: plain_lane({**DEEP, "threads": t})["seconds_for_64_tokens"] for t in (2, 3, 4)}
    print(json.dumps(result, indent=1))


if __name__ == "__main__":
    main()
