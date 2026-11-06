"""The /fix benchmark: break a small clean project one compile error at a time, run the real /fix with every change accepted, then
build it ourselves to see whether it worked. A temporary gateway and llama-server do the work; the real Jazari install is never used.

    py -I eval/fix_bench.py --label tiny --model spike/models/x.gguf --llama spike/bin/llama-server.exe [--law-book lawbook/out/law-book.db]

Ships if at least 7 of 12 are fixed, none is left worse, and the median is under 2 minutes. Don't tune against these seeds and call
them proof: new seeds decide after any tuning.
"""
import argparse, json, os, re, shutil, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import GATEWAY_DLL, GATEWAY_PORT, ServerDidNotStart, add_key, build_gateway, gateway_env, launch, stamp, start_server, stop, wait_for_gateway
from run_eval import PORT, server_args, wait_until_ready

SEED = os.path.join(HERE, "fix", "Seed")
DRIVER_PROJECT = os.path.join(HERE, "fix", "Driver")
DRIVER = os.path.join(DRIVER_PROJECT, "bin", "Release", "net10.0", "Driver.dll")
RESULTS = os.path.join(HERE, "results", "fix")
NEED_FIXED, MAX_MEDIAN_S, DRIVER_TIMEOUT_S = 7, 120, 900
ERROR = re.compile(r"^\s*(?P<file>.+?)\(\d+,\d+\): error (?P<code>CS\d+): (?P<message>.*?)(?: \[[^\]]*\])?\s*$", re.M)


def seeds():
    with open(os.path.join(HERE, "fix", "seeds.json"), encoding="utf-8") as f:
        return json.load(f)


def fresh_copy(folder):
    """The clean project, restored, in a git repository as a user's would be, since /fix's lookup only walks a repository."""
    project = os.path.join(folder, "Shop")
    shutil.copytree(SEED, project, ignore=shutil.ignore_patterns("bin", "obj"))
    subprocess.run(["git", "init", "-q"], cwd=project, check=True)
    subprocess.run(["dotnet", "restore", "--nologo", "-v", "q"], cwd=project, check=True, capture_output=True)
    return project


def _read(path):
    with open(path, encoding="utf-8") as f:
        return f.read().replace("\r\n", "\n")


def apply_seed(project, seed):
    path = os.path.join(project, seed["file"])
    text = _read(path)
    if text.count(seed["search"]) != 1:
        raise ValueError(f"{seed.get('id')}: its text should appear once in {seed['file']}")
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace(seed["search"], seed["replace"]))


def restore_seed(project, seed):
    shutil.copy(os.path.join(SEED, seed["file"]), os.path.join(project, seed["file"]))


def build_errors(project):
    out = subprocess.run(["dotnet", "build", "--no-restore", "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
                         cwd=project, capture_output=True, text=True, encoding="utf-8", errors="replace")
    found = {(m["file"].lower(), m["code"], m["message"]) for m in ERROR.finditer(out.stdout)}
    if out.returncode != 0 and not found:
        raise RuntimeError(f"the build failed without a compiler error: {out.stdout[-500:]}")
    return [{"file": f, "code": c, "message": m} for f, c, m in sorted(found)]


def ships(rows):
    return (sum(r["fixed"] for r in rows) >= NEED_FIXED and not any(r["worse"] for r in rows)
            and statistics.median(r["seconds"] for r in rows) < MAX_MEDIAN_S)


def run_seed(seed, gateway_url, key):
    with tempfile.TemporaryDirectory(prefix="fix-bench") as folder:
        project = fresh_copy(folder)
        apply_seed(project, seed)
        before = build_errors(project)
        started = time.time()
        try:
            out = subprocess.run(["dotnet", DRIVER, gateway_url, key, project, os.path.join(folder, "trust.json")], capture_output=True, text=True,
                                 encoding="utf-8", errors="replace", timeout=DRIVER_TIMEOUT_S)
            output = out.stdout + out.stderr
        except subprocess.TimeoutExpired as e:
            output = f"timed out: {e}"
        seconds = round(time.time() - started, 1)
        after = build_errors(project)
        clean = _read(os.path.join(SEED, seed["file"]))
        now = _read(os.path.join(project, seed["file"]))
        broken = clean.replace(seed["search"], seed["replace"])
        return {"id": seed["id"], "code": seed["code"], "fixed": not after, "worse": len(after) > len(before), "changed": now != broken,
                "restored": now == clean, "errors_before": len(before), "errors_after": len(after), "seconds": seconds,
                "file_after": now, "output": output[-3000:]}


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--label", required=True)
    p.add_argument("--model", required=True)
    p.add_argument("--llama", required=True)
    p.add_argument("--threads", type=int, default=4)
    p.add_argument("--law-book")
    a = p.parse_args()
    a.device, a.gpu_layers, a.reasoning, a.router_preset = "none", 0, "auto", None
    build_gateway()
    subprocess.run(["dotnet", "build", DRIVER_PROJECT, "-c", "Release", "--disable-build-servers", "-v", "q", "-nologo"], check=True)
    os.makedirs(RESULTS, exist_ok=True)
    rows, processes, folder = [], [], tempfile.mkdtemp(prefix="jazari-fix-")
    result_file = os.path.join(RESULTS, f"{a.label}.json")
    try:
        processes.append(start_server(server_args(a), PORT))
        env = gateway_env(folder, PORT, GATEWAY_PORT, a.law_book)
        key = add_key(env, folder)
        processes.append(launch(["dotnet", GATEWAY_DLL], GATEWAY_PORT, env=env, cwd=folder))
        wait_until_ready(processes[0])
        wait_for_gateway(processes[1], key)
        for seed in seeds():
            row = run_seed(seed, f"http://127.0.0.1:{GATEWAY_PORT}", key)
            rows.append(row)
            print(f"{'fixed' if row['fixed'] else 'WORSE' if row['worse'] else 'no   '} {row['seconds']:6.0f}s {'same-as-clean' if row['restored'] else '             '} {row['code']} {row['id']}", flush=True)
            with open(result_file, "w", encoding="utf-8") as f:
                json.dump({"label": a.label, "law_book": a.law_book, **stamp(a.llama, a.model), "answers": rows}, f, indent=1)
    except ServerDidNotStart as e:
        sys.exit(f"{a.label}: {e}")
    finally:
        stop(processes)
        shutil.rmtree(folder, ignore_errors=True)
    print(f"{a.label}: fixed {sum(r['fixed'] for r in rows)} of {len(rows)}, worse {sum(r['worse'] for r in rows)}, "
          f"median {statistics.median(r['seconds'] for r in rows):.0f} s: {'SHIPS' if ships(rows) else 'DOES NOT SHIP'}")


if __name__ == "__main__":
    main()
