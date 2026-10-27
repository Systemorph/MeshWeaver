#!/usr/bin/env python3
"""Refuse a change to the platform's FROZEN chart paths: the chart lives in Systemorph/MeshWeaver.Deploy.

THE SHAPE (policy `chart-lives-in-deploy`). MeshWeaver.Deploy was seeded from this repository
(Deploy#1) with the chart, the db chart, the AKS overlay and manifests, the chart tooling, memex-local
and the chart gate. Both copies stayed editable, both moved, and two pipelines baked
`hosting-operator:main` from the two copies, so the moving tag flapped between them. Every edit here
after the seed is one more edit somebody must port by hand, and nothing reports one that is missed.

So the paths Deploy#1 copied are FROZEN here: a pull request may DELETE them (that is how they leave,
MeshWeaver#7127) and may do nothing else to them. A file the diff adds, modifies, retypes, copies or
renames INTO a frozen path is refused, naming the file and where the chart lives now. A frozen
DIRECTORY matches its own path as well as everything below it, so replacing it with a symlink (its
files deleted, the directory path added) is refused too.

The build-host deployment followed (MeshWeaver.Deploy#5): its runner `.github/scripts/build-run.py`,
`deploy/build-host/**`, the announcement script, the declaration `.github/build-hosts.json` and the
stable roll `build-host-stable.yml` live in MeshWeaver.Deploy and are frozen here the same way, so a
second copy cannot grow back beside the one Deploy builds the `mw-build-host` image from.

The deployment sidecars followed (core slimming): the service images an instance may run beside its
portal (`deploy/{ocr,ollama,whisper,s1-extract}` and their `*-image.yml` lanes), the generated
deployment targets (`deploy/{compose,compose-ha,aca,marketplace}`, `deploy/.env.example`) and the
AKS bring-up docs (`deploy/aks/{README,SELF-UPDATE,INSTANCE-LIFECYCLE}.md`). They are frozen here
the same way, so a second publisher of those image names cannot grow back beside Deploy's.

The listing is read NUL-delimited (`-z`): in its default output git C-quotes a path holding a
non-ASCII byte, a quote or a control character, and a quoted path would match no frozen prefix.

Renames are read as delete + add (`--no-renames`), so moving a frozen file OUT of a frozen path is
a deletion (allowed) plus an addition elsewhere (not this gate's business), and moving a file INTO
one is an addition (refused).

Usage:
  check-chart-freeze.py --base <sha>          diff <sha>..HEAD in this checkout
  check-chart-freeze.py --diff-file <file>    read a saved NUL-delimited name-status diff
                                              (`git diff -z --name-status --no-renames`)
  check-chart-freeze.py --self-test           prove the gate can fail and can pass
"""
from __future__ import annotations

import argparse
import subprocess
import sys

HOME = "Systemorph/MeshWeaver.Deploy"

# Exactly what Deploy#1 copied. A trailing slash is a directory; anything else is one file.
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
    # The build-host deployment, which MeshWeaver.Deploy#5 took.
    "deploy/build-host/",
    ".github/scripts/build-run.py",
    ".github/scripts/build-host-announcement.py",
    ".github/build-hosts.json",
    ".github/workflows/build-host-stable.yml",
    # The deployment sidecars: the service images, the generated targets, the AKS bring-up docs.
    "deploy/ocr/",
    "deploy/ollama/",
    "deploy/whisper/",
    "deploy/s1-extract/",
    ".github/workflows/ocr-image.yml",
    ".github/workflows/ollama-image.yml",
    ".github/workflows/whisper-image.yml",
    ".github/workflows/s1-extract-image.yml",
    "deploy/compose/",
    "deploy/compose-ha/",
    "deploy/aca/",
    "deploy/marketplace/",
    "deploy/.env.example",
    "deploy/aks/README.md",
    "deploy/aks/SELF-UPDATE.md",
    "deploy/aks/INSTANCE-LIFECYCLE.md",
)

# The status letters `git diff --name-status` prints (C and R do not occur with --no-renames).
KNOWN_STATUS = set("ADMTUXBCR")


def _matches(path: str, prefixes: tuple[str, ...]) -> bool:
    """A trailing slash names a directory: the directory path ITSELF matches as well as everything
    below it, so a frozen directory replaced by a symlink is an addition at the frozen location."""
    return any((path.startswith(p) or path == p[:-1]) if p.endswith("/") else path == p
               for p in prefixes)


def is_frozen(path: str) -> bool:
    return _matches(path, FROZEN)


def violations(listing: str) -> list[tuple[str, str]]:
    """Every (status, path) in a NUL-delimited name-status listing that touches a frozen path with
    anything but a deletion. The listing is a flat sequence of fields: status, path, status, path, …
    A field this gate cannot read — an unknown status, or a status with no path after it — is itself
    a violation: a change it could not read is a change it did not check."""
    out: list[tuple[str, str]] = []
    fields = listing.split("\0")
    if fields and fields[-1] == "":
        fields.pop()
    i = 0
    while i < len(fields):
        status = fields[i]
        if not status or status[0] not in KNOWN_STATUS or i + 1 >= len(fields):
            # Past an unreadable field the status/path pairing is lost, so nothing after it can be
            # read either: one violation names where reading stopped.
            out.append(("?", status))
            break
        # A C or R entry carries two paths; --no-renames should never produce one, but read both.
        width = 3 if status[0] in "CR" else 2
        for path in fields[i + 1:i + width]:
            if is_frozen(path) and status[0] != "D":
                out.append((status[0], path))
        i += width
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
        ["git", "diff", "-z", "--name-status", "--no-renames", base, "HEAD"],
        check=True, capture_output=True, text=True, encoding="utf-8").stdout


def _z(lines: str) -> str:
    """A readable fixture (`S<TAB>path` per line) as the NUL-delimited listing git emits."""
    return "".join(f"{s}\0{p}\0" for s, p in (ln.split("\t", 1) for ln in lines.splitlines()))


def self_test() -> int:
    cases = [
        # (name, NUL-delimited listing, expected violation count)
        ("a modified chart template is refused",
         _z("M\tdeploy/helm/templates/memex-portal/config.yaml"), 1),
        ("an added chart file is refused",
         _z("A\tdeploy/helm/templates/build-host/new.yaml"), 1),
        ("a modified frozen single file is refused",
         _z("M\t.github/workflows/chart-gate.yml"), 1),
        ("a retyped frozen file is refused",
         _z("T\tdeploy/homebrew/bin/memex-local"), 1),
        ("a non-ASCII file name added to the chart is refused (read unquoted through -z)",
         _z("A\tdeploy/helm/templates/café.yaml"), 1),
        ("a frozen directory replaced by a symlink is refused (its files D, the directory A)",
         _z("D\tdeploy/helm/Chart.yaml\nD\tdeploy/helm/values.yaml\nA\tdeploy/helm"), 1),
        ("a deletion of every frozen kind is allowed (the #7127 shape)",
         _z("D\tdeploy/helm/values.yaml\nD\tdeploy/helm-db/Chart.yaml\nD\tdeploy/aks/values.aks.yaml\n"
            "D\tdeploy/aks/manifests/hosting-operator/operator-rbac.yaml\n"
            "D\tdeploy/aks/scripts/check-chart-invariants.sh\nD\tdeploy/homebrew/README.md\n"
            "D\t.github/workflows/homebrew.yml\nD\t.github/workflows/chart-gate.yml\n"
            "D\tscripts/check-config-key-coverage.py\nD\t.github/scripts/test-bundle-registry.py"), 0),
        ("the build-host material that left with Deploy#5 is frozen too (re-added or edited is refused)",
         _z("A\t.github/scripts/build-run.py\nM\tdeploy/build-host/Dockerfile\nA\t.github/build-hosts.json"), 3),
        ("deleting the build-host material is allowed (the Deploy#5 shape)",
         _z("D\t.github/scripts/build-run.py\nD\tdeploy/build-host/Dockerfile\n"
            "D\tdeploy/build-host/Dockerfile.dockerignore\nD\t.github/scripts/build-host-announcement.py\n"
            "D\t.github/build-hosts.json\nD\t.github/workflows/build-host-stable.yml"), 0),
        ("the deployment sidecars are frozen too (re-added or edited is refused)",
         _z("A\tdeploy/ocr/Dockerfile\nM\tdeploy/aks/INSTANCE-LIFECYCLE.md\n"
            "A\t.github/workflows/whisper-image.yml\nM\tdeploy/compose/docker-compose.yaml\n"
            "A\tdeploy/.env.example"), 5),
        ("deleting the deployment sidecars is allowed (the sidecar move's shape)",
         _z("D\tdeploy/ocr/Dockerfile\nD\tdeploy/ollama/helm/values.yaml\nD\tdeploy/whisper/README.md\n"
            "D\tdeploy/s1-extract/app/server.py\nD\t.github/workflows/ollama-image.yml\n"
            "D\tdeploy/aca/main.bicep\nD\tdeploy/marketplace/mainTemplate.json\n"
            "D\tdeploy/compose-ha/docker-compose.yaml\nD\tdeploy/.env.example\nD\tdeploy/aks/SELF-UPDATE.md"), 0),
        ("what core keeps under deploy/ is not frozen (its own images and CI)",
         _z("M\tdeploy/base-images/portal-ai/Dockerfile\nM\tdeploy/core-tests/core-tests.py\n"
            "M\tdeploy/python-gate/Dockerfile\nM\tdeploy/node-gate/README.md\nM\tdeploy/pandas-gate/Dockerfile\n"
            "M\tdeploy/CHANGE-PROPAGATION.md"), 0),
        ("unrelated paths are not this gate's business",
         _z("M\tsrc/MeshWeaver.Hosting/NodeTypeBakeGate.cs\nA\tdeploy/ocrx/Dockerfile\n"
            "M\tdeploy/aks/README.md.bak\nM\t.github/scripts/lock-pinned-digests.py"), 0),
        ("a prefix lookalike is not frozen",
         _z("M\tdeploy/helmfile.yaml\nM\tdeploy/aks/values.aks.yaml.bak\nA\tdeploy/helm-other"), 0),
        ("a mixed diff counts only the non-deletions",
         _z("D\tdeploy/helm/Chart.yaml\nM\tdeploy/helm/values.yaml\nA\tdeploy/aks/scripts/x.sh"), 2),
        ("a rename INTO a frozen path is refused (both paths read)",
         "R100\0docs/x.yaml\0deploy/helm/x.yaml\0", 1),
        ("an unknown status is a violation, never a silent pass",
         "Q\0deploy/helm/values.yaml\0", 1),
        ("a status with no path is a violation, never a silent pass",
         "M\0", 1),
        ("an empty listing is no change",
         "", 0),
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
    g.add_argument("--diff-file", dest="name_status",
                   help="a file holding a saved NUL-delimited name-status diff (see Usage)")
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
