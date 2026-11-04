"""The repo benchmark: blank out a method in one of Eni-M's own repos, ask Jazari to write it, then build and run its tests.

    py -I eval/repo_bench.py select --repo C:/path --name educore --tests EduCore.Tests --limit 20   # adds tasks to tasks.local.json
    py -I eval/repo_bench.py select --repo C:/path --name wareflow --limit 10                         # no tests: scored on the build
    py -I eval/repo_bench.py validate --keep 20                                                              # drops tasks whose tests can't tell
    py -I eval/repo_bench.py run --label tiny-regress --model spike/models/x.gguf --llama spike/bin/llama-server.exe [--deep]

Everything happens in a throwaway clone, so the repo's own folder, its .git and its uncommitted work are never touched. The repos are
private: eval/repo/tasks.local.json and eval/results/repo/ are git-ignored, and this script prints only counts and timings.
"""
import argparse, difflib, json, os, re, shutil, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gateway import (GATEWAY_DLL, GATEWAY_PORT, LLAMA_PORT, add_key, build_gateway, gateway_chat, gateway_env, launch, stamp, start_server, stop, wait_for_gateway)
from score import held_out

TASKS = os.path.join(HERE, "repo", "tasks.local.json")
RESULTS = os.path.join(HERE, "results", "repo")
BLANK_PROJECT = os.path.join(HERE, "repo", "Blank")
BLANK = os.path.join(BLANK_PROJECT, "bin", "Release", "net10.0", "Blank.dll")
MIN_LINES, MAX_LINES = 5, 60  # one line says nothing, and a long method won't fit the answer's token cap
MAX_TOKENS = 1024  # the gateway's MaxChatTokens
COMMAND_TIMEOUT_S = 900


def passed(task, compiled, tests_passed):
    return bool(tests_passed) if task["test_filter"] else bool(compiled)


def task_is_valid(tests_pass_blanked, tests_pass_original):
    """Its tests have to pass on the real code and notice when it's gone, or a pass says nothing."""
    return tests_pass_original and not tests_pass_blanked


def closeness(answer, original):
    """How alike two pieces of code are, 0 to 1, whatever their whitespace."""
    tidy = lambda code: " ".join(code.split())
    return round(difflib.SequenceMatcher(None, tidy(answer), tidy(original), autojunk=False).ratio(), 3)


def first_code_block(answer):
    found = re.search(r"```[^\n]*\n(.*?)\n?```", answer or "", re.S)
    return found.group(1) if found else None


def candidates_to_tasks(name, repo, commit, found, test_files, limit):
    """Methods of a sensible size. With test_files ({class name: its text}) only those a test names, filtered to that test class;
    with None, every one, scored on the build alone."""
    tasks = []
    for c in found:
        if not MIN_LINES <= c["lines"] <= MAX_LINES:
            continue
        test_class = None
        if test_files is not None:
            test_class = next((t for t, text in sorted(test_files.items()) if re.search(rf"\b{re.escape(c['method'])}\b", text)), None)
            if test_class is None:
                continue
        tasks.append({"id": f"{name}-{c['class']}-{c['method']}", "repo": repo, "commit": commit, "file": c["file"], "method": c["method"],
                      "request": f"Write the method {c['method']} in full. Answer with the complete method in one C# code block.",
                      "test_filter": f"FullyQualifiedName~{test_class}" if test_class else None, "category": "repo-tested" if test_class else "repo-build"})
        if len(tasks) == limit:
            break
    return tasks


def run(args, cwd=None, timeout=COMMAND_TIMEOUT_S):
    """Exit code and output of a command, which stays in memory: it can hold private code."""
    try:
        out = subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return out.returncode, out.stdout
    except subprocess.TimeoutExpired:
        return -1, ""


def test_command(project, project_text, test_filter):
    """.NET 10's dotnet test refuses an xunit.v3 project that opts into Microsoft.Testing.Platform, so such a project's
    test app runs itself, with xunit's own VSTest-filter option."""
    # ponytail: an xunit.v3 project with UseMicrosoftTestingPlatformRunner speaks MTP and would need --filter-class instead
    if re.search(r'Include="xunit\.v3"', project_text):
        return ["dotnet", "run", "--project", project, "--no-build", "--", "-filterVSTest", test_filter]
    return ["dotnet", "test", project, "--no-build", "--nologo", "--filter", test_filter]


def git(folder, *args):
    return run(["git", "-C", folder, "-c", "core.fsmonitor=false", *args])


class Worktree:
    """A throwaway clone of a repo at a commit. Tasks in the same repo share it and put their file back after."""

    def __init__(self, repo, commit):
        self.path = tempfile.mkdtemp(prefix="jazari-bench-")
        self.restored = set()
        code, _ = run(["git", "clone", "--quiet", "--no-hardlinks", "--no-checkout", repo, self.path])
        if code == 0:
            code, _ = git(self.path, "checkout", "--quiet", "--detach", commit)
        if code != 0:
            shutil.rmtree(self.path, ignore_errors=True)
            raise RuntimeError(f"couldn't clone the repo (git exit {code})")

    def build(self, project):
        if project not in self.restored:
            run(["dotnet", "restore", project, "--disable-build-servers"], cwd=self.path)
            self.restored.add(project)
        return run(["dotnet", "build", project, "--no-restore", "--disable-build-servers", "-v", "q", "-nologo"], cwd=self.path)[0] == 0

    def test(self, project, test_filter):
        with open(os.path.join(self.path, project), encoding="utf-8", errors="replace") as f:
            return run(test_command(project, f.read(), test_filter), cwd=self.path)[0] == 0

    def reset(self, file):
        git(self.path, "checkout", "--", file)

    def close(self):
        shutil.rmtree(self.path, ignore_errors=True)


def worktrees(tasks):
    """{(repo, commit): Worktree}, one for each."""
    return {key: Worktree(*key) for key in sorted({(t["repo"], t["commit"]) for t in tasks})}


def load_tasks():
    with open(TASKS, encoding="utf-8") as f:
        return json.load(f)


def save_tasks(tasks):
    os.makedirs(os.path.dirname(TASKS), exist_ok=True)
    with open(TASKS, "w", encoding="utf-8") as f:
        json.dump(tasks, f, indent=1)


def build_blank():
    if subprocess.run(["dotnet", "build", BLANK_PROJECT, "-c", "Release", "--disable-build-servers", "-v", "q", "-nologo"], stdout=subprocess.DEVNULL).returncode != 0:
        sys.exit("eval/repo/Blank didn't build")


def blank(worktree, task):
    """Blanks the task's method in the worktree and returns the original, or None if it can't be found alone."""
    code, original = run(["dotnet", BLANK, "blank", os.path.join(worktree.path, task["file"]), task["method"]])
    return original if code == 0 else None


def nearest_project(repo, file):
    """The project file nearest above a file, relative to the repo; a folder with several projects can't be built by name alone."""
    folder = os.path.dirname(file)
    while True:
        projects = [n for n in os.listdir(os.path.join(repo, folder) if folder else repo) if n.endswith(".csproj")]
        if projects:
            return os.path.join(folder, projects[0])
        if not folder:
            return None
        folder = os.path.dirname(folder)


def select(a):
    build_blank()
    code, commit = git(a.repo, "rev-parse", "HEAD")
    if code != 0:
        sys.exit("that folder isn't a git repository")
    commit = commit.strip()
    code, listed = run(["dotnet", BLANK, "list", a.repo])
    if code != 0:
        sys.exit("couldn't list the repo's methods")
    found = [json.loads(line) for line in listed.splitlines() if line.strip()]
    test_root = os.path.join(a.repo, a.tests) if a.tests else None
    in_tests = lambda c: test_root and os.path.join(a.repo, c["file"]).startswith(test_root)
    test_files = None
    if test_root:
        test_files = {}
        for folder, _, names in os.walk(test_root):
            if os.path.basename(folder) in ("bin", "obj"):
                continue
            for name in names:
                if name.endswith("Tests.cs"):
                    with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as f:
                        test_files[name[:-3]] = f.read()
    test_project = None
    if test_root:
        projects = [n for n in os.listdir(test_root) if n.endswith(".csproj")]
        if not projects:
            sys.exit("no project file in that test folder")
        test_project = os.path.relpath(os.path.join(test_root, projects[0]), a.repo)
    tasks = candidates_to_tasks(a.name, a.repo, commit, [c for c in found if not in_tests(c)], test_files, a.limit * 4 if test_root else a.limit)
    for task in tasks:
        task["project"] = test_project or nearest_project(a.repo, task["file"])
    kept = [t for t in (load_tasks() if os.path.exists(TASKS) else []) if not t["id"].startswith(a.name + "-")]
    save_tasks(kept + tasks)
    print(f"{a.name}: {len(found)} methods, {len(tasks)} tasks chosen, {len(kept) + len(tasks)} in the file")


def validate(a):
    """Keeps tasks whose tests pass on the real code and fail without it, up to --keep for each repo. Build-only tasks need a build."""
    build_blank()
    tasks = load_tasks()
    trees = worktrees(tasks)
    valid, kept = [], {}
    try:
        for task in tasks:
            tree = trees[(task["repo"], task["commit"])]
            if kept.get(task["repo"], 0) >= a.keep or not task.get("project") or not tree.build(task["project"]):
                continue
            ok = True
            if task["test_filter"]:
                original_ok = tree.test(task["project"], task["test_filter"])
                ok = original_ok and blank(tree, task) is not None and tree.build(task["project"]) and task_is_valid(tree.test(task["project"], task["test_filter"]), original_ok)
                tree.reset(task["file"])
            if ok:
                valid.append(task)
                kept[task["repo"]] = kept.get(task["repo"], 0) + 1
            print(f"{len(valid)} valid so far", flush=True)
    finally:
        for tree in trees.values():
            tree.close()
    save_tasks(valid)
    print(f"{len(valid)} of {len(tasks)} tasks are valid, {sum(held_out(t['id']) for t in valid)} of them held out")


def attempt(task, tree, ask):
    """One task, in the worktree: the question, its answer in the file, the build, the tests."""
    path = os.path.join(tree.path, task["file"])
    original = blank(tree, task)
    if original is None:
        return None
    with open(path, encoding="utf-8", errors="replace") as f:
        context = f"--- {os.path.basename(task['file'])} ---\n{f.read()}\n"
    started = time.time()
    try:
        answer = ask(task["request"], context)
    except OSError as e:  # the model server went away: counts as a miss, not a crash after an hour's run
        answer, _ = "", e
    seconds = round(time.time() - started, 1)
    code = first_code_block(answer)
    compiled, tests_passed = False, None
    if code is not None:
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
            f.write(code)
        try:
            put = run(["dotnet", BLANK, "put", path, task["method"], f.name])[0]
        finally:
            os.unlink(f.name)
        compiled = put == 0 and tree.build(task["project"])
        if compiled and task["test_filter"]:
            tests_passed = tree.test(task["project"], task["test_filter"])
    tree.reset(task["file"])
    return {"id": task["id"], "category": task["category"], "passed": passed(task, compiled, tests_passed), "compiled": compiled, "tests_passed": tests_passed,
            "closeness": closeness(code or "", original), "seconds": seconds, "answer": answer}


def run_bench(a):
    build_blank()
    build_gateway()
    tasks = load_tasks()
    folder = tempfile.mkdtemp(prefix="jazari-bench-gateway-")
    env = gateway_env(folder, LLAMA_PORT, GATEWAY_PORT)
    if a.router_preset:
        server_args = [a.llama, "--models-preset", a.router_preset, "--models-max", "1", "--no-webui", "--host", "127.0.0.1", "--port", str(LLAMA_PORT)]
    else:
        server_args = [a.llama, "-m", a.model, "-dev", "none", "-ngl", "0", "-t", str(a.threads), "--poll", "0", "-fa", "on", "-c", "16384", "--cache-ram", "1024",
                       "-ctxcp", "8", "-np", "1", "--cache-reuse", "256", "--no-webui", "--host", "127.0.0.1", "--port", str(LLAMA_PORT)]
    server = start_server(server_args, LLAMA_PORT)
    processes = [server]
    trees = {}
    answers = []
    try:
        key = add_key(env, folder)
        gateway = launch(["dotnet", GATEWAY_DLL], GATEWAY_PORT, env=env, cwd=folder)
        processes.append(gateway)
        wait_for_gateway(gateway, key, seconds=300)
        ask = lambda question, context: gateway_chat(key, [{"role": "user", "content": question}], MAX_TOKENS, context=context, deep=a.deep)
        trees = worktrees(tasks)
        for i, task in enumerate(tasks, 1):
            result = attempt(task, trees[(task["repo"], task["commit"])], ask)
            if result:
                answers.append(result)
            print(f"{i}/{len(tasks)}: {sum(x['passed'] for x in answers)} passed so far", flush=True)
    finally:
        stop(processes)
        for tree in trees.values():
            tree.close()
        shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(RESULTS, exist_ok=True)
    times = [x["seconds"] for x in answers] or [0]
    result = {"label": a.label, "model": os.path.basename(a.model or a.router_preset), "deep": a.deep, **stamp(a.llama, a.model or a.router_preset),
              "score": sum(x["passed"] for x in answers), "total": len(answers), "compiled": sum(x["compiled"] for x in answers),
              "closeness": round(statistics.mean(x["closeness"] for x in answers), 3) if answers else 0, "median_s": statistics.median(times), "answers": answers}
    with open(os.path.join(RESULTS, f"{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f'{a.label}: {result["score"]}/{result["total"]} passed, {result["compiled"]} compiled, closeness {result["closeness"]}, median {result["median_s"]} s')


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    s = commands.add_parser("select")
    s.add_argument("--repo", required=True)
    s.add_argument("--name", required=True)
    s.add_argument("--tests", help="the test project's folder inside the repo; none means build only")
    s.add_argument("--limit", type=int, default=20)
    v = commands.add_parser("validate")
    v.add_argument("--keep", type=int, default=20, help="valid tasks to keep for each repo")
    r = commands.add_parser("run")
    r.add_argument("--label", required=True)
    r.add_argument("--model")
    r.add_argument("--router-preset")
    r.add_argument("--threads", type=int, default=4)
    r.add_argument("--llama", default=os.path.join(HERE, "..", "spike", "bin", "llama-server.exe"))
    r.add_argument("--deep", action="store_true")
    a = parser.parse_args()
    {"select": select, "validate": validate, "run": run_bench}[a.command](a)


if __name__ == "__main__":
    main()
