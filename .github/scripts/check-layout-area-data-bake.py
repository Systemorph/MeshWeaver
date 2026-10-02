#!/usr/bin/env python3
"""check-layout-area-data-bake.py — no layout area may LOAD data on the hub and BAKE it into controls.

(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at the lane's
scripts-ref and refuses a body whose first 400 bytes do not name it.)

THE RULE (Doc/GUI/DataBinding -> "Templates first, data later")
---------------------------------------------------------------
Every view is data-bound. A layout area emits its TEMPLATE at once — with the platform's loading
shape standing in as progress — and the GUI binds the values through node paths / pointers
(`LayoutAreaReference.GetMeshNodeDataContext(path)` + a `JsonPointerReference`,
`MeshNodeThumbnailControl { NodePath }`, `Controls.MeshSearch` for lists). FORBIDDEN: waiting on a
node stream / query / workspace stream on the hub where the area lives and constructing controls
out of the values — the page shows a spinner until the slowest read answers and the result is a
snapshot.

THIS IS THE SATELLITE HALF OF ONE RATCHET
-----------------------------------------
Core holds the same shape in `LayoutAreaDataBakeRatchetGuard` (test/MeshWeaver.Documentation.Test)
against `test/LayoutAreaDataBakeSites.allow`. This script is a line-for-line port of that guard's
scanner — LoadPattern, ControlPattern, the unit header, the untyped `(host, ctx) =>` lambda, the
same-file loader helpers and the outermost-unit rule — so the two count the same units on the
same text; the self-test below plants the guard's own cases. Change a pattern in one and you must
change it in the other.

A "bake unit" is a method, local function or lambda whose parameters name a `LayoutAreaHost`
(typed, or the conventional untyped `(host, ctx) =>`), outermost only, whose body BOTH reads data
(a node stream, a query, a workspace stream, or a same-file helper that does) AND builds controls.
It is a text heuristic: recall is a floor (a load through another file's helper or a domain
service is missed) and an area reading data only to choose STRUCTURE — a permission gate — is
counted too. The allow-file is therefore an inventory to shrink, not a verdict on each line.

THE ALLOW-FILE (the caller's, at its repo root)
-----------------------------------------------
`layout-area-data-bake.allow`: `<repo-relative file><TAB><bake units in that file>`, `#` comments.
It is SHRINK-ONLY:

  * NEW     a file that bakes and has no line             -> red: a new area is born a template.
  * MORE    a file holding more units than its line       -> red.
  * MISSING / malformed allow-file                        -> red, never skipped: a repo that has
                                                             not adopted the ratchet is unguarded,
                                                             and a skipped check reads as a pass.

On a pull request the lane also passes `--base <base sha>`; the base tree is scanned with the same
scanner and its allow-file read, because the rules above alone would pass a PR that adds a baking
area AND its line:

  * ADDED   the tree holds more units than the base did   -> red. Moving a unit between files keeps
                                                             the total and passes (it still needs
                                                             its line — NEW — and the old one goes).
  * GREW    the allow-file's total exceeds the base's     -> red.
  * RAISED  a line's count exceeds the base's line        -> red.
  * STALE   a file the PR CONVERTED is now below its line -> red until the line is lowered or
                                                             deleted: the allowance is spent in the
                                                             PR that earned it.

A STALE line the PR did NOT cause — it was already above its file at the base, because two
converting PRs merged concurrently or one ran before the allow-file existed — is a WARNING naming
the one-line tidy, on pull requests and on main. Failing it would red every unrelated PR in the
repository for someone else's merge, and ADDED already stops the slack being re-used. Any PR may
carry the tidy. A base with no allow-file is the adoption PR itself: the seed is reviewed as written.

USAGE
-----
  check-layout-area-data-bake.py --root .               gate the tree at . against its allow-file
  check-layout-area-data-bake.py --root . --report      print the current inventory in allow-file
                                                        form (to seed or tidy), exit 0
  check-layout-area-data-bake.py --self-test            prove the scanner and the ratchet fire

Node repos do not copy this file: the `validate` lane (node-repo-validate.yml) fetches it from the
platform at its scripts-ref and runs it against the caller's tree.
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
import subprocess
import tarfile
import tempfile
from pathlib import Path

ALLOW_FILE = "layout-area-data-bake.allow"

# Directories that are never production source: build output, dependencies, and TEST trees (a test
# may build a baking area on purpose to prove something about it).
SKIP_DIRS = {"bin", "obj", "node_modules", ".git", ".github", ".worktrees", "TestResults", "wwwroot",
             "test", "tests", "e2e"}


def _is_test_dir(name: str) -> bool:
    low = name.lower()
    return low in SKIP_DIRS or name in SKIP_DIRS or low.endswith(".test") or low.endswith(".tests")


# ── the scanner: a port of LayoutAreaDataBakeRatchetGuard ────────────────────────────────────────

LOAD = re.compile(
    r"\b(GetMeshNodeStream|ObserveQuery|GetQuery|QueryAsync|GetRemoteStream|GetSingle|ReduceToTypes|GetMeshNode|GetMeshNodeOutcome|GetObservable|ObserveNode|ObserveChildren|GetChildren|GetNodeStream|GetStreamForPartition|GetMarkdownContent|GetFileContent|GetContentAsText|ObserveNodeTypeRelease)\s*(<[^;()]*>)?\s*\("
    r"|\.Query\s*(<[^;()]*>)?\s*\("
    r"|\b(Workspace|workspace)\s*\.\s*GetStream\s*(<[^;()]*>)?\s*\("
    r"|\.GetStream\s*<[^;()]*>\s*\(")
CTRL = re.compile(r"\bControls\s*\.\s*\w+|\bnew\s+\w+Control\b|\b\w+Control\s*\.\s*(For|From)\w*\(|\.WithView\s*\(")
HEADER = re.compile(
    r"(?P<name>[A-Za-z_]\w*)\s*(<[^<>()]*(<[^<>()]*>[^<>()]*)*>)?\s*\((?P<params>[^()]*(\([^()]*\)[^()]*)*)\)\s*"
    r"(where[^{=;]*)?(?P<open>\{|=>)")
UNTYPED_LAMBDA = re.compile(
    r"\(\s*(?:LayoutAreaHost\s+)?(host|h|area|layoutArea|layoutAreaHost)\s*,\s*(?:RenderingContext\s+)?\w+\s*\)\s*=>"
    r"|\b(host|layoutArea)\s*=>")
KEYWORDS = {"if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "nameof",
            "typeof", "when", "select", "sizeof", "default", "base", "this", "static"}


def mask(text: str) -> str:
    """Blank comments and string/char literal CONTENTS (newlines kept), so text is never code."""
    out = list(text)
    i, n = 0, len(text)

    def blank(a: int, b: int) -> None:
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j); i = j; continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j); i = j; continue
        m = re.match(r'(\$+@?|@\$+|@)?("""+)', text[i:i + 12])
        if m and m.group(2):
            q = m.group(2)
            j = text.find(q, i + len(m.group(0)))
            j = n if j < 0 else j + len(q)
            blank(i + len(m.group(0)), j - len(q)); i = j; continue
        m = re.match(r'(\$@|@\$|@|\$)?"', text[i:i + 3])
        if m:
            pre = m.group(1) or ""
            verbatim = "@" in pre
            j = i + len(m.group(0))
            depth = 0
            while j < n:
                ch = text[j]
                if "$" in pre and ch == "{":
                    if text.startswith("{{", j):
                        j += 2; continue
                    depth += 1; j += 1; continue
                if "$" in pre and ch == "}" and depth > 0:
                    depth -= 1; j += 1; continue
                if depth > 0:
                    j += 1; continue
                if verbatim:
                    if ch == '"':
                        if text.startswith('""', j):
                            j += 2; continue
                        break
                else:
                    if ch == "\\":
                        j += 2; continue
                    if ch == '"' or ch == "\n":
                        break
                j += 1
            blank(i + len(m.group(0)), j); i = j + 1; continue
        if c == "'":
            m = re.match(r"'(\\.|[^'\\\n]){1,8}'", text[i:i + 12])
            if m:
                blank(i + 1, i + len(m.group(0)) - 1); i += len(m.group(0)); continue
        i += 1
    return "".join(out)


def _block_end(code: str, open_brace: int) -> int:
    depth = 0
    for i in range(open_brace, len(code)):
        if code[i] == "{":
            depth += 1
        elif code[i] == "}":
            depth -= 1
            if depth == 0:
                return i + 1
    return len(code)


def _expression_end(code: str, start: int) -> int:
    depth = 0
    for i in range(start, len(code)):
        c = code[i]
        if c in "({[":
            depth += 1
        elif c in ")}]":
            if depth == 0:
                return i
            depth -= 1
        elif c in ";," and depth == 0:
            return i
    return len(code)


def _body_span(code: str, m: re.Match) -> tuple[int, int]:
    if m.group("open") == "{":
        s = m.start("open")
        return s, _block_end(code, s)
    s = m.end("open")
    return s, _expression_end(code, s)


def _preceded_by_name(code: str, index: int) -> bool:
    i = index - 1
    while i >= 0 and code[i].isspace():
        i -= 1
    if i < 0 or not (code[i].isalnum() or code[i] in "_>"):
        return False
    end = i + 1
    while i >= 0 and (code[i].isalnum() or code[i] == "_"):
        i -= 1
    return code[i + 1:end] != "return"


def _units(code: str) -> list[tuple[int, int, int, str | None]]:
    found = []
    for m in HEADER.finditer(code):
        if m.group("name") in KEYWORDS or "LayoutAreaHost" not in m.group("params"):
            continue
        s, e = _body_span(code, m)
        found.append((m.start(), s, e, m.group("name")))
    for m in UNTYPED_LAMBDA.finditer(code):
        # `Name(LayoutAreaHost host, RenderingContext ctx) =>` is a METHOD's list, already a unit.
        if m.group(0)[0] == "(" and _preceded_by_name(code, m.start()):
            continue
        j = m.end()
        while j < len(code) and code[j].isspace():
            j += 1
        e = _block_end(code, j) if j < len(code) and code[j] == "{" else _expression_end(code, j)
        found.append((m.start(), m.start(), e, None))
    found.sort(key=lambda u: (u[1], -u[2]))
    outer: list[tuple[int, int, int, str | None]] = []
    for u in found:
        if outer and u[1] >= outer[-1][1] and u[2] <= outer[-1][2]:
            continue
        outer.append(u)
    return outer


def _loader_helpers(code: str) -> set[str]:
    names = set()
    for m in HEADER.finditer(code):
        name = m.group("name")
        if name in KEYWORDS or "LayoutAreaHost" in m.group("params"):
            continue
        s, e = _body_span(code, m)
        body = code[s:e]
        if LOAD.search(body) and not CTRL.search(body):
            names.add(name)
    return names


def _enclosing_name(code: str, position: int) -> str:
    best = None
    for m in HEADER.finditer(code[:position]):
        if m.group("name") not in KEYWORDS:
            best = m.group("name")
    return best or "?"


def units_in_code(text: str) -> list[str]:
    """The names of the bake units in a C# text (lambdas as `lambda@Enclosing`)."""
    code = mask(text)
    helpers = _loader_helpers(code)
    helper_call = re.compile(r"\b(" + "|".join(re.escape(h) for h in sorted(helpers)) + r")\s*\(") if helpers else None
    result = []
    for (header_start, s, e, name) in _units(code):
        body = code[s:e]
        loads = bool(LOAD.search(body)) or bool(helper_call and helper_call.search(body))
        if loads and CTRL.search(body):
            result.append(name or "lambda@" + _enclosing_name(code, header_start))
    return result


def _json_code(path: Path) -> list[str]:
    """C# carried inside a node JSON string (a NodeType `configuration`, an inline area)."""
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    out: list[str] = []

    def walk(o):
        if isinstance(o, dict):
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
        elif isinstance(o, str) and ("LayoutAreaHost" in o or UNTYPED_LAMBDA.search(o)):
            out.append(o)

    walk(data)
    return out


def scan(root: Path) -> dict[str, list[str]]:
    """{repo-relative posix path: [unit names]} for every production file that bakes."""
    found: dict[str, list[str]] = {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if not _is_test_dir(d))
        for f in sorted(filenames):
            p = Path(dirpath) / f
            rel = p.relative_to(root).as_posix()
            units: list[str] = []
            if f.endswith(".cs") or f.endswith(".csx"):
                try:
                    text = p.read_text(encoding="utf-8", errors="replace")
                except OSError:
                    continue
                if "LayoutAreaHost" not in text and not UNTYPED_LAMBDA.search(text):
                    continue
                units = units_in_code(text)
            elif f.endswith(".json"):
                for code in _json_code(p):
                    units.extend(units_in_code(code))
            if units:
                found[rel] = units
    return found


# ── the ratchet ──────────────────────────────────────────────────────────────────────────────────

def read_allow(path: Path) -> tuple[dict[str, int], list[str]]:
    return parse_allow(path.read_text(encoding="utf-8"))


def parse_allow(text: str) -> tuple[dict[str, int], list[str]]:
    allowed: dict[str, int] = {}
    errors: list[str] = []
    for no, raw in enumerate(text.splitlines(), 1):
        line = raw.split("#", 1)[0].strip() if raw.lstrip().startswith("#") else raw.strip()
        if not line:
            continue
        parts = line.rsplit(None, 1)
        if len(parts) != 2 or not parts[1].isdigit() or int(parts[1]) < 1:
            errors.append(f"{ALLOW_FILE}:{no}: expected `<repo-relative file><TAB><count >= 1>`, got `{raw}`")
            continue
        file, count = parts[0].strip(), int(parts[1])
        if file in allowed:
            errors.append(f"{ALLOW_FILE}:{no}: `{file}` is listed twice")
            continue
        allowed[file] = count
    return allowed, errors


def _git(root: Path, *args: str, binary: bool = False) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=not binary)


def base_state(root: Path, base: str) -> tuple[dict[str, int] | None, dict[str, int] | None, list[str]]:
    """At the BASE commit: (its allow-file, or None when it has none — the adoption PR; its per-file unit
    counts; errors). The base tree is exported and scanned with the same scanner."""
    if _git(root, "cat-file", "-e", f"{base}^{{commit}}").returncode != 0:
        return None, None, [f"BASE  the base commit `{base}` is not in this checkout, so this pull request cannot be "
                          f"compared with its base — refusing rather than passing on no evidence."]
    errors: list[str] = []
    allowed: dict[str, int] | None = None
    shown = _git(root, "show", f"{base}:{ALLOW_FILE}")
    if shown.returncode == 0:
        allowed, errs = parse_allow(shown.stdout)
        errors += [f"BASE  {e}" for e in errs]
    archive = _git(root, "archive", "--format=tar", base, binary=True)
    if archive.returncode != 0:
        return allowed, None, errors + [f"BASE  `git archive {base}` failed: {archive.stderr.decode(errors='replace').strip()}"]
    with tempfile.TemporaryDirectory() as tmp:
        with tarfile.open(fileobj=io.BytesIO(archive.stdout)) as tar:
            tar.extractall(tmp, filter="data")
        counts = {f: len(u) for f, u in scan(Path(tmp)).items()}
    return allowed, counts, errors


def check(root: Path, base: str | None = None) -> tuple[list[str], list[str]]:
    """(failures, warnings); no failures means the ratchet holds.

    STALE is charged to the change that CAUSED it: on a pull request whose own diff lowered a file's
    count below its line, it is a failure — the converting PR spends its allowance. A line that was
    already above its file at the base (two converting PRs merged concurrently, or a PR whose run
    predates the allow-file) is a WARNING naming the one-line tidy: failing it would red every
    unrelated PR in the repository for someone else's merge, and the ADDED rule below already keeps
    such an allowance from being re-used: on a pull request the TREE's total may not exceed the
    base's, whatever the allow-file says (moving a unit between files keeps the total)."""
    allow_path = root / ALLOW_FILE
    if not allow_path.is_file():
        return [f"MISSING {ALLOW_FILE} at the repository root. This repo has not adopted the layout-area "
                f"data-bake ratchet, so it is UNGUARDED — a skipped check would read as a pass. Seed it with "
                f"`python3 check-layout-area-data-bake.py --root . --report > {ALLOW_FILE}` (an empty file "
                f"is correct when nothing bakes)."], []
    allowed, errors = read_allow(allow_path)
    if errors:
        return errors, []
    found = scan(root)
    failures: list[str] = []
    warnings: list[str] = []
    before_allow: dict[str, int] | None = None
    before: dict[str, int] | None = None
    if base:
        before_allow, before, errs = base_state(root, base)
        failures += errs
    for file in sorted(found):
        count, budget = len(found[file]), allowed.get(file)
        if budget is None:
            failures.append(f"NEW   {file}\t{count} — {', '.join(found[file])}: a layout area loads data on the hub "
                            f"and builds controls out of it. Emit the TEMPLATE at once and BIND the data instead. "
                            f"Adding a line to {ALLOW_FILE} is not a fix.")
        elif count > budget:
            failures.append(f"MORE  {file}\t{count} > {budget} allowed — {', '.join(found[file])}: a baking area was "
                            f"ADDED to a file that already carries one.")
    if before is not None:
        now, then = sum(len(u) for u in found.values()), sum(before.values())
        if now > then:
            grown = [f"{f} {then_n}->{len(found[f])}" for f in sorted(found)
                     for then_n in [before.get(f, 0)] if len(found[f]) > then_n]
            failures.append(f"ADDED the tree holds {now} baking unit(s), the base held {then} — this pull request adds "
                            f"a layout area that bakes data ({'; '.join(grown)}). Moving an existing one between "
                            f"files keeps the total and passes; adding one does not.")
    for file in sorted(allowed):
        count, budget = len(found.get(file, [])), allowed[file]
        if count >= budget:
            continue
        fix = f"delete the line `{file}`" if count == 0 else f"lower `{file}` to {count}"
        message = f"STALE {file}\t{count} found, {budget} allowed — {fix} in {ALLOW_FILE}."
        if before is not None and before.get(file, 0) > count:
            failures.append(message + " This pull request converted it, so it spends the allowance: the "
                                      "allow-file only SHRINKS, and an unspent line is room to regrow.")
        else:
            warnings.append(message + " It was already above its file before this change; any pull request may "
                                      "carry the one-line tidy.")
    if before_allow is not None:
        total, total_before = sum(allowed.values()), sum(before_allow.values())
        if total > total_before:
            failures.append(f"GREW  {ALLOW_FILE} totals {total} unit(s), the base holds {total_before}. The "
                            f"inventory only shrinks — a new baking area is not admitted by adding its line.")
        for file in sorted(allowed):
            if file in before_allow and allowed[file] > before_allow[file]:
                failures.append(f"RAISED {file}\t{allowed[file]} > {before_allow[file]} at the base — a line is only "
                                f"ever lowered or deleted.")
    return failures, warnings


def report(root: Path) -> str:
    found = scan(root)
    lines = [
        "# layout-area-data-bake.allow — layout areas that LOAD data on the hub and BAKE it into controls.",
        "# Format: <repo-relative file><TAB><bake units in that file>. SHRINK-ONLY: a new baking file, a",
        "# raised count, or (on a PR) more units than the base held fails. Convert an area to a template",
        "# that BINDS its data (Doc/GUI/DataBinding -> \"Templates first, data later\"), then lower or",
        "# delete its line in the same PR — leaving it is STALE and fails that PR. Never add a line.",
        "# Gate: core's .github/scripts/check-layout-area-data-bake.py, run by node-repo-validate.yml.",
    ]
    lines += [f"{f}\t{len(u)}" for f, u in sorted(found.items())]
    return "\n".join(lines) + "\n"


# ── self-test ────────────────────────────────────────────────────────────────────────────────────

_BAKE = """
public static class Areas
{
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
        => Controls.Stack.WithView((h, c) => host.Workspace.GetMeshNodeStream()
            .Select(node => MeshNodeThumbnailControl.FromNode(node, "x")));
}
"""
_TEMPLATE = """
public static class Areas
{
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
        => new MeshNodeThumbnailControl(host.Hub.Address.ToString(), "");
}
"""


def self_test() -> int:
    cases = [
        # The guard's own planted cases (LayoutAreaDataBakeRatchetGuard.TheMatcherCountsBakingNotBinding).
        ("bake: wait for the node, build a control", _BAKE, ["Thumbnail"]),
        ("bake: untyped area lambda over a query", """
            void Register(LayoutDefinition layout) => layout.WithView("List", (host, ctx) =>
                host.Workspace.GetQuery("k", "nodeType:X").Select(nodes => Controls.Markdown("n")));
            """, ["lambda@Register"]),
        ("bake: load through a same-file helper", """
            static IObservable<MeshNode?> Load(IWorkspace ws) => ws.GetMeshNodeStream("p");
            public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
                => Load(host.Workspace).Select(n => (UiControl?)Controls.Markdown(n?.Name ?? ""));
            """, ["Overview"]),
        ("template: bound by path, no read", _TEMPLATE, []),
        ("no control: a menu predicate", """
            static IObservable<bool> Show(LayoutAreaHost host, RenderingContext ctx)
                => host.Workspace.GetMeshNodeStream().Select(n => n is not null);
            """, []),
        ("comments and strings are not code", """
            // public static UiControl X(LayoutAreaHost host) => host.Workspace.GetMeshNodeStream().Select(n => Controls.Markdown(""));
            var s = "UiControl X(LayoutAreaHost host) => GetMeshNodeStream().Select(Controls.Markdown)";
            """, []),
    ]
    failures = 0
    for name, code, expected in cases:
        got = units_in_code(code)
        ok = got == expected
        failures += not ok
        print(f"self-test {'ok' if ok else 'FAIL':4} scanner  {name:44} expected={expected} got={got}")

    def ratchet(name: str, files: dict[str, str], allow: str | None, expect: str | None) -> None:
        nonlocal failures
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for rel, body in files.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(body, encoding="utf-8")
            if allow is not None:
                (root / ALLOW_FILE).write_text(allow, encoding="utf-8")
            fails, warns = check(root)
            got = fails + ["warn:" + w for w in warns]
            ok = (not fails) if expect is None else any(g.startswith(expect) for g in got)
            failures += not ok
            print(f"self-test {'ok' if ok else 'FAIL':4} ratchet  {name:44} expected={expect or 'pass'} "
                  f"got={[g.split(' ', 1)[0] for g in got] or 'pass'}")

    bake = {"Mod/Source/Areas.cs": _BAKE}
    ratchet("missing allow-file is red, not skipped", bake, None, "MISSING")
    ratchet("missing allow-file is red on a clean tree too", {"Mod/Source/Areas.cs": _TEMPLATE}, None, "MISSING")
    ratchet("listed at its count passes", bake, "# c\nMod/Source/Areas.cs\t1\n", None)
    ratchet("empty allow-file on a clean tree passes", {"Mod/Source/Areas.cs": _TEMPLATE}, "", None)
    ratchet("an unlisted baking file is NEW", bake, "", "NEW")
    ratchet("a raised count is MORE", {"Mod/Source/Areas.cs": _BAKE + _BAKE.replace("Areas", "More")},
            "Mod/Source/Areas.cs\t1\n", "MORE")
    ratchet("no base (main): a stale line WARNS, never reds", {"Mod/Source/Areas.cs": _TEMPLATE},
            "Mod/Source/Areas.cs\t1\n", "warn:STALE")
    ratchet("no base (main): a line above the count WARNS", bake, "Mod/Source/Areas.cs\t2\n", "warn:STALE")
    ratchet("a malformed line is red", bake, "Mod/Source/Areas.cs\n", ALLOW_FILE)
    ratchet("a test tree is not scanned", {"Mod/Mod.Test/AreaTest.cs": _BAKE, "test/X.cs": _BAKE}, "", None)
    ratchet("C# inside node JSON is scanned", {"Mod/X.json": json.dumps({"configuration": _BAKE})}, "", "NEW")
    def based(name: str, base_allow_text: str | None, head_files: dict[str, str], head_allow: str,
              expect: str | None, base_override: str | None = None,
              base_files: dict[str, str] | None = None) -> None:
        nonlocal failures
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            git = lambda *a: subprocess.run(["git", "-C", tmp, "-c", "user.name=t", "-c", "user.email=t@t",
                                             "-c", "commit.gpgsign=false", *a], capture_output=True, text=True, check=True)
            git("init", "-q")
            if base_allow_text is not None:
                (root / ALLOW_FILE).write_text(base_allow_text, encoding="utf-8")
            (root / "README").write_text("base", encoding="utf-8")
            for rel, body in (base_files or {}).items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(body, encoding="utf-8")
            git("add", "-A"); git("commit", "-q", "-m", "base")
            base = git("rev-parse", "HEAD").stdout.strip()
            for rel, body in head_files.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(body, encoding="utf-8")
            (root / ALLOW_FILE).write_text(head_allow, encoding="utf-8")
            fails, warns = check(root, base_override or base)
            got = fails + ["warn:" + w for w in warns]
            ok = (not fails) if expect is None else any(g.startswith(expect) for g in got)
            failures += not ok
            print(f"self-test {'ok' if ok else 'FAIL':4} base     {name:44} expected={expect or 'pass'} "
                  f"got={[g.split(' ', 1)[0] for g in got] or 'pass'}")

    two = {"Mod/Source/Areas.cs": _BAKE, "Other/Source/Areas.cs": _BAKE}
    based("a PR adding a bake AND its line is ADDED", "Mod/Source/Areas.cs\t1\n", two,
          "Mod/Source/Areas.cs\t1\nOther/Source/Areas.cs\t1\n", "ADDED", base_files=bake)
    based("a PR growing the allow-file alone is GREW", "Mod/Source/Areas.cs\t1\n", two,
          "Mod/Source/Areas.cs\t1\nOther/Source/Areas.cs\t1\n", "GREW", base_files=two)
    raised = {"Mod/Source/Areas.cs": _BAKE + _BAKE.replace("Areas", "More"), "Other/Source/Areas.cs": _BAKE}
    based("a PR raising a line is RAISED", "Mod/Source/Areas.cs\t1\nOther/Source/Areas.cs\t2\n", raised,
          "Mod/Source/Areas.cs\t2\nOther/Source/Areas.cs\t1\n", "RAISED", base_files=raised)
    based("relocating a bake keeps the total and passes", "Mod/Source/Areas.cs\t1\n",
          {"Mod/Source/Areas.cs": _TEMPLATE, "Other/Source/Areas.cs": _BAKE}, "Other/Source/Areas.cs\t1\n", None,
          base_files=bake)
    based("the adoption PR (no base allow-file) passes", None, {"Doc/x.md": "seed"}, "Mod/Source/Areas.cs\t1\n",
          None, base_files=bake)
    conv = {"Mod/Source/Areas.cs": _BAKE}
    based("a PR converting an area without its line is STALE (red)", "Mod/Source/Areas.cs\t1\n",
          {"Mod/Source/Areas.cs": _TEMPLATE}, "Mod/Source/Areas.cs\t1\n", "STALE", base_files=conv)
    based("the same PR deleting its line passes", "Mod/Source/Areas.cs\t1\n",
          {"Mod/Source/Areas.cs": _TEMPLATE}, "", None, base_files=conv)
    based("a stale line inherited from the base WARNS", "Mod/Source/Areas.cs\t1\n",
          {"Doc/x.md": "unrelated"}, "Mod/Source/Areas.cs\t1\n", "warn:STALE",
          base_files={"Mod/Source/Areas.cs": _TEMPLATE})
    based("re-using an inherited allowance is ADDED", "Mod/Source/Areas.cs\t1\n",
          {"Mod/Source/Areas.cs": _BAKE}, "Mod/Source/Areas.cs\t1\n", "ADDED",
          base_files={"Mod/Source/Areas.cs": _TEMPLATE})
    based("an unknown base commit is red, not skipped", "", bake, "Mod/Source/Areas.cs\t1\n", "BASE",
          base_override="0" * 40)
    if failures:
        print(f"::error::check-layout-area-data-bake.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".", help="the repository to gate (default: cwd)")
    ap.add_argument("--report", action="store_true", help="print the inventory in allow-file form and exit 0")
    ap.add_argument("--base", default=None,
                    help="a pull request's base commit: also hold the allow-file to the base's copy (GREW / RAISED)")
    ap.add_argument("--self-test", action="store_true", help="prove the scanner and the ratchet fire, and exit")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    if args.report:
        sys.stdout.write(report(root))
        return 0
    if args.base is not None and not args.base.strip():
        print("::error title=Layout area bakes data::--base was given EMPTY — a pull request's base sha did not "
              "reach the gate, so the shrink-only comparison would check nothing. Refusing.")
        return 1
    failures, warnings = check(root, args.base)
    for w in warnings:
        print(f"::warning title=Layout area allow-file is stale::{w}")
    found = scan(root)
    total = sum(len(u) for u in found.values())
    if failures:
        for f in failures:
            print(f"::error title=Layout area bakes data::{f}")
        print(f"\n{len(failures)} violation(s). Current inventory ({total} unit(s) in {len(found)} file(s)), "
              f"in allow-file form — copy only the lines that SHRANK:")
        sys.stdout.write("\n".join(f"{f}\t{len(u)}" for f, u in sorted(found.items())) + "\n")
        print("See Doc/GUI/DataBinding -> \"Templates first, data later\" for the before/after.")
        return 1
    print(f"layout-area data-bake ratchet holds: {total} unit(s) in {len(found)} file(s), none above its line "
          f"in {ALLOW_FILE}" + (f"; {len(warnings)} stale line(s) to tidy (warnings above)." if warnings else "."))
    return 0


if __name__ == "__main__":
    sys.exit(main())
