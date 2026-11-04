"""Temporary servers for the tests: a llama-server and the real gateway, each on its own port with its own data folder,
so the installed Jazari is never touched. Used by run_eval.py and recall.py."""
import hashlib, json, os, re, socket, subprocess, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.join(HERE, "..")
GATEWAY_PROJECT = os.path.join(REPO, "src", "MyOwnLLM.Gateway")
GATEWAY_DLL = os.path.join(GATEWAY_PROJECT, "bin", "Release", "net10.0", "MyOwnLLM.Gateway.dll")
LLAMA_PORT, GATEWAY_PORT = 8099, 5099


class PortInUse(Exception):
    pass


class ServerDidNotStart(Exception):
    pass


def start_server(args, port, log=None, **popen):
    """`popen` goes to subprocess.Popen: the gateway starts with its own settings and folder. `log`: a file the server's
    own output is added to, so what it did (checkpoints, cached prompts) can be read afterwards; without one it's dropped."""
    with socket.socket() as probe:
        probe.settimeout(1)
        if probe.connect_ex(("127.0.0.1", port)) == 0:
            raise PortInUse(f"port {port} is in use: stop whatever is on it, then run this again")
    if not log:
        return subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **popen)
    with open(log, "ab") as out:  # the server keeps its own handle to it
        return subprocess.Popen(args, stdout=out, stderr=subprocess.STDOUT, **popen)


def launch(args, port, log=None, **popen):
    """start_server, with a missing or blocked program reported like a server that didn't start."""
    try:
        return start_server(args, port, log, **popen)
    except OSError as e:
        raise ServerDidNotStart(f"couldn't start {args[0]}: {e}")


def build_gateway():
    if subprocess.run(["dotnet", "build", GATEWAY_PROJECT, "-c", "Release", "--disable-build-servers"]).returncode != 0:
        raise ServerDidNotStart("the gateway didn't build")


def gateway_env(data_dir, llama_port, port, law_book=None):
    """The gateway's settings, pointed at the temporary server and folder. law_book: the law book file it searches, if any."""
    env = {**os.environ, "Gateway__LlamaUrl": f"http://127.0.0.1:{llama_port}", "Gateway__Port": str(port), "Gateway__DataDir": data_dir}
    if law_book:
        env["Gateway__LawBookFile"] = os.path.abspath(law_book)
    return env


def add_key(env, folder):
    """A key in the temporary folder's own keys.json, from the first line add-key prints."""
    out = subprocess.run(["dotnet", GATEWAY_DLL, "add-key"], env=env, cwd=folder, capture_output=True, text=True)
    if out.returncode != 0 or not out.stdout.strip():
        raise ServerDidNotStart(f"the gateway's add-key failed: {out.stderr.strip() or out.returncode}")
    return out.stdout.splitlines()[0].strip()


def search_law(env, folder, question):
    """What the gateway's law book sends for a question: [{"title", "url", "text"}], or [] for nothing."""
    out = subprocess.run(["dotnet", GATEWAY_DLL, "search-law", question], env=env, cwd=folder, capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        raise ServerDidNotStart(f"the gateway's search-law failed: {out.stderr.strip() or out.returncode}")
    return json.loads(out.stdout.strip().splitlines()[-1])


def wait_for_gateway(gateway, key, seconds=60, port=GATEWAY_PORT):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if gateway.poll() is not None:
            raise ServerDidNotStart(f"the gateway stopped with exit code {gateway.returncode}")
        try:
            request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/info", headers={"Authorization": f"Bearer {key}"})
            urllib.request.urlopen(request, timeout=2).close()
            return
        except urllib.error.HTTPError as e:
            raise ServerDidNotStart(f"the gateway answered {e.code} to its own key")
        except (urllib.error.URLError, OSError):
            time.sleep(1)
    raise ServerDidNotStart(f"the gateway wasn't ready after {seconds} s")


def gateway_chat(key, messages, max_tokens, port=GATEWAY_PORT, context=None, deep=False):
    """What the CLI sends, without a project folder: no project note goes into the prompt."""
    body = {"messages": messages, "max_tokens": max_tokens, "stream": False}
    if context:
        body["context"] = context
    if deep:
        body["deep"] = True
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", json.dumps(body).encode(),
                                     {"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(request, timeout=1800) as response:
        return json.load(response)["choices"][0]["message"]["content"] or ""


def stamp(llama, model):
    """What a run measured, saved with its result, as Aider's harness stamps each run: the code (and whether it had
    uncommitted changes), the llama.cpp build and the model file. Results from different setups can then never be
    compared unawares. The model's fingerprint is its size and its first and last MiB, so a 4 GB file takes no time."""
    def git(*args):
        try:
            out = subprocess.run(["git", "-C", REPO, *args], capture_output=True, text=True, timeout=30)
            return out.stdout.strip() if out.returncode == 0 else None
        except (OSError, subprocess.TimeoutExpired):
            return None
    try:
        version = subprocess.run([os.path.abspath(llama), "--version"], capture_output=True, text=True, timeout=60)
        build = re.search(r"build (\d+)", version.stdout + version.stderr)
    except (OSError, subprocess.TimeoutExpired):
        build = None
    fingerprint = None
    try:
        size = os.path.getsize(model)
        digest = hashlib.sha256(str(size).encode())
        with open(model, "rb") as f:
            digest.update(f.read(1 << 20))
            f.seek(max(size - (1 << 20), 0))
            digest.update(f.read(1 << 20))
        fingerprint = f"{size}:{digest.hexdigest()[:16]}"
    except OSError:
        pass
    status = git("status", "--porcelain")
    return {"git_commit": git("rev-parse", "HEAD"), "git_dirty": None if status is None else status != "",
            "llama_build": build.group(1) if build else None, "model_fingerprint": fingerprint}


def stop(processes):
    for p in processes:
        if p.poll() is None:
            p.kill()
        p.wait()
    processes.clear()
