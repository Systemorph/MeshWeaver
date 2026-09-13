#!/usr/bin/env python3
"""affected-tests.py — which test projects a change set owes (Systemorph/MeshWeaver, 2026-09-13).

Maintainer: "tests only for changed functionality" — the fleet rule (Plugins `.github/ci-tests.json`
since 2026-09-12), applied to core. dotnet-test.yml ran every test project on every run (5 shards,
~21 of a run's 31 job-minutes) whatever the change touched. This script maps the diff to the
ProjectReference graph and names the test projects whose transitive references reach a changed
project — or says ALL when the change is anything but project-scoped, or NONE when nothing a test
could see changed.

Rules (deliberately conservative — a wrong "none" is a skipped gate, a wrong "all" costs minutes):
  * `src/<P>/…` or `test/<P>/…` → project P changed; a test project is owed when its transitive
    ProjectReference closure contains a changed project, or it changed itself.
  * `docs/**`, `.claude/**`, `*.md` anywhere, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/**`,
    `.github/*.txt` → nothing a test reads: contributes no project and forces nothing.
  * anything else (workflows, `.github/scripts`, `Directory.*.props|targets`, `global.json`,
    `*.slnx`, `test/*.allow`, `test/appsettings.json`, `tools/**`, `deploy/**`, `Doc/**`, …) → ALL.
  * an empty diff, a `merge_group`, a `push` to main, a `schedule` or `workflow_dispatch` → ALL
    (the caller decides the event; `--all` forces it).

Output (stdout, and `$GITHUB_OUTPUT` when set): `mode=all|incremental|none`,
`projects=<comma-separated test project names>` (empty for all/none), `reason=<one line>`.
`--self-test` runs the rules against a synthetic graph and exits non-zero on any wrong answer.
"""
from __future__ import annotations
import argparse, os, re, subprocess, sys
from pathlib import Path

SAFE = re.compile(r"^(docs/|\.claude/|CODEOWNERS$|\.github/ISSUE_TEMPLATE/|\.github/[^/]+\.txt$)|\.md$")
PROJECT = re.compile(r"^(src|test)/([^/]+)/")

def project_graph(root: Path) -> tuple[dict[str, set[str]], set[str]]:
    """name → referenced project names (direct), and the set of names under test/."""
    refs: dict[str, set[str]] = {}; tests: set[str] = set()
    for top in ("src", "test", "tools"):
        for csproj in (root / top).rglob("*.csproj"):
            if "/bin/" in str(csproj) or "/obj/" in str(csproj): continue
            name = csproj.stem
            text = csproj.read_text(encoding="utf-8", errors="replace")
            refs[name] = {Path(m.replace("\\", "/")).stem for m in re.findall(r'ProjectReference\s+Include="([^"]+)"', text)}
            if top == "test": tests.add(name)
    return refs, tests

def closure(name: str, refs: dict[str, set[str]], memo: dict[str, set[str]]) -> set[str]:
    if name in memo: return memo[name]
    memo[name] = set(); out = set()
    stack = list(refs.get(name, ()))
    while stack:
        n = stack.pop()
        if n in out: continue
        out.add(n); stack.extend(refs.get(n, ()))
    memo[name] = out; return out

def classify(changed: list[str], refs: dict[str, set[str]], tests: set[str]) -> tuple[str, list[str], str]:
    if not changed:
        return "all", [], "no diff could be computed — the full suite runs"
    projects: set[str] = set(); forcing: list[str] = []; ignored = 0
    for f in changed:
        m = PROJECT.match(f)
        if m:
            projects.add(m.group(2))
        elif SAFE.search(f):
            ignored += 1
        else:
            forcing.append(f)
    if forcing:
        return "all", [], f"{forcing[0]}{' +%d more' % (len(forcing)-1) if len(forcing) > 1 else ''} is outside src/ and test/ — the full suite runs"
    if not projects:
        return "none", [], f"{len(changed)} changed file(s) are docs/markdown only — no test project reads them"
    memo: dict[str, set[str]] = {}
    owed = sorted(t for t in tests if t in projects or (closure(t, refs, memo) & projects))
    if not owed:
        return "none", [], f"changed project(s) {', '.join(sorted(projects))} are referenced by no test project"
    return "incremental", owed, f"{len(owed)} of {len(tests)} test project(s) reach {', '.join(sorted(projects))}"

def self_test() -> int:
    refs = {"Core": set(), "Data": {"Core"}, "Hosting": {"Data"}, "Data.Test": {"Data", "Fixture"}, "Hosting.Test": {"Hosting", "Fixture"}, "Fixture": {"Core"}, "Other.Test": {"Core"}, "Lonely": set()}
    tests = {"Data.Test", "Hosting.Test", "Other.Test", "Fixture"}
    def ok(changed, mode, projects=None):
        m, p, r = classify(changed, refs, tests)
        assert m == mode and (projects is None or p == projects), f"{changed} → {m} {p} ({r})"
    ok(["src/Data/X.cs"], "incremental", ["Data.Test", "Hosting.Test"])
    ok(["src/Core/X.cs"], "incremental", ["Data.Test", "Fixture", "Hosting.Test", "Other.Test"])
    ok(["test/Other.Test/T.cs"], "incremental", ["Other.Test"])
    ok(["src/Lonely/X.cs"], "none")
    ok(["docs/a.md", "README.md", ".claude/skills/x/SKILL.md"], "none")
    ok(["src/Data/X.cs", ".github/workflows/dotnet-test.yml"], "all")
    ok(["Directory.Packages.props"], "all"); ok(["test/AwaitedMeshReadSites.allow"], "all"); ok(["Doc/Overview.md"], "none")
    ok([], "all")
    print("✓ affected-tests self-test: closure, safe paths, forcing paths, empty diff"); return 0

def main() -> int:
    ap = argparse.ArgumentParser(); ap.add_argument("--base"); ap.add_argument("--head", default="HEAD"); ap.add_argument("--all", action="store_true"); ap.add_argument("--self-test", action="store_true"); ap.add_argument("--root", default=".")
    a = ap.parse_args()
    if a.self_test: return self_test()
    root = Path(a.root).resolve()
    if a.all or not a.base:
        mode, projects, reason = "all", [], "not a pull request (merge group, push, schedule or dispatch) — the full suite runs"
    else:
        r = subprocess.run(["git", "-C", str(root), "diff", "--name-only", f"{a.base}...{a.head}"], capture_output=True, text=True)
        if r.returncode != 0:
            r = subprocess.run(["git", "-C", str(root), "diff", "--name-only", a.base, a.head], capture_output=True, text=True)
        changed = [l.strip() for l in r.stdout.splitlines() if l.strip()] if r.returncode == 0 else []
        refs, tests = project_graph(root)
        mode, projects, reason = classify(changed, refs, tests)
    lines = [f"mode={mode}", f"projects={','.join(projects)}", f"reason={reason}"]
    print("\n".join(lines))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f: f.write("\n".join(lines) + "\n")
    return 0

if __name__ == "__main__": sys.exit(main())
