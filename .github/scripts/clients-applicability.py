#!/usr/bin/env python3
"""clients-applicability.py — can this event start a `Clients` workflow run at all? (Systemorph/MeshWeaver)

Doc: Doc/Architecture/ClientsVerdict. Used by the `Clients workflow verdict` job of dotnet-test.yml.

WHY THIS EXISTS (CI profile R10, Doc/Architecture/CiProfile2026-11)
------------------------------------------------------------------
`Clients workflow verdict` reads the newest `Clients` run for the commit under test and reports its
conclusion into `Consolidate test results`. `clients.yml` is path-filtered, so most change sets start
no Clients run — and "zero runs" is ambiguous at the moment the gate first looks: both workflows fire
on the same event, so zero can also mean "GitHub has not CREATED the run yet". The job therefore
believed a zero only after 180 s of consecutive zeros. Measured: the job spent 3.1 min polling in every
run, about 250 runner-minutes a day, almost all of it waiting out that grace period on change sets
that could never have started a Clients run.

This script removes the ambiguity instead of the wait: it applies `clients.yml`'s OWN trigger (read
from the workflow file at the commit under test, never a copy) to the change set's file list. When no
changed path matches, no Clients run is coming, and the gate may believe a zero on its first read.

THE RULE — anything this script cannot PROVE is `unknown`, and `unknown` keeps the full grace period:
  * only `pull_request` and `push` can be decided (the only events `clients.yml` triggers on);
  * the trigger must be plain `paths:` globs (no `paths-ignore`, no `!` negation, no `?`/`[`/`+`
    pattern characters) and, for `push`, a `branches:` list of literal names;
  * the change-set listing must be COMPLETE: non-empty, its count equal to `--expected` when given,
    and shorter than `--truncated-at` when given (the compare API stops listing at 300 files);
  * a renamed file is matched under BOTH its new and its previous name.

Output (stdout, and `$GITHUB_OUTPUT` when set): `applicable=true|false|unknown` and `reason=<one line>`.
`false` is the ONLY answer that shortens anything, and it does not make the gate green on its own: the
gate still reads the runs once and gates on any Clients run it finds for the commit.

`--self-test` proves the rule both ways over synthetic change sets and the real clients.yml,
including the negative controls that a change under clients/ or to the protos is `true`.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
from pathlib import Path

DECIDABLE_EVENTS = ("pull_request", "push")
UNSUPPORTED_PATTERN_CHARS = set("?[]+")


def glob_to_regex(pattern: str) -> re.Pattern[str]:
    """GitHub filter-pattern semantics for the subset this script accepts: `**` matches any
    characters including `/`, `*` matches any characters except `/`, everything else is literal."""
    out = []
    i = 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return re.compile("".join(out) + r"\Z")


def load_trigger(workflow: Path, event: str) -> tuple[dict | None, str]:
    import yaml  # noqa: PLC0415 — preinstalled on the hosted runner, as for the sibling scripts

    if not workflow.is_file():
        return None, f"{workflow} does not exist"
    try:
        doc = yaml.safe_load(workflow.read_text(encoding="utf-8"))
    except yaml.YAMLError as e:
        return None, f"{workflow.name} does not parse: {str(e).splitlines()[0]}"
    if not isinstance(doc, dict):
        return None, f"{workflow.name} does not parse to a mapping"
    # PyYAML reads the bare key `on` as the boolean True.
    on = doc.get("on", doc.get(True))
    if isinstance(on, (str, list)):
        names = [on] if isinstance(on, str) else on
        if event in names:
            return None, f"{workflow.name} triggers on {event} with no path filter"
        return {"never": True}, ""
    if not isinstance(on, dict):
        return None, f"{workflow.name} has no readable `on:` block"
    if event not in on:
        return {"never": True}, ""
    trigger = on[event]
    if not isinstance(trigger, dict):
        return None, f"{workflow.name} triggers on {event} with no path filter"
    return trigger, ""


def decide(workflow: Path, event: str, ref_name: str, rows: list[list[str]],
           expected: int | None, truncated_at: int | None) -> tuple[str, str]:
    if event not in DECIDABLE_EVENTS:
        return "unknown", f"event '{event}' is not one this script decides"
    trigger, why = load_trigger(workflow, event)
    if trigger is None:
        return "unknown", why
    if trigger.get("never"):
        return "false", f"{workflow.name} has no {event} trigger"
    for key in ("paths-ignore", "branches-ignore", "tags", "tags-ignore"):
        if key in trigger:
            return "unknown", f"{workflow.name}'s {event} trigger carries `{key}`, which this script does not evaluate"
    if event == "push":
        branches = trigger.get("branches")
        if branches is not None:
            if not isinstance(branches, list) or not all(isinstance(b, str) for b in branches):
                return "unknown", f"{workflow.name}'s push `branches:` is not a list of names"
            if any(set(b) & (UNSUPPORTED_PATTERN_CHARS | {"*", "!"}) for b in branches):
                return "unknown", f"{workflow.name}'s push `branches:` uses patterns"
            if not ref_name:
                return "unknown", "no branch name given for a push event"
            if ref_name not in branches:
                # A `false` must be PROVEN. A name that matches only when case is ignored is
                # undecided here, whichever way GitHub compares case.
                if ref_name.casefold() in {b.casefold() for b in branches}:
                    return "unknown", f"'{ref_name}' matches {workflow.name}'s push `branches:` only when case is ignored"
                return "false", f"{workflow.name} does not trigger on a push to '{ref_name}'"
    paths = trigger.get("paths")
    if paths is None:
        return "unknown", f"{workflow.name}'s {event} trigger has no `paths:` filter"
    if not isinstance(paths, list) or not paths or not all(isinstance(p, str) and p for p in paths):
        return "unknown", f"{workflow.name}'s {event} `paths:` is not a non-empty list of globs"
    for p in paths:
        if p.startswith("!") or set(p) & UNSUPPORTED_PATTERN_CHARS:
            return "unknown", f"pattern '{p}' uses syntax this script does not evaluate"
    if not rows:
        return "unknown", "the change set is empty or could not be listed"
    if expected is not None and len(rows) != expected:
        return "unknown", f"the listing holds {len(rows)} file(s) but the change set has {expected} — incomplete"
    if truncated_at is not None and len(rows) >= truncated_at:
        return "unknown", f"the listing reached its {truncated_at}-file cap and may be truncated"
    regexes = [(p, glob_to_regex(p)) for p in paths]
    folded = [(p, re.compile(rx.pattern, re.IGNORECASE)) for p, rx in regexes]
    case_only = None
    for row in rows:
        for name in row:
            if not name:
                continue
            for p, rx in regexes:
                if rx.match(name):
                    return "true", f"'{name}' matches {workflow.name}'s path filter '{p}'"
            if case_only is None:
                case_only = next(((name, p) for p, rx in folded if rx.match(name)), None)
    if case_only is not None:
        # A `false` must be PROVEN: a file that matches only when case is ignored leaves the
        # answer undecided, whichever way GitHub compares case.
        return "unknown", f"'{case_only[0]}' matches {workflow.name}'s path filter '{case_only[1]}' only when case is ignored"
    return "false", f"none of the {len(rows)} changed file(s) matches {workflow.name}'s {event} path filter"


def read_rows(path: Path) -> list[list[str]]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            rows.append(line.split("\t"))
    return rows


def emit(applicable: str, reason: str) -> None:
    print(f"applicable={applicable}")
    print(f"reason={reason}")
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as fh:
            fh.write(f"applicable={applicable}\nreason={reason}\n")


def self_test() -> int:
    real = Path(__file__).resolve().parents[1] / "workflows" / "clients.yml"
    failures = []

    def check(label: str, got: tuple[str, str], want: str) -> None:
        if got[0] != want:
            failures.append(f"{label}: wanted {want}, got {got[0]} ({got[1]})")

    # The real trigger, both ways. The negative controls first: a change the Clients run DOES watch.
    check("real: a client file", decide(real, "pull_request", "", [["clients/python/x.py"]], 1, None), "true")
    check("real: a proto", decide(real, "pull_request", "", [["src/MeshWeaver.Mesh.Contract/Protos/a/b.proto"]], 1, None), "true")
    check("real: the workflow itself", decide(real, "pull_request", "", [[".github/workflows/clients.yml"]], 1, None), "true")
    check("real: renamed out of clients/", decide(real, "pull_request", "", [["docs/x.py", "clients/python/x.py"]], 1, None), "true")
    check("real: push to main touching clients", decide(real, "push", "main", [["clients/python/x.py"]], None, 300), "true")
    check("real: server-only change", decide(real, "pull_request", "", [["src/MeshWeaver.Mesh/A.cs"], ["README.md"]], 2, None), "false")
    check("real: near miss (clientsX/)", decide(real, "pull_request", "", [["clientsX/a"], [".github/workflows/clients.yaml"]], 2, None), "false")
    check("real: Protos sibling", decide(real, "pull_request", "", [["src/MeshWeaver.Mesh.Contract/ProtosX/a"]], 1, None), "false")
    check("real: push to main3", decide(real, "push", "main3", [["clients/python/x.py"]], None, 300), "false")
    check("real: push to main, server-only", decide(real, "push", "main", [["src/A.cs"]], None, 300), "false")
    # Anything unprovable stays unknown.
    check("real: merge_group", decide(real, "merge_group", "", [["src/A.cs"]], 1, None), "unknown")
    check("real: dispatch", decide(real, "workflow_dispatch", "", [["src/A.cs"]], 1, None), "unknown")
    check("real: empty listing", decide(real, "pull_request", "", [], 0, None), "unknown")
    check("real: short listing", decide(real, "pull_request", "", [["src/A.cs"]], 2, None), "unknown")
    check("real: capped listing", decide(real, "push", "main", [["src/A.cs"]] * 300, None, 300), "unknown")
    check("real: push without a ref", decide(real, "push", "", [["src/A.cs"]], None, 300), "unknown")
    check("real: path matches only ignoring case", decide(real, "pull_request", "", [["Clients/python/x.py"]], 1, None), "unknown")
    check("real: branch matches only ignoring case", decide(real, "push", "Main", [["clients/python/x.py"]], None, 300), "unknown")
    check("absent workflow", decide(real.with_name("nope.yml"), "pull_request", "", [["src/A.cs"]], 1, None), "unknown")

    with tempfile.TemporaryDirectory() as tmp:
        def wf(body: str) -> Path:
            p = Path(tmp) / f"w{len(list(Path(tmp).iterdir()))}.yml"
            p.write_text(body, encoding="utf-8")
            return p
        rows = [["src/A.cs"]]
        check("negation", decide(wf("on:\n  pull_request:\n    paths: ['src/**', '!src/A.cs']\n"), "pull_request", "", rows, 1, None), "unknown")
        check("paths-ignore", decide(wf("on:\n  pull_request:\n    paths-ignore: ['docs/**']\n"), "pull_request", "", rows, 1, None), "unknown")
        check("no filter", decide(wf("on:\n  pull_request:\n"), "pull_request", "", rows, 1, None), "unknown")
        check("bare list", decide(wf("on: [pull_request]\n"), "pull_request", "", rows, 1, None), "unknown")
        check("no such trigger", decide(wf("on:\n  push:\n    paths: ['src/**']\n"), "pull_request", "", rows, 1, None), "false")
        check("branch pattern", decide(wf("on:\n  push:\n    branches: ['release/*']\n    paths: ['x/**']\n"), "push", "main", rows, None, 300), "unknown")
        check("single star stops at /", decide(wf("on:\n  pull_request:\n    paths: ['src/*']\n"), "pull_request", "", [["src/a/A.cs"]], 1, None), "false")
        check("single star in a segment", decide(wf("on:\n  pull_request:\n    paths: ['src/*']\n"), "pull_request", "", rows, 1, None), "true")
        check("unparseable", decide(wf("on: [\n"), "pull_request", "", rows, 1, None), "unknown")

    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}", file=sys.stderr)
        return 1
    print("clients-applicability.py self-test: all cases pass, both ways, against the real clients.yml")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--workflow", type=Path, default=Path(".github/workflows/clients.yml"))
    ap.add_argument("--event")
    ap.add_argument("--ref-name", default="")
    ap.add_argument("--files", type=Path, help="TSV: one changed file per line, optional previous name in column 2")
    ap.add_argument("--expected", type=int)
    ap.add_argument("--truncated-at", type=int)
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.event or a.files is None:
        ap.error("--event and --files are required")
    rows = read_rows(a.files) if a.files.is_file() else []
    applicable, reason = decide(a.workflow, a.event, a.ref_name, rows, a.expected, a.truncated_at)
    emit(applicable, reason)
    return 0


if __name__ == "__main__":
    sys.exit(main())
