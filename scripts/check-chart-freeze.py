#!/usr/bin/env python3
"""Refuse a change to the platform's FROZEN chart paths: the chart lives in Systemorph/MeshWeaver.Deploy.

THE SHAPE (policy `chart-lives-in-deploy`). MeshWeaver.Deploy was seeded from this repository
(Deploy#1) with the chart, the db chart, the AKS overlay and manifests, the chart tooling, memex-local
and the chart gate. Both copies stayed editable, both moved, and two pipelines baked
`hosting-operator:main` from the two copies, so the moving tag flapped between them. Every edit here
after the seed is one more edit somebody must port by hand, and nothing reports one that is missed.

So the paths Deploy#1 copied are FROZEN here: a pull request may DELETE them (that is how they leave,
MeshWeaver#7127) and may do nothing else to them. A file the diff adds, modifies, retypes, copies or
renames INTO a frozen path is refused, naming the file and where the chart lives now.

Deliberately NOT frozen, although they sit beside the chart: `.github/scripts/build-run.py` and
`deploy/build-host/**`. main-cd builds the build-host runner image from them in this repository;
the Deploy chart's own gate reads them from a declared checkout of this repository.

Renames are read as delete + add (`--no-renames`), so moving a frozen file OUT of a frozen path is
a deletion (allowed) plus an addition elsewhere (not this gate's business), and moving a file INTO
one is an addition (refused).

Usage:
  check-chart-freeze.py --base <sha>            diff <sha>..HEAD in this checkout
  check-chart-freeze.py --name-status <file>    read `git diff --name-status --no-renames` output
  check-chart-freeze.py --self-test             prove the gate can fail and can pass
"""
from __future__ import annotations

import argparse
import subprocess
import sys

HOME = "Systemorph/MeshWeaver.Deploy"

# Exactly what Deploy#1 copied. A trailing slash is a directory prefix; anything else is one file.
FROZEN = (
    "deploy/helm/",
    "deploy/helm-db/",
    "deploy/aks/values.aks.yaml",
    "deploy/aks/manifests/",
    "deploy/aks/scripts/",
    "deploy/homebrew/",
    ".github/workflows/homebrew.yml",
    ".github/workflows/chart-gate.yml",
    "scripts/check-config-key-coverage.py",
    ".github/scripts/test-bundle-registry.py",
)

# Stays in this repository (main-cd builds the build-host image here). Wins over FROZEN.
EXEMPT = (
    ".github/scripts/build-run.py",
    "deploy/build-host/",
)


def _matches(path: str, prefixes: tuple[str, ...]) -> bool:
    return any(path.startswith(p) if p.endswith("/") else path == p for p in prefixes)


def is_frozen(path: str) -> bool:
    return _matches(path, FROZEN) and not _matches(path, EXEMPT)


def violations(name_status: str) -> list[tuple[str, str]]:
    """Every (status, path) in `git diff --name-status --no-renames` output that touches a frozen
    path with anything but a deletion. An unparseable line is itself a violation: a line this gate
    cannot read is a change it did not check."""
    out: list[tuple[str, str]] = []
    for raw in name_status.splitlines():
        line = raw.rstrip("\n")
        if not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) < 2 or not parts[0]:
            out.append(("?", line))
            continue
        status = parts[0][0]
        # With --no-renames there are no R/C pairs, but read every path a line names anyway.
        for path in parts[1:]:
            if is_frozen(path) and status != "D":
                out.append((status, path))
    return out


def report(found: list[tuple[str, str]]) -> int:
    if not found:
        print("chart freeze: no frozen chart path is added or modified (deletions are allowed).")
        return 0
    for status, path in found:
        print(f"::error file={path}::the chart lives in {HOME} — '{path}' is frozen here "
              f"(status {status}). Make this change in {HOME}; this repository only deletes it "
              f"(policy chart-lives-in-deploy).")
    print(f"chart freeze: {len(found)} frozen path(s) changed — the chart lives in {HOME}.")
    return 1


def diff_against(base: str) -> str:
    return subprocess.run(
        ["git", "diff", "--name-status", "--no-renames", base, "HEAD"],
        check=True, capture_output=True, text=True).stdout


def self_test() -> int:
    cases = [
        # (name, name-status text, expected violation count)
        ("a modified chart template is refused",
         "M\tdeploy/helm/templates/memex-portal/config.yaml\n", 1),
        ("an added chart file is refused",
         "A\tdeploy/helm/templates/build-host/new.yaml\n", 1),
        ("a modified frozen single file is refused",
         "M\t.github/workflows/chart-gate.yml\n", 1),
        ("a retyped frozen file is refused",
         "T\tdeploy/homebrew/bin/memex-local\n", 1),
        ("a deletion of every frozen kind is allowed (the #7127 shape)",
         "D\tdeploy/helm/values.yaml\nD\tdeploy/helm-db/Chart.yaml\nD\tdeploy/aks/values.aks.yaml\n"
         "D\tdeploy/aks/manifests/hosting-operator/operator-rbac.yaml\n"
         "D\tdeploy/aks/scripts/check-chart-invariants.sh\nD\tdeploy/homebrew/README.md\n"
         "D\t.github/workflows/homebrew.yml\nD\t.github/workflows/chart-gate.yml\n"
         "D\tscripts/check-config-key-coverage.py\nD\t.github/scripts/test-bundle-registry.py\n", 0),
        ("the build-host runner and Dockerfile stay editable",
         "M\t.github/scripts/build-run.py\nM\tdeploy/build-host/Dockerfile\n", 0),
        ("unrelated paths are not this gate's business",
         "M\tsrc/MeshWeaver.Hosting/NodeTypeBakeGate.cs\nA\tdeploy/ocr/Dockerfile\n"
         "M\tdeploy/aks/INSTANCE-LIFECYCLE.md\nM\t.github/scripts/lock-pinned-digests.py\n", 0),
        ("a prefix lookalike is not frozen",
         "M\tdeploy/helmfile.yaml\nM\tdeploy/aks/values.aks.yaml.bak\n", 0),
        ("a mixed diff counts only the non-deletions",
         "D\tdeploy/helm/Chart.yaml\nM\tdeploy/helm/values.yaml\nA\tdeploy/aks/scripts/x.sh\n", 2),
        ("an unparseable line is a violation, never a silent pass",
         "garbage-without-a-tab\n", 1),
    ]
    failed = 0
    for name, text, expected in cases:
        got = len(violations(text))
        ok = got == expected
        failed += not ok
        print(f"  {'ok  ' if ok else 'FAIL'}  {name} (expected {expected}, got {got})")
    # The message is the contract the task names; hold it.
    msg_ok = HOME == "Systemorph/MeshWeaver.Deploy"
    failed += not msg_ok
    if failed:
        print(f"chart freeze self-test: {failed} case(s) FAILED")
        return 1
    print(f"chart freeze self-test: all {len(cases)} cases hold")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--base", help="diff this commit against HEAD")
    g.add_argument("--name-status", help="a file holding `git diff --name-status --no-renames` output")
    g.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if args.base:
        text = diff_against(args.base)
    else:
        with open(args.name_status, encoding="utf-8") as fh:
            text = fh.read()
    return report(violations(text))


if __name__ == "__main__":
    sys.exit(main())
