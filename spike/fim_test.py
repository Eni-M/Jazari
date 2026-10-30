# Throwaway spike: fill-in-the-middle check against a running llama-server.
# Usage: python fim_test.py [port]
import json, sys, time, urllib.request

PORT = sys.argv[1] if len(sys.argv) > 1 else "8080"

# (prefix, suffix, test) - the model writes the missing body; test must pass after exec.
CASES = [
    ("def is_palindrome(s):\n    s = ''.join(c.lower() for c in s if c.isalnum())\n",
     "\n\nassert is_palindrome('A man, a plan, a canal: Panama')\n",
     "assert is_palindrome('Racecar') and not is_palindrome('hello')"),
    ("def fizzbuzz(n):\n    out = []\n    for i in range(1, n + 1):\n",
     "    return out\n",
     "assert fizzbuzz(15)[-1] == 'FizzBuzz' and fizzbuzz(5) == ['1','2','Fizz','4','Buzz']"),
    ("def word_count(text):\n    \"\"\"Return dict of lowercase word -> count.\"\"\"\n",
     "\n",
     "assert word_count('a b A c') == {'a': 2, 'b': 1, 'c': 1}"),
    ("def flatten(nested):\n    \"\"\"Flatten arbitrarily nested lists.\"\"\"\n    result = []\n",
     "    return result\n",
     "assert flatten([1, [2, [3, 4]], 5]) == [1, 2, 3, 4, 5]"),
    ("import re\n\ndef is_valid_email(addr):\n",
     "\n",
     "assert is_valid_email('a.b@c.com') and not is_valid_email('nope@') and not is_valid_email('x y@z.com')"),
    ("def binary_search(arr, target):\n    lo, hi = 0, len(arr) - 1\n    while lo <= hi:\n",
     "    return -1\n",
     "assert binary_search([1,3,5,7,9], 7) == 3 and binary_search([1,3,5], 4) == -1"),
]


def infill(prefix, suffix):
    body = json.dumps({"input_prefix": prefix, "input_suffix": suffix,
                       "n_predict": 160, "temperature": 0}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/infill", body,
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)


passed, total_s = 0, 0.0
for prefix, suffix, test in CASES:
    t = time.time()
    out = infill(prefix, suffix)
    total_s += time.time() - t
    mid = out["content"]
    if not mid.endswith("\n"):  # editors join the hole to the next line; do the same for every model
        mid += "\n"
    code = prefix + mid + suffix
    try:
        exec(code + "\n" + test, {})
        ok = True
    except Exception as e:
        ok = False
        err = f"{type(e).__name__}: {e}"
    passed += ok
    name = prefix.split("def ")[1].split("(")[0]
    print(f"{'PASS' if ok else 'FAIL'}  {name:16} {time.time() - t:5.1f}s")
    if not ok:
        print("   ---", err, "\n", repr(out["content"]))

print(f"\n{passed}/{len(CASES)} passed, avg {total_s / len(CASES):.1f}s per completion")
