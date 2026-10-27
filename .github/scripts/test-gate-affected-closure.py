#!/usr/bin/env python3
"""EXECUTE `node-repo-gate.yml`'s "Compute the affected module closure" step against fixtures.

WHY. The step decides which packages the Tests-area gate installs and runs. It narrowed only
`pull_request`; a main PUSH always gated every package, although MeshWeaver.Plugins' change set
already knows the newest GREEN main run the push descends from (its `push-base`) and narrows its
other legs by it (MeshWeaver.Plugins#4313). The `push-base` input mirrors the pull-request path for
`<push-base>..HEAD`. A narrowing step owes proof in BOTH directions: that it narrows exactly what it
was given, and that every doubt about its input is a FULL run that says why — never a silently
smaller gate.

The step is EXTRACTED FROM THE WORKFLOW by its `id:` and executed, never copied here, because a
copy passes while the lane rots. The caller's `scripts/affected-modules.py` is a STUB that records
the range it was handed: its own semantics are pinned by its own --self-test in the repo that owns it.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import workflow_yaml  # noqa: E402 — the ONE step extractor every harness shares

WORKFLOW = ".github/workflows/node-repo-gate.yml"
STEP_ID = "affected"
FAILURES: list[str] = []

STUB = (
    "import json, sys, pathlib\n"
    "a = sys.argv\n"
    "pathlib.Path('stub-called.txt').write_text(a[a.index('--range') + 1])\n"
    "json.dump({'mount': ['Alpha'], 'runAll': False}, sys.stdout)\n")


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=root, check=True, capture_output=True, text=True,
                          env={**os.environ, "GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "gc.auto",
                               "GIT_CONFIG_VALUE_0": "0"}).stdout.strip()


def fixture(root: Path) -> dict[str, str]:
    git(root, "init", "-q", "-b", "main")
    git(root, "config", "user.email", "t@example.test")
    git(root, "config", "user.name", "t")
    (root / "scripts").mkdir()
    (root / "scripts" / "affected-modules.py").write_text(STUB, encoding="utf-8")
    (root / "Alpha").mkdir()
    (root / "Alpha" / "index.json").write_text("{}\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-qm", "base")
    base = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-qb", "side")
    (root / "Alpha" / "side.md").write_text("x\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-qm", "side")
    side = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "main")
    (root / "Alpha" / "index.json").write_text('{"x": 1}\n', encoding="utf-8")
    git(root, "commit", "-qam", "content")
    content = git(root, "rev-parse", "HEAD")
    (root / "scripts" / "gate.py").write_text("# ci\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-qm", "a CI path")
    head = git(root, "rev-parse", "HEAD")
    # WIDE: enough CI paths that `git diff --name-only` writes far past a pipe buffer (~64 KiB), so a
    # reader that stops after the first line SIGPIPEs the writer.
    git(root, "checkout", "-qb", "wide", content)
    many = root / ".github" / "many"
    many.mkdir(parents=True)
    for i in range(4000):
        (many / f"generated-ci-input-{i:05d}-{'x' * 80}.yml").write_text("on: push\n", encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-qm", "many CI paths")
    wide = git(root, "rev-parse", "HEAD")
    git(root, "checkout", "-q", "main")
    return {"base": base, "side": side, "content": content, "head": head, "wide": wide}


def run(root: Path, body: str, head: str, **env: str) -> tuple[int, dict[str, str], str, str | None]:
    git(root, "checkout", "-q", head)
    called = root / "stub-called.txt"
    called.unlink(missing_ok=True)
    (root / "affected.json").unlink(missing_ok=True)
    with tempfile.TemporaryDirectory() as raw:
        out = Path(raw) / "outputs"
        out.touch()
        script = Path(raw) / "step.sh"
        script.write_text(body, encoding="utf-8")
        full_env = {**os.environ, "NARROWING": "true", "ADOPTED_VERDICT": "off", "SCRIPTS_REF": "",
                    "CALLER_WORKFLOW": "ci.yml", "GH_TOKEN": "", "FRESH_MAIN": "", "EVENT": "push",
                    "BASE_REF": "", "REF_NAME": "main", "PUSH_BASE": "", "RUNNER_TEMP": raw,
                    "GITHUB_OUTPUT": str(out), "GITHUB_STEP_SUMMARY": str(Path(raw) / "summary"), **env}
        result = subprocess.run(["bash", str(script)], cwd=root, capture_output=True, text=True, env=full_env)
        rows = dict(line.split("=", 1) for line in out.read_text().splitlines() if "=" in line)
    return (result.returncode, rows, result.stdout + result.stderr,
            called.read_text() if called.exists() else None)


def case(name: str, got: tuple, *, rc: int = 0, run_all: str | None, stub: str | None | bool,
         says: str = "") -> None:
    code, rows, log, called = got
    problems = []
    if code != rc:
        problems.append(f"exit {code}, expected {rc}")
    if run_all is not None and rows.get("run_all") != run_all:
        problems.append(f"run_all={rows.get('run_all')!r}, expected {run_all!r}")
    if stub is False and called is not None:
        problems.append(f"the caller's selector was invoked ({called}) — a full run must not ask it")
    if isinstance(stub, str) and called != stub:
        problems.append(f"the selector was handed {called!r}, expected {stub!r}")
    if says and says not in log:
        problems.append(f"the log does not say {says!r}")
    if problems:
        FAILURES.append(name)
        print(f"  FAIL {name} — {'; '.join(problems)}\n{log[-600:]}")
    else:
        print(f"  ok   {name}")


def main() -> int:
    root_dir = Path(__file__).resolve().parents[2]
    try:
        body = workflow_yaml.step_run(root_dir / WORKFLOW, STEP_ID)
    except workflow_yaml.WorkflowYamlError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    with tempfile.TemporaryDirectory() as raw:
        root = Path(raw) / "repo"
        root.mkdir()
        c = fixture(root)
        print("a main PUSH given a push-base narrows exactly like a pull request:")
        case("a valid push-base hands `<push-base>..HEAD` to the caller's selector",
             run(root, body, c["content"], PUSH_BASE=c["base"]),
             run_all="false", stub=f"{c['base']}..HEAD", says="narrowed to the closure")
        print("every doubt is a FULL run that says why (negative controls):")
        case("no push-base ⇒ full, said", run(root, body, c["content"]),
             run_all="true", stub=False, says="no push-base was given")
        case("a push-base that is not a full sha ⇒ full", run(root, body, c["content"], PUSH_BASE="main"),
             run_all="true", stub=False, says="is not a full commit sha")
        case("a push-base not in the checkout ⇒ full", run(root, body, c["content"], PUSH_BASE="f" * 40),
             run_all="true", stub=False, says="is not in this checkout")
        case("a push-base off HEAD's line ⇒ full", run(root, body, c["content"], PUSH_BASE=c["side"]),
             run_all="true", stub=False, says="is not an ancestor of HEAD")
        case("a push-base equal to HEAD ⇒ full (an empty range is a broken input)",
             run(root, body, c["content"], PUSH_BASE=c["content"]),
             run_all="true", stub=False, says="IS HEAD")
        case("a range touching scripts/ ⇒ full (policy ci-paths-owe-no-module-leg)",
             run(root, body, c["head"], PUSH_BASE=c["base"]),
             run_all="true", stub=False, says="touches the CI path scripts/gate.py")
        case("a range with MORE CI paths than a pipe holds ⇒ full, never a SIGPIPE abort under pipefail",
             run(root, body, c["wide"], PUSH_BASE=c["base"]),
             run_all="true", stub=False, says="touches the CI path .github/many/")
        sigpipe = body.replace("| sed -n '1p')", "| head -n1)")
        if sigpipe == body:
            FAILURES.append("non-vacuity: the step no longer reads its CI path with `| sed -n '1p'`")
            print("  FAIL non-vacuity — the step's CI-path reader moved; re-point this control")
        else:
            code = run(root, sigpipe, c["wide"], PUSH_BASE=c["base"])[0]
            case("NON-VACUITY: the same fixture read with `head -n1` DOES abort the plan (exit "
                 f"{code}) — the case above is not passing on a small pipe",
                 (0 if code != 0 else 1, {}, "", None), run_all=None, stub=None)
        case("affected-narrowing false ⇒ full even with a push-base",
             run(root, body, c["content"], PUSH_BASE=c["base"], NARROWING="false"),
             run_all="true", stub=False)
        for event in ("schedule", "workflow_dispatch", "repository_dispatch"):
            case(f"{event} ignores a push-base ⇒ full",
                 run(root, body, c["content"], PUSH_BASE=c["base"], EVENT=event), run_all="true", stub=False)
        print("the pull-request path is unchanged:")
        case("a pull_request diffs the fresh-merge tip, push-base or not",
             run(root, body, c["content"], EVENT="pull_request", FRESH_MAIN=c["base"], PUSH_BASE=c["side"]),
             run_all="false", stub=f"{c['base']}...HEAD")
        case("a narrowed pull_request with no fresh-merge tip still refuses",
             run(root, body, c["content"], EVENT="pull_request"), rc=1, run_all=None, stub=False,
             says="has no fresh-merge base tip")
    if FAILURES:
        print(f"\n::error title=gate affected-closure step::{len(FAILURES)} case(s) failed: {', '.join(FAILURES)}")
        return 1
    print("\nok: the gate's affected-closure step narrows a push only by a valid push-base, and every doubt is full")
    return 0


if __name__ == "__main__":
    sys.exit(main())
