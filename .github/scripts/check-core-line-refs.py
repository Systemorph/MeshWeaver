#!/usr/bin/env python3
"""Every reference core's workflows make to core itself names THIS branch's delivery line.

Policy `major-lines-run-separately` (Doc/Architecture/MajorLinesRunSeparately): each delivery line
is a branch, declared in `.github/delivery-lines.json` (`main` = v4, `main3` = v3). A shared lane,
composite action, script fetch or lane-input default inside core that names ANOTHER line's branch
crosses majors silently: a `uses: Systemorph/MeshWeaver/…@main` on `main3` runs v4's lane from a
v3 tree the moment `main` moves, and nothing errors. This guard reads the line of the branch it
runs for and refuses every reference to a different declared line.

The SAME file ships on every line's branch (as delivery-lines.json does): the branch decides the
expected line, nothing in this file has to differ between `main` and `main3`.

Which branch? `--branch`, else the event: a pull request's BASE (`GITHUB_BASE_REF`), a merge
group's base (its event payload), otherwise the run's ref (`GITHUB_REF_NAME`). When that branch is
NOT a declared line — a stacked pull request, a feature-branch dispatch, a local run — the guard
cannot read the line from the branch, so it demands CONSISTENCY instead: every reference names one
and the same declared line. It never skips: both modes read every file and print the denominator.

Refused (non-comment lines of every yml/yaml under `.github/`), for any declared line L other
than the expected one:
  * `Systemorph/MeshWeaver/<path>@L`                     a shared workflow or composite action;
  * `"$RUNNER_TEMP/mw-core-file" L <path>`               a core-file read;
  * `raw.githubusercontent.com/Systemorph/MeshWeaver/L/` and `repos/Systemorph/MeshWeaver/…?ref=L`;
  * `scripts-ref:` / `platform-ref:` / `SCRIPTS_REF:` set to L (a caller's input or an env);
  * a `scripts-ref` / `platform-ref` input whose `default:` is L (a lane's own default);
  * an `actions/checkout` of core (`repository: Systemorph/MeshWeaver`) at `ref: L` — or with no
    `ref:` at all, which checks out the repository's DEFAULT branch (`defaultBranch` in the
    declaration), so it counts as a reference to that line.
A sha or any undeclared ref is not a line and is not judged here.

Stdlib only. Usage:
    python3 .github/scripts/check-core-line-refs.py --self-test
    python3 .github/scripts/check-core-line-refs.py [--root .] [--branch <line>]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
from pathlib import Path

DECLARATION = Path(".github/delivery-lines.json")
_REF = r"(?P<ref>[A-Za-z0-9_.-]+)"
_END = r"(?![A-Za-z0-9_.-])"
SINGLE = [
    ("lane or action", re.compile(r"Systemorph/MeshWeaver/[^@\s'\"]+@" + _REF + _END)),
    ("core-file read", re.compile(r"mw-core-file\"?\s+['\"]?" + _REF + _END)),
    ("raw fetch", re.compile(r"raw\.githubusercontent\.com/Systemorph/MeshWeaver/" + _REF + r"/")),
    ("contents fetch", re.compile(r"repos/Systemorph/MeshWeaver/[^\s'\"]*\?ref=" + _REF + _END)),
    ("ref input or env", re.compile(r"^\s*(?:-\s*)?(?:scripts-ref|platform-ref|SCRIPTS_REF):\s*['\"]?" + _REF + r"['\"]?\s*$")),
]
INPUT_KEY = re.compile(r"^(?P<indent>\s+)(?:scripts-ref|platform-ref):\s*$")
DEFAULT = re.compile(r"^\s+default:\s*['\"]?" + _REF + r"['\"]?\s*$")
CORE_REPOSITORY = re.compile(r"^(?P<indent>\s*)repository:\s*['\"]?Systemorph/MeshWeaver['\"]?\s*$")
CHECKOUT_REF = re.compile(r"^\s*ref:\s*(?P<ref>.*?)\s*$")


def declaration(root: Path) -> tuple[list[str], str]:
    """(declared line branches, the repository's default branch) from delivery-lines.json."""
    doc = json.loads((root / DECLARATION).read_text(encoding="utf-8"))
    lines = [entry["branch"] for entry in doc.get("lines", [])]
    if not lines or not all(isinstance(b, str) and b for b in lines):
        raise SystemExit(f"::error::{DECLARATION} declares no delivery line — this guard has nothing to read")
    default = doc.get("defaultBranch")
    if default not in lines:
        raise SystemExit(f"::error::{DECLARATION} defaultBranch {default!r} is not a declared line")
    return lines, default


def _checkout_ref(text: list[str], at: int, indent: int) -> str | None:
    """The `ref:` sibling of the `repository:` key at `text[at]` (same `with:` block), or None."""
    def ind(s: str) -> int:
        return len(s) - len(s.lstrip())
    lo = at
    while lo > 0 and (not text[lo - 1].strip() or ind(text[lo - 1]) >= indent):
        lo -= 1
    hi = at + 1
    while hi < len(text) and (not text[hi].strip() or ind(text[hi]) >= indent):
        hi += 1
    for line in text[lo:hi]:
        m = CHECKOUT_REF.match(line)
        if m and ind(line) == indent:
            return m.group("ref").split(" #")[0].strip().strip("'\"")
    return None


def this_branch(env: dict) -> str:
    event = env.get("GITHUB_EVENT_NAME", "")
    if event in ("pull_request", "pull_request_target") and env.get("GITHUB_BASE_REF"):
        return env["GITHUB_BASE_REF"]
    if event == "merge_group" and env.get("GITHUB_EVENT_PATH"):
        try:
            base = json.loads(Path(env["GITHUB_EVENT_PATH"]).read_text())["merge_group"]["base_ref"]
            return base.removeprefix("refs/heads/")
        except (OSError, KeyError, TypeError, ValueError):
            return ""
    return env.get("GITHUB_REF_NAME", "")


def references(root: Path, lines: list[str], default: str) -> tuple[list[tuple[str, int, str, str, str]], int]:
    """(file, line, kind, ref, text) for every reference naming a DECLARED line; and files read."""
    github = root / ".github"
    files = sorted([*github.rglob("*.yml"), *github.rglob("*.yaml")])
    out = []
    for path in files:
        rel = path.relative_to(root).as_posix()
        text = path.read_text(encoding="utf-8").splitlines()
        for n, raw in enumerate(text, 1):
            if raw.lstrip().startswith("#"):
                continue
            for kind, pattern in SINGLE:
                for m in pattern.finditer(raw):
                    if m.group("ref") in lines:
                        out.append((rel, n, kind, m.group("ref"), raw.strip()))
            repo = CORE_REPOSITORY.match(raw)
            if repo:
                ref = _checkout_ref(text, n - 1, len(repo.group("indent")))
                if ref is None:
                    out.append((rel, n, "core checkout with no ref (the default branch)", default, raw.strip()))
                elif ref in lines:
                    out.append((rel, n, "core checkout", ref, f"{raw.strip()} … ref: {ref}"))
            key = INPUT_KEY.match(raw)
            if key:
                indent = len(key.group("indent"))
                for k, follow in enumerate(text[n:], n + 1):
                    if follow.strip() and len(follow) - len(follow.lstrip()) <= indent:
                        break
                    d = DEFAULT.match(follow)
                    if d and d.group("ref") in lines:
                        out.append((rel, k, "input default", d.group("ref"), f"{raw.strip()} … {follow.strip()}"))
    return out, len(files)


def check(root: Path, branch: str) -> tuple[list[str], str]:
    lines, default = declaration(root)
    refs, files = references(root, lines, default)
    if branch in lines:
        expected, mode = branch, f"branch {branch!r} is the declared line {branch!r}"
        wrong = [r for r in refs if r[3] != expected]
    else:
        named = sorted({r[3] for r in refs})
        expected = named[0] if len(named) == 1 else None
        mode = (f"branch {branch or '<none>'!r} is not a declared line ({', '.join(lines)}); "
                f"references must agree on ONE line — they name {named or 'none'}")
        wrong = refs if len(named) > 1 else []
    violations = [
        f"::error file={rel},line={n}::{kind} names core line `{ref}`, but this branch's line is "
        f"`{expected or 'ambiguous'}` ({mode}). Each line's branch references only itself (policy "
        f"major-lines-run-separately): `{text}`"
        for rel, n, kind, ref, text in wrong]
    summary = (f"check-core-line-refs: {files} workflow file(s) read, {len(refs)} reference(s) to a "
               f"declared line, {len(violations)} violation(s); {mode}")
    return violations, summary


def self_test() -> int:
    decl = json.dumps({"schema": 1, "defaultBranch": "main",
                       "lines": [{"branch": "main"}, {"branch": "main3"}]})
    lane = "jobs:\n  a:\n    uses: Systemorph/MeshWeaver/.github/workflows/x.yml@{r}\n"
    action = "jobs:\n  a:\n    steps:\n      - uses: Systemorph/MeshWeaver/.github/actions/core-file@{r}\n"
    corefile = "jobs:\n  a:\n    steps:\n      - run: \"$RUNNER_TEMP/mw-core-file\" {r} .github/scripts/x.py\n"
    raw = "jobs:\n  a:\n    steps:\n      - run: curl https://raw.githubusercontent.com/Systemorph/MeshWeaver/{r}/.github/x.py\n"
    api = "jobs:\n  a:\n    steps:\n      - run: gh api repos/Systemorph/MeshWeaver/contents/x.py?ref={r}\n"
    caller = "jobs:\n  a:\n    with:\n      scripts-ref: {r}\n"
    env = "env:\n  SCRIPTS_REF: {r}\n"
    default = ("on:\n  workflow_call:\n    inputs:\n      platform-ref:\n        description: x\n"
               "        type: string\n        default: {r}\n")
    checkout = ("jobs:\n  a:\n    steps:\n      - uses: actions/checkout@v7\n        with:\n"
                "          repository: Systemorph/MeshWeaver\n          ref: {r}\n          path: core\n")
    shapes = {"checkout": checkout, "lane": lane, "action": action, "core-file": corefile, "raw": raw, "api": api,
              "caller scripts-ref": caller, "env SCRIPTS_REF": env, "input default": default}
    cases = []
    for name, body in shapes.items():
        cases.append((f"main3 branch: {name} at main3", "main3", {"w.yml": body.format(r="main3")}, False))
        cases.append((f"main3 branch: {name} at main (v4)", "main3", {"w.yml": body.format(r="main")}, True))
        cases.append((f"main branch: {name} at main", "main", {"w.yml": body.format(r="main")}, False))
        cases.append((f"main branch: {name} at main3 (v3)", "main", {"w.yml": body.format(r="main3")}, True))
    cases += [
        ("a sha is not a line", "main3", {"w.yml": lane.format(r="a" * 40)}, False),
        ("another repo at main is not core", "main3", {"w.yml": "jobs:\n  a:\n    steps:\n      - uses: jlumbroso/free-disk-space@main\n"}, False),
        ("a comment naming main", "main3", {"w.yml": "# was uses: Systemorph/MeshWeaver/.github/workflows/x.yml@main\njobs: {}\n"}, False),
        ("a default of another input", "main3", {"w.yml": default.replace("platform-ref", "lane-id").format(r="main")}, False),
        ("stacked PR, all main3", "feature/x", {"w.yml": lane.format(r="main3"), "v.yml": caller.format(r="main3")}, False),
        ("stacked PR, all main", "feature/x", {"w.yml": lane.format(r="main")}, False),
        ("stacked PR, MIXED lines", "feature/x", {"w.yml": lane.format(r="main3"), "v.yml": caller.format(r="main")}, True),
        ("no branch at all, MIXED lines", "", {"w.yml": lane.format(r="main3") + "    with:\n      scripts-ref: main\n"}, True),
        ("main3 branch: core checkout with NO ref (default = main, v4)", "main3",
         {"w.yml": checkout.replace("          ref: {r}\n", "")}, True),
        ("main branch: core checkout with NO ref (default = main)", "main",
         {"w.yml": checkout.replace("          ref: {r}\n", "")}, False),
        ("core checkout at a resolved sha", "main3",
         {"w.yml": checkout.format(r="${{ steps.p.outputs.sha }}")}, False),
        ("a Plugins checkout with no ref is not core", "main3",
         {"w.yml": checkout.replace("MeshWeaver\n", "MeshWeaver.Plugins\n").replace("          ref: {r}\n", "")}, False),
        ("composite action file is read", "main3", {"../actions/a/action.yml": "runs:\n  steps:\n    - uses: Systemorph/MeshWeaver/.github/actions/x@main\n"}, True),
    ]
    failures = 0
    for name, branch, files, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github/workflows").mkdir(parents=True)
            (root / DECLARATION).write_text(decl, encoding="utf-8")
            for fn, body in files.items():
                target = (root / ".github/workflows" / fn).resolve()
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(body, encoding="utf-8")
            got = bool(check(root, branch)[0])
        failures += got != expect
        print(f"  {'ok  ' if got == expect else 'FAIL'} {name:<52} expected={'fire' if expect else 'silent'} "
              f"got={'fire' if got else 'silent'}")
    env_cases = [
        ({"GITHUB_EVENT_NAME": "pull_request", "GITHUB_BASE_REF": "main3", "GITHUB_REF_NAME": "1/merge"}, "main3"),
        ({"GITHUB_EVENT_NAME": "push", "GITHUB_REF_NAME": "main"}, "main"),
        ({"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF_NAME": "main3"}, "main3"),
    ]
    for env, want in env_cases:
        got = this_branch(env)
        failures += got != want
        print(f"  {'ok  ' if got == want else 'FAIL'} branch of {env['GITHUB_EVENT_NAME']:<44} expected={want} got={got}")
    with tempfile.TemporaryDirectory() as tmp:
        payload = Path(tmp) / "event.json"
        payload.write_text(json.dumps({"merge_group": {"base_ref": "refs/heads/main3"}}))
        got = this_branch({"GITHUB_EVENT_NAME": "merge_group", "GITHUB_EVENT_PATH": str(payload)})
    failures += got != "main3"
    print(f"  {'ok  ' if got == 'main3' else 'FAIL'} branch of merge_group{'':<38} expected=main3 got={got}")
    total = len(cases) + len(env_cases) + 1
    if failures:
        print(f"\n::error::check-core-line-refs self-test: {failures} of {total} case(s) wrong.")
        return 1
    print(f"\ncheck-core-line-refs self-test: {total}/{total} cases green (both directions, both lines).")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=".")
    ap.add_argument("--branch", default=None, help="the branch whose line to enforce (default: from the event)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    branch = args.branch if args.branch is not None else this_branch(dict(os.environ))
    violations, summary = check(Path(args.root), branch)
    for v in violations:
        print(v)
    print(summary)
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
