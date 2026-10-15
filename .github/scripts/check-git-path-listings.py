#!/usr/bin/env python3
"""check-git-path-listings.py — every git PATH LISTING a script parses is read NUL-delimited (`-z`).

    python3 .github/scripts/check-git-path-listings.py               # scan .github/scripts/*.py + scripts/*.py
    python3 .github/scripts/check-git-path-listings.py --self-test   # prove the scan and the trap are real

THE TRAP. With git's default `core.quotePath`, a plain `git diff --name-only` (and `ls-files`,
`ls-tree` with or without `--name-only`, `merge-tree --name-only`, `status --porcelain`) C-QUOTES every path that holds
a byte outside printable ASCII, a double quote, a backslash or a control character:
`samples/Northwind/Product/Côte_de_Blaye.json` is printed as
`"samples/Northwind/Product/C\\303\\264te_de_Blaye.json"`. A script that reads that line verbatim
holds a path that names no file — it matches no module, no project, no `endswith(".cs")`, no allow
entry. Depending on the reader that is a REFUSED selection (Plugins#3566: 23 accented sample nodes
turned a core-candidate verdict red with nothing measured), a file silently dropped from a scan, or
a changed module that is never selected. Core carries its own copies of `gen-manifests.py` and
`resolve-generated-conflicts.py` (satellite lanes fetch them from here at the pinned platform ref),
so the defect fixed in MeshWeaver.Plugins#3568 had to be fixed here as well — and every other core
script that parses a listing with it. A reader that splits on WHITESPACE (`.split()`) is worse:
it also breaks every path that holds a space.

THE RULE. A listing is read with `-z` and split on NUL. `-z` output is never quoted, so there is
nothing to decode and nothing to guess.

WHAT THIS SCANS. Every `.github/scripts/*.py` and `scripts/*.py`, by AST: a list, tuple or call whose literal string elements
name a path-listing git command (`--name-only`, `--name-status`, `ls-files`, `ls-tree`, `--porcelain`) must also
carry the literal `"-z"` — and a `-z` listing must not then be parsed by LINE or by WHITESPACE
(`.splitlines()`, `.split()`, `.split("\\n")`, `.split(" ")`), whether the call is chained onto the
listing (`run([... "-z" ...]).stdout.splitlines()`) or made on a name the listing was assigned to in
the same scope (`out = _git(root, "ls-files", "-z")` … `out.split()`): that returns one NUL-joined
string, or whitespace fragments, never the paths (issue #6746). Splitting on NUL (`.split("\\0")`)
and consumers that inspect the raw output pass. Code inside a function whose name contains `self_test` is exempt — its
listings run over fixture repositories whose paths the test itself wrote in ASCII. A file that does
not parse is a FAILURE, never a skip.
"""
from __future__ import annotations

import ast
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
# Core keeps scripts in TWO folders; both are scanned, and an absent one is a failure, never a skip.
SCRIPT_DIRS = (".github/scripts", "scripts")

# A git argument that makes the command print PATHS, one per line, C-quoted unless `-z` is given.
LISTING_MARKERS = frozenset({"--name-only", "--name-status", "ls-files", "ls-tree", "--porcelain"})


def _literal_strings(nodes) -> list[str]:
    return [n.value for n in nodes if isinstance(n, ast.Constant) and isinstance(n.value, str)]


# Separators that split a `-z` listing into lines or whitespace fragments instead of paths.
LINE_OR_SPACE_SEPARATORS = frozenset({"\n", "\r\n", "\r", " ", "\t"})


def _is_z_listing(node: ast.AST) -> bool:
    """A list, tuple or call whose literal elements name a path-listing git command AND carry `-z`."""
    if isinstance(node, (ast.List, ast.Tuple)):
        literals = _literal_strings(node.elts)
    elif isinstance(node, ast.Call):
        literals = _literal_strings(node.args)
    else:
        return False
    return bool(LISTING_MARKERS.intersection(literals)) and "-z" in literals


def _holds_z_listing(node: ast.AST) -> bool:
    return any(_is_z_listing(n) for n in ast.walk(node))


def _line_or_space_split(node: ast.AST) -> ast.AST | None:
    """The receiver of a `.splitlines()` / whitespace-or-line `.split(...)` call, else None."""
    if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)):
        return None
    if node.func.attr == "splitlines":
        return node.func.value
    if node.func.attr not in ("split", "rsplit"):
        return None
    sep = node.args[0] if node.args else next((k.value for k in node.keywords if k.arg == "sep"), None)
    if sep is None or (isinstance(sep, ast.Constant) and (sep.value is None or sep.value in LINE_OR_SPACE_SEPARATORS)):
        return node.func.value
    return None


def _base_name(node: ast.AST) -> str | None:
    """`out` for `out`, `out.stdout`, `out.stdout.strip()` — the name a receiver chain starts from."""
    while True:
        if isinstance(node, ast.Name):
            return node.id
        if isinstance(node, ast.Attribute):
            node = node.value
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute):
            node = node.func.value
        else:
            return None


def _scope_nodes(scope: ast.AST):
    """Every node of `scope`, not descending into nested functions (each is its own scope)."""
    stack = list(ast.iter_child_nodes(scope))
    while stack:
        node = stack.pop()
        yield node
        if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)):
            stack.extend(ast.iter_child_nodes(node))


def _z_listing_misparses(tree: ast.AST, filename: str) -> list[str]:
    """`-z` listings split by line or whitespace — chained, or through a name assigned in the same scope."""
    found: list[str] = []
    message = ("a `-z` listing is split by line or whitespace — that yields one NUL-joined string "
               "(or whitespace fragments), never the paths; split it on \"\\0\"")

    def check_scope(scope: ast.AST, in_self_test: bool) -> None:
        if isinstance(scope, (ast.FunctionDef, ast.AsyncFunctionDef)) and "self_test" in scope.name:
            in_self_test = True
        nodes = list(_scope_nodes(scope))
        if not in_self_test:
            listed = {t.id for n in nodes if isinstance(n, (ast.Assign, ast.AnnAssign)) and n.value is not None
                      and _holds_z_listing(n.value)
                      for t in (n.targets if isinstance(n, ast.Assign) else [n.target]) if isinstance(t, ast.Name)}
            for n in nodes:
                receiver = _line_or_space_split(n)
                if receiver is not None and (_holds_z_listing(receiver) or _base_name(receiver) in listed):
                    found.append(f"{filename}:{n.lineno}: {message}")
        for n in nodes:
            if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)):
                check_scope(n, in_self_test)

    check_scope(tree, False)
    return found


def violations(source: str, filename: str) -> list[str]:
    """`file:line: …` for every git path listing in `source` that is not read with `-z`, or whose
    `-z` output is then split by line or whitespace."""
    tree = ast.parse(source, filename=filename)
    found: list[str] = _z_listing_misparses(tree, filename)

    def visit(node: ast.AST, in_self_test: bool) -> None:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and "self_test" in node.name:
            in_self_test = True
        if not in_self_test:
            elements = None
            if isinstance(node, (ast.List, ast.Tuple)):
                elements = node.elts
            elif isinstance(node, ast.Call):
                elements = node.args
            if elements is not None:
                literals = _literal_strings(elements)
                hit = sorted(LISTING_MARKERS.intersection(literals))
                if hit and "-z" not in literals:
                    found.append(f"{filename}:{node.lineno}: git {' '.join(hit)} is read without `-z` — "
                                 f"a non-ASCII path arrives C-quoted (\"…\\303\\264…\") and names no file")
        for child in ast.iter_child_nodes(node):
            visit(child, in_self_test)

    visit(tree, False)
    # A list literal nested in a call is reported once, at the innermost node that carries the marker.
    return sorted(set(found), key=lambda v: int(v.split(":")[1]))


def scan(root: Path, label: str = "scripts") -> tuple[list[str], int]:
    problems: list[str] = []
    files = sorted(root.glob("*.py"))
    for f in files:
        try:
            problems += violations(f.read_text(encoding="utf-8"), f"{label}/{f.name}")
        except (SyntaxError, UnicodeDecodeError) as e:
            problems.append(f"{label}/{f.name}: could not be parsed ({e}) — an unread file is not a clean one")
    return problems, len(files)


def self_test() -> int:
    failures = 0

    def check(label: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        print(f"  {'✓' if ok else '✗'} {label}" + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    # 1. The trap is real, measured on real git output.
    with tempfile.TemporaryDirectory() as tmp:
        repo = Path(tmp)
        accented = "Mod/Côte de Blaye.json"
        (repo / "Mod").mkdir()
        (repo / accented).write_text("{}")
        g = ["git", "-C", str(repo), "-c", "user.email=t@example.com", "-c", "user.name=t",
             "-c", "core.quotePath=true"]
        subprocess.run([*g, "init", "-q"], check=True)
        subprocess.run([*g, "add", "-A"], check=True)
        subprocess.run([*g, "commit", "-qm", "base"], check=True)
        (repo / accented).write_text('{"x":1}')
        plain = subprocess.run([*g, "diff", "--name-only"], capture_output=True, text=True, check=True).stdout
        nul = subprocess.run([*g, "diff", "-z", "--name-only"], capture_output=True, text=True, check=True).stdout
        check("NEGATIVE CONTROL: a plain --name-only listing C-quotes the accented path",
              plain.strip().startswith('"') and "\\303\\264" in plain, repr(plain))
        check("NEGATIVE CONTROL: the plain line names no file; split on whitespace it is not even one path",
              plain.splitlines() != [accented] and len(plain.split()) != 1, repr(plain.split()))
        check("a -z listing split on NUL is exactly the real path",
              [p for p in nul.split("\0") if p] == [accented], repr(nul))
        ls = subprocess.run([*g, "ls-files", "-z"], capture_output=True, text=True, check=True).stdout
        check("ls-files -z reads the real path too", [p for p in ls.split("\0") if p] == [accented], repr(ls))
        tree_plain = subprocess.run([*g, "ls-tree", "-r", "HEAD"], capture_output=True, text=True, check=True).stdout
        tree_nul = subprocess.run([*g, "ls-tree", "-r", "-z", "HEAD"], capture_output=True, text=True, check=True).stdout
        check("NEGATIVE CONTROL: a plain ls-tree -r line carries the accented path C-quoted after its tab",
              [ln.partition("\t")[2] for ln in tree_plain.splitlines()] != [accented] and "\\303\\264" in tree_plain,
              repr(tree_plain))
        check("ls-tree -r -z split on NUL carries the real path after its tab",
              [e.partition("\t")[2] for e in tree_nul.split("\0") if e] == [accented], repr(tree_nul))
        st = subprocess.run([*g, "status", "--porcelain", "-z"], capture_output=True, text=True, check=True).stdout
        check("status --porcelain -z carries the real path after its two status columns",
              [p[3:] for p in st.split("\0") if p] == [accented], repr(st))

    # 2. The scan sees every shape the scripts use, and passes the fixed form.
    bad = '''
import subprocess
def a(root, r):
    return subprocess.run(["git", "diff", "--name-only", r], cwd=root).stdout.splitlines()
def b(root, r):
    return _git(root, "diff", "--name-only", "--diff-filter=U").split()
def c(root):
    return git(root, ["ls-files", "--others",
                      "--exclude-standard"])
def d(root):
    return git(root, ["status", "--porcelain", "--", "*/manifest.lock"])
def e(base):
    return _run(["git", "ls-tree", "-r", base]).splitlines()
'''
    got = violations(bad, "fixture.py")
    check("NEGATIVE CONTROL: a list, a call, a multi-line listing and an ls-tree without -z are each reported",
          [v.split(":")[1] for v in got] == ["4", "6", "8", "11", "13"], "\n".join(got))
    with_z = bad.replace('"--name-only"', '"-z", "--name-only"').replace('"ls-files"', '"ls-files", "-z"') \
                .replace('"--porcelain"', '"--porcelain", "-z"').replace('"ls-tree"', '"ls-tree", "-z"')
    got = violations(with_z, "fixture.py")
    check("NEGATIVE CONTROL: -z added but the output still split by line/whitespace is reported (#6746)",
          [v.split(":")[1] for v in got] == ["4", "6", "13"], "\n".join(got))
    good = with_z.replace(".splitlines()", '.split("\\0")').replace(".split()", '.split("\\0")')
    check("the same calls with -z, split on NUL, pass", violations(good, "fixture.py") == [],
          "\n".join(violations(good, "fixture.py")))
    indirect = '''
def f(root):
    out = _git(root, "ls-files", "-z")
    return out.split()
def g(root):
    res = subprocess.run(["git", "diff", "-z", "--name-only"], capture_output=True, text=True)
    return [p for p in res.stdout.split("\\n") if p]
def h(root):
    out = _git(root, "ls-files", "-z")
    return [p for p in out.split("\\0") if p]
def raw(root):
    return bool(_git(root, "status", "--porcelain", "-z").strip())
def other(root):
    out = _git(root, "ls-files", "-z")
    def nested(text):
        return text.split()
    return nested
'''
    got = violations(indirect, "fixture.py")
    check("NEGATIVE CONTROL: a -z listing assigned to a name and then split by whitespace or line is reported",
          [v.split(":")[1] for v in got] == ["4", "7"], "\n".join(got))
    exempt = 'def self_test():\n    git(root, ["diff", "--name-only"])\n    def inner():\n        g("ls-files")\n'
    check("a self-test's fixture listings are exempt, nested helpers included",
          violations(exempt, "fixture.py") == [], "\n".join(violations(exempt, "fixture.py")))
    prose = 'X = "use git diff --name-only"\n"""a docstring naming --name-only"""\n'
    check("prose that merely names the flag is not a listing", violations(prose, "fixture.py") == [])

    # 3. An unparsable file is a failure, never a skip.
    with tempfile.TemporaryDirectory() as tmp:
        (Path(tmp) / "broken.py").write_text("def (:\n")
        problems, n = scan(Path(tmp))
        check("a file that does not parse is reported", n == 1 and len(problems) == 1 and "parsed" in problems[0],
              repr(problems))

    print(f"self-test: {failures} failure(s)")
    return 1 if failures else 0


def main() -> int:
    if "--self-test" in sys.argv[1:]:
        return self_test()
    problems: list[str] = []
    n = 0
    for rel in SCRIPT_DIRS:
        found, count = scan(REPO / rel, rel)
        if count == 0:
            print(f"::error::check-git-path-listings: no scripts found under {rel}/ — a scan of nothing is not a pass")
            return 1
        problems += found
        n += count
    for p in problems:
        print(f"::error::{p}")
    print(f"git path listings: {len(problems)} read without -z, over {n} script(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
