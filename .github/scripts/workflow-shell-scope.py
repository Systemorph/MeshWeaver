#!/usr/bin/env python3
"""workflow-shell-scope.py — does this change set owe CI's own shell? (Systemorph/MeshWeaver)

Doc: Doc/Architecture/CiOwnShellScope. The `CI's own shell` self-test jobs of dotnet-test.yml
(`workflow-shell`, `-executions`, `-executions-publish`, `-executions-msbuild`) prove the CI's own
code: workflows, composite actions, `.github/scripts`, `scripts/`, `deploy/` and the MSBuild files
their harnesses evaluate. Running them on a pull request that changes none of that proves nothing
new and spent about 870 runner-minutes a day (Doc/Architecture/CiProfile2026-11, R1). This script
decides, ONCE, whether they are owed — and the decision is printed and recorded in the run, so a
skipped self-test job can never be mistaken for a pass on a change that touched their inputs:
`Consolidate test results` accepts `skipped` from those jobs ONLY when this job succeeded and said
`run=false`.

THE RULE — deliberately an allow-list of what is INERT, so anything unknown RUNS them:
  * not a pull request (push, merge_group, schedule, dispatch) → run (`--all`);
  * the change set cannot be read, or is empty → run (UNKNOWN fails safe);
  * a changed path is inert only when ALL of these hold:
      - it sits under `src/`, `test/`, `samples/`, `docs/` or `.claude/` (the product and its
        prose — every other top-level path, root files included, is CI input or unknown);
      - it is not MSBuild or solution input (`*.csproj|props|targets`, `*.sln[x]`) and not a
        script or workflow-shaped file (`*.py|sh|bash|ps1|yml|yaml`), which the self-tests
        evaluate or scan wherever they sit;
      - no self-test names it: a path NAMED_INPUTS lists, or one a `--self-test`/harness script
        names literally (derived from the tree, see `named_inputs`), is input wherever it sits;
  * every changed path inert → run=false; otherwise run=true, naming the paths that owe it.

The whole-tree guards that READ product files (the Key Vault command scan, the build-stage census
of test sources, the node-tree path and retired-field scans) are NOT path-selected: they run in
`workflow-shell-tree` on every pull request, so this rule never has to classify what they read.

Output (stdout, and `$GITHUB_OUTPUT` when set): `run=true|false`, `reason=<one line>`,
`changed=<count>`. `--self-test` proves the rule both ways, including the negative control that a
change under `.github/scripts` RUNS the self-tests, over synthetic paths, a throwaway git repository
shaped like a pull request's merge commit, and the real tree.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

INERT_TOPS = ("src/", "test/", "samples/", "docs/", ".claude/")
# Input wherever it sits: MSBuild/solution files (the MSBuild-reading harnesses evaluate the real
# projects; the template feed and the build-stage census read them) and script/workflow-shaped files
# (the shell, path-listing and core-line-reference scans read them by suffix).
INPUT_SUFFIXES = (".csproj", ".props", ".targets", ".slnx", ".sln", ".py", ".sh", ".bash", ".ps1",
                  ".yml", ".yaml")
# Files under an inert top that a path-selected self-test reads by name. Measured with an audit hook
# over every step of the four jobs (the PR body carries the method); `named_inputs` adds every literal
# the scripts spell, so a new one cannot be forgotten here.
NAMED_INPUTS = (
    # release-channels.py check reads its edition table (the audit's one product file read by name).
    "src/MeshWeaver.Documentation/Data/Architecture/ReleaseEditions.md",
)
# Where a self-test's literal path may be spelled: the CI's own code. A literal in a FIXTURE names a
# file that does not exist and so matches nothing; only literals naming an existing path count.
LITERAL_HOMES = (".github/scripts", ".github/actions", "scripts", "deploy/core-tests")
LITERAL = re.compile(r"(?<![A-Za-z0-9_.\-/])((?:src|test|samples|docs|\.claude)/[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+)")


def named_inputs(root: Path) -> set[str]:
    """NAMED_INPUTS plus every existing FILE under an inert top that the CI's own code spells
    literally (two segments at least, no glob, and it must exist — a fixture's path matches nothing)."""
    out = set(NAMED_INPUTS)
    for home in LITERAL_HOMES:
        base = root / home
        if not base.is_dir():
            continue
        for f in base.rglob("*"):
            if not f.is_file() or f.suffix in (".png", ".zip", ".nupkg", ".dll"):
                continue
            try:
                text = f.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            for m in LITERAL.finditer(text):
                lit = m.group(1).rstrip(".")
                p = root / lit
                # FILES only: a directory a script walks is a whole-tree read, and those guards run
                # in `workflow-shell-tree` on every pull request instead of being path-selected.
                if p.is_file():
                    out.add(lit)
    return out


def owes(path: str, named: set[str]) -> str | None:
    """None when `path` is inert for CI's own shell; otherwise why it is input."""
    if not path.startswith(INERT_TOPS):
        return "outside src/, test/, samples/, docs/ and .claude/ — CI input or unknown"
    # Case-insensitive: the fail-safe direction — a `.Yml` or `.CSPROJ` is input, never inert.
    if path.lower().endswith(INPUT_SUFFIXES):
        return "an MSBuild, solution, script or workflow-shaped file — the self-tests evaluate or scan it"
    for n in named:
        if path == n or (n.endswith("/") and path.startswith(n)):
            return f"named by a self-test ({show(n)})"
    return None


def show(path: str) -> str:
    """A changed path as LITERAL, single-line text, safe in every place the reason lands.

    File names are pull-request controlled, and the reason is written into `$GITHUB_OUTPUT`
    (one `key=value` per line), into a `::notice::` workflow command and into the Markdown run
    summary. So: `%`, CR and LF are escaped the way GitHub's command parser requires (`%25`, `%0D`,
    `%0A` — no line break can start a second command or a second output key), `:` becomes `%3A` (no
    `::` sequence survives), and the result is a Markdown code span with every backtick replaced, so
    the summary renders the name as text, never as markup."""
    esc = (path.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
               .replace(":", "%3A").replace("`", "'"))
    return f"`{esc}`"


def classify(changed: list[str], named: set[str]) -> tuple[bool, str]:
    if not changed:
        return True, "the change set is empty or could not be read — UNKNOWN runs CI's own shell"
    owing = [(p, why) for p in changed if (why := owes(p, named))]
    if owing:
        shown = "; ".join(f"{show(p)} ({why})" for p, why in owing[:3])
        more = f" and {len(owing) - 3} more" if len(owing) > 3 else ""
        return True, f"{len(owing)} of {len(changed)} changed path(s) are CI's own shell's input: {shown}{more}"
    return False, (f"all {len(changed)} changed path(s) are product source or prose no self-test reads — "
                   "CI's own shell is not owed (its whole-tree guards still ran)")


def changed_in_merge(root: Path) -> list[str] | None:
    """The pull request's own change set: the merge commit GitHub checks out (refs/pull/N/merge)
    against its FIRST parent, the base it merged onto. None when HEAD is not a two-parent merge or
    git cannot answer — the caller then runs everything."""
    parents = subprocess.run(["git", "-C", str(root), "rev-list", "--parents", "-n", "1", "HEAD"],
                             capture_output=True, text=True)
    if parents.returncode != 0 or len(parents.stdout.split()) != 3:
        return None
    r = subprocess.run(["git", "-C", str(root), "diff", "-z", "--no-renames", "--name-only", "HEAD^1", "HEAD"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        return None
    return [p for p in r.stdout.split("\0") if p]


# ── self-test ────────────────────────────────────────────────────────────────────────────────

def self_test(root: Path) -> int:
    failures: list[str] = []

    def case(label: str, got: bool, want: bool) -> None:
        if got != want:
            failures.append(f"{label}: run={got}, expected run={want}")

    named = {"src/A/Named.cs", "src/MeshWeaver.Documentation/Data/Named/"}
    # NEGATIVE CONTROL first: a change to CI's own code must run its self-tests.
    case("NEGATIVE CONTROL: a .github/scripts change runs them", classify([".github/scripts/x.py"], named)[0], True)
    case("a workflow change runs them", classify([".github/workflows/dotnet-test.yml"], named)[0], True)
    case("a composite action change runs them", classify([".github/actions/a/action.yml"], named)[0], True)
    case("a scripts/ change runs them", classify(["scripts/check-x.py"], named)[0], True)
    case("a deploy/ change runs them", classify(["deploy/core-tests/core-tests.py"], named)[0], True)
    case("a root file runs them", classify(["Directory.Build.props"], named)[0], True)
    case("an unknown top-level path runs them", classify(["newdir/x.txt"], named)[0], True)
    case("a csproj under src runs them", classify(["src/A/A.csproj"], named)[0], True)
    case("a props file under test runs them", classify(["test/Directory.Build.props"], named)[0], True)
    case("a .py under test runs them", classify(["test/A/tool.py"], named)[0], True)
    case("a .yml under src runs them", classify(["src/A/x.yml"], named)[0], True)
    case("a named file runs them", classify(["src/A/Named.cs"], named)[0], True)
    case("a file under a named directory runs them", classify(["src/MeshWeaver.Documentation/Data/Named/x.md"], named)[0], True)
    case("an empty change set runs them", classify([], named)[0], True)
    case("one input among product files runs them", classify(["src/A/B.cs", ".github/scripts/x.py"], named)[0], True)
    # …and the saving: the product alone does not.
    case("C# source alone does not", classify(["src/A/B.cs", "test/A.Test/BTest.cs"], named)[0], False)
    case("a doc page alone does not", classify(["src/MeshWeaver.Documentation/Data/Architecture/X.md"], named)[0], False)
    case("a sample node alone does not", classify(["samples/Graph/Data/X.json"], named)[0], False)
    case("a near-miss of a named file does not", classify(["src/A/Named.cs.bak"], named)[0], False)
    # Suffixes match case-insensitively — the fail-safe direction.
    case("a .Yml under src runs them", classify(["src/A/x.Yml"], named)[0], True)
    case("a .CSPROJ under src runs them", classify(["src/A/A.CSPROJ"], named)[0], True)

    # A HOSTILE file name must not inject a workflow command, an output key or Markdown: the reason
    # lands in $GITHUB_OUTPUT, in `::notice ...::$reason` and in the run summary.
    hostile = [".github/x%0A::error::owned\n::add-mask::secret\r`run=false`.md",
               "docs/y\nrun=false\n::stop-commands::tok.yml"]
    run, reason = classify(hostile, named)
    case("a hostile .github path still runs them", run, True)
    notice = f"::notice title=CI's own shell::{reason}"
    for label, bad in (("an unescaped LF", "\n"), ("an unescaped CR", "\r"), ("a `::` sequence", "::")):
        if bad in reason:
            failures.append(f"hostile path: the reason carries {label}: {reason!r}")
    if notice.count("::") != 2 or "\n" in notice:
        failures.append(f"hostile path: the notice line is not ONE command: {notice!r}")
    if "%250A" not in reason or "%0A" not in reason or "%0D" not in reason:
        failures.append(f"hostile path: %, LF and CR were not escaped the way GitHub requires: {reason!r}")
    for p in hostile:
        if show(p).count("`") != 2:
            failures.append(f"hostile path: a backtick survived inside its code span: {show(p)!r}")
    out_lines = "\n".join([f"run={'true' if run else 'false'}", f"reason={reason}", "changed=2"]).split("\n")
    if [l.split("=", 1)[0] for l in out_lines] != ["run", "reason", "changed"]:
        failures.append(f"hostile path: $GITHUB_OUTPUT would carry extra keys: {out_lines!r}")

    # The literal derivation reads `.claude/` like the other inert tops (a file a script names there).
    with tempfile.TemporaryDirectory() as tmp:
        t = Path(tmp)
        (t / ".claude/skills/x").mkdir(parents=True); (t / ".claude/skills/x/SKILL.md").write_text("x\n")
        (t / ".github/scripts").mkdir(parents=True)
        (t / ".github/scripts/reader.py").write_text("open('.claude/skills/x/SKILL.md')\n")
        got = named_inputs(t)
        if ".claude/skills/x/SKILL.md" not in got:
            failures.append(f"a .claude/ file a script names literally was not derived as input: {sorted(got)}")
        case("a .claude/ file a script names runs them", classify([".claude/skills/x/SKILL.md"], got)[0], True)

    # End to end through git: a throwaway repository with a pull-request-shaped merge commit.
    with tempfile.TemporaryDirectory() as tmp:
        t = Path(tmp)
        def git(*a: str) -> None:
            subprocess.run(["git", "-C", tmp, *a], check=True, capture_output=True,
                           env={**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
                                "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"})
        def write(p: str, s: str) -> None:
            (t / p).parent.mkdir(parents=True, exist_ok=True); (t / p).write_text(s)
        git("init", "-q", "-b", "main")
        write("src/A/B.cs", "class B {}\n"); write(".github/scripts/x.py", "print(1)\n")
        git("add", "-A"); git("commit", "-q", "-m", "base")
        for label, path, want in (("NEGATIVE CONTROL (git): a .github/scripts change in the merge runs them", ".github/scripts/x.py", True),
                                  ("(git) a C# change alone does not", "src/A/B.cs", False)):
            git("checkout", "-q", "-B", "topic", "main")   # never "head": HEAD on a case-insensitive disk
            write(path, "changed\n"); git("commit", "-q", "-am", "head")
            git("checkout", "-q", "main"); write("src/A/Main.cs", f"// {label}\n"); git("add", "-A"); git("commit", "-q", "-m", "main moves")
            git("checkout", "-q", "--detach", "main"); git("merge", "-q", "--no-ff", "-m", "merge", "topic")
            got = changed_in_merge(t)
            if got != [path]:
                failures.append(f"{label}: the merge's change set read {got}, expected [{path!r}] — main's own move must not count")
            case(label, classify(got or [], set())[0], want)
            git("checkout", "-q", "main")
        git("checkout", "-q", "--detach", "main")
        if changed_in_merge(t) is not None:
            failures.append("a non-merge HEAD was read as a change set — it must be None (UNKNOWN runs everything)")

    # The real tree: every file of CI's own code classifies as input, and so does every named one.
    if (root / ".github/workflows/dotnet-test.yml").is_file():
        real = named_inputs(root)
        for p in NAMED_INPUTS:
            if not (root / p).is_file():
                failures.append(f"NAMED_INPUTS lists {p}, which does not exist — a stale entry protects nothing")
        for home in (".github", "scripts", "deploy"):
            for f in (root / home).rglob("*"):
                if f.is_file():
                    rel = str(f.relative_to(root)).replace(os.sep, "/")
                    if owes(rel, real) is None:
                        failures.append(f"real tree: {rel} classified inert — CI's own code must always run its self-tests")
        for n in sorted(real):
            probe = n + "probe.md" if n.endswith("/") else n
            if owes(probe, real) is None:
                failures.append(f"real tree: named input {n} classified inert")
        print(f"  real tree: {len(real)} named input(s) under the product tops")

    for f in failures:
        print(f"FAIL: {f}")
    print(f"workflow-shell-scope self-test: {'FAIL' if failures else 'ok'} ({len(failures)} failure(s))")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=".")
    ap.add_argument("--all", action="store_true", help="not a pull request: CI's own shell runs")
    ap.add_argument("--files", help="classify these newline-separated paths (a file, or - for stdin) instead of HEAD's merge")
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--explain", action="store_true", help="print the named inputs")
    a = ap.parse_args()
    root = Path(a.root).resolve()
    if a.self_test:
        return self_test(root)
    named = named_inputs(root)
    if a.explain:
        print("\n".join(sorted(named)))
        return 0
    if a.all:
        run, reason, changed = True, "not a pull request (push, merge group, schedule or dispatch) — CI's own shell runs", []
    else:
        if a.files is not None:
            src = sys.stdin.read() if a.files == "-" else Path(a.files).read_text(encoding="utf-8")
            changed = [l.strip() for l in src.splitlines() if l.strip()]
        else:
            changed = changed_in_merge(root) or []
        run, reason = classify(changed, named)
    lines = [f"run={'true' if run else 'false'}", f"reason={reason}", f"changed={len(changed)}"]
    print("\n".join(lines))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
