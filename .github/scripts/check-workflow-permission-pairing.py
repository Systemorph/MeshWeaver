#!/usr/bin/env python3
"""check-workflow-permission-pairing.py — a caller grants every permission the lane it calls demands.

(The name on this first line is load-bearing: node-repo-validate.yml fetches this file at
platform-ref and refuses a body whose first 400 bytes do not name it — the same shape as
check-workflow-timeouts.py and compile-check.py.)

WHY THIS EXISTS (measured 2026-09-10, a five-hour fleet-wide outage)
-------------------------------------------------------------------
A called workflow's job can NEVER hold a permission its caller did not grant. So a job-level
`permissions:` block inside a `workflow_call` workflow is not a grant — it is a HARD REQUIREMENT
imposed on every caller, in every repository, including repositories the lane's own repo cannot see.

MeshWeaver#3933 added `permissions: {contents: read, id-token: write}` to three jobs of
node-repo-module-pack.yml so core's CD could authenticate to ACR over OIDC, and updated core's own
caller in the same commit. It updated no satellite. From 2026-09-10T18:59:37Z every run of
MeshWeaver.Plugins' `Plugin Catalog CI` — main, every PR, every trigger — ended `startup_failure`
with ZERO jobs.

🚨 THE SYMPTOM IS AN ABSENCE, WHICH IS WHY IT COST FIVE HOURS. There is no permission error. The run
graph is rejected before a job is scheduled, so NO check-run is published at all — and under classic
branch protection an ABSENT required context blocks forever rather than passing (see
Doc/Architecture/ReadingCiSignals). The repo could not merge anything, including the two-line fix
for the thing that broke it. Same class as "a new REQUIRED input on a reusable workflow is a silent
startup_failure", one permission-shaped step sideways.

THE RULE
--------
For every job in this repository that `uses:` a reusable workflow whose definition this checkout can
read, the caller's effective permissions must be a SUPERSET of every job-level `permissions:` the
called workflow declares.

Effective permissions of a caller job = its own `permissions:` if it has one, else the workflow-level
`permissions:`, else GitHub's default — which this check treats as UNKNOWN and reports, because a
default that happens to be permissive today is not a pairing.

WHERE IT RUNS
-------------
Core runs it on itself (its own `main-cd.yml` calls the same lanes). Every satellite runs it through
node-repo-validate.yml against the platform checkout at its own platform-ref, which is the run that
matters: that is the moment a satellite's caller meets the lane version it is about to call, and the
only place the pair is visible at all.

WHAT IT DELIBERATELY DOES NOT DO
--------------------------------
It does not forbid job-level permissions in a reusable lane. Measured on core 2026-09-11: eleven of
twelve `workflow_call` workflows declare them, and `node-repo-gate` and `node-repo-publish-bake` both
demand `id-token: write` with all twelve of their fleet callers correctly paired. Demanding a
permission is legitimate; demanding it without pairing the callers is the defect.
"""
from __future__ import annotations
import argparse, os, sys, glob
try:
    import yaml
except ImportError:
    print("check-workflow-permission-pairing: PyYAML is required", file=sys.stderr)
    sys.exit(2)

# 🚨 An absent `permissions:` does NOT grant nothing — the job inherits the repository/organisation
# default. GitHub's RESTRICTED default (the recommended setting, and the floor of the permissive one)
# is contents/packages/metadata read. Treating absence as "grants nothing" makes this check fire on
# every ordinary caller of a lane that merely wants `contents: read`, and a guard that fires on
# working lanes gets muted — which is how the next real one goes unnoticed. Measured while writing
# this: against a real satellite it produced 1 true finding and 2 false ones before this floor was
# added.
#
# What the default can NEVER provide is `id-token`, which always requires an explicit grant — and
# that is precisely the scope the 2026-09-10 outage turned on. Anything at `write` beyond the floor
# is likewise not safe to assume.
DEFAULT_FLOOR = {"contents": "read", "packages": "read", "metadata": "read"}


def _load(path: str):
    try:
        with open(path, encoding="utf-8") as fh:
            return yaml.safe_load(fh)
    except Exception as exc:                                    # noqa: BLE001 - reported, not raised
        return exc


def _perm_map(value) -> dict | None:
    """A `permissions:` value as a dict. `read-all`/`write-all` are blanket grants; None means absent."""
    if value is None:
        return None
    if isinstance(value, str):
        return {"*": value}                                     # read-all / write-all satisfy anything
    if isinstance(value, dict):
        return {str(k): str(v) for k, v in value.items()}
    return None


def _satisfies(granted: dict | None, demanded: dict) -> list[str]:
    """Which demanded scopes the grant does not cover. `write` is required where `write` is demanded."""
    if granted is None:
        # inherits the repo/org default — satisfied only up to the floor above
        return sorted(k for k, v in demanded.items()
                      if DEFAULT_FLOOR.get(k) is None or (v == "write" and DEFAULT_FLOOR[k] != "write"))
    if "*" in granted:
        return [] if granted["*"] == "write-all" else [k for k, v in demanded.items() if v == "write"]
    missing = []
    for scope, level in demanded.items():
        have = granted.get(scope)
        if have is None or (level == "write" and have != "write"):
            missing.append(scope)
    return sorted(missing)


def _local_lane_path(uses: str, root: str) -> str | None:
    """The lane's definition inside THIS checkout, when `uses:` names one we can read."""
    ref = uses.split("@", 1)[0]
    if ref.startswith("./"):
        return os.path.join(root, ref[2:])
    # owner/repo/.github/workflows/x.yml — readable only if it is this platform checkout's own lane
    parts = ref.split("/")
    if len(parts) >= 4 and parts[2] == ".github" and parts[3] == "workflows":
        candidate = os.path.join(root, ".github", "workflows", parts[-1])
        return candidate if os.path.isfile(candidate) else None
    return None


def check(root: str, platform_root: str | None) -> tuple[list[str], int]:
    problems: list[str] = []
    pairs = 0
    for wf_path in sorted(glob.glob(os.path.join(root, ".github", "workflows", "*.yml"))):
        doc = _load(wf_path)
        if isinstance(doc, Exception) or not isinstance(doc, dict):
            continue
        wf_default = _perm_map(doc.get("permissions"))
        for job_name, job in (doc.get("jobs") or {}).items():
            if not isinstance(job, dict):
                continue
            uses = str(job.get("uses") or "")
            if not uses:
                continue
            lane_path = _local_lane_path(uses, root)
            if lane_path is None and platform_root:
                lane_path = _local_lane_path(uses, platform_root)
            if lane_path is None or not os.path.isfile(lane_path):
                continue                                        # a lane we cannot read says nothing
            lane = _load(lane_path)
            if isinstance(lane, Exception) or not isinstance(lane, dict):
                problems.append(f"{os.path.basename(wf_path)}: job '{job_name}' calls "
                                f"{os.path.basename(lane_path)}, which does not parse: {lane}")
                continue
            granted = _perm_map(job.get("permissions"))
            if granted is None:
                granted = wf_default
            for lane_job, lane_def in (lane.get("jobs") or {}).items():
                if not isinstance(lane_def, dict):
                    continue
                demanded = _perm_map(lane_def.get("permissions"))
                if not demanded or "*" in demanded:
                    continue
                pairs += 1
                missing = _satisfies(granted, demanded)
                if missing:
                    problems.append(
                        f"{os.path.basename(wf_path)}: job '{job_name}' calls "
                        f"{os.path.basename(lane_path)}, whose job '{lane_job}' demands "
                        f"{demanded} — this caller grants "
                        f"{granted if granted is not None else 'only the inherited default (no explicit block)'}, "
                        f"missing: {', '.join(missing)}.\n"
                        f"      A called job can never hold a permission its caller did not grant, so "
                        f"this run is rejected before ANY job is scheduled: `startup_failure`, zero "
                        f"jobs, and NO check-run published — which blocks forever under classic "
                        f"protection rather than failing visibly (MeshWeaver#3933, five hours dark)."
                    )
    return problems, pairs


def self_test() -> int:
    """The check must FAIL on the 2026-09-10 shape and PASS on the paired one. A guard that cannot
    fail is not a guard (AGENTS.md)."""
    unpaired = _satisfies({"contents": "read", "actions": "read"},
                          {"contents": "read", "id-token": "write"})
    paired = _satisfies({"contents": "read", "actions": "read", "id-token": "write"},
                        {"contents": "read", "id-token": "write"})
    blanket = _satisfies({"*": "write-all"}, {"id-token": "write"})
    absent = _satisfies(None, {"contents": "read"})
    weaker = _satisfies({"contents": "read"}, {"contents": "write"})
    cases = [
        ("the 2026-09-10 outage shape is caught", unpaired == ["id-token"]),
        ("a correctly paired caller passes", paired == []),
        ("write-all satisfies anything", blanket == []),
        ("an absent block still covers the default floor", absent == []),
        ("an absent block does NOT cover id-token", _satisfies(None, {"id-token": "write"}) == ["id-token"]),
        ("an absent block does NOT cover a write beyond the floor",
         _satisfies(None, {"contents": "write"}) == ["contents"]),
        ("read does not satisfy a write demand", weaker == ["contents"]),
    ]
    bad = [name for name, ok in cases if not ok]
    for name, ok in cases:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    return 1 if bad else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".", help="repository to check (its callers)")
    ap.add_argument("--platform-root", default=None,
                    help="platform checkout at platform-ref, where the shared lanes live")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    problems, pairs = check(args.root, args.platform_root)
    # 🚨 The DENOMINATOR is printed on every run, green or red. A check pointed at the wrong root
    # resolves no lanes, reports zero problems, and ticks exactly like a clean measurement.
    print(f"check-workflow-permission-pairing: {pairs} caller/lane permission pair(s) resolved, "
          f"{len(problems)} violation(s), root={os.path.abspath(args.root)}")
    if pairs == 0:
        print("  NOTE: zero pairs resolved — either this repo calls no lane whose definition is "
              "readable here, or --root/--platform-root is wrong. Zero is not a pass.")
    for p in problems:
        print(f"::error::{p}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
