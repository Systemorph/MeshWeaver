#!/usr/bin/env python3
"""arm-promoted-set.py — THE ONE PROMOTION GATE: which promoted set may the fleet roll to?

    python3 .github/scripts/arm-promoted-set.py select --armed-max N [--override SET] [--resume SET]
    python3 .github/scripts/arm-promoted-set.py armed-state --manifests portal.json   # the cursor
    python3 .github/scripts/arm-promoted-set.py control-first --control-manifests control.json --version V
    python3 .github/scripts/arm-promoted-set.py control-announcement --version V --image-repository R --instance URL [--deployment D]
    python3 .github/scripts/arm-promoted-set.py first-instance --branch B          # the line's first instance (policy line-first-instance)
    python3 .github/scripts/arm-promoted-set.py control-lag --control-record control.json  # control-always-latest, per line
    python3 .github/scripts/arm-promoted-set.py armed-base --manifests portal.json   # promote's record base
    python3 .github/scripts/arm-promoted-set.py --self-test

Policies `one-promotion-gate`, `platform-deploy-control-first`, `platform-module-deploy-separate` and
`control-always-latest` (register: Doc/Architecture/PolicyNotProse). Design of record:
Doc/Architecture/PlatformAndModuleDeploy (and Doc/Architecture/OnePromotionGate for the arming
mechanics this file still owns: the cursor, the completion marker, the resume, the base).

WHAT A SET IS. main-cd builds core main HEAD together with MeshWeaver.Plugins main HEAD as of its
`gate` job (both full commits are in the promotion record; the `<core7>-p<plugins7>` pair tag that
used to name them on memex-portal-ai is retired). `promote` tags the
set for CI — identity tags, `main`, `mw-plugin-test:latest`, the tester's and the migration's
version tags — and uploads a PROMOTION RECORD (artifact `promotion-record`, one JSON object: the
run number, both full commits, the staging tag, the three image versions and the verdict key).
Every CI consumer (the platform resolver, the satellites, the runner volume) reads a promoted set
from there on; none of them waits for anything below.

WHAT ARMING IS. The fleet does not roll to a promoted set. It rolls to an ARMED one: the portal's
version tag `memex-portal-ai:<version>` (what SelfUpdateHostedService lists, pattern `3.0.0-ci*`),
the line pointers `3-latest` / `3.0-latest` / `3.0.0-latest`, and the signed release event into
the control instance. main-cd's `arm` job writes those, for the set this script SELECTS:

  * the NEWEST promoted set NEWER than every set already armed (never backwards — a set older than
    the armed one is passed over, saying so);
  * whose PLATFORM verdict is green (`judge`): the platform's own tests (a promoted set comes only
    from a commit whose required check is green), that run's compatibility LADDER (the module set
    the fleet runs, as PUBLISHED by the modules' own lanes, links unchanged against this platform),
    and CONTROL FIRST — the control instance (`.github/control-instance.json`) is RUNNING a build
    that contains the set's core commit and answers `/health` 200. No MeshWeaver.Plugins
    dependent-suites verdict, seal or pair is read: a module's own problems never hold a platform
    roll (policy `platform-module-deploy-separate`);
  * or the one set named by `--override` (a workflow_dispatch carrying its reason) — an
    instruction, so a set that is not promoted is a RED, never a substitution.

A set whose ladder is still running (or unreadable), or whose control reading is absent, is
WAITING; one whose ladder is red, MISSING (the run has no ladder job) or AMBIGUOUS (several jobs
answer to the ladder's name) is REFUSED: neither is armed, and a newer green set supersedes both.
Silence is never a pass.

THE LINE'S FIRST INSTANCE (policy `line-first-instance`, MeshWeaver#8365). "Control first" is one
case of a per-line rule: every delivery line has ONE instance that runs its build before the fleet is
offered it. A line that declares none in `.github/delivery-lines.json` has the control instance (its
url from `.github/control-instance.json`, its builds from `control-first` below) — the line control
follows, unchanged. A line that declares `firstInstance` has THAT instance: `first-instance-first`
tags `<firstInstanceImageRepository>:<version>` and sends its record the signed announcement (the same
`control-first` decision and `control-announcement` body), and `select` reads that instance's
`/api/version` + `/health` at its record's `content.host` with the very `judge`/`control_lag`/
`arming_frozen` it reads control with — the reading carries the instance's NAME and the LINE, so every
sentence says whose run and which line. Its own repository is the point: an instance on the fleet's
`memex-portal-ai` can only run a set after `arm` writes its version tag, i.e. after the fleet is
offered it — a wait that can never end.

NO FIRST INSTANCE, DECLARED (policy `line-no-first-instance`). A line may declare `firstInstance:
"none"` with a `noFirstInstanceReason` — a NAMED state cd-line.py validates, never the reading of an
omitted field (omitted is control, which HOLDS a line whose major its record excludes). Such a line
has no instance to read: `select` reads no record and no /api/version, and a set whose ladder passed
is GREEN on the platform evidence alone — promoted from a commit whose required check is green, its
compatibility ladder green, and (in the workflow) the image-identity gate before the arming write.
Every one of its sentences carries the declaration and its reason; nothing is ever frozen on it.

CONTROL FIRST. `control-first` decides the control image's tags for THIS run's accepted build:
every platform build that passed the control image's own acceptance is tagged
`memex-control:<version>` (+ line pointers, never backwards) in the run that built it — before,
and independent of, any arming. `control-lag` is the alarm on the other end: control not running
the newest build it was given, past the bound in `.github/control-instance.json`, is RED — read
against the builds of the ONE line control's record's `updatePattern` names, never across lines.

ARMING IS COMPLETE ONLY WHEN THE WHOLE SEQUENCE LANDED. The cursor — the newest `<version>` tag on
memex-portal-ai — moves at phase C, before phase D (the line pointers) and before the release event.
If either of those fails, the next run would see the set as armed and skip it forever. So
`notify-platform-update` writes a NON-selectable marker tag `arm-complete-<version>` on the portal
manifest only after the build fact was delivered, `armed-state` reports whether the newest armed set
carries it, and when it does not and nothing newer is green, `select --resume <version>` re-arms
exactly that set (one with no promotion record — armed by the pre-gate `promote`, before the marker
existed — cannot be resumed and is reported, not failed) (phase C is an idempotent re-tag of the same digest; phase D and the event run
again — the control instance consumes a repeated build fact idempotently). A newer green set still
wins: arming it moves the pointers and sends the event past the incomplete one.

THE BUNDLE'S BASE — the newest ARMED set, never the first parent. A promoted set usually carries
several core merges (the batch window), and
every one of them reaches the fleet when the set is armed. So the record's `base` — the commit the
Plugins run diffs from and re-runs its control arm at, and the one the verdict is pinned to — is the
core commit of the newest ARMED set: `base..candidate` is then exactly the merges the fleet has not
seen. `armed-base` resolves it from memex-portal-ai's manifests (the armed version tag shares its
manifest with a core sha or staging tag — or, for a set armed before the pair tag's retirement, a
pair tag). The record says which rule produced `base` in
`base_kind`:

  * `armed`         — the newest armed set's commit (the normal case);
  * `first-parent`  — no set has ever been armed, or the candidate IS the armed set: nothing wider
                      exists to measure, said out loud;
  * `unresolved`    — the armed set could not be read. The record keeps the first parent so CI still
                      promotes, but `select` REFUSES to arm it: a
                      narrower bundle than the truth would arm merges nobody measured. The next
                      promoted set tries again.

A record written before this rule carries no `base_kind` and is judged as before (legacy, first
parent), so a set promoted in the transition is never stranded.

There is no second consumer any more. MeshWeaver.Plugins' advisory poller (`promotion-candidate.yml`)
and the `pending` command that answered it were retired once no verdict it wrote decided anything
(Doc/Architecture/PlatformAndModuleDeploy, migration step 6).

"Does armed set X contain core commit C / Plugins commit P?" is answered by ANCESTRY against the
record's two commits — Memex `scripts/image-contains.py`, which reads the same record.

API budget: one runs page, one artifact listing per run examined (+1 download per record). REST only (AGENTS.md: never GraphQL).
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ghrest  # noqa: E402 — the shared GitHub REST client beside this file

CORE = "Systemorph/MeshWeaver"
API = "https://api.github.com"
WORKFLOW = "main-cd.yml"
RECORD_ARTIFACT = "promotion-record"
RUNS_EXAMINED = 40
UNARMED_MAX_PAGES = 5   # the fleet-unarmed reading pages up to 200 runs back to the armed cursor
RECORD_KEYS = ("run_number", "core_sha", "base", "plugins_sha", "short", "plugins_short", "staging",
               "v_portal", "v_migration", "v_plugin", "key")
SHA = re.compile(r"^[0-9a-f]{40}$")
SHORT_SHA_TAG = re.compile(r"^[0-9a-f]{7}$")
# LEGACY — the retired `<core7>-p<plugins7>` pair tag (policy `platform-module-deploy-separate`).
# main-cd no longer mints it; sets promoted before the retirement still carry it, so it is READ,
# never written. The per-run identity that replaced it is the staging tag below.
PAIR_TAG = re.compile(r"^([0-9a-f]{7})-p[0-9a-f]{7}$")
# `staging-<core7>-<run id>` — the build's own tag, written before promote, unique per run and never
# moved to another build, so it names the core of the manifest it sits on even after a rebuild of the
# same core commit re-points the bare `<core7>` tag. `arm` writes the version tag FROM it.
STAGING_TAG = re.compile(r"^staging-([0-9a-f]{7})-[0-9]+$")
BASE_KINDS = ("armed", "first-parent", "unresolved")
SET_NAME = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?[.-]ci\.(\d+)$")
# 🚨 THE SEMVER NOTATION (policy `platform-semver-versioning`, core Doc/Architecture/PlatformVersioning):
# from line 3.1 on a set is the plain `<major>.<minor>.<run>` and the PATCH is the CD run number —
# the same monotonic counter `-ci.<run>` carried. Read ONLY at or above the boundary, so no tag the
# old notation published (`3.0.0`, the withdrawn `3.1.0-ci.7841`) changes meaning. The C# twin is
# `PlatformReleaseOrder.SemVerEraStart`; the two must name the same boundary.
SEMVER_ERA_START = (3, 1)
SEMVER_SET_NAME = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def _semver_set(version: str):
    m = SEMVER_SET_NAME.match(version or "")
    # A zero patch is a floor or a release (`3.1.0`), never a CD run.
    return m if m and (int(m.group(1)), int(m.group(2))) >= SEMVER_ERA_START and int(m.group(3)) > 0 else None

Get = Callable[[str, str], tuple[int, object]]


def verdict_key(short: str, plugins_short: str) -> str:
    """The verdict ref's key for a pair — the pair tag, prefixed. Deterministic, so any run (and the
    Plugins poller) finds the same ref for the same image without being told."""
    return f"pair-{short}-p{plugins_short}"


def run_number_of(version: str) -> int | None:
    m = SET_NAME.match(version or "")
    if m:
        return int(m.group(1))
    m = _semver_set(version)
    return int(m.group(3)) if m else None


def major_of(version: str) -> int | None:
    """The platform MAJOR of a set name (`3.0.0-ci.9538` → 3, `4.0.11000` → 4), or None for a name
    that is not a set. Pure. Policy `major-lines-run-separately`: every selection of "the newest
    armed/promoted set" is made WITHIN one major, because the lines share one run counter — without
    it, the first 4.x set would be "newest" for the v3 line forever."""
    if run_number_of(version) is None:
        return None
    return int(str(version).split(".", 1)[0])


def _in_major(version: str, major: int | None) -> bool:
    """`major` None is the unfiltered, single-line reading (a caller that names no line)."""
    return major is None or major_of(version) == major


def check_record(rec: object) -> str | None:
    """Why a record cannot be used, or None. Pure."""
    if not isinstance(rec, dict):
        return "the record is not a JSON object"
    missing = [k for k in RECORD_KEYS if not rec.get(k)]
    if missing:
        return f"the record lacks {', '.join(missing)}"
    for k in ("core_sha", "base", "plugins_sha"):
        if not SHA.match(str(rec[k])):
            return f"{k} {rec[k]!r} is not a 40-hex commit"
    if not str(rec["core_sha"]).startswith(str(rec["short"])) or not str(rec["plugins_sha"]).startswith(str(rec["plugins_short"])):
        return "the short shas do not prefix the full ones"
    if rec["key"] != verdict_key(rec["short"], rec["plugins_short"]):
        return f"the key {rec['key']!r} is not {verdict_key(rec['short'], rec['plugins_short'])!r}"
    if run_number_of(str(rec["v_portal"])) != int(rec["run_number"]):
        return f"v_portal {rec['v_portal']!r} does not carry run number {rec['run_number']}"
    if "base_kind" in rec and rec["base_kind"] not in BASE_KINDS:
        return f"base_kind {rec['base_kind']!r} is not one of {BASE_KINDS}"
    return None


ARM_COMPLETE_PREFIX = "arm-complete-"


def armed_state(manifests: object, major: int | None = None) -> tuple[int, str, bool] | None:
    """(run number, version, complete) of the newest ARMED set, or None when nothing was ever armed.
    `complete` = its manifest also carries `arm-complete-<version>`, which `notify-platform-update`
    writes only after the release event was delivered. The marker is not version-shaped, so no
    self-updater can ever select it. Pure."""
    got = armed_commit(manifests, require_sha=False, major=major)
    if got is None:
        return None
    version = got[0]
    tags: list[str] = []
    for row in manifests if isinstance(manifests, list) else []:
        t = row.get("tags") if isinstance(row, dict) else None
        if isinstance(t, list) and version in t:
            tags = [str(x) for x in t]
            break
    return run_number_of(version) or 0, version, f"{ARM_COMPLETE_PREFIX}{version}" in tags


def arming_cursor(manifests: object, major: int | None = None) -> dict | None:
    """The `arm` job's CURSOR for a line of `major` — {"max", "version", "complete", "cold_start"} —
    or None when memex-portal-ai carries no armed set of ANY major (a broken read, or a registry that
    was never armed: refused, never read as "nothing armed", which would arm anything). Pure.

    🚨 COLD START OF A MAJOR (policy `major-lines-run-separately`). When a line moves to a NEW major
    (main → 4.x) no set of that major has ever been armed, yet the fleet is armed — on the previous
    major. That is not "nothing armed": it is a cold start FOR THAT MAJOR. The cursor is then 0 of
    that major, nothing is resumed, and `select` arms the newest set of the major whose platform
    verdict is green — through the ordinary path, never `--override`. The previous major's pointers
    (`3-latest`, …) are untouched by construction: Phase D moves only the armed set's own major."""
    if armed_commit(manifests, require_sha=False) is None:
        return None
    st = armed_state(manifests, major=major)
    if st is None:
        return {"max": "0", "version": "", "complete": "true", "cold_start": "true"}
    return {"max": str(st[0]), "version": st[1], "complete": "true" if st[2] else "false", "cold_start": "false"}


def armed_commit(manifests: object, require_sha: bool = True, major: int | None = None) -> tuple[str, str] | None:
    """(the newest ARMED version, its seven-character core sha) from memex-portal-ai's manifest
    metadata (`az acr manifest list-metadata -o json`), or None when no manifest carries a version
    tag at all (nothing was ever armed). Pure.

    The version tag is written by `arm` onto the promoted image's manifest, FROM its staging tag
    `staging-<core7>-<run id>`. `promote` also writes the bare core sha tag, but a rebuild of the SAME
    core commit (a host-relevant MeshWeaver.Plugins change, or an operator's `rebuild`) moves that tag
    to the later manifest. The staging tag stays on the armed manifest and still names its core. Sets
    armed before the pair tag was retired are read through their legacy `<core7>-p<plugins7>` tag.
    Conflicting or missing identities are errors, never guesses (a wrong base narrows the bundle).
    With `major`, only version tags of THAT major count (policy `major-lines-run-separately`)."""
    if not isinstance(manifests, list):
        raise ValueError("the manifest metadata is not a JSON list")
    best: tuple[int, str, list[str]] | None = None
    for row in manifests:
        tags = row.get("tags") if isinstance(row, dict) else None
        if not isinstance(tags, list):
            continue
        for t in tags:
            n = run_number_of(str(t))
            if n is not None and _in_major(str(t), major) and (best is None or n > best[0]):
                best = (n, str(t), [str(x) for x in tags])
    if best is None:
        return None
    if not require_sha:
        return best[1], ""
    shas = {t for t in best[2] if SHORT_SHA_TAG.match(t)}
    shas.update(m.group(1) for t in best[2] if (m := PAIR_TAG.match(t)))
    shas.update(m.group(1) for t in best[2] if (m := STAGING_TAG.match(t)))
    if len(shas) != 1:
        raise ValueError(f"the newest armed manifest {best[1]} names {len(shas)} distinct core commits "
                         f"({best[2]}) — cannot name its core commit")
    return best[1], next(iter(shas))


def control_first(control_manifests: object, version: str) -> dict:
    """Policy `platform-deploy-control-first`: what the CONTROL image (`memex-control`) must be told
    about THIS run's accepted platform build. Pure.

    Every platform build that passed the control image's own acceptance (`control-acceptance`:
    empty DB, a broken NodeType row, N-1 on the migrated DB) is the control instance's NEXT image,
    unconditionally — it never waits for the fleet's arming, a MeshWeaver.Plugins verdict, a seal
    or a module publication (policy `platform-module-deploy-separate`). So the version tag (what a
    Continuous record with `updatePattern: 3.0.0-ci*` rolls to) and the line pointers are written
    for `version` here, in the same run that accepted the image. The fleet is offered the build
    only AFTER control is running it (`select`, the control half of the platform verdict).

    Returns {"action", "version", "move_pointers", "announce", "control_newest"}:
      * `done` — memex-control already carries `version` (idempotent re-run);
      * `tag`  — write the version tag, and the line pointers when `move_pointers` (never backwards:
                 a NEWER control version already tagged keeps the pointers where they are).
    `announce` — hand THIS build to control's roll lane (`control_announcement`): true unless a NEWER
    control version is already tagged, on `done` as on `tag` (a re-run re-announces; the routing
    side dedupes, and a re-delivery is how a lost POST is closed). Never an older build: that
    would only be refused as a backwards roll on the receiving side.
    A version that is not `X.Y.Z[-pre]-ci.N` is a ValueError — never a guess."""
    n_self = run_number_of(version)
    if n_self is None:
        raise ValueError(f"{version!r} is not a set name X.Y.Z[-pre]-ci.N — refusing to tag memex-control with it")
    if not isinstance(control_manifests, list):
        raise ValueError("the memex-control manifest metadata is not a JSON list")
    control_tags: set[str] = set()
    for row in control_manifests:
        t = row.get("tags") if isinstance(row, dict) else None
        if isinstance(t, list):
            control_tags.update(str(x) for x in t)
    numbered = [(n, t) for t in control_tags if (n := run_number_of(t)) is not None]
    newest = max(numbered)[1] if numbered else ""
    newest_or_self = not numbered or n_self >= max(numbered)[0]
    if version in control_tags:
        return {"action": "done", "version": version, "move_pointers": False,
                "announce": newest_or_self, "control_newest": newest}
    return {"action": "tag", "version": version, "move_pointers": newest_or_self,
            "announce": newest_or_self, "control_newest": newest}


SELF_UPDATE_AVAILABLE = "self-update-available"
CONTROL_FIRST_REPORTER = "main-cd control-first"


def line_pattern(version: str) -> str:
    """The version LINE a set belongs to, as the self-update pattern syntax spells it:
    `3.0.0-ci.10052` → `3.0.0-ci*`, and in the SemVer notation `3.1.10052` → `3.1.*` (the minor
    line — a record's `3.*` admits it). Pure; a non-set name is a ValueError."""
    m = SET_NAME.match(version or "")
    if m is not None:
        return version[: m.start(1) - 1] + "*"
    e = _semver_set(version)
    if e is not None:
        return f"{e.group(1)}.{e.group(2)}.*"
    raise ValueError(f"{version!r} is not a set name X.Y.Z[-pre]-ci.N or <major>.<minor>.<run> — it names no line")


def control_announcement(deployment: str, version: str, image_repository: str, instance: str,
                         detected_at: str) -> dict:
    """Policy `platform-deploy-control-first`, the DELIVERY half: the signed `self-update-available`
    event `control-first` POSTs into the control plane's inbox (`Hosting/PlatformBuilds`) for the
    control record, so the build it just tagged becomes a routed `Roll` (MeshWeaver.Plugins
    `SelfUpdateRouting`) — which runs UNATTENDED because the record is Continuous, its pattern admits
    the tag and it carries no `rollGate` (`ActionsExecutor.AdmittedUnattended`). Pure.

    🚨 WHY CD SENDS IT, and not control's own in-pod self-updater. That updater lists
    `SelfUpdate:PortalRepository` — `memex-portal-ai` by default, rendered by nothing for control —
    and `memex-portal-ai:<version>` is written only by `arm`, which waits for control to RUN the
    build: a circular wait, so control could only ever follow the fleet, never lead it. Measured
    2026-10-06: control's `Admin/UpdatePolicy` read `check FAILED: CredentialUnavailableException`
    on every hourly check, its handover route was its own inbox (whose mesh holds no `Deployments`
    record), and `Ops/Actions` held NO `selfupdate-roll-control-*` ever. The job that tagged the
    image is the one place that knows, deterministically, that control has a new build.

    The record still decides everything: the receiving side resolves `deployment` to its record
    (the lookup IS the authorization), refuses a repository other than the record's
    `imageRepository`, never rolls backwards, and parks the roll for an approval whenever the
    record is not Continuous, its pattern does not admit the tag, or it declares a `rollGate`.
    `pattern` is the build's own line (`line_pattern`) — the narrowest admission that names this
    tag; the record's `updatePattern` is the ceiling it is intersected with."""
    dep = (deployment or "").strip()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]*", dep):
        raise ValueError(f"{deployment!r} is not a plain deployment id or path")
    repo = (image_repository or "").strip()
    if not repo or ":" in repo.rsplit("/", 1)[-1] or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9./_:-]*", repo):
        raise ValueError(f"{image_repository!r} is not a plain image repository (registry/name, no tag)")
    pattern = line_pattern(version)
    # Key order is the wire order: `event` first, as the instance's own announcer writes it.
    return {
        "event": SELF_UPDATE_AVAILABLE,
        "deployment": dep,
        "instance": instance,
        "newVersion": version,
        "newImage": f"{repo}:{version}",
        "policy": "platform-deploy-control-first",
        "pattern": pattern,
        "trigger": "control-first",
        "reporter": CONTROL_FIRST_REPORTER,
        "detectedAt": detected_at,
    }


# Only the matched version prefix is ever echoed (into a ::notice:: and the step summary) — never the raw
# /api/version string, so nothing after it can spoof an annotation or render as markdown.
RUNNING_MAJOR = re.compile(r"^\s*((\d+)\.\d+(?:\.\d+)?)")


def control_line(version: str, code: int, body: str, who: str = "control") -> dict:
    """Policy `major-lines-run-separately`, the ANNOUNCING half of `platform-deploy-control-first`:
    whether THIS line's build may be handed to control at all. A line announces only a build of the
    MAJOR control currently runs (its public `/api/version`, `code`/`body` as fetched). Pure.

    🚨 WHY (2026-11-14): core `main` became the v4 line while control stayed on v3 (its record
    declares `updatePattern: 3.*`). `control-first` on `main` announced `4.0.13212` and `4.0.13217`
    for control anyway — the announcement names only the BUILD's own line (`line_pattern`) — and the
    control plane opened an approvable Roll; it was approved and control stuck mid-roll, because v3
    modules do not load on v4. The control plane now refuses any build outside the record's
    `updatePattern` (MeshWeaver.Plugins `SelfUpdateRouting.PatternRefusal`) and records the refusal;
    this is the first layer, so a line never even offers control a build of another major.

    The RECORD stays the one declaration of control's line: a cross-line move is declared there
    (`updatePattern: 4.*`) and its first roll is a deliberate, hand-filed Roll; once control RUNS the
    new major, that major's line announces and the old line stops — the same rule, from both ends.

    Returns `{"line_announce", "control_major", "build_major", "line_reason"}`; a control whose
    `/api/version` cannot be read, or names no version, is a ValueError — "could not decide" is RED,
    never "announce anyway" and never a silent skip (policy `control-first-never-silent`)."""
    build_major = major_of(version)
    if build_major is None:
        raise ValueError(f"{version!r} is not a set name — it names no major")
    try:
        running = str(json.loads(body).get("version") or "") if code == 200 else ""
    except (ValueError, AttributeError):
        running = ""
    m = RUNNING_MAJOR.match(running)
    if m is None:
        raise ValueError(f"{who}'s /api/version answered {code} without a version (body: {str(body)[:200]!r}) — "
                         f"cannot tell which major {who} runs, so this build is NOT handed to it")
    control_major = int(m.group(2))
    running = m.group(1)
    if control_major == build_major:
        return {"line_announce": True, "control_major": control_major, "build_major": build_major,
                "line_reason": f"{who} runs {running} (major {control_major}) — this {build_major}.x build is on its line"}
    return {"line_announce": False, "control_major": control_major, "build_major": build_major,
            "line_reason": f"{who} runs {running} (major {control_major}); this line's build {version} is major "
                           f"{build_major} — NOT announced (policy major-lines-run-separately). A move of {who} to "
                           f"{build_major}.x is declared on its record (updatePattern) and rolled deliberately"}


# ───────────────────────────────── transport ──────────────────────────────────────────────

def http_get(url: str, token: str, raw: bool = False) -> tuple[int, object]:
    """GET over REST → (status, parsed JSON | raw bytes), (HTTP code, None), or (0, transport error).
    A RAW download (an artifact zip) is a 302 to blob storage; ghrest follows it and keeps the
    bearer on GitHub's origin — the same rule now covers the JSON reads, which used to follow every
    redirect with the token attached."""
    try:
        if raw:
            return 200, ghrest.download_artifact(url, token=token, api=API, timeout=120)
        with ghrest.request(url, token, api=API, timeout=60) as r:
            body = r.read()
            return r.status, json.loads(body.decode() or "null")
    except urllib.error.HTTPError as e:
        return e.code, None
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, str(e)


def read_records(get: Callable[..., tuple[int, object]], core_token: str,
                 above: int = 0, log: Callable[[str], None] = print, max_pages: int = 1,
                 coverage: dict | None = None, branch: str = "main", major: int | None = None,
                 major_start: bool = False) -> list[dict]:
    """Promotion records of main-cd runs on `branch` (the delivery LINE) numbered ABOVE `above`,
    newest first — and, with `major`, only records whose set is of that major (policy
    `major-lines-run-separately`: a line never arms another major's set).

    A page is RUNS_EXAMINED RUNS, not promotions: hourly reconcile ticks promote nothing and still
    fill it. With `max_pages` > 1 the listing pages on until it reaches a run at or below `above`
    (the armed cursor), the runs run out, or the cap — so a still-unarmed promotion cannot age out of
    the window and read as "armed" (Copilot on MeshWeaver#6367). `coverage`, when given, receives
    `reached` (the listing got down to the cursor) and `floor_at` (the oldest run's `created_at`).

    🚨 COLD START OF A MAJOR (`major_start`, MeshWeaver#8208). When no set of `major` was ever armed
    the cursor is 0, and no run is numbered at or below 0 — so the listing could never "reach" it,
    the window always read incomplete, and the fleet-unarmed alert clocked the stall from the oldest
    run the bounded listing happened to hold (measured: 83.6 h, off pre-split 3.1 runs, when the
    line's first 4.x set was 5.6 h old). With `major_start` the window is complete once the listing
    passes the line's FIRST set of `major`: a promotion record of an OLDER major on the line's own
    branch is the set the line carried before it moved, so nothing of `major` lies beyond it.
    `coverage["major_start_run"]` names that run."""
    runs: list[dict] = []
    out: list[dict] = []
    reached = False
    boundary: int | None = None

    def record_of(run: dict) -> None:
        nonlocal boundary
        number = int(run.get("run_number", 0))
        code, arts = get(f"repos/{CORE}/actions/runs/{run['id']}/artifacts?name={RECORD_ARTIFACT}", core_token)
        if code != 200 or not isinstance(arts, dict):
            log(f"  run #{number}: artifacts unreadable (HTTP {code}) — skipped")
            return
        found = [a for a in arts.get("artifacts", []) if a.get("name") == RECORD_ARTIFACT and not a.get("expired")]
        if not found:
            return            # not a promoting run (reconcile, superseded, or still before promote)
        code, blob = get(found[0]["archive_download_url"], core_token, raw=True)  # type: ignore[call-arg]
        if code != 200 or not isinstance(blob, (bytes, bytearray)):
            log(f"  run #{number}: record download failed (HTTP {code}) — skipped")
            return
        try:
            with zipfile.ZipFile(io.BytesIO(blob)) as z:
                rec = json.loads(z.read(z.namelist()[0]).decode())
        except (zipfile.BadZipFile, IndexError, ValueError, KeyError) as e:
            log(f"  run #{number}: record unreadable ({e}) — skipped")
            return
        why = check_record(rec)
        if why:
            log(f"  run #{number}: record refused — {why}")
            return
        if not _in_major(str(rec["v_portal"]), major):
            older = major_of(str(rec["v_portal"]))
            if major_start and major is not None and older is not None and older < major:
                boundary = number
                log(f"  run #{number}: {rec['v_portal']} is the line's last set before major {major} — "
                    f"the cold-start window is complete here")
            else:
                log(f"  run #{number}: {rec['v_portal']} is not of major {major} — another line's set, skipped")
            return
        rec["run_url"] = run.get("html_url", "")
        rec["run_id"] = run.get("id")
        # WHEN the set was promoted: `promote` itself uploads the record, so the artifact's
        # `created_at` is the promotion time (the run's creation is the fallback). The fleet-unarmed
        # alert clocks a stall from it (fleet-unarmed-alert.py, MeshWeaver#6042).
        rec["promoted_at"] = str(found[0].get("created_at") or run.get("created_at") or "")
        out.append(rec)

    for page_no in range(1, max(1, max_pages) + 1):
        url = f"repos/{CORE}/actions/workflows/{WORKFLOW}/runs?branch={branch}&per_page={RUNS_EXAMINED}"
        code, page = get(url + (f"&page={page_no}" if page_no > 1 else ""), core_token)
        if code != 200 or not isinstance(page, dict):
            raise RuntimeError(f"cannot list {WORKFLOW} runs (HTTP {code}, page {page_no}) — refusing to decide on nothing")
        batch = [r for r in page.get("workflow_runs", []) if isinstance(r, dict)]
        runs.extend(batch)
        for run in batch:
            # Runs are listed newest first: once the line's last older-major set is passed, every
            # run below it is older still, so nothing more is read.
            if boundary is None and int(run.get("run_number", 0)) > above:
                record_of(run)
        if any(int(r.get("run_number", 0)) <= above for r in batch) or len(batch) < RUNS_EXAMINED \
                or boundary is not None:
            reached = True
            break
    if coverage is not None:
        coverage["reached"] = reached
        coverage["floor_at"] = str(runs[-1].get("created_at") or "") if runs else ""
        coverage["major_start_run"] = boundary
    out.sort(key=lambda r: int(r["run_number"]), reverse=True)
    return out


def read_run_jobs(get: Get, core_token: str, run_id: object) -> list[dict] | None:
    """Every job of one main-cd run (REST, one page of 100 — main-cd has fewer jobs than that, the
    matrix legs included), or None when the run is unreadable."""
    code, page = get(f"repos/{CORE}/actions/runs/{run_id}/jobs?per_page=100&filter=latest", core_token)
    if code != 200 or not isinstance(page, dict):
        return None
    jobs = [j for j in page.get("jobs", []) if isinstance(j, dict)]
    if jobs:
        return jobs
    # 🚨 A RE-RUN THAT NEVER STARTED (main-cd #11961/#11966, 2026-10-29): attempt 1 ran every job, the
    # transient-retry lane re-ran it, and attempt 2 was cancelled in the pending slot before it created a
    # job — so `filter=latest` answers ZERO records and a ladder that PASSED reads `missing`. Only an
    # attempt with no record at all is passed over (it ran nothing, so it undid nothing); the newest
    # earlier attempt that ran is read instead. Core's resolver reads a seal the same way (`seal_jobs`).
    code, run = get(f"repos/{CORE}/actions/runs/{run_id}", core_token)
    if code != 200 or not isinstance(run, dict):
        return None
    # The walk starts AT the freshly reported attempt, inclusive: the empty `filter=latest` answer and
    # this read are two requests, and a re-run created between them makes the fresh number NEWER than
    # the attempt that answered empty — skipping it could pass over a ladder that attempt re-ran and lost.
    for attempt in range(int(run.get("run_attempt") or 1), 0, -1):
        code, page = get(f"repos/{CORE}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100", core_token)
        if code != 200 or not isinstance(page, dict):
            return None
        rows = [j for j in page.get("jobs", []) if isinstance(j, dict)]
        if rows:
            return rows
    return jobs


def ladder_of(jobs: list[dict] | None) -> str | None:
    """The ladder's conclusion in one run's jobs: its `conclusion` once completed, `missing` when the
    run has no ladder job at all (a refusal — a promoted set nobody linked the modules against),
    `ambiguous` when SEVERAL jobs answer to the ladder's name (a refusal — a green first one must
    never mask a red later one; a matrix ladder would need this reading rewritten, not guessed), None
    while it runs or when the run could not be read. Pure."""
    if jobs is None:
        return None
    hits = [j for j in jobs if str(j.get("name", "")).rstrip().endswith(LADDER_JOB_SUFFIX)]
    if not hits:
        return "missing"
    if len(hits) > 1:
        return "ambiguous"
    j = hits[0]
    return str(j.get("conclusion")) if j.get("status") == "completed" and j.get("conclusion") else None


def control_given_at(jobs: list[dict] | None, prefix: str = "") -> str | None:
    """When the run's job that handed the line's first instance its build SUCCEEDED (ISO timestamp),
    else None. Pure. `prefix` is that job's name prefix: `Deploy control first` for the control
    instance (the default), `Deploy the line's first instance first` for a declared one."""
    prefix = prefix or CONTROL_FIRST_JOB_PREFIX
    for j in jobs or []:
        if str(j.get("name", "")).startswith(prefix) and j.get("conclusion") == "success":
            return str(j.get("completed_at") or "") or None
    return None


def read_control(url: str, get: Get, core_token: str, core_shas: list[str],
                 fetch: Callable[[str], tuple[int, str]] | None = None) -> dict:
    """The control instance as the platform verdict reads it: its running core commit (public
    `/api/version`), whether `/health` answers 200, and — by ANCESTRY, never by timestamps — whether
    that commit contains each of `core_shas` (GitHub compare: `identical` or `ahead`). Every failure
    is recorded in `why` and leaves the answer unknown, which `judge` and `control_lag` read as
    NOT shown — never as a pass."""
    fetch = fetch or _fetch_public
    out: dict = {"commit": "", "healthy": False, "contains": {}, "why": ""}
    code, body = fetch(f"{url.rstrip('/')}/api/version")
    try:
        commit = str(json.loads(body).get("commit") or "") if code == 200 else ""
    except (ValueError, AttributeError):
        commit = ""
    if not SHA.match(commit):
        out["why"] = f"{url}/api/version answered {code} without a 40-hex commit"
        return out
    out["commit"] = commit
    hcode, hbody = fetch(f"{url.rstrip('/')}/health")
    out["healthy"] = hcode == 200
    if hcode != 200:
        out["why"] = f"{url}/health answered {hcode}"
    out["self_update"] = self_update_reading(hbody)
    for sha in dict.fromkeys(core_shas):
        out["contains"][sha] = contains_commit(get, core_token, commit, sha)
    return out


def contains_commit(get: Get, core_token: str, commit: str, sha: str) -> bool | None:
    """Whether `commit` contains `sha`, by ANCESTRY (GitHub compare `identical`/`ahead`); None when
    the compare could not be read — never a guess."""
    code, cmp = get(f"repos/{CORE}/compare/{sha}...{commit}", core_token)
    return (cmp.get("status") in CONTAINED) if code == 200 and isinstance(cmp, dict) else None


SELF_UPDATE_CHECK = "self_update"
"""The `/health` entry control's OWN self-updater publishes (MeshWeaver.Plugins src/Memex.Portal.ServiceDefaults
`SelfUpdateHealthCheck`, census-tagged, so it prints whatever its status). Its line reads
`self_update: <Healthy|Degraded> — <the last check's verdict>`."""


def self_update_reading(health_body: str) -> dict | None:
    """Control's own self-update reading off its PUBLIC `/health` body — {"status", "text"} — or None
    when the body carries no `self_update` entry (an image older than the check, or no body). Pure.

    🚨 WHY THE ARMING READS IT. Control-first makes the whole fleet wait for ONE instance, so what a
    reader of a frozen fleet needs first is why THAT instance takes no build. Measured 2026-10-07/08:
    control sat on cac0664de for 10+ hours while its own `Admin/UpdatePolicy` read `check FAILED:
    CredentialUnavailableException … The requested identity has not been assigned to this resource` on
    every check — a node nobody watches — and the arm step said `waiting` on every tick."""
    for line in (health_body or "").splitlines():
        if line.startswith(f"{SELF_UPDATE_CHECK}: "):
            status, _, text = line[len(SELF_UPDATE_CHECK) + 2:].partition(" — ")
            return {"status": status.strip(), "text": text.strip()}
    return None


def describe_self_update(control: dict) -> str:
    """One sentence naming control's OWN self-update reading, for every place that says control has
    not taken a build. An absent reading is said as one, never read as a clean one. Pure."""
    su = control.get("self_update")
    who = _who(control)
    if not su:
        return (f"{who}'s /health publishes no `self_update` reading (its image predates the check, or /health "
                f"was not read) — read `Admin/UpdatePolicy.lastCheckVerdict` on {who}")
    if su.get("status") != "Healthy":
        return f"{who}'s OWN self-update check is FAILING ({su.get('status')}): {su.get('text') or 'no verdict text'}"
    return f"{who}'s own self-update reads Healthy: {su.get('text') or 'no verdict text'}"


def _who(control: dict) -> str:
    """The NAME of the instance a reading is of: `control`, or a line's declared first instance
    (`first_instance_reading` stamps it). A reading without one is control's (every caller before
    policy `line-first-instance`)."""
    return str(control.get("name") or "control")


def _first_rule(control: dict) -> str:
    """How the wait is named: `control first` for control; for a declared first instance, whose and of
    which line — so a 3.x reading never reads as a statement about control."""
    who = _who(control)
    if who == "control":
        return "control first"
    return f"{who} first (the first instance of line `{control.get('line') or '?'}`, policy line-first-instance)"


def _fetch_public(url: str) -> tuple[int, str]:
    """GET a PUBLIC endpoint of the control instance — no credential is ever sent to it."""
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={"Accept": "application/json"}),
                                    timeout=60) as r:
            return r.status, r.read().decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, ""
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, str(e)


def load_control_instance(path: Path) -> dict:
    """`.github/control-instance.json` — the ONE declaration of which instance is control, for the
    platform verdict and the `control-always-latest` alarm. RED (ValueError) on anything missing."""
    d = json.loads(path.read_text())
    if not isinstance(d, dict) or not str(d.get("url", "")).startswith("https://"):
        raise ValueError(f"{path}: `url` must be the control instance's https:// base URL")
    bound = d.get("platformLagBoundMinutes")
    if type(bound) is not int or bound <= 0:  # not isinstance: `true` is an int in Python
        raise ValueError(f"{path}: `platformLagBoundMinutes` must be a positive integer")
    return d


LADDER_JOB_SUFFIX = "(ladder)"
CONTROL_FIRST_JOB_PREFIX = "Deploy control first"
# The job that hands a line's DECLARED first instance its build (main-cd `first-instance-first`).
FIRST_INSTANCE_JOB_PREFIX = "Deploy the line's first instance first"
NO_FIRST_INSTANCE = "LINE HAS NO FIRST INSTANCE"
# The DECLARED absence (policy `line-no-first-instance`): cd-line.py's FirstInstance.NONE, and the
# sentence every verdict of such a line starts with.
FIRST_INSTANCE_NONE = "none"
NO_FIRST_INSTANCE_DECLARED = "NO FIRST INSTANCE, DECLARED"
CONTAINED = ("identical", "ahead")


def judge(rec: dict, ladder: str | None, control: dict) -> tuple[str, str]:
    """(state, sentence) — the PLATFORM verdict on one promoted set: `green`, `waiting` or
    `refused`. Pure. Policy `platform-module-deploy-separate`: nothing here asks MeshWeaver.Plugins
    (no dependent-suites verdict, no seal, no pair). What guards a fleet roll is

      1. the platform's OWN tests — a set is promoted only from a commit whose required check is
         green (`gate`), so every record here already carries them;
      2. the compatibility LADDER of that run (`platform-ladder-compat`, conclusion `ladder`): the
         module set the fleet runs, PUBLISHED by the modules' own lanes, links unchanged against
         this platform image. A platform that breaks a module's declared compatibility is red here,
         and is answered by fixing compatibility or a declared epoch bump, never by re-baking;
      3. CONTROL FIRST (policy `platform-deploy-control-first`): the control instance is RUNNING a
         build that contains this set's core commit, and answers `/health` 200. `control` is
         {"commit", "healthy", "contains": {core_sha: True|False|None}, "why"}.

    A still-running (or unreadable) ladder is WAITING. A ladder that is `missing` (the run has no
    ladder job), `ambiguous` (several jobs answer to its name) or ended any other way than `success`
    is REFUSED — each with its own sentence. Control not (yet) on the set, unreadable, or unhealthy
    is WAITING: the fleet is offered a build only after control proved it, and
    `control-always-latest` alarms on a control that does not take it. Silence is never a pass."""
    if ladder is None:
        return "waiting", "its compatibility ladder has not finished (or its run is unreadable)"
    if ladder == "missing":
        return "refused", ("its run has NO compatibility-ladder job — nobody linked the published module set "
                           "against this platform, so there is no verdict to arm on")
    if ladder == "ambiguous":
        return "refused", ("its run has SEVERAL jobs answering to the ladder's name — one verdict cannot be read "
                           "off them (a green leg must never mask a red one); fix the workflow, not the set")
    if ladder != "success":
        return "refused", (f"its compatibility ladder ended `{ladder}` — the published module set does not link "
                           "against this platform; fix compatibility (or declare an epoch bump), never re-bake")
    if control.get("no_first_instance"):
        # 🚨 Policy `line-no-first-instance`: the line DECLARES it has no first instance, so there is no
        # running build to read and nothing to wait for — the set is green on the platform evidence
        # (promoted from a green required check + the ladder above), and the sentence says so by name.
        return "green", f"ladder success; {control['no_first_instance']}"
    commit = control.get("commit") or ""
    who = _who(control)
    if not commit:
        instance = "the control instance" if who == "control" else f"{who} (line `{control.get('line')}`'s first instance)"
        return "waiting", f"{instance}'s running build is unknown ({control.get('why') or 'not read'})"
    contains = (control.get("contains") or {}).get(rec["core_sha"])
    if contains is None:
        return "waiting", f"could not establish whether {who}'s {commit[:9]} contains {rec['core_sha'][:9]}"
    if not contains:
        if str(control.get("line_hold") or "").startswith(NO_FIRST_INSTANCE):
            # The line has NO instance that could run it first (`control_line_hold`, undeclared):
            # "control runs X" would be true and answer nothing, so the sentence is the missing
            # declaration alone (MeshWeaver#8365: control on 4.0 read as the 3.x line's wait for 9 h).
            return "waiting", str(control["line_hold"])
        if control.get("line_hold"):
            # The line is HELD by a declaration (`control_line_hold`): its first instance will not
            # take this build at all, so "not yet" would be a false promise. Still a wait (the first
            # instance first holds), but one that names the declaration that ends it.
            return "waiting", (f"{who} runs {commit[:9]}, which does not contain {rec['core_sha'][:9]} — "
                               + str(control["line_hold"]))
        return "waiting", (f"{who} runs {commit[:9]}, which does not contain {rec['core_sha'][:9]} yet — "
                           f"{_first_rule(control)}: the fleet is offered a build only after {who} runs it; "
                           + describe_self_update(control))
    if not control.get("healthy"):
        return "waiting", (f"{who} runs {commit[:9]} (contains this set) but /health is not 200 "
                           f"({control.get('why') or 'unhealthy'}) — not offered to the fleet until {who} is healthy")
    return "green", f"ladder success; {who} runs {commit[:9]} (contains it) and is healthy"


def select(records: list[dict], ladders: dict[int, str | None], control: dict, armed_max: int,
           override: str = "", resume: str = "") -> tuple[dict | None, list[str]]:
    """(the record to arm or None, the report lines). Pure — the self-test drives it.
    `ladders` maps a record's run number to its ladder job's conclusion (None = not finished/unread)."""
    lines: list[str] = []
    if override:
        n = run_number_of(override)
        if n is None:
            raise ValueError(f"--override {override!r} is not a set name X.Y.Z[-pre]-ci.N")
        # The EXACT set named — never the run number alone: a record of the same run number on
        # another version line would otherwise be armed while the summary claims the requested one.
        hit = next((r for r in records if r["v_portal"] == override), None)
        if hit is None:
            raise ValueError(f"--override names {override}, which has no promotion record among the newest "
                             f"{RUNS_EXAMINED} main-cd runs — an override is an instruction; refusing to substitute")
        lines.append(f"OVERRIDE: arming {hit['v_portal']} WITHOUT the platform verdict, as instructed")
        return hit, lines
    for rec in records:
        n = int(rec["run_number"])
        if n <= armed_max:
            lines.append(f"{rec['v_portal']}: not newer than the armed ci.{armed_max} — never backwards")
            continue
        state, text = judge(rec, ladders.get(n), control)
        lines.append(f"{rec['v_portal']} ({rec['core_sha'][:9]}): {state} — {text}")
        if state == "green":
            return rec, lines
    if resume:
        # Nothing newer is green, and the newest ARMED set never completed its sequence (no
        # `arm-complete-<version>` marker): re-arm exactly it. Its verdict was green when it was
        # armed; the resume re-runs the idempotent tag writes and the event, and decides nothing new.
        hit = next((r for r in records if r["v_portal"] == resume), None)
        if hit is None:
            lines.append(f"RESUME NOT POSSIBLE: {resume} carries no `arm-complete` marker and has no promotion record "
                         f"among the newest {RUNS_EXAMINED} main-cd runs (armed before the marker existed, or aged out) "
                         "— the next newer green set completes the line")
            return None, lines
        lines.append(f"RESUME: {resume} was armed but its sequence never completed — re-arming it")
        return hit, lines
    return None, lines


def unarmed_sets(records: list[dict], ladders: dict[int, str | None], control: dict, cursor: int) -> list[dict]:
    """Every promoted set NEWER than `cursor` (the newest armed run number, after this run's own
    arming), newest first, each with its platform verdict — the reading fleet-unarmed-alert.py
    clocks a stall from (MeshWeaver#6042). Pure. A set whose ladder `select` did not read (outside
    the ten newest it judges) is `not examined`, never guessed into `waiting`."""
    out: list[dict] = []
    for rec in records:
        n = int(rec["run_number"])
        if n <= cursor:
            continue
        if n in ladders:
            state, text = judge(rec, ladders[n], control)
        else:
            state, text = "not examined", "older than the ten newest promoted sets `select` judges"
        out.append({"v_portal": rec["v_portal"], "run_number": n, "core_sha": rec["core_sha"], "state": state,
                    "sentence": text, "run_url": rec.get("run_url", ""), "promoted_at": rec.get("promoted_at", "")})
    return out


def cold_start_of(major: int | None, armed_max: int, armed_version: str, override: str) -> bool:
    """Whether `select` runs at the COLD START of its line's major: no set of `major` was ever armed
    (`armed-state` answered cursor 0 with no version). Pure. Only a line that names its major can be
    at a cold start; an override names its set and reads no window."""
    return major is not None and armed_max <= 0 and not armed_version and not override


def armed_name(rec: dict | None, armed_version: str, armed_max: int, major: int | None, cold: bool) -> str:
    """The newest ARMED set of the line, as the fleet-unarmed reading names it. Pure. 🚨 A cold start
    is NAMED as one (MeshWeaver#8208): it used to render the cursor `ci.0`, which reads as a set
    called `ci.0` the fleet runs rather than "this major has never been armed"."""
    if rec:
        return str(rec["v_portal"])
    if armed_version:
        return armed_version
    if cold:
        return f"no {major}.x set armed yet"
    if armed_max <= 0:
        return f"no {major}.x set armed yet" if major is not None else "no set armed yet"
    return f"ci.{armed_max}"


def pattern_major(pattern: object) -> int:
    """The ONE platform major a record's `updatePattern` admits: `3.*` → 3, `3.1.*` → 3,
    `3.0.0-ci*` → 3. Pure. 🚨 A pattern that admits more than one major (`*`, `3*`, which also
    admits 30.x) or none names no line, and is a ValueError — the control-lag reading never guesses
    which line control follows (MeshWeaver#8197)."""
    text = str(pattern or "").strip()
    m = re.fullmatch(r"(\d+)\.[0-9A-Za-z.*+-]*", text)
    if not m:
        raise ValueError(f"updatePattern {pattern!r} does not name exactly one platform major "
                         "(expected `<major>.…`, e.g. `3.*`) — control's line cannot be derived from it")
    return int(m.group(1))


def record_update_pattern(record: object) -> str:
    """`content.updatePattern` of a Hosting/Deployment record (the MeshNode JSON as the deployments
    repository stores it). Pure; a record without one is a ValueError, never a default line."""
    content = record.get("content") if isinstance(record, dict) else None
    pattern = content.get("updatePattern") if isinstance(content, dict) else None
    if not isinstance(pattern, str) or not pattern.strip():
        raise ValueError("the control record carries no `content.updatePattern` — which line control follows "
                         "is declared there, and is never assumed")
    return pattern.strip()


def line_of_major(majors: dict[str, int | None], major: int) -> str:
    """The ONE declared delivery line (branch) whose PlatformVersion is of `major`. Pure. `majors`
    maps every declared line to the major its branch's Directory.Build.props declares (None when
    unreadable). No line, or two lines, of that major is a ValueError naming every line read."""
    hits = [b for b, m in majors.items() if m == major]
    seen = ", ".join(f"{b}={m if m is not None else 'unreadable'}" for b, m in majors.items()) or "none declared"
    if len(hits) != 1:
        raise ValueError(f"{len(hits)} declared delivery line(s) carry major {major} (lines read: {seen}) — "
                         "control's line must be exactly one")
    if any(m is None for m in majors.values()):
        raise ValueError(f"a declared line's major could not be read (lines read: {seen}) — refusing to pick "
                         f"control's line from a partial reading")
    return hits[0]


def control_line_hold(control_pattern: str, major: int | None, branch: str, who: str = "control",
                      declared: bool = True) -> str | None:
    """Policy `major-lines-run-separately`: the sentence saying this delivery line is HELD by control's
    own declaration, or None when control's record admits the line's major. Pure.

    🚨 WHY (MeshWeaver#8279): core `main` delivers 4.x while control's record declares
    `updatePattern: 3.*`, deliberately: control moves to a new major only when its record says so.
    Control therefore never runs a 4.x build, control-first holds every 4.x set, and the arming read
    that as `control-always-latest` lag: `FLEET DELIVERY FROZEN ON CONTROL` on every run, a red CD
    and a CD-failed issue each hour. A line whose major control's record EXCLUDES is not a control
    that failed to take a build. It is a declared hold, and its remedy is a declaration (control's
    record moving to that major), never control's roll. So the frozen verdict is not computed for
    it; control's own line is still judged by `control-lag` against that line's builds.

    The hold is LOUD, never a pass: `select` prints it as a ::warning:: and in every waiting set's
    sentence, and the fleet-unarmed reading carries it into that major's alert issue. It arms
    NOTHING: control first still decides, and control runs no build of an excluded major.
    `major` None (a caller that names no line) holds nothing. A pattern naming no single major is a
    ValueError (`pattern_major`), never guessed into a hold or a pass.

    🚨 PER LINE (policy `line-first-instance`, MeshWeaver#8365): the record read is the LINE'S FIRST
    INSTANCE'S (`who`), control's only when the line declares none. And a line that declares none
    (`declared` False) while control's record excludes its major is a DIFFERENT fact from a held
    line: control cannot stand in for it at all, so the sentence names the missing declaration — the
    reading this used to give, "control runs X", is true and answers nothing (control on 4.0 left the
    3.x fleet `waiting` behind it for 9 h)."""
    if major is None:
        return None
    control_major = pattern_major(control_pattern)
    if control_major == major:
        return None
    if not declared:
        return (f"{NO_FIRST_INSTANCE}: line `{branch}` (major {major}) declares no `firstInstance` in "
                f".github/delivery-lines.json, so the control instance would stand in for it — and control cannot: "
                f"its record declares updatePattern `{control_pattern}` (major {control_major}), so control takes no "
                f"{major}.x build and nothing it runs can arm this line. The remedy is a DECLARATION: the line's own "
                f"first instance (`firstInstance` + `firstInstanceImageRepository`, policy line-first-instance), or "
                f"the line's DECLARED absence of one (`firstInstance: \"{FIRST_INSTANCE_NONE}\"` + "
                f"`noFirstInstanceReason`, policy line-no-first-instance) — never control's roll and never an arm "
                f"override, and never by omission")
    if who != "control":
        return (f"LINE HELD BY DECLARATION: {who}'s record (line `{branch}`'s first instance) declares updatePattern "
                f"`{control_pattern}` (major {control_major}), and line `{branch}` delivers major {major}. {who} takes no "
                f"{major}.x build until its record declares that major, so every {major}.x set stays unarmed (policies "
                f"line-first-instance, major-lines-run-separately). The remedy is a declaration — {who}'s record moving "
                f"to {major}.x, or the line declaring another first instance — never {who}'s roll and never an arm override")
    return (f"LINE HELD BY DECLARATION: control's record declares updatePattern `{control_pattern}` (major "
            f"{control_major}), and line `{branch}` delivers major {major}. Control takes no {major}.x build until "
            f"its record declares that major, so control first holds every {major}.x set unarmed (policy "
            f"major-lines-run-separately). This is NOT a frozen control: control-always-latest judges control "
            f"against its own line's {control_major}.x builds. The remedy is the owner moving control's record to "
            f"{major}.x, never control's roll and never an arm override")


HOST_RE = re.compile(r"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")


def line_first_instance(line: dict | None, control_instance: dict, branch: str) -> dict:
    """Policy `line-first-instance` (MeshWeaver#8365): the ONE instance that runs line `branch`'s build
    before the fleet is offered it, as `.github/delivery-lines.json` declares it. Pure.

    Returns {"deployment", "name", "line", "declared", "image_repository", "given_prefix",
    "record_path"}. A line that declares no `firstInstance` has the CONTROL instance — its deployment,
    record and url from `.github/control-instance.json`, its builds handed by `control-first` — which
    is the line control follows, unchanged. A declared one names its deployment, its OWN image
    repository and the job that hands it builds (`first-instance-first`); its record sits beside
    control's (same deployments repository and folder). The declaration's shape is cd-line.py's to
    refuse; a line this function is asked about that is not declared at all is a ValueError."""
    if line is None:
        raise ValueError(f"line `{branch}` is not declared in .github/delivery-lines.json — no first instance can be named")
    control_dep = str(control_instance.get("deployment") or "").strip()
    record = control_instance.get("record") if isinstance(control_instance.get("record"), dict) else {}
    control_path = str(record.get("path") or "").strip()
    if not control_dep or not control_path.endswith(".json"):
        raise ValueError("`.github/control-instance.json` names no `deployment` and `record.path` — the default first "
                         "instance (control) cannot be named")
    declared = str(line.get("firstInstance") or "").strip()
    if not declared:
        return {"deployment": control_dep, "name": "control", "line": branch, "declared": False, "none": False,
                "reason": "", "image_repository": "memex-control", "given_prefix": CONTROL_FIRST_JOB_PREFIX,
                "record_path": control_path}
    if declared == FIRST_INSTANCE_NONE:
        # Policy `line-no-first-instance`: DECLARED, with its reason — no deployment, no record, no
        # repository and no job that hands anything. cd-line.py refuses the shape; this refuses a
        # caller that hands it an unvalidated one without the reason.
        reason = str(line.get("noFirstInstanceReason") or "").strip()
        minimum = _cd_line().NO_FIRST_INSTANCE_REASON_MIN  # cd-line.py's own bound — one number, never a copy
        if len(reason) < minimum:
            raise ValueError(f"line `{branch}` declares firstInstance \"{FIRST_INSTANCE_NONE}\" without a "
                             f"noFirstInstanceReason of at least {minimum} characters (got {reason!r}) — the "
                             "declared absence of a first instance says why")
        if line.get("firstInstanceImageRepository"):
            raise ValueError(f"line `{branch}` declares firstInstance \"{FIRST_INSTANCE_NONE}\" beside an image "
                             "repository — no instance runs one")
        return {"deployment": "", "name": FIRST_INSTANCE_NONE, "line": branch, "declared": True, "none": True,
                "reason": reason, "image_repository": "", "given_prefix": "", "record_path": ""}
    image = str(line.get("firstInstanceImageRepository") or "").strip()
    if not image:
        raise ValueError(f"line `{branch}` declares firstInstance {declared!r} without firstInstanceImageRepository")
    folder = control_path.rsplit("/", 1)[0] if "/" in control_path else ""
    return {"deployment": declared, "name": declared, "line": branch, "declared": True, "none": False, "reason": "",
            "image_repository": image, "given_prefix": FIRST_INSTANCE_JOB_PREFIX,
            "record_path": f"{folder}/{declared}.json" if folder else f"{declared}.json"}


def no_first_instance_sentence(first: dict) -> str:
    """The ONE sentence a line that DECLARES no first instance is armed under (policy
    `line-no-first-instance`). Pure. It names the line, the declaration, its reason and the evidence
    that still gates — so a green arming of such a line can never read as an instance having run it."""
    return (f"{NO_FIRST_INSTANCE_DECLARED}: line `{first['line']}` declares firstInstance \"{FIRST_INSTANCE_NONE}\" "
            f"in .github/delivery-lines.json ({first['reason']}) — no instance runs its sets before the fleet is "
            "offered them, so this set arms on the platform evidence alone: promoted from a commit whose required "
            "check is green, its compatibility ladder green, and the image-identity gate at the arming write "
            "(policy line-no-first-instance)")


def first_instance_url(first: dict, control_instance: dict, record: object) -> str:
    """The public base URL whose /api/version and /health the arming reads for the line's first
    instance. Pure. Control's is the one `.github/control-instance.json` declares (unchanged); a
    declared first instance's is `https://` + its record's `content.host` — the record is the one
    declaration of where an instance answers. A record that is not the first instance's (another
    `id`), or carries no plain host, is a ValueError: reading the wrong instance would arm on it. A line
    that DECLARES no first instance has no url at all — asking for one is a ValueError, never control's."""
    if first.get("none"):
        raise ValueError(f"line `{first['line']}` declares no first instance — there is no instance to read")
    if not first.get("declared"):
        return str(control_instance["url"]).rstrip("/")
    if not isinstance(record, dict):
        raise ValueError(f"{first['name']}'s record is not a JSON object")
    rid = str(record.get("id") or "")
    if rid and rid != first["deployment"]:
        raise ValueError(f"the record read for line `{first['line']}`'s first instance is {rid!r}, not "
                         f"{first['deployment']!r} — refusing to judge the line on another instance")
    content = record.get("content") if isinstance(record.get("content"), dict) else {}
    host = str(content.get("host") or "").strip().lower()
    if not HOST_RE.match(host):
        raise ValueError(f"{first['name']}'s record carries no plain `content.host` (got {content.get('host')!r}) — "
                         "where the line's first instance answers is declared there, and is never guessed")
    return f"https://{host}"


def first_instance_hold(first: dict, record: object, major: int | None) -> str | None:
    """Every DECLARATION that keeps the line's first instance from running this line's builds, as
    ONE sentence, or None. Pure. Two facts, both read off the first instance's record:

      * its `updatePattern` excludes the line's major (`control_line_hold`; for a line that declares
        no first instance this names the MISSING declaration — control cannot stand in);
      * a DECLARED first instance whose record does not run the line's first-instance repository
        (`content.imageRepository` ending `/<firstInstanceImageRepository>`): `first-instance-first`
        tags and announces on a repository that record does not follow, so the instance can never
        run the set — the state between this declaration and the record moving to that repository.

    A hold arms nothing and is LOUD (a ::warning:: on every run and in the fleet-unarmed issue); it
    is never the frozen red, because no roll of the instance would end it — a declaration does. A line
    that DECLARES no first instance has no record to read: asking is a ValueError."""
    if first.get("none"):
        raise ValueError(f"line `{first['line']}` declares no first instance — there is no record to hold it")
    pattern = record_update_pattern(record)
    hold = control_line_hold(pattern, major, first["line"], who=first["name"], declared=first["declared"])
    if hold or not first["declared"]:
        return hold
    content = record.get("content") if isinstance(record, dict) and isinstance(record.get("content"), dict) else {}
    repo = str(content.get("imageRepository") or "").strip()
    if repo.rsplit("/", 1)[-1] != first["image_repository"]:
        return (f"LINE HELD BY DECLARATION: {first['name']}'s record names imageRepository `{repo or '<none>'}`, but "
                f"line `{first['line']}` hands its first instance builds on `{first['image_repository']}` "
                f"(.github/delivery-lines.json). {first['name']} follows another repository, so it can never run a set "
                f"before the fleet is offered it. The remedy is {first['name']}'s record moving to "
                f"`<registry>/{first['image_repository']}` (with an explicit migrationImageRepository), once that "
                "repository carries a tag — never an arm override (policy line-first-instance)")
    return None


def _cd_line():
    """cd-line.py (beside this file) — the ONE reader of the declared delivery lines and of a line's
    PlatformVersion major; loaded, never re-implemented here."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("cd_line", Path(__file__).resolve().parent / "cd-line.py")
    mod = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
    spec.loader.exec_module(mod)  # type: ignore[union-attr]
    return mod


def declared_line_majors(lines_file: Path, core_token: str, get: Get | None = None) -> dict[str, int | None]:
    """Every declared delivery line → the platform major its branch's Directory.Build.props declares
    (REST contents at `ref=<branch>`), None when unreadable. The major is READ from each line's own
    tree — the same source main-cd's `gate` reads — never a number written here."""
    get = get or http_get
    cd = _cd_line()
    try:
        doc = cd.load(lines_file)
    except (OSError, ValueError) as e:
        raise ValueError(f"{lines_file}: the declared delivery lines cannot be read ({e})") from None
    out: dict[str, int | None] = {}
    for line in doc["lines"]:
        branch = str(line["branch"])
        code, body = get(f"repos/{CORE}/contents/Directory.Build.props?ref={branch}", core_token)
        major: int | None = None
        if code == 200 and isinstance(body, dict) and body.get("encoding") == "base64":
            import base64
            try:
                major = cd.platform_major(base64.b64decode(str(body.get("content") or "")).decode("utf-8"))
            except (cd.Refusal, ValueError):  # no or two PlatformVersion majors, or undecodable: unreadable
                major = None
        out[branch] = major
    return out


def given_to_control(records: list[dict], control: dict, jobs_of: Callable[[dict], list[dict] | None],
                     contains_of: Callable[[str], bool | None], prefix: str = "") -> list[dict]:
    """The builds given to control, newest first, back to the first one control CONTAINS — the
    `control-lag` walk (the lag clock starts at the oldest one it does not, see `control_lag`).
    Ancestry is monotone along ONE line, so the walk stops at the first contained build: one jobs
    read and one compare per build control is behind. Fills `control["contains"]` as it goes."""
    given: list[dict] = []
    for r in records:
        at = control_given_at(jobs_of(r), prefix)
        if not at:
            continue
        given.append({"core_sha": r["core_sha"], "v_portal": r["v_portal"], "given_at": _iso_epoch(at)})
        if not control.get("commit"):
            break
        verdict = contains_of(r["core_sha"])
        control.setdefault("contains", {})[r["core_sha"]] = verdict
        if verdict is not False:
            break
    return given


def control_lag(given: list[dict] | dict | None, control: dict, now: float, bound_minutes: int) -> tuple[str, str]:
    """Policy `control-always-latest`, the PLATFORM half: is the control instance on the newest
    platform build it was given? Pure. Returns (state, sentence): `ok`, `converging` or `lag`.

    `given` is every promoted set whose `Deploy control first` job SUCCEEDED, NEWEST FIRST, each
    carrying `core_sha`, `v_portal` and `given_at` (epoch seconds the control image was tagged with
    it) — read back to the first one control contains (or to the end of the examined runs). A single
    dict is read as a one-element list. `control` is the same reading `judge` uses; its `contains`
    must answer for each entry walked. RED (`lag`) when control does not contain the newest build
    and has been BEHIND for more than `bound_minutes` — or when control's running build cannot be
    read at all (an instance that cannot say what it runs cannot be shown to be latest; silence is
    never a pass). Within the bound it is `converging`, said out loud. Control ON the newest build
    but with `/health` not 200 is `lag` too: `judge` offers the fleet nothing from an unhealthy
    control, so that state blocks every later arming and must be red, never an `ok` that closes the
    issue (review on MeshWeaver#6143).

    🚨 THE CLOCK STARTS AT THE FIRST BUILD CONTROL MISSED, never at the newest one. Measured
    2026-10-06: control sat on 060afe8 (ci.9939) for 22 hours while ~30 builds were given to it, and
    this alarm read `converging — ci.10052 was given 29 min ago` on almost every tick, because a
    platform build every 30-60 minutes reset a clock measured from the NEWEST build. A lag bound
    that every new build resets is not a bound: the lag it exists to catch — control taking no
    build at all — is exactly the state in which builds keep arriving."""
    if isinstance(given, dict):
        given = [given]
    who = _who(control)
    job = CONTROL_FIRST_JOB_PREFIX if who == "control" else FIRST_INSTANCE_JOB_PREFIX
    if not given:
        return "lag", (f"no promoted set among the examined runs has a successful `{job}` job — "
                       f"nothing proves {who} was given a build")
    newest = given[0]
    commit = control.get("commit") or ""
    if not commit:
        return "lag", (f"{who}'s running build cannot be read ({control.get('why') or 'no answer'}) — "
                       f"it cannot be shown to be on {newest['v_portal']}")
    contains = control.get("contains") or {}
    first = contains.get(newest["core_sha"])
    policy = "platform-deploy-control-first" if who == "control" else "line-first-instance"
    if first is True and not control.get("healthy"):
        return "lag", (f"{who} runs {commit[:9]}, which contains the newest platform build {newest['v_portal']}, "
                       f"but /health is not 200 ({control.get('why') or 'unhealthy'}) — the fleet is offered nothing "
                       f"until {who} is healthy (policy {policy})")
    if first is True:
        return "ok", (f"{who} runs {commit[:9]}, which contains the newest platform build "
                      f"{newest['v_portal']} ({newest['core_sha'][:9]})")
    if first is None:
        return "lag", (f"could not establish whether {who}'s {commit[:9]} contains {newest['v_portal']} "
                       f"({newest['core_sha'][:9]}) — not shown to be latest")
    # Behind. Walk back to the OLDEST given build control does not contain: that is when it fell
    # behind. Reaching a build it contains ends the walk; an unreadable answer or the end of the
    # examined runs leaves a FLOOR, said as one ("at least").
    missed, floor = newest, True
    for entry in given[1:]:
        verdict = contains.get(entry["core_sha"])
        if verdict is True:
            floor = False
            break
        if verdict is None:
            break
        missed = entry
    behind_min = int(max(0.0, now - float(missed["given_at"])) // 60)
    since = (f"since {missed['v_portal']} ({missed['core_sha'][:9]}), the first build it was given and did not take, "
             f"{'at least ' if floor else ''}{behind_min} min ago")
    if behind_min <= bound_minutes:
        return "converging", (f"{who} runs {commit[:9]}; it has been behind the newest build {newest['v_portal']} "
                              f"{since} — inside the {bound_minutes} min bound")
    lag_policy = "control-always-latest" if who == "control" else "line-first-instance"
    return "lag", (f"{who} runs {commit[:9]}, which does NOT contain the newest platform build {newest['v_portal']} "
                   f"({newest['core_sha'][:9]}); it has been behind {since} — over the {bound_minutes} min bound "
                   f"(policy {lag_policy}). {describe_self_update(control)}")


def given_builds(records: list[dict], jobs_of: Callable[[dict], list[dict] | None],
                 contains_of: Callable[[str], bool | None], prefix: str = "") -> list[dict]:
    """Every examined promoted set whose `Deploy control first` job SUCCEEDED, newest first, in the
    shape `control_lag` reads ({core_sha, v_portal, given_at epoch seconds}) — walked back to the
    FIRST one control contains (inclusive) or to the end of the examined records. Pure over its two
    readers, which the caller backs with REST (and caches), so the walk costs one jobs read and one
    compare per build control is behind, exactly as the `control-lag` command's walk does.

    🚨 It walks the WHOLE examined history, never a fixed prefix (review on MeshWeaver#6289): a
    window of the newest ten sets drops the first build control missed as soon as it misses more
    than ten, and when those ten span less than the bound `control_lag` reads `converging` for ever
    — the very reset-by-new-builds blindness the alarm's clock was moved to avoid."""
    out = []
    for r in records:
        at = control_given_at(jobs_of(r), prefix)
        if not at:
            continue
        out.append({"core_sha": r["core_sha"], "v_portal": r["v_portal"], "given_at": _iso_epoch(at)})
        if contains_of(r["core_sha"]) is not False:
            break
    return out


def arming_frozen(chosen: dict | None, override: str, given: list[dict], control: dict, now: float,
                  bound_minutes: int, hold: str | None = None) -> str | None:
    """🚨 Policy `control-first-never-silent`: the sentence that turns the arm job RED, or None. Pure.

    Control-first makes the whole fleet wait for ONE instance. Until this rule the arming said
    `waiting — control runs …, which does not contain … yet` on every tick and the job stayed green,
    so a control that took NO build froze memex and memex-cloud while every main-cd run read success
    (measured 2026-10-07/08: ci.10184 … ci.10206 each `waiting`, control on cac0664de for 10+ hours).
    Waiting is right while control converges; it is a FAILURE once control has been behind past the
    SAME bound the `control-always-latest` alarm reads (`platformLagBoundMinutes`), on the SAME clock
    (the first build control missed) — one rule (`control_lag`), so the arming and the alarm can never
    disagree about when the wait stopped being a wait. A set that was selected, or an override, is
    never frozen; an override stays the maintainer's call and is never suggested as the remedy.

    Only the BOUNDED state freezes: control's running build is read and does NOT contain the newest
    build it was given, past the bound. That fact is clocked, so it freezes whatever `/health`
    answers in the same reading — a control that has taken no build for hours is frozen whether or
    not its last probe also failed (review on MeshWeaver#6289; the self-test pins
    behind-and-unhealthy). What never freezes is a reading with NO clock behind it: an unreadable
    control, a control that RUNS the newest build but answered one non-200, or no build given at all
    (this run's own `Deploy control first` may still be running). `control-always-latest` alarms on
    those on its own schedule; turning one 503 into a red CD run would make the red mean nothing.

    A line HELD by control's declaration (`hold`, from `control_line_hold`) is never frozen either:
    control was never going to take its builds, so its lag on them measures no failure of control's
    (MeshWeaver#8279). The caller says the hold as a ::warning::; it is never dropped."""
    if chosen is not None or override or not given or hold:
        return None
    if not control.get("commit") or (control.get("contains") or {}).get(given[0]["core_sha"]) is not False:
        return None
    state, text = control_lag(given, control, now, bound_minutes)
    if state != "lag":
        return None
    who = _who(control)
    if who != "control":
        # 🚨 Per line (policy `line-first-instance`): the line's DECLARED first instance is the one
        # instance this line's fleet waits for, so its frozen reading names it and the line — never
        # control, which neither runs nor is handed this line's builds.
        line = control.get("line") or "?"
        return (f"FLEET DELIVERY FROZEN ON {who} (the first instance of line `{line}`) — nothing can be armed: {text}. "
                f"{who} first holds every newer set of line `{line}` until {who} RUNS it, so this is no longer a wait "
                f"(policies control-first-never-silent, line-first-instance). The remedy is {who}'s own roll "
                f"(read its Ops/Actions selfupdate-roll-{who}-*); the open `fleet-unarmed` issue of this major "
                "carries the same reading.")
    return (f"FLEET DELIVERY FROZEN ON CONTROL — nothing can be armed: {text}. Control-first holds every newer "
            "set until control RUNS it, so this is no longer a wait (policy control-first-never-silent). The "
            "remedy is control's own roll; the open `control-lag` issue carries the same reading.")


def write_outputs(rec: dict | None, lines: list[str]) -> None:
    out = os.environ.get("GITHUB_OUTPUT")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    rows = {"armed": "true" if rec else "false"}
    if rec:
        rows.update({k: str(rec[k]) for k in RECORD_KEYS})
    if out:
        with open(out, "a") as f:
            for k, v in rows.items():
                f.write(f"{k}={v}\n")
    if summary:
        with open(summary, "a") as f:
            f.write("### Promotion gate — which promoted set may the fleet roll to?\n\n")
            for line in lines:
                f.write(f"- {line}\n")
            f.write(f"\n**{'ARM ' + rec['v_portal'] if rec else 'nothing to arm'}**\n")


# ───────────────────────────────── self-test ──────────────────────────────────────────────

def _raises(f: Callable[[], object]) -> bool:
    try:
        f()
    except ValueError:
        return True
    return False


def self_test() -> int:
    failures = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    def rec(n: int, core: str, plug: str) -> dict:
        c, p = core * 40, plug * 40
        return {"run_number": n, "core_sha": c, "plugins_sha": p, "short": c[:10], "plugins_short": p[:10],
                "staging": f"staging-{c[:7]}-{n}", "v_portal": f"3.0.0-ci.{n}", "v_migration": f"3.0.0-ci.{n}",
                "v_plugin": f"3.0.0-ci.{n}", "key": verdict_key(c[:10], p[:10]), "base": "b" * 40}

    a, b, c = rec(9459, "a", "1"), rec(9460, "c", "2"), rec(9461, "d", "3")
    records = [c, b, a]
    check("a well-formed record is accepted", check_record(a) is None, str(check_record(a)))
    check("a record whose key is not its pair tag is refused", check_record({**a, "key": "pair-x"}) is not None)
    check("a record whose version does not carry its run number is refused",
          check_record({**a, "v_portal": "3.0.0-ci.1"}) is not None)
    check("a record with a short core sha is refused", check_record({**a, "core_sha": "abc"}) is not None)
    check("a record without its base is refused (the verdict is pinned to a base)",
          check_record({k: v for k, v in a.items() if k != "base"}) is not None)

    # ── the PLATFORM verdict (policies platform-module-deploy-separate + platform-deploy-control-first) ──
    def ctl(*on: dict, healthy: bool = True, commit: str = "f" * 40, unknown: tuple = ()) -> dict:
        contains = {r["core_sha"]: True for r in on}
        contains.update({r["core_sha"]: None for r in unknown})
        for r in (a, b, c):
            contains.setdefault(r["core_sha"], False)
        return {"commit": commit, "healthy": healthy, "contains": contains, "why": "" if healthy else "503"}

    ok = {9459: "success", 9460: "success", 9461: "success"}
    chosen, lines = select(records, ok, ctl(a, b, c), armed_max=9458)
    check("the newest set whose ladder passed and that control runs is armed", chosen is c, "\n".join(lines))
    chosen, lines = select(records, ok, ctl(a, b), armed_max=9458)
    check("control first: a newer set control does NOT run yet is waiting; the newest one control runs is armed",
          chosen is b and "control first" in lines[0], "\n".join(lines))
    chosen, lines = select(records, ok, ctl(), armed_max=9458)
    check("control first: nothing control runs is ever offered to the fleet", chosen is None, "\n".join(lines))
    chosen, lines = select(records, ok, ctl(a, b, c, healthy=False), armed_max=9458)
    check("control first: an UNHEALTHY control offers nothing, even on the set", chosen is None and "/health" in lines[0],
          "\n".join(lines))
    chosen, lines = select(records, ok, {"commit": "", "healthy": False, "contains": {}, "why": "timeout"}, armed_max=9458)
    check("an unreadable control is waiting, never a pass", chosen is None and "unknown" in lines[0], "\n".join(lines))
    chosen, lines = select(records, ok, ctl(b, unknown=(c,)), armed_max=9458)
    check("an UNKNOWN containment is waiting, never read as contained", chosen is b and "could not establish" in lines[0],
          "\n".join(lines))
    chosen, lines = select(records, {**ok, 9461: "failure"}, ctl(a, b, c), armed_max=9458)
    check("a RED ladder is refused and the next older green set is armed", chosen is b and "refused" in lines[0],
          "\n".join(lines))
    chosen, lines = select(records, {9460: "success", 9459: "success"}, ctl(a, b, c), armed_max=9458)
    check("a ladder still running (absent from the map) is waiting, never a pass", chosen is b and "waiting" in lines[0],
          "\n".join(lines))
    chosen, lines = select(records, {**ok, 9461: "missing"}, ctl(a, b, c), armed_max=9458)
    check("a run with NO ladder job is refused (nobody linked the modules against it)", chosen is b and "NO compatibility-ladder job" in lines[0],
          "\n".join(lines))
    chosen, lines = select(records, ok, ctl(a, b), armed_max=9460)
    check("a green set OLDER than the armed one is never armed (never backwards)",
          chosen is None and len(lines) == 3 and "never backwards" in lines[1], "\n".join(lines))
    chosen, _ = select(records, {}, ctl(), armed_max=0)
    check("no ladder and no control arms nothing (silence is not a pass)", chosen is None)
    # 🚨 NEGATIVE CONTROL for the separation: a red MeshWeaver.Plugins dependent-suites verdict and an
    # UNRESOLVED bundle base — the two things that used to hold the fleet for module reasons — are
    # inert: the platform verdict reads neither, so the set is armed on the platform's own evidence.
    u = {**c, "base_kind": "unresolved"}
    chosen, lines = select([u, b, a], ok, ctl(a, b, u), armed_max=9458)
    check("separation: a set is armed WITHOUT any Plugins verdict (none is even passed in)", chosen is u, "\n".join(lines))
    import inspect
    check("separation: `select` and `judge` take no Plugins verdict parameter at all",
          not any("verdict" in p for p in inspect.signature(select).parameters)
          and not any("verdict" in p for p in inspect.signature(judge).parameters))
    # override
    chosen, lines = select(records, {}, ctl(), armed_max=9460, override="3.0.0-ci.9459")
    check("an override arms exactly the named set, verdict or not", chosen is a and "OVERRIDE" in lines[0])
    try:
        select(records, {}, ctl(), armed_max=0, override="3.0.0-ci.1")
        check("an override naming no promoted set is RED", False)
    except ValueError:
        check("an override naming no promoted set is RED, never a substitution", True)
    try:
        select(records, {}, ctl(), armed_max=0, override="3.0.1-ci.9459")
        check("an override naming the right RUN on another version line is RED", False)
    except ValueError:
        check("an override naming the right RUN on another version line is RED (exact version, never the run number)", True)
    # resume — the cursor moved at phase C but the sequence never finished
    chosen, lines = select(records, {}, ctl(), armed_max=9460, resume="3.0.0-ci.9460")
    check("resume: nothing newer green → the incomplete newest armed set is re-armed", chosen is b and "RESUME" in lines[-1],
          "\n".join(lines))
    chosen, _ = select(records, ok, ctl(c), armed_max=9460, resume="3.0.0-ci.9460")
    check("resume: a NEWER green set still wins (arming it completes the line past the incomplete one)", chosen is c)
    chosen, lines = select(records, {}, ctl(), armed_max=9460, resume="3.0.0-ci.9000")
    check("resume: an incomplete set with NO record (armed before the marker existed) is SAID, not red — "
          "a red would hold every later arming", chosen is None and "RESUME NOT POSSIBLE" in lines[-1], "\n".join(lines))
    # the readings `judge` is fed
    check("ladder_of: a completed ladder job gives its conclusion",
          ladder_of([{"name": "Compatibility: the published module set links against this platform (ladder)",
                      "status": "completed", "conclusion": "success"}]) == "success")
    check("ladder_of: a running ladder is None (waiting)",
          ladder_of([{"name": "x (ladder)", "status": "in_progress", "conclusion": None}]) is None)
    two = [{"name": "Compatibility: a (ladder)", "status": "completed", "conclusion": "success"},
           {"name": "Compatibility: b (ladder)", "status": "completed", "conclusion": "failure"}]
    check("ladder_of: SEVERAL ladder-named jobs are `ambiguous` — a green first one never masks a red later one",
          ladder_of(two) == "ambiguous" and ladder_of(list(reversed(two))) == "ambiguous")
    chosen, lines = select(records, {**ok, 9461: "ambiguous"}, ctl(a, b, c), armed_max=9458)
    check("...and an ambiguous ladder is REFUSED, the next older green set armed", chosen is b and "refused" in lines[0],
          "\n".join(lines))
    st, text = judge(c, "missing", ctl(c))
    check("judge: a MISSING ladder is refused with its OWN sentence, never 'does not link' advice",
          st == "refused" and "NO compatibility-ladder job" in text and "does not link" not in text, text)
    st, text = judge(c, "ambiguous", ctl(c))
    check("judge: an AMBIGUOUS ladder is refused with its OWN sentence",
          st == "refused" and "SEVERAL jobs" in text and "does not link" not in text, text)
    # ── a re-run that never started does not erase what attempt 1 ran (main-cd #11961/#11966) ──
    ladder_ok = [{"name": "Compatibility: the published module set links against this platform (ladder)",
                  "status": "completed", "conclusion": "success"}]

    def runs_api(attempts: dict[int, list[dict]], latest: int, fresh: int | None = None) -> Get:
        """`latest` answers the `filter=latest` read; `fresh` is the attempt the run read reports
        AFTER it — newer when a re-run is created between the two requests."""
        def get(url: str, token: str) -> tuple[int, object]:
            m = re.search(r"/runs/7/attempts/(\d+)/jobs", url)
            if m:
                return 200, {"jobs": attempts.get(int(m.group(1)), [])}
            if "/runs/7/jobs" in url:
                return 200, {"jobs": attempts.get(latest, [])}
            if url.endswith("/runs/7"):
                return 200, {"id": 7, "run_attempt": fresh or latest}
            return 404, None
        return get
    check("jobs: an attempt 2 that created no job → attempt 1's records (its ladder still PASSED)",
          ladder_of(read_run_jobs(runs_api({1: ladder_ok, 2: []}, 2), "t", 7)) == "success")
    check("jobs: negative control — a MATERIALISED attempt 2 is read as is (a ladder it lost stays lost)",
          ladder_of(read_run_jobs(runs_api({1: ladder_ok, 2: [{**ladder_ok[0], "conclusion": "failure"}]}, 2), "t", 7))
          == "failure")
    check("jobs: negative control — the freshly reported attempt is read FIRST (empty attempt 2, a newer "
          "attempt 3 that lost the ladder → failure, never attempt 1's pass)",
          ladder_of(read_run_jobs(runs_api({1: ladder_ok, 2: [], 3: [{**ladder_ok[0], "conclusion": "failure"}]},
                                           2, fresh=3), "t", 7)) == "failure")
    check("jobs: negative control — a first attempt with no job reads no job (`missing`, never invented)",
          ladder_of(read_run_jobs(runs_api({1: []}, 1), "t", 7)) == "missing")
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        decl = Path(tmp) / "ci.json"
        for bad, why in (({"url": "https://c", "platformLagBoundMinutes": True}, "a boolean bound"),
                         ({"url": "https://c", "platformLagBoundMinutes": 0}, "a zero bound"),
                         ({"url": "http://c", "platformLagBoundMinutes": 5}, "a non-https url")):
            decl.write_text(json.dumps(bad))
            try:
                load_control_instance(decl)
                check(f"load_control_instance: {why} is RED", False)
            except ValueError:
                check(f"load_control_instance: {why} is RED", True)
    shipped = Path(__file__).resolve().parents[1] / "control-instance.json"
    try:
        load_control_instance(shipped)
        check("the shipped .github/control-instance.json validates", True)
    except (OSError, ValueError) as e:
        check("the shipped .github/control-instance.json validates", False, str(e))
    check("ladder_of: a run with no ladder job is `missing` (refused), an unreadable run None",
          ladder_of([{"name": "Promote: tag", "status": "completed", "conclusion": "success"}]) == "missing"
          and ladder_of(None) is None)
    check("control_given_at: only a SUCCEEDED `Deploy control first` job counts",
          control_given_at([{"name": "Deploy control first: memex-control:<version>", "conclusion": "failure",
                             "completed_at": "t1"}]) is None
          and control_given_at([{"name": "Deploy control first: memex-control:<version>", "conclusion": "success",
                                 "completed_at": "t2"}]) == "t2")
    pub = {"https://ctl/api/version": (200, json.dumps({"commit": "f" * 40})), "https://ctl/health": (200, "Degraded")}
    cmp = {f"repos/{CORE}/compare/{a['core_sha']}...{'f' * 40}": (200, {"status": "ahead"}),
           f"repos/{CORE}/compare/{c['core_sha']}...{'f' * 40}": (200, {"status": "behind"}),
           f"repos/{CORE}/compare/{b['core_sha']}...{'f' * 40}": (502, None)}
    rc = read_control("https://ctl", lambda u, t, raw=False: cmp.get(u, (404, None)), "t",
                      [a["core_sha"], b["core_sha"], c["core_sha"]], fetch=lambda u: pub.get(u, (404, "")))
    check("read_control: ancestry decides containment (ahead = contained, behind = not, an error = UNKNOWN)",
          rc["contains"] == {a["core_sha"]: True, b["core_sha"]: None, c["core_sha"]: False} and rc["healthy"], str(rc))
    rc = read_control("https://ctl", lambda u, t, raw=False: (404, None), "t", [a["core_sha"]],
                      fetch=lambda u: (200, json.dumps({"commit": "f" * 40})) if u.endswith("version") else (503, ""))
    check("read_control: /health 503 is unhealthy and says so", rc["healthy"] is False and "503" in rc["why"], str(rc))
    rc = read_control("https://ctl", lambda u, t, raw=False: (404, None), "t", [a["core_sha"]],
                      fetch=lambda u: (200, json.dumps({"version": "3.0.0"})))
    check("read_control: a version answer without a commit leaves control UNKNOWN", rc["commit"] == "" and rc["why"], str(rc))
    # ── control-always-latest (the platform half) ──
    newest = {"core_sha": c["core_sha"], "v_portal": c["v_portal"], "given_at": 1_000_000.0}
    st, text = control_lag(newest, ctl(c), now=1_000_000.0 + 9 * 3600, bound_minutes=120)
    check("control-always-latest: control on the newest build is ok, however long ago it was given", st == "ok", text)
    st, text = control_lag(newest, ctl(c, healthy=False), now=1_000_000.0, bound_minutes=120)
    check("control-always-latest: control ON the newest build but /health not 200 is RED — it blocks every arming",
          st == "lag" and "/health" in text, text)
    st, text = control_lag(newest, ctl(b), now=1_000_000.0 + 60 * 60, bound_minutes=120)
    check("control-always-latest: behind INSIDE the bound is converging (said, not red)", st == "converging", text)
    st, text = control_lag(newest, ctl(b), now=1_000_000.0 + 121 * 60, bound_minutes=120)
    check("control-always-latest: behind PAST the bound is RED and names the lag",
          st == "lag" and "121 min" in text and c["v_portal"] in text, text)
    st, text = control_lag(newest, {"commit": "", "contains": {}, "why": "timeout"}, now=1_000_000.0, bound_minutes=120)
    check("control-always-latest: an unreadable control is RED (cannot be shown latest), never a pass", st == "lag", text)
    st, text = control_lag(newest, ctl(unknown=(c,)), now=1_000_000.0, bound_minutes=120)
    check("control-always-latest: unknown containment is RED, never read as contained", st == "lag", text)
    st, text = control_lag(None, ctl(c), now=1_000_000.0, bound_minutes=120)
    check("control-always-latest: no build ever given to control is RED", st == "lag", text)
    # 🚨 The clock starts at the FIRST build control missed. Negative control for the 2026-10-06
    # reading: the newest build was given 29 min ago, but control has missed every build for 10 h —
    # a clock on the newest build calls that `converging` for ever while builds keep arriving.
    t0 = 1_000_000.0
    given = [{"core_sha": c["core_sha"], "v_portal": c["v_portal"], "given_at": t0 + 10 * 3600 - 29 * 60},
             {"core_sha": b["core_sha"], "v_portal": b["v_portal"], "given_at": t0},
             {"core_sha": a["core_sha"], "v_portal": a["v_portal"], "given_at": t0 - 3600}]
    st, text = control_lag(given, ctl(a), now=t0 + 10 * 3600, bound_minutes=180)
    check("control-always-latest: behind for 10 h is RED although the NEWEST build was given 29 min ago",
          st == "lag" and b["v_portal"] in text and "600 min" in text and "at least" not in text, text)
    st_old, text_old = control_lag(given[0], ctl(a), now=t0 + 10 * 3600, bound_minutes=180)
    check("...and the newest-build clock alone (the old reading) would have called it converging — the control",
          st_old == "converging", text_old)
    st, text = control_lag(given, ctl(a, b), now=t0 + 10 * 3600, bound_minutes=180)
    check("control-always-latest: behind only the newest build, given 29 min ago, is converging",
          st == "converging" and "29 min" in text, text)
    st, text = control_lag(given[:2], ctl(), now=t0 + 10 * 3600, bound_minutes=180)
    check("control-always-latest: behind every examined build is a FLOOR, said as one, and RED past the bound",
          st == "lag" and "at least 600 min" in text, text)
    st, text = control_lag(given, ctl(unknown=(b,)), now=t0 + 10 * 3600, bound_minutes=180)
    check("control-always-latest: an unreadable older answer stops the walk at a floor, never reads as contained",
          st == "converging" and "at least 29 min" in text, text)
    # ── policy control-first-never-silent: a frozen fleet is RED and names control's own verdict ──
    failing_body = ("Degraded\ntiming: 7ms total\nself_update: Degraded — check FAILED: CredentialUnavailableException: "
                    "ManagedIdentityCredential … The requested identity has not been assigned to this resource\n"
                    "required_modules: Healthy — ok")
    su = self_update_reading(failing_body)
    check("self_update_reading: the census line is parsed off the /health body (status + verdict)",
          su is not None and su["status"] == "Degraded" and su["text"].startswith("check FAILED: CredentialUnavailable"), str(su))
    check("self_update_reading: a body WITHOUT the entry is None (absent, never clean)",
          self_update_reading("Degraded\nbake-report: Degraded — x") is None and self_update_reading("") is None)
    check("describe_self_update: a failing reading is said as FAILING with its verdict",
          "FAILING" in describe_self_update({"self_update": su}) and "CredentialUnavailable" in describe_self_update({"self_update": su}))
    check("describe_self_update: NO reading is said as absent and points at the node, never as clean",
          "publishes no `self_update` reading" in describe_self_update({}))
    rc = read_control("https://ctl", lambda u, t, raw=False: (404, None), "t", [],
                      fetch=lambda u: (200, json.dumps({"commit": "f" * 40})) if u.endswith("version") else (200, failing_body))
    check("read_control: control's own self-update reading rides the control reading",
          rc["self_update"] == su, str(rc))
    frozen_ctl = {**ctl(a), "self_update": su}
    _, text = judge(c, "success", frozen_ctl)
    check("judge: a set control has not taken NAMES control's own failing self-update verdict",
          "control first" in text and "CredentialUnavailable" in text, text)
    st, text = control_lag(given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180)
    check("control-always-latest: the RED sentence (the control-lag issue) names control's own verdict",
          st == "lag" and "CredentialUnavailable" in text, text)
    fz = arming_frozen(None, "", given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180)
    check("arming_frozen: nothing armed and control behind PAST the bound is RED, naming the verdict",
          fz is not None and "FROZEN" in fz and "CredentialUnavailable" in fz and b["v_portal"] in fz, str(fz))
    # Negative controls: each of these is a WAIT, never a red.
    check("arming_frozen: behind only INSIDE the bound is a wait (negative control)",
          arming_frozen(None, "", given, {**ctl(a, b), "self_update": su}, now=t0 + 10 * 3600, bound_minutes=180) is None)
    check("arming_frozen: a selected set is never frozen",
          arming_frozen(c, "", given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180) is None)
    check("arming_frozen: an override is never frozen (and never suggested)",
          arming_frozen(None, "3.0.0-ci.9461", given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180) is None
          and "override" not in (fz or "").lower())
    check("arming_frozen: no build given yet (this run's control-first still running) is not frozen",
          arming_frozen(None, "", [], frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180) is None)
    check("arming_frozen: one unreadable or unhealthy control reading has no clock behind it — the alarm's job, not a red CD",
          arming_frozen(None, "", given, {"commit": "", "contains": {}, "why": "timeout"}, now=t0 + 10 * 3600,
                        bound_minutes=180) is None
          and arming_frozen(None, "", given, ctl(a, b, c, healthy=False), now=t0 + 10 * 3600, bound_minutes=180) is None)
    # ── a line HELD by control's declaration is not a frozen control (MeshWeaver#8279) ──
    held = control_line_hold("3.*", 4, "main")
    check("control_line_hold: a line whose major control's record EXCLUDES is HELD, naming the pattern, the line and its major",
          held is not None and "HELD BY DECLARATION" in held and "`3.*`" in held and "`main`" in held
          and "major 4" in held and "never control's roll" in held, str(held))
    check("control_line_hold: the line control's record admits is NOT held (`3.*` × 3, `4.*` × 4, `3.1.*` × 3)",
          control_line_hold("3.*", 3, "main3") is None and control_line_hold("4.*", 4, "main") is None
          and control_line_hold("3.1.*", 3, "main3") is None)
    check("control_line_hold: a caller that names no major holds nothing",
          control_line_hold("3.*", None, "main") is None)
    check("control_line_hold: a pattern naming no single major is REFUSED, never read as a hold or a pass",
          _raises(lambda: control_line_hold("*", 4, "main")) and _raises(lambda: control_line_hold("", 4, "main")))
    check("arming_frozen: a line HELD by control's declaration is never FROZEN, however long control has been behind it",
          arming_frozen(None, "", given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180, hold=held) is None)
    check("NEGATIVE CONTROL — the same lag on a line control's pattern ADMITS is still FROZEN (hold None)",
          (arming_frozen(None, "", given, frozen_ctl, now=t0 + 10 * 3600, bound_minutes=180,
                         hold=control_line_hold("3.*", 3, "main3")) or "").startswith("FLEET DELIVERY FROZEN ON CONTROL"))
    held_ctl = {**ctl(), "line_hold": held}
    st, text = judge(c, "success", held_ctl)
    check("judge: a green-ladder set on a HELD line WAITS and names the declaration (no 'not yet' promise)",
          st == "waiting" and "HELD BY DECLARATION" in text and " yet " not in text, f"{st}: {text}")
    st, text = judge(c, "success", ctl())
    check("NEGATIVE CONTROL — the same set on an admitted line keeps the ordinary 'not yet' wait",
          st == "waiting" and "HELD" not in text and " yet " in text, f"{st}: {text}")
    chosen, lines = select(records, ok, held_ctl, armed_max=9458)
    check("select: a HELD line arms NOTHING control does not run (control first still holds)",
          chosen is None and any("HELD BY DECLARATION" in ln for ln in lines), str(lines))
    ua = unarmed_sets(records, ok, held_ctl, 9458)
    check("unarmed_sets: every examined set of a HELD line carries the hold into the fleet-unarmed reading",
          ua and all(s["state"] == "waiting" and "HELD BY DECLARATION" in s["sentence"] for s in ua), str(ua))
    # ── THE LINE'S FIRST INSTANCE (policy `line-first-instance`, MeshWeaver#8365) ─────────────────────
    # A DECLARED first instance (memex on an image line of its own, memex-portal-ai-first3, exactly like
    # memex-control is control's) — fabricated here: the shipped 3.x line now DECLARES none (policy
    # `line-no-first-instance`, its own cases below), and the declared-instance path stays proven.
    shipped_ci = load_control_instance(Path(__file__).resolve().parents[1] / "control-instance.json")
    cd_mod = _cd_line()
    shipped_lines = cd_mod.by_branch(cd_mod.load(Path(__file__).resolve().parents[1] / "delivery-lines.json"))
    f4 = line_first_instance(shipped_lines.get("main"), shipped_ci, "main")
    check("first instance: line `main` (4.x) declares none → the CONTROL instance, its record and its job — unchanged",
          f4["name"] == "control" and not f4["declared"] and f4["deployment"] == shipped_ci["deployment"]
          and f4["record_path"] == shipped_ci["record"]["path"] and f4["given_prefix"] == CONTROL_FIRST_JOB_PREFIX
          and first_instance_url(f4, shipped_ci, {}) == str(shipped_ci["url"]).rstrip("/"), str(f4))
    f3 = line_first_instance({"branch": "main3", "firstInstance": "memex",
                              "firstInstanceImageRepository": "memex-portal-ai-first3"}, shipped_ci, "main3")
    check("first instance: line `main3` (3.x) → memex, its record beside control's, its own repository and job",
          f3["name"] == "memex" and f3["declared"] and f3["record_path"] == "mesh/Deployments/memex.json"
          and f3["image_repository"] == "memex-portal-ai-first3" and f3["given_prefix"] == FIRST_INSTANCE_JOB_PREFIX,
          str(f3))
    check("first instance: an undeclared LINE is refused, never defaulted",
          _raises(lambda: line_first_instance(None, shipped_ci, "release/9")))
    memex_rec = {"id": "memex", "content": {"host": "memex.systemorph.com", "updatePattern": "3.*",
                                            "imageRepository": "meshweaver.azurecr.io/memex-portal-ai-first3"}}
    check("first instance: a declared one answers at https:// + its record's content.host",
          first_instance_url(f3, shipped_ci, memex_rec) == "https://memex.systemorph.com")
    check("first instance: ANOTHER instance's record is refused (never judge the line on the wrong instance)",
          _raises(lambda: first_instance_url(f3, shipped_ci, {**memex_rec, "id": "memex-cloud"})))
    check("first instance: a record without a plain host is refused, never guessed",
          _raises(lambda: first_instance_url(f3, shipped_ci, {"id": "memex", "content": {"updatePattern": "3.*"}}))
          and _raises(lambda: first_instance_url(f3, shipped_ci, {"id": "memex", "content": {"host": "https://x.y/"}})))
    check("first instance: memex on its own repository with `3.*` holds nothing for line 3",
          first_instance_hold(f3, memex_rec, 3) is None)
    mh = first_instance_hold(f3, {**memex_rec, "content": {**memex_rec["content"],
                                                          "imageRepository": "meshweaver.azurecr.io/memex-portal-ai"}}, 3)
    check("first instance: memex's record still on the FLEET's repository HOLDS the line, naming both repositories",
          mh is not None and "HELD BY DECLARATION" in mh and "`meshweaver.azurecr.io/memex-portal-ai`" in mh
          and "memex-portal-ai-first3" in mh and "main3" in mh, str(mh))
    mp = first_instance_hold(f3, {**memex_rec, "content": {**memex_rec["content"], "updatePattern": "4.*"}}, 3)
    check("first instance: memex's record on `4.*` HOLDS line 3 and names memex, not control",
          mp is not None and mp.startswith("LINE HELD BY DECLARATION: memex's record") and "control's record" not in mp, str(mp))
    control_v4 = {"id": "control", "content": {"host": "control.systemorph.com", "updatePattern": "4.*"}}
    check("line 4 UNCHANGED: control on `4.*` holds nothing for the undeclared line `main`",
          first_instance_hold(f4, control_v4, 4) is None)
    # the reading, named: memex on line main3
    def mctl(*on: dict, **kw) -> dict:
        return {**ctl(*on, **kw), "name": "memex", "line": "main3"}
    chosen, lines = select(records, ok, mctl(a, b, c), armed_max=9458)
    check("line 3: the newest green set MEMEX runs is armed",
          chosen is c and any("memex runs" in ln for ln in lines) and not any("control runs" in ln for ln in lines),
          str(lines))
    chosen, lines = select(records, ok, mctl(a), armed_max=9458)
    check("line 3: memex not on the newer sets → only the one it runs is armed (memex first)",
          chosen is a and any("memex first" in ln and "main3" in ln for ln in lines), str(lines))
    chosen, lines = select(records, ok, mctl(), armed_max=9458)
    check("line 3: memex running NONE of them → nothing armed, every wait NAMES memex and the line, never control",
          chosen is None and all(("memex runs" in ln and "line `main3`" in ln) for ln in lines if "waiting" in ln)
          and not any("control" in ln.split("—")[1].split(":")[0] for ln in lines if "waiting" in ln), str(lines))
    check("line 4 UNCHANGED: the same reading of control still says `control runs … control first`",
          "control runs" in judge(c, "success", ctl())[1] and "control first:" in judge(c, "success", ctl())[1])
    # undeclared line 3 while control runs v4: no instance can run it first — the reading says THAT
    nofi = first_instance_hold({**f4, "line": "main3"}, control_v4, 3)
    st, text = judge(c, "success", {**ctl(), "line_hold": nofi})
    check("undeclared line 3 while control is on 4.*: REFUSED to arm, naming the MISSING first instance — never 'control runs X'",
          nofi is not None and nofi.startswith("LINE HAS NO FIRST INSTANCE") and "`main3`" in nofi
          and "`4.*`" in nofi and st == "waiting" and "control runs" not in text and "firstInstance" in text,
          f"{nofi} / {st}: {text}")
    chosen, lines = select(records, ok, {**ctl(), "line_hold": nofi}, armed_max=9458)
    check("...and select arms nothing on it", chosen is None, str(lines))
    check("NEGATIVE CONTROL — a DECLARED line on a 4.* control-style record is a hold, not 'no first instance'",
          not (control_line_hold("4.*", 3, "main3", who="memex", declared=True) or "").startswith("LINE HAS NO"))
    # ── NO FIRST INSTANCE, DECLARED (policy `line-no-first-instance`) ─────────────────────────────────
    # The shipped 3.x line declares none: its sets arm on the platform evidence alone, by NAME.
    fn = line_first_instance(shipped_lines.get("main3"), shipped_ci, "main3")
    check("no first instance: the shipped line `main3` DECLARES none — no deployment, record, repository or job",
          fn["none"] and fn["declared"] and fn["name"] == FIRST_INSTANCE_NONE and fn["deployment"] == ""
          and fn["record_path"] == "" and fn["image_repository"] == "" and fn["given_prefix"] == ""
          and len(fn["reason"]) >= 20, str(fn))
    check("no first instance: there is no url and no record to read — asking is REFUSED, never control's",
          _raises(lambda: first_instance_url(fn, shipped_ci, {})) and _raises(lambda: first_instance_hold(fn, {}, 3)))
    check("no first instance: `none` without its reason is REFUSED, never armed on",
          _raises(lambda: line_first_instance({"branch": "x", "firstInstance": FIRST_INSTANCE_NONE}, shipped_ci, "x")))
    cd_min = cd_mod.NO_FIRST_INSTANCE_REASON_MIN
    check("no first instance: a reason SHORTER than cd-line.py's minimum is REFUSED here too (one bound, two readers)",
          _raises(lambda: line_first_instance({"branch": "x", "firstInstance": FIRST_INSTANCE_NONE,
                                               "noFirstInstanceReason": "y" * (cd_min - 1)}, shipped_ci, "x")))
    check("NEGATIVE CONTROL — a reason of exactly cd-line.py's minimum is accepted",
          line_first_instance({"branch": "x", "firstInstance": FIRST_INSTANCE_NONE,
                               "noFirstInstanceReason": "y" * cd_min}, shipped_ci, "x")["none"])
    check("no first instance: `none` beside a repository is REFUSED",
          _raises(lambda: line_first_instance({"branch": "x", "firstInstance": FIRST_INSTANCE_NONE,
                                               "noFirstInstanceReason": "a reason long enough to read",
                                               "firstInstanceImageRepository": "memex-portal-ai-first3"},
                                              shipped_ci, "x")))
    nsent = no_first_instance_sentence(fn)
    nctl = {"commit": "", "healthy": False, "contains": {}, "why": "the line declares no first instance",
            "name": FIRST_INSTANCE_NONE, "line": "main3", "no_first_instance": nsent}
    st, text = judge(c, "success", nctl)
    check("no first instance: a green-ladder set ARMS, its sentence naming the declaration, the line and the reason",
          st == "green" and NO_FIRST_INSTANCE_DECLARED in text and "`main3`" in text and fn["reason"] in text
          and "line-no-first-instance" in text and not re.search(r" runs [0-9a-f]{9}", text), f"{st}: {text}")
    chosen, lines = select(records, ok, nctl, armed_max=9458)
    check("no first instance: select arms the NEWEST promoted set whose ladder passed — no instance waited on",
          chosen is c and any(NO_FIRST_INSTANCE_DECLARED in ln for ln in lines), str(lines))
    for bad in ("failure", "missing", "ambiguous"):
        st, text = judge(c, bad, nctl)
        check(f"no first instance: every OTHER gate stays — a `{bad}` ladder is still REFUSED", st == "refused", text)
    st, text = judge(c, None, nctl)
    check("no first instance: an unfinished ladder still WAITS", st == "waiting", text)
    chosen, lines = select(records, {9461: None, 9460: "failure", 9459: "success"}, nctl, armed_max=9458)
    check("no first instance: past a red and an unfinished ladder, the newest GREEN set is armed",
          chosen is a, str(lines))
    chosen, lines = select(records, ok, nctl, armed_max=9461)
    check("no first instance: never backwards — nothing newer than the armed set, nothing armed", chosen is None, str(lines))
    check("no first instance: never frozen — no instance was handed a build, so there is no lag clock",
          arming_frozen(None, "", [], nctl, now=t0 + 10 * 3600, bound_minutes=180) is None)
    ua = unarmed_sets(records, {9461: None, 9460: "success", 9459: "success"}, nctl, 9459)
    check("no first instance: the fleet-unarmed reading carries the declaration on each green set",
          [s_["state"] for s_ in ua] == ["waiting", "green"] and NO_FIRST_INSTANCE_DECLARED in ua[1]["sentence"], str(ua))
    # NEGATIVE CONTROL — an OMITTED firstInstance on the 3.x line is control, and control on 4.* HOLDS it
    fo = line_first_instance({"branch": "main3"}, shipped_ci, "main3")
    omitted_hold = first_instance_hold(fo, control_v4, 3)
    st, text = judge(c, "success", {**ctl(), "line_hold": omitted_hold})
    check("NEGATIVE CONTROL — OMITTING firstInstance is NOT the declared absence: control on 4.* HOLDS line 3, arms nothing",
          not fo["none"] and fo["name"] == "control" and (omitted_hold or "").startswith(NO_FIRST_INSTANCE)
          and "line-no-first-instance" in (omitted_hold or "") and st == "waiting"
          and select(records, ok, {**ctl(), "line_hold": omitted_hold}, armed_max=9458)[0] is None,
          f"{omitted_hold} / {st}: {text}")
    # the lag clock and the frozen red, for memex
    fjob = lambda ok_, at: [{"name": f"{FIRST_INSTANCE_JOB_PREFIX}: memex-portal-ai-first3:<version>",
                             "conclusion": "success" if ok_ else "failure", "completed_at": at}]
    cjob = lambda at: [{"name": "Deploy control first: memex-control:<version>", "conclusion": "success",
                        "completed_at": at}]
    gm = given_builds([c, b, a], lambda r: fjob(True, "2026-10-07T19:02:20Z"), lambda sha: False, FIRST_INSTANCE_JOB_PREFIX)
    check("given_builds: memex's clock reads `Deploy the line's first instance first`",
          [g["v_portal"] for g in gm] == [c["v_portal"], b["v_portal"], a["v_portal"]], str(gm))
    check("NEGATIVE CONTROL — a `Deploy control first` success is NOT a build given to memex",
          given_builds([c, b, a], lambda r: cjob("2026-10-07T19:02:20Z"), lambda sha: False,
                       FIRST_INSTANCE_JOB_PREFIX) == [])
    mfrozen = {**frozen_ctl, "name": "memex", "line": "main3"}
    st, text = control_lag(given, mfrozen, now=t0 + 10 * 3600, bound_minutes=180)
    check("lag clock: memex behind past the bound is RED, naming memex and its policy",
          st == "lag" and text.startswith("memex runs") and "line-first-instance" in text
          and "control-always-latest" not in text, text)
    fz_m = arming_frozen(None, "", given, mfrozen, now=t0 + 10 * 3600, bound_minutes=180)
    check("arming_frozen: FROZEN ON memex, naming the line — never 'ON CONTROL'",
          (fz_m or "").startswith("FLEET DELIVERY FROZEN ON memex (the first instance of line `main3`)")
          and "CONTROL" not in (fz_m or "") and "control-lag" not in (fz_m or ""), str(fz_m))
    check("arming_frozen: memex inside the bound is a wait, not a red",
          arming_frozen(None, "", given, mfrozen, now=t0 + 2 * 3600, bound_minutes=180) is None)
    check("arming_frozen: a line HELD for memex (record still on the fleet's repository) is never FROZEN",
          arming_frozen(None, "", given, mfrozen, now=t0 + 10 * 3600, bound_minutes=180, hold=mh) is None)
    # the handover reuses control's two decisions, aimed at memex
    ann = control_announcement("memex", "3.1.13471", "meshweaver.azurecr.io/memex-portal-ai-first3", "run", "t")
    check("control_announcement: memex is handed <its own repository>:<version>, nothing of the fleet's",
          ann["deployment"] == "memex" and ann["newImage"] == "meshweaver.azurecr.io/memex-portal-ai-first3:3.1.13471"
          and ann["pattern"] == "3.1.*", str(ann))
    mline = control_line("3.1.13471", 200, json.dumps({"version": "3.1.13430"}), who="memex")
    check("control_line: memex on 3.1 takes the 3.x build, and the reason names memex",
          mline["line_announce"] is True and mline["line_reason"].startswith("memex runs"), str(mline))
    check("control_first: an EMPTY first-instance repository (its first build) is tagged and announced",
          control_first([], "3.1.13471") == {"action": "tag", "version": "3.1.13471", "move_pointers": True,
                                             "announce": True, "control_newest": ""})
    job = lambda ok_, at: [{"name": "Deploy control first: memex-control:<version>",
                            "conclusion": "success" if ok_ else "failure", "completed_at": at}]
    jm = {9461: job(True, "2026-10-07T19:02:20Z"), 9460: job(False, "x"), 9459: None}
    gb = given_builds([c, b, a], lambda r: jm.get(r["run_number"]), lambda sha: False)
    check("given_builds: only sets whose control-first SUCCEEDED, newest first, with the epoch it was given",
          [g["v_portal"] for g in gb] == [c["v_portal"]] and gb[0]["given_at"] == _iso_epoch("2026-10-07T19:02:20Z"), str(gb))
    gb = given_builds([c, b, a], lambda r: job(True, "2026-10-07T19:02:20Z"), lambda sha: sha == b["core_sha"])
    check("given_builds: the walk stops AT the first build control contains (inclusive), reading nothing older",
          [g["v_portal"] for g in gb] == [c["v_portal"], b["v_portal"]], str(gb))
    # 🚨 Review on #6289: a fixed window of the newest ten sets loses the first build control missed.
    # Fifteen sets given 20 min apart, control on none of them: the newest ten span 3 h — inside a 240
    # min bound — while the first miss was 4 h 40 min ago.
    many = [rec(9600 - i, "%x" % (i % 16), "9") for i in range(15)]
    for i, r in enumerate(many):
        r["core_sha"] = f"{i:040x}"
    t_new = _iso_epoch("2026-10-08T05:00:00Z")
    stamp = lambda r: job(True, __import__("datetime").datetime.fromtimestamp(
        t_new - (9600 - int(r["run_number"])) * 20 * 60, __import__("datetime").timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
    behind_all = {"commit": "f" * 40, "healthy": True, "contains": {r["core_sha"]: False for r in many}, "why": ""}
    full = given_builds(many, stamp, lambda sha: False)
    now_ = t_new + 1
    check("arming_frozen: control behind 15 builds is RED from the FIRST miss (the whole walk)",
          len(full) == 15 and arming_frozen(None, "", full, behind_all, now_, 240) is not None
          and many[-1]["v_portal"] in (arming_frozen(None, "", full, behind_all, now_, 240) or ""))
    check("...and a newest-ten window would have read the same state as a wait — the negative control",
          arming_frozen(None, "", full[:10], behind_all, now_, 240) is None)
    check("arming_frozen: BEHIND past the bound AND unhealthy is still RED — the lag is clocked, the 503 is not the reason",
          arming_frozen(None, "", given, {**ctl(a, healthy=False), "self_update": su}, now=t0 + 10 * 3600,
                        bound_minutes=180) is not None)
    # ── control first, the delivery half: CD hands the build to control's roll lane ──
    cf = control_first([{"tags": ["3.0.0-ci.9590"]}], "3.0.0-ci.9598")
    check("control-first: the newest accepted build is ANNOUNCED to control's roll lane", cf["announce"] is True, str(cf))
    cf = control_first([{"tags": ["3.0.0-ci.9598"]}], "3.0.0-ci.9598")
    check("control-first: a re-run of the newest build re-announces (the receiver dedupes; a lost POST is closed)",
          cf["action"] == "done" and cf["announce"] is True, str(cf))
    cf = control_first([{"tags": ["3.0.0-ci.9601"]}], "3.0.0-ci.9598")
    check("control-first: an OLDER build than the newest control version is never announced", cf["announce"] is False, str(cf))
    check("line_pattern: a set names its own line", line_pattern("3.0.0-ci.10052") == "3.0.0-ci*", line_pattern("3.0.0-ci.10052"))
    check("line_pattern: a pre-release line keeps its prefix", line_pattern("3.0.0-rc9.ci.7231") == "3.0.0-rc9.ci*")
    check("line_pattern: a SemVer-notation set names its minor line", line_pattern("3.1.10052") == "3.1.*", line_pattern("3.1.10052"))
    check("run_number_of: the SemVer notation's run number is the PATCH", run_number_of("3.1.10052") == 10052)
    check("run_number_of: both notations share ONE lineage (old 9999 < new 10000)",
          run_number_of("3.0.0-ci.9999") < run_number_of("3.1.10000"))
    check("run_number_of: the withdrawn slip keeps its ci number", run_number_of("3.1.0-ci.7841") == 7841)
    for not_a_set in ("3.0.0", "3.1.0", "3.0.5", "2.9.12345", "3.1.10052-rc1", "3-latest", "3.1-latest"):
        check(f"run_number_of: {not_a_set!r} below the boundary or not a build is no set", run_number_of(not_a_set) is None)
    cf = control_first([{"tags": ["3.0.0-ci.9999"]}], "3.1.10000")
    check("control-first: the first SemVer-notation build is newer than the last ci set (announced, pointers move)",
          cf["announce"] is True and cf["move_pointers"] is True, str(cf))
    cf = control_first([{"tags": ["3.1.10003"]}], "3.0.0-ci.10001")
    check("control-first: an old-notation build OLDER than a SemVer-notation one is never announced",
          cf["announce"] is False and cf["move_pointers"] is False, str(cf))
    try:
        line_pattern("main")
        check("line_pattern: a non-set name is RED", False)
    except ValueError:
        check("line_pattern: a non-set name is RED, never a guess", True)
    ann = control_announcement("control", "3.0.0-ci.10052", "meshweaver.azurecr.io/memex-control",
                               "https://github.com/Systemorph/MeshWeaver/actions/runs/1", "2026-10-06T06:00:00Z")
    check("control_announcement: the routing side's Roll shape — event first, tag, image on the record's repository, line pattern",
          list(ann)[0] == "event" and ann["event"] == "self-update-available" and ann["deployment"] == "control"
          and ann["newVersion"] == "3.0.0-ci.10052" and ann["newImage"] == "meshweaver.azurecr.io/memex-control:3.0.0-ci.10052"
          and ann["pattern"] == "3.0.0-ci*" and ann["detectedAt"] == "2026-10-06T06:00:00Z", json.dumps(ann))
    check("control_announcement: the pattern ADMITS the tag it announces (else the roll would park for an approval)",
          re.fullmatch(re.escape(ann["pattern"][:-1]) + ".*", ann["newVersion"]) is not None, json.dumps(ann))
    # 🚨 The incident of 2026-11-14: the v4 line announced 4.0.13212 to a control running v3.
    v3_body = json.dumps({"version": "3.1.0+60fa43e8f7c0b1178088d8ae8aaacdcc7e500dd1", "commit": "6" * 40})
    out_of_line = control_line("4.0.13212", 200, v3_body)
    check("control_line: a 4.x build is NOT announced to a control running 3.x",
          out_of_line["line_announce"] is False and out_of_line["control_major"] == 3 and out_of_line["build_major"] == 4
          and "NOT announced" in out_of_line["line_reason"], json.dumps(out_of_line))
    on_line = control_line("3.1.13192", 200, v3_body)
    check("control_line: a 3.x build IS announced to a control running 3.x (the rule refuses nothing it should hand over)",
          on_line["line_announce"] is True, json.dumps(on_line))
    check("control_line: the old notation is on the 3 line too",
          control_line("3.0.0-ci.10052", 200, v3_body)["line_announce"] is True)
    after_cutover = control_line("3.1.13300", 200, json.dumps({"version": "4.0.13250+" + "a" * 40}))
    check("control_line: once control RUNS 4.x the v3 line stops announcing — lines run separately both ways",
          after_cutover["line_announce"] is False and after_cutover["control_major"] == 4, json.dumps(after_cutover))
    check("control_line: …and the v4 line announces to it",
          control_line("4.0.13260", 200, json.dumps({"version": "4.0.13250"}))["line_announce"] is True)
    spoof = control_line("4.0.13212", 200, json.dumps({"version": "3.1.0+abc\n::error::spoofed **bold**"}))
    check("control_line: only the matched version prefix is echoed — nothing after it reaches a ::notice:: or the summary",
          "3.1.0 (major 3)" in spoof["line_reason"] and "::" not in spoof["line_reason"]
          and "\n" not in spoof["line_reason"] and "**" not in spoof["line_reason"], json.dumps(spoof))
    # The workflow compares `steps.control_line.outputs.line_announce == 'true'` — so the ROUND TRIP through
    # GITHUB_OUTPUT must spell a Python bool in lowercase, or the handover never runs (and fails soft).
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        out_file = Path(tmp) / "out"
        saved = os.environ.get("GITHUB_OUTPUT")
        os.environ["GITHUB_OUTPUT"] = str(out_file)
        try:
            _write_rows(control_line("3.1.13192", 200, v3_body))
            _write_rows({"line_announce": control_line("4.0.13212", 200, v3_body)["line_announce"]})
        finally:
            if saved is None:
                os.environ.pop("GITHUB_OUTPUT", None)
            else:
                os.environ["GITHUB_OUTPUT"] = saved
        lines = out_file.read_text().splitlines()
    check("control_line → GITHUB_OUTPUT: the workflow reads `line_announce=true` / `line_announce=false` (lowercase)",
          "line_announce=true" in lines and "line_announce=false" in lines
          and not any(l in ("line_announce=True", "line_announce=False") for l in lines), str(lines))
    for bad_code, bad_body, why in ((0, "connection refused", "an unreachable control"),
                                    (200, json.dumps({"commit": "6" * 40}), "a /api/version naming no version"),
                                    (503, "", "a 503"),
                                    (200, "<html>", "a body that is not JSON")):
        try:
            control_line("4.0.13212", bad_code, bad_body)
            check(f"control_line: {why} is RED, never 'announce anyway' or a silent skip", False)
        except ValueError:
            check(f"control_line: {why} is RED, never 'announce anyway' or a silent skip", True)
    # NEGATIVE CONTROL: the announcement alone (what control-first sent before) carries the build's own
    # line and is composed for ANY major — so without control_line the 4.x build WOULD have been handed over.
    neg = control_announcement("control", "4.0.13212", "meshweaver.azurecr.io/memex-control", "u", "t")
    check("NEGATIVE CONTROL — control_announcement composes a 4.0.* announcement for control regardless of its line",
          neg["pattern"] == "4.0.*" and neg["deployment"] == "control", json.dumps(neg))
    for bad_dep, bad_repo, why in (("", "meshweaver.azurecr.io/memex-control", "no deployment"),
                                   ("control; rm", "meshweaver.azurecr.io/memex-control", "a deployment that is not a plain id"),
                                   ("control", "meshweaver.azurecr.io/memex-control:3.0.0-ci.1", "a repository carrying a tag"),
                                   ("control", "", "no repository")):
        try:
            control_announcement(bad_dep, "3.0.0-ci.10052", bad_repo, "u", "t")
            check(f"control_announcement: {why} is RED", False)
        except ValueError:
            check(f"control_announcement: {why} is RED, never a guess", True)
    # read_records against a scripted API
    blob = io.BytesIO()
    with zipfile.ZipFile(blob, "w") as z:
        z.writestr("promotion-record.json", json.dumps(b))
    calls = {
        f"repos/{CORE}/actions/workflows/{WORKFLOW}/runs?branch=main&per_page={RUNS_EXAMINED}":
            (200, {"workflow_runs": [{"id": 2, "run_number": 9460, "html_url": "u2"},
                                     {"id": 1, "run_number": 9459, "html_url": "u1"},
                                     {"id": 0, "run_number": 9400, "html_url": "u0"}]}),
        f"repos/{CORE}/actions/runs/2/artifacts?name={RECORD_ARTIFACT}":
            (200, {"artifacts": [{"name": RECORD_ARTIFACT, "archive_download_url": "dl2"}]}),
        f"repos/{CORE}/actions/runs/1/artifacts?name={RECORD_ARTIFACT}": (200, {"artifacts": []}),
        "dl2": (200, blob.getvalue()),
    }
    seen: list[str] = []

    def fake(url: str, token: str, raw: bool = False):
        seen.append(url)
        return calls.get(url, (404, None))
    got = read_records(fake, "t", above=9405, log=lambda _l: None)
    check("read_records reads the record of a promoting run and skips a run without one",
          [r["run_number"] for r in got] == [9460], str(got))
    check("read_records never reads a run at or below the armed set",
          not any("runs/0/" in u for u in seen), str(seen))
    # A page is 40 RUNS, not 40 promotions (Copilot on MeshWeaver#6367): page 1 full of promotion-free
    # reconcile ticks must not hide the unarmed promotion on page 2 from the fleet-unarmed reading.
    base_url = f"repos/{CORE}/actions/workflows/{WORKFLOW}/runs?branch=main&per_page={RUNS_EXAMINED}"
    ticks = [{"id": 1000 + i, "run_number": 9600 - i, "html_url": "t", "created_at": "2026-10-09T10:00:00Z"}
             for i in range(RUNS_EXAMINED)]
    paged = {base_url: (200, {"workflow_runs": ticks}),
             base_url + "&page=2": (200, {"workflow_runs": [
                 {"id": 2, "run_number": 9460, "html_url": "u2", "created_at": "2026-10-08T00:00:00Z"},
                 {"id": 0, "run_number": 9400, "html_url": "u0", "created_at": "2026-10-07T00:00:00Z"}]}),
             f"repos/{CORE}/actions/runs/2/artifacts?name={RECORD_ARTIFACT}":
                 (200, {"artifacts": [{"name": RECORD_ARTIFACT, "archive_download_url": "dl2",
                                       "created_at": "2026-10-08T00:05:00Z"}]}),
             "dl2": (200, blob.getvalue())}
    pfake = lambda url, token, raw=False: paged.get(url, (200, {"artifacts": []}) if "/artifacts" in url else (404, None))  # noqa: E731
    cov: dict = {}
    got = read_records(pfake, "t", above=9405, log=lambda _l: None, max_pages=UNARMED_MAX_PAGES, coverage=cov)
    check("read_records pages past promotion-free ticks down to the armed cursor (the promotion on page 2 is kept)",
          [r["run_number"] for r in got] == [9460] and cov.get("reached") is True
          and got[0]["promoted_at"] == "2026-10-08T00:05:00Z", f"{got} {cov}")
    cov = {}
    got = read_records(pfake, "t", above=9405, log=lambda _l: None, max_pages=1, coverage=cov)
    check("negative control: ONE page loses that promotion and SAYS the window did not reach the cursor",
          got == [] and cov.get("reached") is False and cov.get("floor_at") == "2026-10-09T10:00:00Z", f"{got} {cov}")
    # the bundle base — the newest ARMED set
    live = [  # the shape measured on memex-portal-ai, 2026-09-28
        {"tags": ["3-latest", "3.0-latest", "3.0.0-ci.9538", "3.0.0-latest", "b9fe5ed", "b9fe5ed-pb7390a0", "main"]},
        {"tags": ["staging-9357060-36379800125"]},
        {"tags": ["3.0.0-ci.9535", "9357060", "9357060-p90f70cd"]},
        {"tags": None},
    ]
    check("armed-base: the NEWEST version tag's manifest names the base (its core sha tag)",
          armed_commit(live) == ("3.0.0-ci.9538", "b9fe5ed"), str(armed_commit(live)))
    pair_only = [{"tags": ["3.0.0-ci.9564", "f0f9a7b-p217aa51", "arm-complete-3.0.0-ci.9564"]},
                 {"tags": ["f0f9a7b", "f0f9a7b-pb7390a0"]}]
    check("armed-base: pair tag names the armed core after its bare sha tag moves to a newer pair",
          armed_commit(pair_only) == ("3.0.0-ci.9564", "f0f9a7b"))
    # After the pair tag's retirement: `arm` writes the version FROM the staging tag, and a rebuild of
    # the same core moves the bare sha tag away — the staging tag alone must still name the core.
    staging_only = [{"tags": ["3.0.0-ci.9970", "staging-260b3c4-41000000001", "arm-complete-3.0.0-ci.9970"]},
                    {"tags": ["260b3c4", "staging-260b3c4-41000000002", "main"]}]
    check("armed-base: with NO pair tag, the staging tag names the armed core after its sha tag moved",
          not _raises(lambda: armed_commit(staging_only))
          and armed_commit(staging_only) == ("3.0.0-ci.9970", "260b3c4"))
    check("armed-base: a version tag with neither a sha, a pair nor a staging tag is RED (negative control)",
          _raises(lambda: armed_commit([{"tags": ["3.0.0-ci.9970", "arm-complete-3.0.0-ci.9970"]}])))
    check("armed-base: a staging tag naming a DIFFERENT core than the sha tag is RED, never a guess",
          _raises(lambda: armed_commit([{"tags": ["3.0.0-ci.9970", "260b3c4", "staging-1111111-4"]}])))
    check("armed-base: newest by RUN NUMBER, not by listing order",
          armed_commit(list(reversed(live))) == ("3.0.0-ci.9538", "b9fe5ed"))
    check("armed-base: no version tag anywhere means nothing was ever armed (None, not a guess)",
          armed_commit([{"tags": ["main", "b9fe5ed"]}]) is None)
    for bad, why in (([{"tags": ["3.0.0-ci.9538", "main"]}], "no identity tag"),
                     ([{"tags": ["3.0.0-ci.9538", "b9fe5ed", "a1b2c3d"]}], "two sha tags"),
                     ([{"tags": ["3.0.0-ci.9538", "b9fe5ed", "a1b2c3d-p217aa51"]}], "conflicting sha and pair tags"),
                     ({"tags": []}, "not a list")):
        try:
            armed_commit(bad)
            check(f"armed-base: an uninterpretable manifest ({why}) is RED", False)
        except ValueError:
            check(f"armed-base: an uninterpretable manifest ({why}) is RED, never a guess", True)
    check("a record with an unknown base_kind is refused", check_record({**a, "base_kind": "nope"}) is not None)
    check("an `armed` and a legacy (no base_kind) record are both accepted",
          check_record({**a, "base_kind": "armed"}) is None and check_record(a) is None)
    st = armed_state(live)
    check("armed-state: the newest armed set WITHOUT its marker is incomplete", st == (9538, "3.0.0-ci.9538", False), str(st))
    marked = [{"tags": live[0]["tags"] + ["arm-complete-3.0.0-ci.9538"]}] + live[1:]
    st = armed_state(marked)
    check("armed-state: WITH `arm-complete-<version>` it is complete", st == (9538, "3.0.0-ci.9538", True), str(st))
    stale = [{"tags": ["3.0.0-ci.9538", "b9fe5ed"]}, {"tags": ["3.0.0-ci.9535", "arm-complete-3.0.0-ci.9535"]}]
    check("armed-state: another set's marker does not complete the newest", armed_state(stale)[2] is False)
    check("armed-state: the marker is never version-shaped (no self-updater can select it)",
          run_number_of("arm-complete-3.0.0-ci.9538") is None)
    check("armed-state: nothing ever armed is None", armed_state([{"tags": ["main"]}]) is None)
    cf = control_first([{"tags": ["0d14493", "main", "staging-0d14493-1"]},
                        {"tags": ["3.0.0-ci.9590", "5145540", "3-latest"]}], "3.0.0-ci.9598")
    check("control-first: every accepted build is tagged with its version and moves the line pointers",
          cf["action"] == "tag" and cf["version"] == "3.0.0-ci.9598" and cf["move_pointers"] is True, str(cf))
    cf = control_first([{"tags": ["3.0.0-ci.9598", "0d14493"]}], "3.0.0-ci.9598")
    check("control-first: already carrying the version is DONE (idempotent)", cf["action"] == "done", str(cf))
    cf = control_first([{"tags": ["0d14493"]}, {"tags": ["3.0.0-ci.9601"]}], "3.0.0-ci.9598")
    check("control-first: never moves the line pointers backwards past a newer control version",
          cf["action"] == "tag" and cf["move_pointers"] is False and cf["control_newest"] == "3.0.0-ci.9601", str(cf))
    check("control-first: a first-ever control version moves the pointers",
          control_first([], "3.0.0-ci.9598")["move_pointers"] is True)
    import inspect as _i
    check("control-first: the decision takes NO arming input — control never waits for the fleet",
          list(_i.signature(control_first).parameters) == ["control_manifests", "version"])
    try:
        control_first([], "main")
        check("control-first: a non-version is RED", False)
    except ValueError:
        check("control-first: a version that is not X.Y.Z-ci.N is RED, never a guess", True)
    # the fleet-unarmed reading (MeshWeaver#6042): every set newer than the cursor, each its OWN verdict
    ua = unarmed_sets(records, {9461: "failure", 9460: None}, ctl(), 9459)
    check("unarmed_sets: every set newer than the armed one, newest first, each with its own verdict",
          [(u["run_number"], u["state"]) for u in ua] == [(9461, "refused"), (9460, "waiting")], str(ua))
    ua = unarmed_sets(records, {9461: None}, ctl(), 9459)
    check("unarmed_sets: a set whose ladder was never read is `not examined`, never guessed `waiting`",
          ua[-1]["run_number"] == 9460 and ua[-1]["state"] == "not examined", str(ua))
    check("unarmed_sets: nothing newer than the cursor is an empty reading (negative control)",
          unarmed_sets(records, {}, ctl(), 9461) == [])
    # ── per line, per MAJOR (policy `major-lines-run-separately`) ───────────────────────────────
    # The lines share ONE run counter, so once 4.x sets exist they are always "newest". Each line's
    # selection must stay inside its own major — and the negative controls prove the filter is what
    # makes the difference: without it, the v3 line reads the 4.x set.
    mixed = [
        {"tags": ["4.0.11005", "staging-4444444-1", "4-latest", "4.0-latest", "latest"]},
        {"tags": ["3.1.11003", "staging-3333333-2", "3-latest", "3.1-latest", "arm-complete-3.1.11003"]},
        {"tags": ["4.0.11001", "staging-4141414-3"]},
        {"tags": ["3.1.10999", "staging-3131313-4"]},
    ]
    check("major: a set's major is read from its name, both notations; a non-set has none",
          major_of("3.0.0-ci.9538") == 3 and major_of("4.0.11000") == 4 and major_of("3-latest") is None
          and major_of("main3") is None)
    check("armed-state, major 3: the newest armed 3.x set, although a 4.x set is newer",
          armed_state(mixed, major=3) == (11003, "3.1.11003", True), str(armed_state(mixed, major=3)))
    check("armed-state, major 4: the newest armed 4.x set",
          armed_state(mixed, major=4) == (11005, "4.0.11005", False), str(armed_state(mixed, major=4)))
    check("NEGATIVE CONTROL — without the major filter the v3 line reads the 4.x set as its cursor",
          armed_state(mixed)[1] == "4.0.11005")
    check("armed-base, major 3: the 3.x set's own core, never the 4.x one",
          armed_commit(mixed, major=3) == ("3.1.11003", "3333333"), str(armed_commit(mixed, major=3)))
    check("NEGATIVE CONTROL — armed-base without the filter names the 4.x core",
          armed_commit(mixed) == ("4.0.11005", "4444444"))
    check("armed-state: a major with no armed set is None (main-cd reds it, never 'arm anything')",
          armed_state(mixed, major=5) is None)
    # COLD START of a line's major: the fleet is armed on 3.x only, and main has moved to 4.x.
    only3 = [r for r in mixed if not any(str(t).startswith("4.") for t in r["tags"])]
    cur = arming_cursor(only3, major=4)
    check("cold start: the FIRST set of a new major is armable — cursor 0 of major 4, nothing to resume",
          cur == {"max": "0", "version": "", "complete": "true", "cold_start": "true"}, str(cur))
    cur3 = arming_cursor(only3, major=3)
    check("cold start is per major: the 3.x line keeps its own cursor beside it",
          cur3 == {"max": "11003", "version": "3.1.11003", "complete": "true", "cold_start": "false"}, str(cur3))
    check("cold start is never 'nothing armed for the fleet': a registry with NO armed set at all is still refused",
          arming_cursor([{"tags": ["main", "staging-1111111-1"]}], major=4) is None
          and arming_cursor([{"tags": ["main"]}]) is None)
    check("NEGATIVE CONTROL — the per-major reading alone (what the job used to red on) has NO cursor for 4.x",
          armed_state(only3, major=4) is None)

    def first_of_major(n: int, core: str, version: str) -> dict:
        r = rec(n, core, "9")
        return {**r, "v_portal": version, "v_migration": version, "v_plugin": version}
    first4 = first_of_major(11006, "e", "4.0.11006")
    cs_ctl = {"commit": "e" * 40, "healthy": True, "contains": {"e" * 40: True}, "why": ""}
    chosen, _ = select([first4], {11006: "success"}, cs_ctl, int(cur["max"]))
    check("cold start: `select` arms the first 4.x set on its green verdict from cursor 0 — no --override",
          chosen is not None and chosen["v_portal"] == "4.0.11006", str(chosen))
    chosen, _ = select([first4], {11006: "waiting"}, cs_ctl, int(cur["max"]))
    check("cold start still needs the verdict: a 4.x set whose ladder has not finished is NOT armed",
          chosen is None)

    # MUTATION CONTROL: disable the filter itself — the per-major cases above must then FAIL.
    real_filter = globals()["_in_major"]
    globals()["_in_major"] = lambda _version, _major: True
    try:
        mutated = (armed_state(mixed, major=3), armed_commit(mixed, major=3))
    finally:
        globals()["_in_major"] = real_filter
    check("MUTATION CONTROL — with the major filter disabled, major 3 is handed the 4.x set (the cases above can fail)",
          mutated[0] is not None and mutated[0][1] == "4.0.11005" and mutated[1][0] == "4.0.11005", str(mutated))

    def mrec(n: int, core: str, version: str) -> dict:
        r = rec(n, core, "9")
        return {**r, "v_portal": version, "v_migration": version, "v_plugin": version}

    def lfake(branch_seen: list[str], recs: list[dict]):
        def get(url: str, _token: str, raw: bool = False):
            if "/actions/workflows/" in url:
                branch_seen.append(url.split("branch=", 1)[1].split("&", 1)[0])
                return 200, {"workflow_runs": [{"id": r["run_number"], "run_number": r["run_number"],
                                                "html_url": "", "created_at": ""} for r in recs]}
            if "/artifacts?" in url:
                rid = int(url.split("/runs/", 1)[1].split("/", 1)[0])
                return 200, {"artifacts": [{"name": RECORD_ARTIFACT, "expired": False,
                                            "archive_download_url": f"blob/{rid}", "created_at": ""}]}
            rid = int(url.rsplit("/", 1)[1])
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as z:
                z.writestr("promotion-record.json", json.dumps(next(r for r in recs if r["run_number"] == rid)))
            return 200, buf.getvalue()
        return get
    both = [mrec(11006, "e", "4.0.11006"), mrec(11004, "f", "3.1.11004"), mrec(11002, "a", "3.1.11002")]
    seen: list[str] = []
    got = read_records(lfake(seen, both), "t", above=0, log=lambda _l: None, branch="main3", major=3)
    check("read_records: the line's OWN runs (branch=main3), and only its major's sets",
          seen == ["main3"] and [r["v_portal"] for r in got] == ["3.1.11004", "3.1.11002"], f"{seen} {got}")
    seen = []
    unfiltered = read_records(lfake(seen, both), "t", above=0, log=lambda _l: None)
    check("NEGATIVE CONTROL — without the line, read_records lists `main` and hands back the 4.x set first",
          seen == ["main"] and unfiltered[0]["v_portal"] == "4.0.11006", f"{seen} {unfiltered}")

    # ── a line, read over REST: runs per branch, records, jobs, compare (MeshWeaver#8208 / #8197) ──
    def line_api(by_branch: dict[str, list[tuple[int, dict | None]]], given_at: dict[int, str] | None = None,
                 contained: dict[tuple[str, str], str] | None = None, props: dict[str, str] | None = None,
                 urls: list[str] | None = None):
        """A fake GitHub REST over several delivery lines. `by_branch` lists each branch's runs NEWEST
        FIRST as (run_number, promotion record or None for a run that promoted nothing)."""
        records = {n: r for runs in by_branch.values() for n, r in runs}
        given_at, contained, props = given_at or {}, contained or {}, props or {}

        def get(url: str, _token: str, raw: bool = False):
            if urls is not None:
                urls.append(url)
            if "/actions/workflows/" in url:
                branch = url.split("branch=", 1)[1].split("&", 1)[0]
                page = int(url.split("&page=", 1)[1]) if "&page=" in url else 1
                runs = by_branch.get(branch, [])[(page - 1) * RUNS_EXAMINED: page * RUNS_EXAMINED]
                return 200, {"workflow_runs": [{"id": n, "run_number": n, "html_url": f"https://run/{n}",
                                                "created_at": "2026-11-11T00:00:00Z"} for n, _ in runs]}
            if "/artifacts?" in url:
                rid = int(url.split("/runs/", 1)[1].split("/", 1)[0])
                if records.get(rid) is None:
                    return 200, {"artifacts": []}
                return 200, {"artifacts": [{"name": RECORD_ARTIFACT, "expired": False,
                                            "archive_download_url": f"blob/{rid}", "created_at": f"promoted-{rid}"}]}
            if url.startswith("blob/"):
                buf = io.BytesIO()
                with zipfile.ZipFile(buf, "w") as z:
                    z.writestr("promotion-record.json", json.dumps(records[int(url.split("/", 1)[1])]))
                return 200, buf.getvalue()
            if "/jobs?" in url:
                rid = int(url.split("/runs/", 1)[1].split("/", 1)[0])
                jobs = [{"name": "Deploy control first", "status": "completed", "conclusion": "success",
                         "completed_at": given_at[rid]}] if rid in given_at else [{"name": "promote"}]
                return 200, {"jobs": jobs}
            if "/compare/" in url:
                sha, commit = url.rsplit("/", 1)[1].split("...", 1)
                return 200, {"status": contained.get((sha, commit), "diverged")}
            if "/contents/Directory.Build.props?ref=" in url:
                ref = url.split("ref=", 1)[1]
                if ref not in props:
                    return 404, None
                import base64
                return 200, {"encoding": "base64", "content": base64.b64encode(props[ref].encode()).decode()}
            return 404, None
        return get

    # (a) COLD START of a major: the window is complete once the listing passes the line's FIRST set
    # of the major — not after the bounded listing runs out of pages over the previous major's runs.
    full_page = [(13230 - i, None) for i in range(RUNS_EXAMINED)]       # 13230 … 13191, a FULL page
    full_page[15] = (13215, mrec(13215, "e", "4.0.13215"))
    full_page[30] = (13200, mrec(13200, "f", "4.0.13200"))                # the line's FIRST 4.x set
    full_page[35] = (13195, mrec(13195, "a", "3.1.13195"))                # the last 3.x set before the move
    older_page = [(13190 - i, mrec(13190 - i, "b", f"3.1.{13190 - i}")) for i in range(5)]
    urls: list[str] = []
    cov: dict = {}
    got = read_records(line_api({"main": full_page + older_page}, urls=urls), "t", above=0, log=lambda _l: None,
                       max_pages=UNARMED_MAX_PAGES, coverage=cov, branch="main", major=4, major_start=True)
    check("cold start: the window is COMPLETE once the listing passes the line's first set of the major",
          cov.get("reached") is True and cov.get("major_start_run") == 13195, str(cov))
    check("cold start: the unarmed sets are the major's own, oldest = the line's FIRST 4.x set",
          [r["v_portal"] for r in got] == ["4.0.13215", "4.0.13200"] and got[-1]["promoted_at"] == "promoted-13200",
          str([r["v_portal"] for r in got]))
    check("cold start: no page beyond the boundary is listed",
          not any("&page=2" in u for u in urls), str([u for u in urls if "workflows" in u]))
    cov = {}
    read_records(line_api({"main": full_page + older_page}), "t", above=0, log=lambda _l: None,
                 max_pages=1, coverage=cov, branch="main", major=4)
    check("NEGATIVE CONTROL — without major_start the same full page never reaches cursor 0 (the 83.6 h floor)",
          cov.get("reached") is False, str(cov))
    cov = {}
    read_records(line_api({"main3": older_page}), "t", above=0, log=lambda _l: None, max_pages=1,
                 coverage=cov, branch="main3", major=3, major_start=True)
    check("cold start: a run of a NEWER major is never a boundary (only the line's own previous major is)",
          cov.get("major_start_run") is None, str(cov))
    check("cold start is the line's own: major named, cursor 0, no armed version, no override",
          cold_start_of(4, 0, "", "") and not cold_start_of(4, 13192, "4.0.13192", "")
          and not cold_start_of(None, 0, "", "") and not cold_start_of(4, 0, "", "4.0.13200"))
    check("a cold start is NAMED as one — never the cursor `ci.0`",
          armed_name(None, "", 0, 4, True) == "no 4.x set armed yet", armed_name(None, "", 0, 4, True))
    check("NEGATIVE CONTROL — an armed line still names its armed set (and a selection beats it)",
          armed_name(None, "3.1.13192", 13192, 3, False) == "3.1.13192"
          and armed_name({"v_portal": "3.1.13199"}, "3.1.13192", 13192, 3, False) == "3.1.13199")

    # (d) CONTROL-LAG PER LINE: the line is derived from control's record, never `main` by default.
    check("updatePattern → major: `3.*`, `3.1.*`, `3.0.0-ci*`, `4.*`",
          [pattern_major(p) for p in ("3.*", "3.1.*", "3.0.0-ci*", "4.*")] == [3, 3, 3, 4])
    for bad in ("*", "3*", "", "latest", None):
        check(f"updatePattern {bad!r} names no single major — REFUSED, never guessed",
              _raises(lambda bad=bad: pattern_major(bad)))
    check("the record's pattern is read off content.updatePattern",
          record_update_pattern({"content": {"$type": "DeploymentContent", "updatePattern": "3.*"}}) == "3.*")
    check("a record without updatePattern is REFUSED (no default line)",
          _raises(lambda: record_update_pattern({"content": {}})) and _raises(lambda: record_update_pattern([])))
    check("line of major 3 among {main: 4, main3: 3} is main3", line_of_major({"main": 4, "main3": 3}, 3) == "main3")
    check("no declared line of the major, two of it, or an unreadable line — each REFUSED",
          _raises(lambda: line_of_major({"main": 4, "main3": 3}, 5))
          and _raises(lambda: line_of_major({"main": 3, "main3": 3}, 3))
          and _raises(lambda: line_of_major({"main": 4, "main3": None}, 4)))
    lines_file = Path(__file__).resolve().parent.parent / "delivery-lines.json"
    props = {"main": "<PlatformVersion>4.0.0</PlatformVersion>", "main3": "<PlatformVersion>3.1.0</PlatformVersion>"}
    majors = declared_line_majors(lines_file, "t", line_api({}, props=props))
    check("each declared line's major is READ from its own Directory.Build.props",
          majors == {"main": 4, "main3": 3}, str(majors))
    check("a line whose props cannot be read reads None (and line_of_major then refuses)",
          declared_line_majors(lines_file, "t", line_api({}, props={"main": props["main"]}))["main3"] is None)

    from datetime import datetime as _dt
    now_ep = _dt.fromisoformat("2026-11-14T18:00:00+00:00").timestamp()
    ctl_commit = "c" * 40                                   # control runs the main3 cherry-pick (#8177)
    ancestor_on_main = mrec(13189, "a", "3.1.13189")        # built on main before the split (3c2cb9d1a)
    v4 = mrec(13212, "d", "4.0.13212")
    cherry = mrec(13192, "c", "3.1.13192")                  # = control's own commit, on main3
    lanes = {"main": [(13212, v4), (13189, ancestor_on_main)], "main3": [(13192, cherry)]}
    given = {13212: "2026-11-14T12:00:00Z", 13189: "2026-11-14T06:00:00Z", 13192: "2026-11-14T10:00:00Z"}
    contained = {("c" * 40, ctl_commit): "identical"}

    def lag_on(lanes_, branch, major, contained_=contained, given_=given):
        api = line_api(lanes_, given_at=given_, contained=contained_)
        recs = read_records(api, "t", above=0, log=lambda _l: None, branch=branch, major=major)
        control = {"commit": ctl_commit, "healthy": True, "contains": {}, "why": ""}
        walk = given_to_control(recs, control, lambda r: read_run_jobs(api, "t", r.get("run_id")),
                                lambda sha: contains_commit(api, "t", ctl_commit, sha))
        return control_lag(walk, control, now_ep, 180)

    state, text = lag_on(lanes, line_of_major(majors, pattern_major("3.*")), 3)
    check("a control on the main3 CHERRY-PICK of a main commit is NOT behind (read per line)",
          state == "ok" and "3.1.13192" in text, f"{state}: {text}")
    state, text = lag_on(lanes, "main", None)
    check("NEGATIVE CONTROL — the old reading (main's builds, every major) calls the same control behind",
          state == "lag", f"{state}: {text}")
    newer3 = mrec(13199, "e", "3.1.13199")                  # a newer v3 build control did not take
    state, text = lag_on({**lanes, "main3": [(13199, newer3), (13192, cherry)]}, "main3", 3,
                         given_={**given, 13199: "2026-11-14T13:00:00Z"})
    check("a REAL lag on control's own line is still reported, clocked from the first build it missed",
          state == "lag" and "3.1.13199" in text and "300 min" in text, f"{state}: {text}")
    print(f"arm-promoted-set self-test: {failures} failure(s)")
    return 1 if failures else 0


# ───────────────────────────────── main ───────────────────────────────────────────────────

def _write_rows(rows: dict) -> None:
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a") as f:
            f.writelines(f"{k}={str(v).lower() if isinstance(v, bool) else v}\n" for k, v in rows.items())


def _iso_epoch(ts: str) -> float:
    from datetime import datetime
    return datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("command", nargs="?",
                    choices=("select", "armed-base", "armed-state", "control-first", "control-announcement", "control-line",
                             "control-lag", "first-instance"))
    ap.add_argument("--manifests", type=Path, help="armed-base/armed-state: memex-portal-ai manifest metadata (JSON list)")
    ap.add_argument("--control-manifests", type=Path, help="control-first: memex-control manifest metadata (JSON list)")
    ap.add_argument("--version", default="", help="control-first/control-announcement: THIS run's accepted platform version")
    ap.add_argument("--image-repository", default="",
                    help="control-announcement: the registry/repository the control image was tagged on (no tag)")
    ap.add_argument("--instance", default="", help="control-announcement: the announcer, for the audit trail (the run URL)")
    ap.add_argument("--deployment", default="",
                    help="control-announcement: the record the build is handed to (default: control's, from "
                         "--control-instance); control-line: read THIS first instance (with --instance-url)")
    ap.add_argument("--instance-url", default="",
                    help="control-line: the first instance's public base URL (default: control's)")
    ap.add_argument("--control-instance", type=Path, default=Path(".github/control-instance.json"),
                    help="select/control-lag: the declaration of which instance is control")
    # 🚨 Policy `major-lines-run-separately`: the delivery LINE (`main`, `main3`) and its MAJOR (read
    # by main-cd from PlatformVersion at the line's commit — never a number written here). main-cd
    # passes both to every selection; omitting them is the single-line reading of a caller that
    # predates the lines (all majors, records of `main`).
    ap.add_argument("--major", type=int, default=None,
                    help="armed-state/armed-base/select/control-lag: only sets of this platform major count")
    ap.add_argument("--branch", default=None,
                    help="select: the delivery line whose main-cd runs hold the promotion records (default main)")
    # 🚨 control-lag reads the line CONTROL follows, never `main` by default (MeshWeaver#8197): control
    # ran the main3 cherry-pick of a commit built on main before the split and read "behind" it.
    ap.add_argument("--control-record", type=Path, default=None,
                    help="control-lag/select: control's Hosting/Deployment record (JSON); its `updatePattern` names "
                         "the major. control-lag compares control with the declared line of that major; select "
                         "reports a line of ANOTHER major as held by that declaration, never as a frozen control")
    ap.add_argument("--delivery-lines", type=Path, default=Path(".github/delivery-lines.json"),
                    help="control-lag: the declared delivery lines (.github/delivery-lines.json)")
    ap.add_argument("--armed-max", type=int, default=0,
                    help="run number of the newest ARMED set (memex-portal-ai's newest version tag)")
    ap.add_argument("--override", default="", help="arm exactly this set name, platform verdict or not")
    ap.add_argument("--resume", default="",
                    help="the newest armed set whose arming never completed; re-armed when nothing newer is green")
    ap.add_argument("--armed-version", default="", help="select: the newest ARMED set's name, for --unarmed-out")
    ap.add_argument("--unarmed-out", type=Path, default=None,
                    help="select: write every promoted set newer than the armed one, with its verdict "
                         "(read by fleet-unarmed-alert.py)")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        ap.error("a command is required")
    if a.command == "armed-state":
        if a.manifests is None:
            ap.error("armed-state needs --manifests")
        try:
            rows = arming_cursor(json.loads(a.manifests.read_text()), major=a.major)
        except (OSError, ValueError) as e:
            print(f"::error::the arming cursor cannot be read: {e}")
            return 1
        if rows is None:
            print("::error::memex-portal-ai carries no version tag at all — refusing to treat that as 'nothing armed' (it would arm anything)")
            return 1
        if rows["cold_start"] == "true":
            print(f"::notice title=Cold start of major {a.major}::no set of major {a.major} has been armed yet (the fleet is "
                  f"armed on another major) — the newest green {a.major}.x set is armed through the ordinary verdict")
        _write_rows(rows)
        print(json.dumps(rows))
        return 0
    if a.command == "control-first":
        if a.control_manifests is None or not a.version:
            ap.error("control-first needs --control-manifests and --version")
        try:
            rows = control_first(json.loads(a.control_manifests.read_text()), a.version)
        except (OSError, ValueError) as e:
            print(f"::error::the control image's next version cannot be decided: {e}")
            return 1
        _write_rows(rows)
        print(json.dumps(rows))
        return 0
    if a.command == "control-line":
        # Reads control's PUBLIC /api/version (no credential) from the url `.github/control-instance.json`
        # declares, and decides whether this line may hand its build to control (major-lines-run-separately).
        if not a.version:
            ap.error("control-line needs --version")
        try:
            if a.instance_url:
                if not a.instance_url.startswith("https://") or not a.deployment:
                    ap.error("control-line --instance-url needs an https:// URL and --deployment (whose it is)")
                base, who = a.instance_url, a.deployment
            else:
                base, who = str(load_control_instance(a.control_instance)["url"]), "control"
            url = f"{base.rstrip('/')}/api/version"
            code, body = _fetch_public(url)
            rows = control_line(a.version, code, body, who=who)
        except (OSError, ValueError) as e:
            print(f"::error::cannot decide whether {a.version} is on control's line: {e}")
            return 1
        _write_rows(rows)
        print(json.dumps(rows))
        return 0
    if a.command == "control-announcement":
        # Offline and pure: prints the ONE body the workflow signs and POSTs — compact, so the bytes
        # signed are the bytes sent. The deployment is the one `.github/control-instance.json` names.
        if not a.version or not a.image_repository:
            ap.error("control-announcement needs --version and --image-repository")
        from datetime import datetime, timezone
        try:
            ci = load_control_instance(a.control_instance)
            body = control_announcement(a.deployment or str(ci.get("deployment") or ""), a.version, a.image_repository,
                                        a.instance, datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
        except (OSError, ValueError) as e:
            print(f"::error::the control announcement cannot be composed: {e}", file=sys.stderr)
            return 1
        print(json.dumps(body, separators=(",", ":")))
        return 0
    if a.command == "first-instance":
        # Offline and pure: which instance runs line --branch first, and where its record is
        # (policy line-first-instance). The workflow reads that record with the fleet-reader App.
        if not a.branch:
            ap.error("first-instance needs --branch")
        try:
            cd = _cd_line()
            first = line_first_instance(cd.by_branch(cd.load(a.delivery_lines)).get(a.branch),
                                        load_control_instance(a.control_instance), a.branch)
        except (OSError, ValueError) as e:
            print(f"::error::line {a.branch}'s first instance cannot be named: {e}")
            return 1
        rows = {"deployment": first["deployment"], "name": first["name"], "declared": str(first["declared"]).lower(),
                "none": str(first["none"]).lower(), "image_repository": first["image_repository"],
                "record_path": first["record_path"], "given_prefix": first["given_prefix"],
                "sentence": no_first_instance_sentence(first) if first["none"] else ""}
        _write_rows(rows)
        print(json.dumps(rows))
        return 0
    if a.command == "armed-base":
        # Offline and pure: the caller reads the registry; this only interprets. Exit 1 = cannot say.
        if a.manifests is None:
            ap.error("armed-base needs --manifests")
        try:
            got = armed_commit(json.loads(a.manifests.read_text()), major=a.major)
        except (OSError, ValueError) as e:
            print(f"::error::the newest armed set cannot be named: {e}")
            return 1
        rows = {"armed_version": got[0], "armed_short": got[1]} if got else {"armed_version": "", "armed_short": ""}
        _write_rows(rows)
        print(json.dumps(rows))
        return 0
    core_token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN", "")
    if not core_token:
        print("::error::GH_TOKEN is empty — the promotion records cannot be read; refusing to decide on nothing")
        return 1
    try:
        ci = load_control_instance(a.control_instance)
    except (OSError, ValueError) as e:
        print(f"::error::which instance is control cannot be read: {e}")
        return 1
    if a.command == "control-lag":
        # 🚨 PER LINE (MeshWeaver#8197, policy `major-lines-run-separately`): control is compared ONLY
        # with builds of the line its record's `updatePattern` names. Ancestry across lines means
        # nothing — a main3 cherry-pick does not contain its main original — so a reading over `main`
        # called a control on the newest v3 build "behind". The record is required: there is no
        # default line, and an explicit --branch/--major beside it would be a second declaration.
        if a.control_record is None:
            print("::error::control-lag needs --control-record (control's Hosting/Deployment record): which line "
                  "control follows is declared by its updatePattern and is never assumed to be `main`")
            return 1
        if a.branch is not None or a.major is not None:
            print("::error::control-lag derives its line from --control-record; --branch/--major beside it would be a "
                  "second declaration of the same fact")
            return 1
        try:
            pattern = record_update_pattern(json.loads(a.control_record.read_text()))
            major = pattern_major(pattern)
            line = line_of_major(declared_line_majors(a.delivery_lines, core_token), major)
        except (OSError, ValueError, RuntimeError) as e:
            print(f"::error::control's line cannot be derived: {e}")
            return 1
        print(f"control follows major {major} (record updatePattern {pattern!r}) — the declared line {line}")
        try:
            records = read_records(http_get, core_token, above=0, branch=line, major=major)
        except RuntimeError as e:
            print(f"::error::{e}")
            return 1
        control = read_control(ci["url"], http_get, core_token, [])
        given = given_to_control(
            records, control, lambda r: read_run_jobs(http_get, core_token, r.get("run_id")),
            lambda sha: contains_commit(http_get, core_token, control["commit"], sha))
        newest = given[0] if given else None
        import time
        state, text = control_lag(given, control, time.time(), int(ci["platformLagBoundMinutes"]))
        text = f"[line {line}, major {major} — record updatePattern `{pattern}`] {text}"
        rows = {"state": state, "sentence": text, "control": ci["url"], "running": control.get("commit", ""),
                "newest": newest["v_portal"] if newest else "", "line": line, "major": major}
        _write_rows(rows)
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a") as f:
                f.write(f"### control-always-latest — platform: **{state}**\n\n{text}\n")
        print(f"control-always-latest (platform): {state} — {text}")
        return 0
    branch = a.branch or "main"
    # 🚨 WHICH INSTANCE RUNS THIS LINE FIRST (policy `line-first-instance`, MeshWeaver#8365): the line's
    # declared `firstInstance`, else control. Everything below — the hold, the reading, the given
    # clock, the frozen red — is of THAT instance, so a 3.x line is never judged on a 4.x control.
    try:
        cd = _cd_line()
        first = line_first_instance(cd.by_branch(cd.load(a.delivery_lines)).get(branch), ci, branch)
    except (OSError, ValueError) as e:
        print(f"::error::line {branch}'s first instance cannot be named: {e}")
        return 1
    # 🚨 Is this line HELD by a declaration (MeshWeaver#8279)? Read off the FIRST INSTANCE's record
    # (`--control-record`: control's for a line that declares none). Required: a selection that cannot
    # tell a declared hold from a frozen instance would turn CD red on a deliberate decision (or, read
    # the other way, pass silently).
    if first["none"]:
        # 🚨 Policy `line-no-first-instance`: the DECLARATION decides, never the arguments. A record
        # passed for a line that declares none is a second, contradicting declaration — RED, by name.
        if a.control_record is not None:
            print(f"::error::line {branch} declares firstInstance \"{FIRST_INSTANCE_NONE}\" "
                  "(.github/delivery-lines.json), but --control-record was passed — no record is read for a line "
                  "with no first instance; two declarations of one fact are refused")
            return 1
    elif a.control_record is None:
        print(f"::error::select needs --control-record ({first['name']}'s Hosting/Deployment record, line {branch}'s "
              "first instance): whether its declared updatePattern admits this line's major decides between a declared "
              "hold and a frozen instance")
        return 1
    no_first = no_first_instance_sentence(first) if first["none"] else ""
    hold: str | None = None
    if no_first:
        first_url = ""
        print(f"line {branch}'s first instance: NONE, declared — {no_first}")
        print(f"::notice title=Line {branch} declares no first instance::{no_first}")
    else:
        try:
            first_record = json.loads(a.control_record.read_text())
            first_url = first_instance_url(first, ci, first_record)
            control_pattern = record_update_pattern(first_record)
            hold = first_instance_hold(first, first_record, a.major)
        except (OSError, ValueError) as e:
            print(f"::error::{first['name']}'s declaration (line {branch}'s first instance) cannot be read off its record: {e}")
            return 1
        print(f"line {branch}'s first instance: {first['name']} ({'declared' if first['declared'] else 'control, the default'}) "
              f"at {first_url}; its record declares updatePattern {control_pattern!r}; major "
              f"{a.major if a.major is not None else 'unnamed'}: {'HELD by a declaration' if hold else 'admitted'}")
    try:
        above = 0 if a.override else (a.armed_max - 1 if a.resume else a.armed_max)
        # The alert's reading must reach the armed cursor, so it pages on (bounded) past the
        # promotion-free reconcile ticks; an override names one set and needs no window.
        coverage: dict = {}
        cold = cold_start_of(a.major, a.armed_max, a.armed_version, a.override)
        records = read_records(http_get, core_token, above=above, coverage=coverage,
                               max_pages=UNARMED_MAX_PAGES if a.unarmed_out is not None and not a.override else 1,
                               branch=branch, major=a.major, major_start=cold)
    except RuntimeError as e:
        print(f"::error::{e}")
        return 1
    jobs = {int(r["run_number"]): read_run_jobs(http_get, core_token, r.get("run_id")) for r in records[:10]}
    ladders = {n: ladder_of(j) for n, j in jobs.items()}
    if no_first:
        # Nothing to read: no instance runs this line first. `judge` reads the declaration instead.
        control = {"commit": "", "healthy": False, "contains": {}, "why": "the line declares no first instance",
                   "name": FIRST_INSTANCE_NONE, "line": branch, "no_first_instance": no_first}
    else:
        control = read_control(first_url, http_get, core_token, [r["core_sha"] for r in records[:10]])
        control["name"], control["line"] = first["name"], branch
        print(f"{first['name']} {first_url}: running {control.get('commit') or 'UNKNOWN'}; "
              f"/health {'200' if control.get('healthy') else 'NOT 200'}{' — ' + control['why'] if control.get('why') else ''}; "
              f"{describe_self_update(control)}")
    if hold:
        control["line_hold"] = hold
    try:
        rec, lines = select(records, ladders, control, a.armed_max, a.override, a.resume)
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    import time
    # 🚨 Policy `control-first-never-silent`: `nothing to arm` because control has been behind past
    # the bound is a RED, never a green wait (see `arming_frozen`). Read off the SAME runs' jobs.
    def jobs_of(r: dict) -> list[dict] | None:
        n = int(r["run_number"])
        if n not in jobs:
            jobs[n] = read_run_jobs(http_get, core_token, r.get("run_id"))
        return jobs[n]

    def contains_of(sha: str) -> bool | None:
        if not control.get("commit"):
            return None
        if sha not in control["contains"]:
            control["contains"][sha] = contains_commit(http_get, core_token, control["commit"], sha)
        return control["contains"][sha]

    # Only when nothing was selected: the walk then reads jobs and compares beyond the ten sets
    # `select` judged, back to the first build control contains — the clock's true start.
    # A HELD line reads no lag walk: control was never going to take its builds (MeshWeaver#8279).
    # A line that DECLARES no first instance has no lag clock: nothing was handed a build.
    given = ([] if rec is not None or a.override or hold or no_first
             else given_builds(records, jobs_of, contains_of, first["given_prefix"]))
    frozen = arming_frozen(rec, a.override, given, control, time.time(), int(ci["platformLagBoundMinutes"]),
                           hold=hold)
    if frozen:
        lines.append(frozen)
    if hold and rec is None:
        lines.append(hold)
    for line in lines:
        print(line)
    write_outputs(rec, lines)
    if a.unarmed_out is not None:
        # Written BEFORE a frozen exit, so the alert reads the very verdicts this run decided on.
        cursor = int(rec["run_number"]) if rec else a.armed_max
        a.unarmed_out.write_text(json.dumps({
            "armed": armed_name(rec, a.armed_version, a.armed_max, a.major, cold),
            "armed_run": cursor,
            # 🚨 The LINE this reading is of (MeshWeaver#8208): fleet-unarmed-alert.py keeps ONE issue
            # per major, opened and closed only by that line's own arm run — v4's cold start and v3's
            # armed fleet are two different facts and must never share (and flap) one issue.
            "major": a.major,
            "line": branch,
            # No set of `major` was ever armed: `armed` says so in words, and the window is complete
            # once the listing passed the line's first set of the major (read_records `major_start`).
            "cold_start": cold,
            # False when the bounded listing never got down to the armed cursor: the list below is
            # then a FLOOR, and the alert clocks the stall from `window_floor_at` at least.
            "window_complete": bool(coverage.get("reached")) or a.override != "",
            "window_floor_at": coverage.get("floor_at", ""),
            # The line is HELD by control's declaration (MeshWeaver#8279): the alert says so above its
            # table, so a held major's issue names the declaration that ends the hold. "" = not held.
            "control_hold": hold or "",
            # The line DECLARES no first instance (policy `line-no-first-instance`): the alert says so
            # above its table, so a stall there can only be the ladder's. "" = the line has one.
            "no_first_instance": no_first,
            "unarmed": unarmed_sets(records, ladders, control, cursor)}, indent=1))
    print(f"ARM {rec['v_portal']} ({rec['key']})" if rec else "nothing to arm")
    if hold and rec is None:
        print(f"::warning title=Line {branch} held by a declaration::{hold}")
    if frozen:
        print(f"::error title=Fleet delivery frozen on {first['name']} (line {branch})::{frozen}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
