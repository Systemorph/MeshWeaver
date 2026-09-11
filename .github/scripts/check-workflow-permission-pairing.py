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
The SAME is true of a `workflow_call` workflow's WORKFLOW-level `permissions:` block.

MeshWeaver#3933 (merged 2026-09-10T18:57:53Z, commit 846bfbc90/d540b0dca) added
`permissions: {contents: read, id-token: write}` to three jobs of node-repo-module-pack.yml so core's
CD could authenticate to ACR over OIDC, and updated core's own caller in the same commit. It updated
no satellite. 104 seconds later, at 2026-09-10T18:59:37Z, MeshWeaver.Plugins' `Plugin Catalog CI`
began ending `startup_failure` with ZERO jobs — 28 runs, every trigger, until MeshWeaver#3968 removed
the blocks at 23:47:56Z.

🚨 THE SYMPTOM IS AN ABSENCE, WHICH IS WHY IT COST FIVE HOURS. There is no permission error. The run
graph is rejected before a job is scheduled, so NO check-run is published at all — and under classic
branch protection an ABSENT required context blocks forever rather than passing (see
Doc/Architecture/ReadingCiSignals). The repo could not merge anything, including the two-line fix
for the thing that broke it. Same class as "a new REQUIRED input on a reusable workflow is a silent
startup_failure", one permission-shaped step sideways.

THE RULE
--------
A caller's effective permissions must be a SUPERSET of every `permissions:` block — job-level and
workflow-level — that the lane it calls declares.

Effective permissions of a caller job = its own `permissions:` if it has one, else the workflow-level
`permissions:`, else GitHub's default, which this check treats as the DEFAULT_FLOOR below.

THE THREE MODES, AND WHY THERE ARE THREE (MeshWeaver#4011, the audit of #3989)
------------------------------------------------------------------------------
#3989 shipped only the first of these, and the first alone cannot see the outage it was written for:

  --root R [--platform-root P]        PAIRING. Both halves in one checkout. This is what a satellite
                                      runs: its callers here, the platform's lanes fetched there.
  --fleet ROSTER --root R             FLEET. The lanes in R against the committed grants of every
                                      caller in the repositories R CANNOT READ. This is the only
                                      mode that runs in core's own pull request, and therefore the
                                      only one that PREVENTS rather than reports.
  --assert-fleet-row REPO --fleet F   ROSTER TRUTH. This repo's real callers equal its roster row.
                                      Runs in every satellite. Without it the roster is a snapshot
                                      that silently goes stale, and FLEET becomes an instrument that
                                      answers confidently from memory.

🚨 WHY FLEET HAD TO EXIST: THE PAIRING MODE IS INSIDE THE RUN THE DEFECT KILLS. Measured
2026-09-11 on the live fleet — MeshWeaver.Plugins calls `node-repo-module-pack.yml@main` from job
`modules-floor` of `.github/workflows/ci.yml` granting only `{contents: read, actions: read}`, and
calls `node-repo-validate.yml` — which hosts this guard — from job `validate` of THE SAME FILE.
A `startup_failure` rejects the whole workflow file's graph: run 34517698518 reports
`path=.github/workflows/ci.yml`, `total_count=0` jobs. So on a recurrence the guard's own job is
never scheduled. Five of six satellites call at `@main`, so a core merge reaches them in seconds and
no pin stands between the two. Pairing mode would have caught #3933 in exactly zero repositories.

WHAT IT DELIBERATELY DOES NOT DO
--------------------------------
It does not forbid `permissions:` in a reusable lane. Measured on core 2026-09-11: eleven of twelve
`workflow_call` workflows declare them, and `node-repo-gate` and `node-repo-publish-bake` both demand
`id-token: write` with every one of their fleet callers correctly paired. Demanding a permission is
legitimate; demanding it without pairing the callers is the defect.

It does not claim to cover a lane nobody is recorded as calling. Those are PRINTED with the verdict,
never silently folded into a green (`--fleet` names them), because a lane with zero known callers is
a coverage gap, not a clean measurement.
"""
from __future__ import annotations
import argparse, copy, os, sys, glob, tempfile
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

WORKFLOW_LEVEL = "«workflow»"           # the pseudo-job name a workflow-level demand is reported as

# Scopes the real-tree control below strengthens a lane with, in order. The first one the caller
# demonstrably does not grant is used, so the control mutates a lane the repo ACTUALLY calls rather
# than a fixture that can drift away from it.
_MUTATION_SCOPES = ("id-token", "packages", "issues", "pull-requests", "actions", "contents")


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
        return {"*": value}                                     # read-all / write-all
    if isinstance(value, dict):
        return {str(k): str(v) for k, v in value.items()}
    return None


def _satisfies(granted: dict | None, demanded: dict) -> list[str]:
    """Which demanded scopes the grant does not cover. `write` is required where `write` is demanded."""
    # A lane demanding a BLANKET is demanding it on every scope; only an equal-or-wider blanket from
    # the caller covers that. Zero lanes do this today — it is here so the shape cannot arrive
    # silently, the way the workflow-level block below did.
    if "*" in demanded:
        want = demanded["*"]
        have = (granted or {}).get("*")
        if have == "write-all" or (want == "read-all" and have in ("read-all", "write-all")):
            return []
        return [want]
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


def _is_workflow_call(doc: dict) -> bool:
    # YAML 1.1 reads the bare key `on:` as the boolean True; both spellings occur in the wild.
    trig = doc.get(True, doc.get("on"))
    return isinstance(trig, dict) and "workflow_call" in trig


def _lane_demands(lane: dict) -> dict[str, dict]:
    """Every `permissions:` block the lane imposes on its callers, keyed by where it is written.

    🚨 The WORKFLOW-level block counts. #3989 read only job-level blocks, and core carries two lanes
    whose demand is workflow-level — `auto-arm.yml` (`contents: write`, `pull-requests: write`,
    called by all six satellites AND Memex) and `plugin-build.yml`. Moving a job-level demand up one
    level is a one-line edit that made the guard go silent while the requirement stayed.
    """
    demands: dict[str, dict] = {}
    wf = _perm_map(lane.get("permissions"))
    if wf:
        demands[WORKFLOW_LEVEL] = wf
    for lane_job, lane_def in (lane.get("jobs") or {}).items():
        if not isinstance(lane_def, dict):
            continue
        d = _perm_map(lane_def.get("permissions"))
        if d:
            demands[str(lane_job)] = d
    return demands


def _startup_failure_note() -> str:
    return ("\n      A called job can never hold a permission its caller did not grant, so this run is "
            "rejected before ANY job is scheduled: `startup_failure`, zero jobs, and NO check-run "
            "published — which blocks forever under classic protection rather than failing visibly "
            "(MeshWeaver#3933, five hours dark).")


def _local_lane_path(uses: str, root: str, platform_root: str | None = None) -> str | None:
    """The lane's definition this run can read, or None.

    🚨 WHERE A REFERENCE RESOLVES IS PART OF ITS MEANING, and conflating the two roots reads the
    WRONG DOCUMENT as the lane. A `./x.yml` reference is a lane in the CALLING repository. An
    `owner/repo/.github/workflows/x.yml` reference is a lane in the PLATFORM checkout and resolves
    only there — matching it inside `root` by basename finds a same-named file in the calling repo
    and compares a caller against itself. That is not hypothetical: every satellite carries its own
    `.github/workflows/auto-arm.yml` — a two-line CALLER wrapper — whose basename equals the core
    lane it calls, so the earlier basename-in-root lookup checked Plugins' wrapper against Plugins'
    wrapper and called it a pair.
    """
    ref = uses.split("@", 1)[0]
    if ref.startswith("./"):
        candidate = os.path.join(root, ref[2:])
        return candidate if os.path.isfile(candidate) else None
    parts = ref.split("/")
    if len(parts) >= 4 and parts[2] == ".github" and parts[3] == "workflows":
        if not platform_root:
            return None
        candidate = os.path.join(platform_root, ".github", "workflows", parts[-1])
        return candidate if os.path.isfile(candidate) else None
    return None


def _workflow_files(root: str) -> list[str]:
    return sorted(glob.glob(os.path.join(root, ".github", "workflows", "*.yml"))
                  + glob.glob(os.path.join(root, ".github", "workflows", "*.yaml")))


def _callers(root: str) -> list[dict]:
    """Every job in `root` that calls a reusable workflow, with the grant it actually makes."""
    out = []
    for wf_path in _workflow_files(root):
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
            explicit = _perm_map(job.get("permissions"))
            out.append({
                "workflow": os.path.basename(wf_path),
                "workflow_path": wf_path,
                "job": str(job_name),
                "uses": uses,
                "lane": uses.split("@", 1)[0].split("/")[-1],
                "grants": explicit if explicit is not None else wf_default,
            })
    return out


# ─────────────────────────────── MODE 1: PAIRING (both halves in one checkout) ───────────────────

def check(root: str, platform_root: str | None) -> tuple[list[str], int]:
    problems: list[str] = []
    pairs = 0
    for caller in _callers(root):
        lane_path = _local_lane_path(caller["uses"], root, platform_root)
        if lane_path is None or not os.path.isfile(lane_path):
            continue                                            # a lane we cannot read says nothing
        lane = _load(lane_path)
        if isinstance(lane, Exception) or not isinstance(lane, dict):
            problems.append(f"{caller['workflow']}: job '{caller['job']}' calls "
                            f"{os.path.basename(lane_path)}, which does not parse: {lane}")
            continue
        granted = caller["grants"]
        for lane_job, demanded in _lane_demands(lane).items():
            pairs += 1
            missing = _satisfies(granted, demanded)
            if missing:
                where = ("its WORKFLOW-level permissions block" if lane_job == WORKFLOW_LEVEL
                         else f"whose job '{lane_job}'")
                problems.append(
                    f"{caller['workflow']}: job '{caller['job']}' calls "
                    f"{os.path.basename(lane_path)}, {where} demands {demanded} — this caller grants "
                    f"{granted if granted is not None else 'only the inherited default (no explicit block)'}, "
                    f"missing: {', '.join(missing)}." + _startup_failure_note())
    return problems, pairs


# ─────────────────────── MODE 2: FLEET (lanes here vs callers core cannot read) ──────────────────

def _load_roster(path: str) -> dict:
    doc = _load(path)
    if isinstance(doc, Exception):
        raise SystemExit(f"::error::check-workflow-permission-pairing: cannot read the fleet roster "
                         f"{path}: {doc}")
    if not isinstance(doc, dict) or not isinstance(doc.get("repos"), dict):
        raise SystemExit(f"::error::check-workflow-permission-pairing: {path} has no `repos:` mapping "
                         f"— a roster that parses to nothing would make the fleet check vacuous")
    return doc


def _roster_grants(value) -> dict | None:
    """A roster `grants:` value. `inherit` means the caller writes no block at all."""
    if value in (None, "inherit"):
        return None
    return _perm_map(value)


def check_fleet(root: str, roster_path: str) -> tuple[list[str], int, list[str]]:
    """The lanes in THIS checkout against every caller recorded in repositories it cannot read.

    This is the mode that runs in core's own pull request. It is the only one that can turn a
    recurrence of #3933 into a red BEFORE the commit merges, because the callers that break live in
    repositories `dotnet-test.yml` has no checkout of and no credential for.
    """
    roster = _load_roster(roster_path)
    problems: list[str] = []
    notes: list[str] = []
    pairs = 0

    lanes: dict[str, dict] = {}
    for wf_path in _workflow_files(root):
        doc = _load(wf_path)
        if isinstance(doc, Exception) or not isinstance(doc, dict) or not _is_workflow_call(doc):
            continue
        lanes[os.path.basename(wf_path)] = doc

    called: dict[str, int] = {name: 0 for name in lanes}
    for repo, entry in sorted(roster["repos"].items()):
        entry = entry or {}
        asserted = str(entry.get("asserted-by") or "")
        if not asserted:
            problems.append(f"fleet roster: {repo} has no `asserted-by:` — every row must say what "
                            f"re-measures it, or `none — <reason>`; an unasserted row is a memory, "
                            f"not a measurement")
        for caller in entry.get("callers") or []:
            lane_name = str(caller.get("lane") or "")
            grants = _roster_grants(caller.get("grants"))
            label = f"{repo} {caller.get('workflow')}#{caller.get('job')}"
            if lane_name not in lanes:
                problems.append(
                    f"fleet roster: {label} calls `{lane_name}`, which this checkout does not have as "
                    f"a `workflow_call` workflow. A lane RENAMED or REMOVED here is the same "
                    f"fleet-wide `startup_failure` as an unpaired permission — every caller's run "
                    f"graph is rejected with zero jobs. Land the caller move first, or restore the "
                    f"name.")
                continue
            called[lane_name] += 1
            for lane_job, demanded in _lane_demands(lanes[lane_name]).items():
                pairs += 1
                missing = _satisfies(grants, demanded)
                if missing:
                    where = ("its WORKFLOW-level permissions block" if lane_job == WORKFLOW_LEVEL
                             else f"its job '{lane_job}'")
                    problems.append(
                        f"{lane_name}: {where} demands {demanded}, which {label} does NOT grant "
                        f"(it grants "
                        f"{grants if grants is not None else 'only the inherited default (no explicit block)'}"
                        f"), missing: {', '.join(missing)}." + _startup_failure_note() +
                        f"\n      Pair the caller in {repo} FIRST, or leave the lane inheriting its "
                        f"caller's permissions the way MeshWeaver#3968 restored it.")
        if asserted.startswith("none"):
            notes.append(f"{repo}: row NOT re-measured by any run — {asserted}")

    for lane_name in sorted(lanes):
        demands = _lane_demands(lanes[lane_name])
        if demands and called.get(lane_name, 0) == 0:
            notes.append(f"{lane_name}: demands {sorted(demands)} and has ZERO recorded fleet callers "
                         f"— NOT covered by this check")
    return problems, pairs, notes


# ──────────────────── MODE 3: ROSTER TRUTH (this repo's callers == its roster row) ───────────────

def _row_for(root: str, platform_repo: str) -> list[dict]:
    """This checkout's callers OF THE PLATFORM's lanes, in roster shape."""
    rows = []
    for caller in _callers(root):
        ref = caller["uses"].split("@", 1)[0]
        if not ref.startswith(platform_repo + "/.github/workflows/"):
            continue
        rows.append({"workflow": caller["workflow"], "job": caller["job"], "lane": caller["lane"],
                     "grants": caller["grants"] if caller["grants"] is not None else "inherit"})
    return sorted(rows, key=lambda r: (r["workflow"], r["job"]))


def _row_key(row: dict) -> tuple:
    grants = row.get("grants")
    grants = "inherit" if grants in (None, "inherit") else tuple(sorted(_perm_map(grants).items()))
    return (str(row.get("workflow")), str(row.get("job")), str(row.get("lane")), grants)


def assert_fleet_row(root: str, repo: str, roster_path: str, platform_repo: str) -> list[str]:
    """The committed roster still describes this repository. Fail closed on any difference.

    🚨 This is what stops the roster being a stale snapshot. The two drift directions that make the
    FLEET check answer a confident green on a broken fleet are (a) a satellite ADDS a caller the
    roster does not know and (b) a satellite LOWERS a grant the roster still records — both of them
    "the roster overstates", both caught here, in a job that DOES run (a roster mismatch is not a
    permission escalation, so the graph is accepted and the red is visible).

    🚨 THE THIRD DIRECTION IS NEITHER OF THOSE, AND STRICT EQUALITY MADE IT UNLANDABLE. A row the
    roster HAS and the repository does NOT is the roster overstating nothing: core checks a caller
    that is not there yet, which is stricter than reality, never looser. But a satellite gains a
    caller by the same inverted ordering as `Pairs-with:` — the core half lands first, because the
    roster is how core's own pull request sees the fleet — and every satellite asserts this row
    against core's `main` (`scripts-ref: main`). Demanding equality in that direction therefore
    reddens EVERY pull request in that repository for the whole gap between the two merges, on a
    caller that is arriving. Measured while wiring MeshWeaver#3878's publication job into
    MeshWeaver.Plugins: 16 open pull requests, and `validate / Validate node repos` is a required
    context there, so the window is a repository-wide block, not a warning.

    So a row may declare itself `pending: <reason>`, and that excuses ABSENCE ONLY. The moment the
    repository has the caller it is compared key for key like every other row, so a pending marker
    can never launder a lowered grant or an unrecorded caller. An absent row WITHOUT the marker
    stays RED — a caller deleted and its row forgotten is the roster describing a repository that
    does not exist — and a marker with an empty reason is RED for the same reason `asserted-by:`
    must say something.
    """
    roster = _load_roster(roster_path)
    entry = roster["repos"].get(repo)
    actual = _row_for(root, platform_repo)
    recipe = (f"      Re-measure with: python3 check-workflow-permission-pairing.py --root . "
              f"--emit-fleet-row {repo}\n      and land the printed block in "
              f"{platform_repo}/.github/lane-caller-grants.yml.")
    if entry is None:
        return [f"fleet roster: {repo} is not in {platform_repo}'s .github/lane-caller-grants.yml, so "
                f"a change to a lane it calls is checked against NOTHING in that repo's own pull "
                f"request — which is how MeshWeaver#3933 reached 28 zero-job runs.\n" + recipe]
    want = sorted((entry.get("callers") or []), key=lambda r: (str(r.get("workflow")), str(r.get("job"))))
    recorded_keys = {_row_key(r) for r in want}
    present_keys = {_row_key(r) for r in actual}
    out: list[str] = []

    # (a) + (b) — the two directions in which the roster OVERSTATES. Every caller this repository
    # really has must be recorded WITH EXACTLY ITS GRANT; a lowered grant changes the key, so it
    # arrives here as an unrecorded caller rather than slipping through as a match.
    for row in actual:
        if _row_key(row) not in recorded_keys:
            out.append(
                f"fleet roster: {repo} calls `{row['lane']}` from {row['workflow']}#{row['job']} "
                f"granting {row['grants']}, and the roster records no such caller — it was either "
                f"ADDED here without a row or LOWERED away from the grant recorded. Both make core's "
                f"fleet check answer a confident green on a broken fleet.\n"
                f"      recorded: {sorted(recorded_keys)}\n"
                f"      actual:   {sorted(present_keys)}\n" + recipe)

    # (c) — a row this repository does not have. Safe for core, so it is a refusal only when nothing
    # DECLARED it, and the declaration has to say something.
    for row in want:
        label = f"{row.get('workflow')}#{row.get('job')} -> {row.get('lane')}"
        if _row_key(row) in present_keys:
            if "pending" in row:
                print(f"  note: {label} is still marked `pending:` in the roster and this repository "
                      f"now HAS it — the marker is spent; remove it from {platform_repo}.")
            continue
        pending = row.get("pending")
        if pending is None:
            out.append(
                f"fleet roster: the roster records {repo} calling {label}, which this repository does "
                f"not have. A row whose caller was DELETED describes a repository that no longer "
                f"exists; a row whose caller has not LANDED yet must say so with "
                f"`pending: <reason naming the pull request that lands it>`.\n" + recipe)
        elif not str(pending).strip():
            out.append(
                f"fleet roster: {repo}'s row for {label} is marked `pending:` with no reason. An "
                f"exemption that says nothing is indistinguishable from one nobody meant — name the "
                f"pull request that lands the caller.")
        else:
            print(f"  note: {label} is recorded as PENDING and is not in this repository yet — "
                  f"{str(pending).strip()}")
    return out


def emit_fleet_row(root: str, repo: str, platform_repo: str) -> int:
    rows = _row_for(root, platform_repo)
    print(yaml.safe_dump({repo: {"asserted-by": "node-repo-validate", "callers": rows}},
                         sort_keys=False, default_flow_style=False))
    return 0


# ─────────────────────────────────────────── SELF-TEST ──────────────────────────────────────────

def _mutate_lane(src_root: str, lane_name: str, lane_job: str, scope: str, level: str) -> str:
    """A COPY of `src_root`'s workflows with one extra demand on one lane job. The copy is written
    from the parsed document, which is exactly what every mode reads, so the control cannot drift
    away from the file the guard actually sees."""
    tmp = tempfile.mkdtemp(prefix="wfperm-")
    dest = os.path.join(tmp, ".github", "workflows")
    os.makedirs(dest)
    for wf_path in _workflow_files(src_root):
        doc = _load(wf_path)
        if isinstance(doc, Exception) or not isinstance(doc, dict):
            continue
        if os.path.basename(wf_path) == lane_name:
            doc = copy.deepcopy(doc)
            if lane_job == WORKFLOW_LEVEL:
                perms = dict(_perm_map(doc.get("permissions")) or {})
                perms[scope] = level
                doc["permissions"] = perms
            else:
                target = (doc.get("jobs") or {}).get(lane_job)
                perms = dict(_perm_map(target.get("permissions")) or {})
                perms[scope] = level
                target["permissions"] = perms
        with open(os.path.join(dest, os.path.basename(wf_path)), "w", encoding="utf-8") as fh:
            yaml.safe_dump(doc, fh, sort_keys=False)
    return tmp


def _pick_scope(granted: dict | None) -> str | None:
    for scope in _MUTATION_SCOPES:
        if _satisfies(granted, {scope: "write"}):
            return scope
    return None


def _unit_cases() -> list[tuple[str, bool]]:
    lane_wf_level = {"jobs": {"a": {}}, "permissions": {"contents": "write"}}
    lane_job_level = {"jobs": {"pack": {"permissions": {"id-token": "write"}}}}
    return [
        ("the 2026-09-10 outage shape is caught",
         _satisfies({"contents": "read", "actions": "read"},
                    {"contents": "read", "id-token": "write"}) == ["id-token"]),
        ("a correctly paired caller passes",
         _satisfies({"contents": "read", "actions": "read", "id-token": "write"},
                    {"contents": "read", "id-token": "write"}) == []),
        ("write-all satisfies anything", _satisfies({"*": "write-all"}, {"id-token": "write"}) == []),
        ("an absent block still covers the default floor", _satisfies(None, {"contents": "read"}) == []),
        ("an absent block does NOT cover id-token",
         _satisfies(None, {"id-token": "write"}) == ["id-token"]),
        ("an absent block does NOT cover a write beyond the floor",
         _satisfies(None, {"contents": "write"}) == ["contents"]),
        ("read does not satisfy a write demand",
         _satisfies({"contents": "read"}, {"contents": "write"}) == ["contents"]),
        # the widening this audit added — each of these was silently unseen by #3989
        ("a lane's WORKFLOW-level block is a demand",
         list(_lane_demands(lane_wf_level)) == [WORKFLOW_LEVEL]),
        ("a lane's job-level block is still a demand",
         list(_lane_demands(lane_job_level)) == ["pack"]),
        ("a lane demanding write-all is not satisfied by a scoped grant",
         _satisfies({"contents": "write", "id-token": "write"}, {"*": "write-all"}) == ["write-all"]),
        ("a lane demanding read-all is satisfied by a caller's write-all",
         _satisfies({"*": "write-all"}, {"*": "read-all"}) == []),
        ("a scope the caller lacks is always findable for the real-tree control",
         _pick_scope({"contents": "read"}) is not None and _pick_scope({"*": "write-all"}) is None),
    ]


def _owns_lanes(root: str, roster_path: str) -> bool:
    """Does THIS checkout hold the lanes the roster describes? True in core, False in a satellite."""
    doc = _load(roster_path)
    if isinstance(doc, Exception) or not isinstance(doc, dict):
        return False
    wanted = {str(c.get("lane")) for e in (doc.get("repos") or {}).values()
              for c in ((e or {}).get("callers") or [])}
    have = {os.path.basename(p) for p in _workflow_files(root)
            if isinstance(_load(p), dict) and _is_workflow_call(_load(p))}
    return bool(wanted) and wanted.issubset(have)


def _materialize_row(rows: list[dict], platform_repo: str) -> str:
    """A throwaway checkout whose callers ARE the roster's recorded rows."""
    tmp = tempfile.mkdtemp(prefix="wfperm-row-")
    dest = os.path.join(tmp, ".github", "workflows")
    os.makedirs(dest)
    by_file: dict[str, dict] = {}
    for r in rows:
        job = {"uses": f"{platform_repo}/.github/workflows/{r['lane']}@main"}
        if r.get("grants") not in (None, "inherit"):
            job["permissions"] = r["grants"]
        by_file.setdefault(str(r["workflow"]), {})[str(r["job"])] = job
    for fname, jobs in by_file.items():
        with open(os.path.join(dest, fname), "w", encoding="utf-8") as fh:
            yaml.safe_dump({"on": {"pull_request": {}}, "jobs": jobs}, fh, sort_keys=False)
    return tmp


def _roster_roundtrip_cases(fleet: str) -> list[tuple[str, bool]]:
    """🚨 CONTROLS ON THE ANTI-STALENESS HALF, derived from the REAL roster.

    `--assert-fleet-row` is what stops the roster becoming a memory, so it needs its own proof that
    it can fail. These build a throwaway checkout FROM THE COMMITTED ROWS and then break it the two
    ways a real repository drifts — both of which make the fleet check answer a confident green on a
    broken fleet. Reading the real roster means the control dies loudly if the roster's shape moves,
    rather than quietly passing against a fixture nobody updated.
    """
    doc = _load(fleet)
    if isinstance(doc, Exception) or not isinstance(doc, dict) or not (doc.get("repos") or {}):
        return [("the fleet roster parses and has rows to build a control from", False)]
    repo, entry = sorted((doc.get("repos") or {}).items())[0]
    rows = (entry or {}).get("callers") or []
    if not rows:
        return [(f"the roster's first row ({repo}) has callers to build a control from", False)]

    cases = [(f"a checkout built from {repo}'s recorded row matches it "
              f"({len(rows)} caller(s))",
              assert_fleet_row(_materialize_row(rows, "Systemorph/MeshWeaver"), repo, fleet,
                               "Systemorph/MeshWeaver") == [])]

    # (a) the repository LOWERS a grant the roster still records
    lowered = copy.deepcopy(rows)
    victim = next((r for r in lowered if isinstance(r.get("grants"), dict) and r["grants"]), None)
    if victim is None:
        cases.append((f"{repo} has an explicit grant to lower — otherwise this control is vacuous",
                      False))
    else:
        victim["grants"] = {k: v for k, v in list(victim["grants"].items())[1:]} or "inherit"
        cases.append((f"a repository that LOWERS a recorded grant is caught",
                      assert_fleet_row(_materialize_row(lowered, "Systemorph/MeshWeaver"), repo,
                                       fleet, "Systemorph/MeshWeaver") != []))

    # (b) the repository ADDS a caller the roster does not know
    added = copy.deepcopy(rows) + [{"workflow": "ci.yml", "job": "unrecorded-caller",
                                    "lane": "node-repo-gate.yml", "grants": {"contents": "read"}}]
    cases.append((f"a repository that ADDS an unrecorded caller is caught",
                  assert_fleet_row(_materialize_row(added, "Systemorph/MeshWeaver"), repo, fleet,
                                   "Systemorph/MeshWeaver") != []))

    # (c) a repository absent from the roster entirely
    cases.append(("a repository missing from the roster is caught",
                  assert_fleet_row(_materialize_row(rows, "Systemorph/MeshWeaver"),
                                   "Systemorph/NotInTheRoster", fleet,
                                   "Systemorph/MeshWeaver") != []))
    return cases


def _real_tree_cases(root: str, platform_root: str | None, fleet: str | None) -> list[tuple[str, bool]]:
    """🚨 CONTROLS DERIVED FROM THE REAL TREE, IN BOTH DIRECTIONS.

    #3989's self-test asserted "the 2026-09-10 outage shape is caught" against a hand-written pair of
    dicts, and passed, while injecting that exact shape into the real node-repo-module-pack.yml
    changed nothing — because core's own caller grants `id-token: write` and the callers that break
    are in repositories core cannot read. A fixture that can be right while the tree is wrong is the
    guard-whose-subject-moved shape AGENTS.md names. These cases mutate the REAL lane files.
    """
    cases: list[tuple[str, bool]] = []

    base_problems, base_pairs = check(root, platform_root)
    cases.append((f"the unmodified tree pairs cleanly ({base_pairs} pair(s))",
                  not base_problems and base_pairs > 0))

    # NEGATIVE CONTROL, pairing mode: strengthen a lane THIS repo really calls with a scope its real
    # caller really lacks, and require the guard to name it.
    mutated_any = False
    for caller in _callers(root):
        lane_path = _local_lane_path(caller["uses"], root, platform_root)
        if lane_path is None or not os.path.isfile(lane_path):
            continue
        in_platform = bool(platform_root) and os.path.abspath(lane_path).startswith(
            os.path.abspath(platform_root) + os.sep)
        src_root = platform_root if in_platform else root
        lane = _load(lane_path)
        if isinstance(lane, Exception) or not isinstance(lane, dict) or not (lane.get("jobs") or {}):
            continue
        scope = _pick_scope(caller["grants"])
        if scope is None:
            continue
        lane_job = sorted((lane.get("jobs") or {}))[0]
        tmp = _mutate_lane(src_root, os.path.basename(lane_path), lane_job, scope, "write")
        probs, pairs = check(root, tmp) if in_platform else check(tmp, platform_root)
        hit = any(scope in p and caller["job"] in p for p in probs)
        cases.append((f"injecting `{scope}: write` into {os.path.basename(lane_path)}#{lane_job} is "
                      f"caught and names {caller['workflow']}#{caller['job']} ({pairs} pair(s))", hit))
        mutated_any = True
        break
    if not mutated_any:
        cases.append(("a real caller/lane pair exists to mutate — otherwise this control is vacuous",
                      False))

    if fleet:
        cases += _roster_roundtrip_cases(fleet)

    # 🚨 THE FLEET CONTROLS BELONG TO THE CHECKOUT THAT OWNS THE LANES, and asking a satellite to run
    # them reds it for a true statement. A satellite's `--root` holds callers and no lane at all, so
    # `check_fleet` there resolves ZERO pairs and every roster row reports its lane missing. This is
    # NOT a skip-trapdoor: ownership is a property of the tree, not of a secret or an event, it is
    # PRINTED either way, and core — the one checkout that does own them — runs these on every pull
    # request. Measured while writing this: without the gate, simulating the satellite lane failed
    # three controls at once and would have reddened `Validate node repos` in all six repositories.
    if fleet and _owns_lanes(root, fleet):
        fleet_problems, fleet_pairs, _ = check_fleet(root, fleet)
        cases.append((f"the unmodified tree passes the fleet roster ({fleet_pairs} pair(s))",
                      not fleet_problems and fleet_pairs > 0))

        # 🚨 THE 2026-09-10 SHAPE, ON THE REAL TREE. #3933 put `id-token: write` on
        # node-repo-module-pack.yml's `pack`. Core's own caller grants it; MeshWeaver.Plugins'
        # `modules-floor` does not. This control asserts the guard now says so.
        lane_name, lane_job, scope = "node-repo-module-pack.yml", "pack", "id-token"
        lane_file = os.path.join(root, ".github", "workflows", lane_name)
        lane_doc = _load(lane_file)
        if isinstance(lane_doc, dict) and lane_job in (lane_doc.get("jobs") or {}):
            tmp = _mutate_lane(root, lane_name, lane_job, scope, "write")
            probs, pairs, _ = check_fleet(tmp, fleet)
            named = [p for p in probs if scope in p and lane_name in p]
            cases.append((f"#3933's exact shape (`{scope}: write` on {lane_name}#{lane_job}) reds the "
                          f"fleet check and names {len(named)} ungranting caller(s): "
                          f"{'; '.join(sorted({p.split(' does NOT grant')[0].split('demands ')[-1].split(', which ')[-1] for p in named})) or '—'}",
                          bool(named)))
        else:
            cases.append((f"{lane_name} still has a `{lane_job}` job to inject #3933's shape into — "
                          f"otherwise this control checks nothing", False))
    return cases


def self_test(root: str | None, platform_root: str | None, fleet: str | None) -> int:
    """The check must FAIL on the 2026-09-10 shape and PASS on the paired one. A guard that cannot
    fail is not a guard (AGENTS.md)."""
    cases = _unit_cases()
    if root:
        # 🚨 SAY WHICH CONTROLS RAN. A self-test whose coverage silently depends on where it is
        # invoked is the instrument that answers confidently from memory — the same defect this
        # whole change is about. The mode is a property of the TREE (does this checkout hold the
        # lanes?), never of a secret or an event, and the fleet controls always run in core.
        owns = bool(fleet) and _owns_lanes(root, fleet)
        if not fleet:
            print("  MODE: pairing controls only — no --fleet roster given, so neither the fleet "
                  "check nor the roster-truth controls ran.")
        elif owns:
            print("  MODE: this checkout OWNS the lanes — unit + pairing + roster-truth + FLEET "
                  "controls, including #3933's exact shape injected into the real lane file.")
        else:
            print("  MODE: this checkout CALLS the lanes but does not own them — unit + pairing + "
                  "roster-truth controls. The fleet controls run in Systemorph/MeshWeaver, whose "
                  "checkout holds the lane definitions they mutate.")
        cases += _real_tree_cases(root, platform_root, fleet)
    else:
        print("  NOTE: no --root given — only the unit cases ran. A caller that can pass --root "
              "should: the real-tree controls are the ones that cannot drift away from the tree.")
    for name, ok in cases:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    bad = [n for n, ok in cases if not ok]
    print(f"  {len(cases) - len(bad)}/{len(cases)} control(s) passed")
    return 1 if bad else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".", help="repository to check (its callers)")
    ap.add_argument("--platform-root", default=None,
                    help="platform checkout at platform-ref, where the shared lanes live")
    ap.add_argument("--fleet", default=None,
                    help="the committed fleet roster (.github/lane-caller-grants.yml): the grants of "
                         "callers in repositories this checkout cannot read")
    ap.add_argument("--assert-fleet-row", default=None, metavar="OWNER/REPO",
                    help="assert the roster's row for this repository still matches its real callers")
    ap.add_argument("--emit-fleet-row", default=None, metavar="OWNER/REPO",
                    help="print this repository's row for the platform's fleet roster")
    ap.add_argument("--platform-repo", default="Systemorph/MeshWeaver",
                    help="the repository whose lanes the roster describes")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()

    if args.emit_fleet_row:
        return emit_fleet_row(args.root, args.emit_fleet_row, args.platform_repo)

    if args.self_test:
        return self_test(args.root, args.platform_root, args.fleet)

    if args.assert_fleet_row:
        if not args.fleet:
            print("::error::--assert-fleet-row needs --fleet <roster>; without it the assertion would "
                  "check nothing", file=sys.stderr)
            return 2
        problems = assert_fleet_row(args.root, args.assert_fleet_row, args.fleet, args.platform_repo)
        print(f"check-workflow-permission-pairing: roster row for {args.assert_fleet_row} — "
              f"{len(_row_for(args.root, args.platform_repo))} caller(s) measured, "
              f"{len(problems)} mismatch(es)")
        for p in problems:
            print(f"::error::{p}")
        return 1 if problems else 0

    if args.fleet:
        problems, pairs, notes = check_fleet(args.root, args.fleet)
        # 🚨 The DENOMINATOR is printed on every run, green or red. A check pointed at the wrong root
        # resolves no lanes, reports zero problems, and ticks exactly like a clean measurement.
        print(f"check-workflow-permission-pairing (fleet): {pairs} roster caller/lane pair(s) "
              f"resolved, {len(problems)} violation(s), root={os.path.abspath(args.root)}, "
              f"roster={os.path.abspath(args.fleet)}")
        for n in notes:
            print(f"  NOT COVERED: {n}")
        if pairs == 0:
            print("  NOTE: zero roster pairs resolved — the roster is empty or names no lane this "
                  "checkout has. Zero is not a pass.")
            problems = problems or ["fleet roster resolved ZERO pairs — the check ran against nothing"]
        for p in problems:
            print(f"::error::{p}")
        return 1 if problems else 0

    problems, pairs = check(args.root, args.platform_root)
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
