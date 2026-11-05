"""Builds the law book: a read-only SQLite FTS5 file of Microsoft's .NET docs at pinned commits.

    py -I lawbook/build.py --out lawbook/out/law-book.db [--work <folder for the clones>]

Each chunk is one piece of a page section, at most 1,000 characters, with the page and section as its title and the
learn.microsoft.com address it came from. Code the docs pull from sample files is inlined, so a passage shows the code
it talks about. Only the latest version's text is kept where a page covers several. Standard library only.
"""
import argparse, json, os, re, sqlite3, subprocess, sys, tempfile, textwrap

HERE = os.path.dirname(os.path.abspath(__file__))
MAX_CHUNK = 1000
SKIPPED_FOLDERS = {"whats-new", "what-is-new", "breaking-changes", "release-notes", "includes", "snippets", "samples", "media"}
# Jazari answers in C#; the same sample in these languages only spends the passage budget.
DROPPED_LANGUAGES = {"vb", "vbnet", "visualbasic", "fsharp", "f#", "cpp", "c++", "cppcli"}
SITE = "https://learn.microsoft.com/"
URL_ROOTS = {
    "dotnet/docs": ("docs/", SITE + "dotnet/"),
    "dotnet/AspNetCore.Docs": ("aspnetcore/", SITE + "aspnet/core/"),
    "dotnet/EntityFramework.Docs": ("entity-framework/", SITE + "ef/"),
}
FENCE = re.compile(r"^\s*(```|~~~)")


# --- pages ---------------------------------------------------------------------------------------------------------

def strip_front_matter(md):
    """(title, body): the YAML front matter's title, or the first heading's, and the page without the front matter."""
    md = md.replace("\r\n", "\n").lstrip("﻿")
    title = ""
    m = re.match(r"---\n(.*?)\n---[ \t]*\n?", md, re.S)
    if m:
        t = re.search(r"^title:\s*(.*?)\s*$", m.group(1), re.M)
        if t:
            title = t.group(1).strip().strip("'\"").strip()
        md = md[m.end():]
    if not title:
        h = re.search(r"^#\s+(.+?)\s*#*\s*$", md, re.M)
        title = h.group(1).strip() if h else ""
    return title, md


_MONIKER = re.compile(r'^\s*:::\s*moniker\s+range\s*=\s*"([^"]*)"\s*:{0,3}\s*$')
_MONIKER_END = re.compile(r"^\s*:::\s*moniker-end\s*:{0,3}\s*$")
_VERSIONED = re.compile(r"(>=|<=|>|<|=)?\s*[A-Za-z][\w.]*?-(\d+(?:\.\d+)*)")


def _version(text):
    return tuple(int(p) for p in text.split("."))


def _in_range(expr, latest):
    for alternative in expr.split("||"):
        tests = _VERSIONED.findall(alternative)
        if tests and all(_compare(op or "=", _version(v), latest) for op, v in tests):
            return True
    return False


def _compare(op, version, latest):
    width = max(len(version), len(latest))
    a, b = latest + (0,) * (width - len(latest)), version + (0,) * (width - len(version))
    return {">=": a >= b, "<=": a <= b, ">": a > b, "<": a < b, "=": a == b}[op]


def keep_latest_version(md):
    """Keeps text outside version blocks, and the blocks whose range holds the newest version the page names."""
    lines = md.split("\n")
    ranges = [m.group(1) for m in map(_MONIKER.match, lines) if m]
    if not ranges:
        return md
    versions = [_version(v) for r in ranges for _, v in _VERSIONED.findall(r)]
    latest = max(versions) if versions else ()
    out, keep = [], [True]
    for line in lines:
        m = _MONIKER.match(line)
        if m:
            keep.append(keep[-1] and bool(latest) and _in_range(m.group(1), latest))
        elif _MONIKER_END.match(line):
            if len(keep) > 1:
                keep.pop()
        elif keep[-1]:
            out.append(line)
    return "\n".join(out)


# --- code from sample files ----------------------------------------------------------------------------------------

_NEW_CODE = re.compile(r":::code\b(.*?):::", re.S)
_OLD_CODE = re.compile(r"\[!code-([\w#+-]+)\[[^\]]*\]\(([^)\s]+)\)\]")
_ATTR = re.compile(r'([\w-]+)\s*=\s*"([^"]*)"')
_REGION_LINE = re.compile(r"^\s*(?:(?://|#|'|--|;|REM\b|<!--|/\*|@\*)\s*</?[\w.-]+>.*|#\s*(?:end)?region\b.*)$", re.I)


def _case_insensitive(path):
    """The docs are built on a case-insensitive file system, so `Program.cs` may name `program.cs`. Same result on any OS."""
    if os.path.exists(path):
        return path
    parent, name = os.path.split(path)
    if not name or parent == path:
        return path
    parent = _case_insensitive(parent)
    try:
        match = next((n for n in os.listdir(parent) if n.lower() == name.lower()), name)
    except OSError:
        match = name
    return os.path.join(parent, match)


def _read(path, repo_root):
    real, root = os.path.realpath(_case_insensitive(os.path.normpath(path))), os.path.realpath(repo_root)
    if os.path.commonpath([real, root]) != root or not os.path.isfile(real):
        return None
    try:
        with open(real, encoding="utf8", errors="replace") as f:
            return f.read().replace("\r\n", "\n").lstrip("﻿")
    except OSError:
        return None


def _region(code, name):
    """The lines between `// <name>` and `// </name>`, or `#region name` and its `#endregion`; all such regions."""
    lines, found, i = code.split("\n"), [], 0
    tag = r"(?:Snippet)?" + re.escape(name) + r"\s*>"  # id X also names <SnippetX>, as on Learn
    tag_start = re.compile(r"(?://|#|'|--|;|REM\b|<!--|/\*|@\*)\s*<\s*" + tag, re.I)
    tag_end = re.compile(r"(?://|#|'|--|;|REM\b|<!--|/\*|@\*)\s*<\s*/\s*" + tag, re.I)
    region_start = re.compile(r"^\s*#\s*region\s+\"?" + re.escape(name) + r"\"?\s*$", re.I)
    while i < len(lines):
        if tag_start.search(lines[i]):
            j = i + 1
            while j < len(lines) and not tag_end.search(lines[j]):
                j += 1
            found.extend(lines[i + 1:j]); i = j
        elif region_start.match(lines[i]):
            depth, j = 1, i + 1
            while j < len(lines):
                if re.match(r"^\s*#\s*region\b", lines[j], re.I): depth += 1
                elif re.match(r"^\s*#\s*end\s*region\b", lines[j], re.I):
                    depth -= 1
                    if depth == 0: break
                j += 1
            found.extend(lines[i + 1:j]); i = j
        i += 1
    return "\n".join(found) if found else None


def _lines(code, spec):
    lines, picked = code.split("\n"), []
    for part in spec.split(","):
        a, _, b = part.strip().partition("-")
        try:
            start, end = int(a), int(b) if b else int(a)
        except ValueError:
            return None
        picked.extend(lines[start - 1:end])
    return "\n".join(picked) if picked else None


def _snippet(lang, source, name, line_range, md_path, repo_root, tilde_root):
    lang = (lang or "").lower()
    if lang in DROPPED_LANGUAGES:
        return ""
    source = source.split("#", 1)[0]
    path = os.path.join(tilde_root, source[2:]) if source.startswith("~/") else os.path.join(os.path.dirname(md_path), source)
    code = _read(path, repo_root)
    if code is None:
        return ""
    if name:
        code = _region(code, name)
    elif line_range:
        code = _lines(code, line_range)
    if code is None:
        return ""
    code = "\n".join(l for l in code.split("\n") if not _REGION_LINE.match(l))
    code = textwrap.dedent(code).strip("\n")
    return f"\n```{lang}\n{code}\n```\n" if code.strip() else ""


def inline_snippets(md, md_path, repo_root, tilde_root):
    """Replaces `:::code` and `[!code-x[](...)]` with the code they point at. A missing file or region drops the directive."""
    def new_style(m):
        a = dict(_ATTR.findall(m.group(1)))
        return _snippet(a.get("language"), a.get("source", ""), a.get("id"), a.get("range"), md_path, repo_root, tilde_root)

    def old_style(m):
        source, _, query = m.group(2).partition("?")
        q = dict(p.partition("=")[::2] for p in query.split("&") if p)
        return _snippet(m.group(1), source, q.get("name"), q.get("range"), md_path, repo_root, tilde_root)

    return _OLD_CODE.sub(old_style, _NEW_CODE.sub(new_style, md))


# --- clean text ----------------------------------------------------------------------------------------------------

def _xref(target):
    target = re.sub(r"[?#].*$", "", target).replace("%2A", "").replace("*", "")
    target = re.sub(r"%60+\d+|`+\d+", "", target)
    parts = [p for p in re.split(r"[./]", target) if p]
    return ".".join(parts[-2:]) if parts else ""


def _clean_text(text):
    text = re.sub(r"\[!INCLUDE\s*\[[^\]]*\]\([^)]*\)\]", "", text, flags=re.I)
    text = re.sub(r"^\s*>?\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION|div[^\]]*)\]\s*$", "", text, flags=re.M | re.I)
    text = re.sub(r"\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*", "", text, flags=re.I)
    text = re.sub(r"!\[[^\]]*\]\((?:[^()]|\([^)]*\))*\)", "", text)
    text = re.sub(r":::no-loc\s+text=\"([^\"]*)\":::", r"\1", text)
    text = re.sub(r"<xref:\s*([^>\s]+)\s*>", lambda m: _xref(m.group(1)), text)
    text = re.sub(r"\[((?:[^\[\]]|\[[^\]]*\])+)\]\((?:[^()]|\([^)]*\))*\)", r"\1", text)
    text = re.sub(r"\[([^\]]+)\]\[[^\]]*\]", r"\1", text)
    text = re.sub(r"^\s*:::.*$", "", text, flags=re.M)
    text = re.sub(r"<a\s[^>]*>\s*</a>|<br\s*/?>", "", text, flags=re.I)
    text = re.sub(r"^>[ \t]?", "", text, flags=re.M)
    text = re.sub(r"[ \t]+$", "", text, flags=re.M)
    return text


def _segments(md):
    """(language, text) pieces: fenced code blocks whole with their language, and the text between them with None."""
    out, buf, lang = [], [], None
    for line in md.split("\n"):
        if FENCE.match(line):
            if lang is None:
                if buf: out.append((None, "\n".join(buf)))
                info = line.strip()[3:].strip()
                buf, lang = [line], (info.split()[0].lower() if info else "")
            else:
                out.append((lang, "\n".join(buf + [line])))
                buf, lang = [], None
            continue
        buf.append(line)
    if buf:
        out.append((lang, "\n".join(buf) + ("\n```" if lang is not None else "")))
    return out


def clean(md):
    """Text a reader would see: links keep their words, includes, images, comments and directives go, code stays."""
    md = re.sub(r"<!--.*?(-->|$)", "", md, flags=re.S)  # before the code split: a comment can hide a whole block
    pieces = []
    for lang, text in _segments(md):
        if lang is None:
            pieces.append(_clean_text(text))
        elif lang not in DROPPED_LANGUAGES:
            pieces.append(textwrap.dedent(text))
    return re.sub(r"\n{3,}", "\n\n", "\n".join(pieces)).strip() + "\n"


# --- chunks --------------------------------------------------------------------------------------------------------

_HEADING = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$")


def slug(heading):
    s = re.sub(r"[^\w\s-]", "", heading.lower())
    return re.sub(r"\s+", "-", s.strip())


def sections(title, body):
    """(heading title, anchor, text) per heading. A heading inside code isn't one."""
    out, heading, anchor, buf, in_code = [], title, "", [], False
    for line in body.split("\n"):
        if FENCE.match(line):
            in_code = not in_code
        m = None if in_code else _HEADING.match(line)
        if m:
            out.append((heading, anchor, "\n".join(buf).strip()))
            text = re.sub(r"[*`_]", "", m.group(2)).strip()
            if len(m.group(1)) == 1:
                heading, anchor = title, ""
            else:
                heading, anchor = f"{title}: {text}", slug(text)
            buf = []
        else:
            buf.append(line)
    out.append((heading, anchor, "\n".join(buf).strip()))
    return [s for s in out if s[2]]


def _units(text):
    """Paragraphs and whole code blocks, in order."""
    units, para, code, in_code = [], [], [], False
    for line in text.split("\n"):
        if FENCE.match(line):
            if not in_code:
                if para: units.append("\n".join(para)); para = []
                code, in_code = [line], True
            else:
                code.append(line); units.append("\n".join(code)); code, in_code = [], False
        elif in_code:
            code.append(line)
        elif line.strip():
            para.append(line)
        elif para:
            units.append("\n".join(para)); para = []
    if in_code: units.append("\n".join(code + ["```"]))
    if para: units.append("\n".join(para))
    return units


def _split_unit(unit):
    """A unit over the limit: code is cut at line breaks with each part closed and reopened; text at lines, then spaces."""
    lines = unit.split("\n")
    if FENCE.match(lines[0]):
        head, tail, body = lines[0].strip(), "```", lines[1:-1]
        room, parts, cur = MAX_CHUNK - len(head) - len(tail) - 2, [], []
        for line in body:
            line = line[:room]
            if cur and len("\n".join(cur + [line])) > room:
                parts.append(cur); cur = []
            cur.append(line)
        if cur: parts.append(cur)
        return [head + "\n" + "\n".join(p) + "\n" + tail for p in parts]
    parts, cur = [], ""
    for word in re.split(r"(?<=\s)", unit):
        while len(word) > MAX_CHUNK:
            if cur: parts.append(cur.strip()); cur = ""
            parts.append(word[:MAX_CHUNK]); word = word[MAX_CHUNK:]
        if len(cur) + len(word) > MAX_CHUNK:
            parts.append(cur.strip()); cur = ""
        cur += word
    if cur.strip(): parts.append(cur.strip())
    return parts


def pack(text):
    out, cur = [], ""
    for unit in _units(text):
        for piece in (_split_unit(unit) if len(unit) > MAX_CHUNK else [unit]):
            if cur and len(cur) + 2 + len(piece) > MAX_CHUNK:
                out.append(cur); cur = ""
            cur = piece if not cur else cur + "\n\n" + piece
    if cur: out.append(cur)
    return out


def chunks(title, body):
    """(heading title, text) pieces of at most 1,000 characters, split at headings, with code fences kept whole."""
    return [(heading, piece) for heading, _, text in sections(title, body) for piece in pack(text)]


def learn_url(repo, path, anchor):
    prefix, base = URL_ROOTS[repo]
    rel = path[len(prefix):] if path.startswith(prefix) else path
    rel = re.sub(r"\.md$", "", rel)
    rel = re.sub(r"(^|/)index$", r"\1", rel)
    return base + rel + (f"#{anchor}" if anchor else "")


# --- build ---------------------------------------------------------------------------------------------------------

def fetch(source, work):
    """A sparse checkout of the source's folders at its pinned commit, reused when already there."""
    path = os.path.join(work, source["repo"].replace("/", "_"))
    folders = source["folders"] + source.get("snippet_folders", [])
    # Long paths on: dotnet/docs has file names of 150 characters, past Windows' 260 once inside a work folder.
    git = lambda *a: subprocess.run(["git", "-c", "core.longpaths=true", "-C", path, *a], check=True, capture_output=True, text=True).stdout.strip()
    if not os.path.isdir(os.path.join(path, ".git")):
        subprocess.run(["git", "-c", "core.longpaths=true", "clone", "-q", "--filter=blob:none", "--sparse", "--no-checkout",
                        f"https://github.com/{source['repo']}", path], check=True)
    git("sparse-checkout", "set", *folders)
    if git("rev-parse", "HEAD") != source["commit"] or git("status", "--porcelain"):
        git("checkout", "-q", "--force", source["commit"])
    return path


def pages(source, root):
    for folder in source["folders"]:
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, folder)):
            dirnames[:] = sorted(d for d in dirnames if d not in SKIPPED_FOLDERS and not d.startswith("."))
            for name in sorted(filenames):
                if name.endswith(".md") and name.lower() not in ("toc.md", "readme.md"):
                    yield os.path.join(dirpath, name)


def is_link_list(anchor):
    """A See also section is a list of titles: it matched questions on word count alone and told the model nothing."""
    return anchor in ("see-also", "related-content", "additional-resources", "next-steps", "next-step")


def page_row(md, url):
    """(title, description, address words, address): what a page says it's about, which the search reads before any passage."""
    md = md.replace("\r\n", "\n").lstrip("\ufeff")
    title, _ = strip_front_matter(md)
    front = re.match(r"---\n(.*?)\n---", md, re.S)
    description = re.search(r"^description:\s*(.*?)\s*$", front.group(1), re.M) if front else None
    words = re.sub(r"[-_/]+", " ", url.split("learn.microsoft.com/")[-1]).strip()
    return title, description.group(1).strip("'\"") if description else "", words, url


def page_chunks(source, root, md_path):
    with open(md_path, encoding="utf8", errors="replace") as f:
        title, body = strip_front_matter(f.read())
    tilde_root = os.path.join(root, source["tilde_root"]) if source["tilde_root"] else root
    body = clean(inline_snippets(keep_latest_version(body), md_path, root, tilde_root))
    rel = os.path.relpath(md_path, root).replace(os.sep, "/")
    for heading, anchor, text in sections(title, body):
        if is_link_list(anchor):
            continue
        url = learn_url(source["repo"], rel, anchor)
        for piece in pack(text):
            yield heading, piece, url


def first_of_each(rows, seen):
    """Rows whose text no earlier row had. The docs repeat boilerplate across pages ("This change is a behavioral change."
    179 times): 11,309 of 69,048 chunks. Keeping one of each made the book 16% smaller and found one more dev question."""
    for row in rows:
        if row[1] not in seen:
            seen.add(row[1])
            yield row


def build(out, work):
    with open(os.path.join(HERE, "sources.json"), encoding="utf8") as f:
        sources = json.load(f)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    if os.path.exists(out):
        os.remove(out)
    db = sqlite3.connect(out)
    db.execute('CREATE VIRTUAL TABLE chunks USING fts5(title, text, url UNINDEXED, tokenize="porter unicode61")')
    db.execute('CREATE VIRTUAL TABLE pages USING fts5(title, description, slug, url UNINDEXED, tokenize="porter unicode61")')
    total, seen = 0, set()
    for source in sources:
        root = fetch(source, work)
        count = page_count = 0
        for md_path in pages(source, root):
            rows = list(first_of_each(page_chunks(source, root, md_path), seen))
            db.executemany("INSERT INTO chunks(title, text, url) VALUES (?, ?, ?)", rows)
            if rows:
                with open(md_path, encoding="utf8", errors="replace") as f:
                    rel = os.path.relpath(md_path, root).replace(os.sep, "/")
                    db.execute("INSERT INTO pages(title, description, slug, url) VALUES (?, ?, ?, ?)", page_row(f.read(), learn_url(source["repo"], rel, "")))
            count += len(rows); page_count += 1
        print(f"{source['repo']}@{source['commit'][:10]}: {page_count} pages, {count} chunks", flush=True)
        total += count
    db.execute("INSERT INTO chunks(chunks) VALUES ('optimize')")
    db.execute("INSERT INTO pages(pages) VALUES ('optimize')")
    db.commit()
    db.execute("VACUUM")
    db.close()
    size = os.path.getsize(out)
    print(f"{total} chunks, {size / 1e6:.1f} MB -> {out}")
    return total, size


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--out", default=os.path.join(HERE, "out", "law-book.db"))
    p.add_argument("--work", help="folder for the clones, kept between runs (default: a new temp folder)")
    a = p.parse_args(argv)
    work = a.work or tempfile.mkdtemp(prefix="lawbook-")
    os.makedirs(work, exist_ok=True)
    build(a.out, work)


if __name__ == "__main__":
    sys.exit(main())
