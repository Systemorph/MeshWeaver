#!/usr/bin/env python3
"""arm-promoted-set.py — THE ONE PROMOTION GATE: which promoted set may the fleet roll to?

    python3 .github/scripts/arm-promoted-set.py select --armed-max N [--override SET] [--resume SET]
    python3 .github/scripts/arm-promoted-set.py armed-state --manifests portal.json   # the cursor
    python3 .github/scripts/arm-promoted-set.py control-first --control-manifests control.json --version V
    python3 .github/scripts/arm-promoted-set.py control-lag        # policy control-always-latest (platform half)
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

CONTROL FIRST. `control-first` decides the control image's tags for THIS run's accepted build:
every platform build that passed the control image's own acceptance is tagged
`memex-control:<version>` (+ line pointers, never backwards) in the run that built it — before,
and independent of, any arming. `control-lag` is the alarm on the other end: control not running
the newest build it was given, past the bound in `.github/control-instance.json`, is RED.

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

CORE = "Systemorph/MeshWeaver"
API = "https://api.github.com"
WORKFLOW = "main-cd.yml"
RECORD_ARTIFACT = "promotion-record"
RUNS_EXAMINED = 40
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

Get = Callable[[str, str], tuple[int, object]]


def verdict_key(short: str, plugins_short: str) -> str:
    """The verdict ref's key for a pair — the pair tag, prefixed. Deterministic, so any run (and the
    Plugins poller) finds the same ref for the same image without being told."""
    return f"pair-{short}-p{plugins_short}"


def run_number_of(version: str) -> int | None:
    m = SET_NAME.match(version or "")
    return int(m.group(1)) if m else None


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


def armed_state(manifests: object) -> tuple[int, str, bool] | None:
    """(run number, version, complete) of the newest ARMED set, or None when nothing was ever armed.
    `complete` = its manifest also carries `arm-complete-<version>`, which `notify-platform-update`
    writes only after the release event was delivered. The marker is not version-shaped, so no
    self-updater can ever select it. Pure."""
    got = armed_commit(manifests, require_sha=False)
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


def armed_commit(manifests: object, require_sha: bool = True) -> tuple[str, str] | None:
    """(the newest ARMED version, its seven-character core sha) from memex-portal-ai's manifest
    metadata (`az acr manifest list-metadata -o json`), or None when no manifest carries a version
    tag at all (nothing was ever armed). Pure.

    The version tag is written by `arm` onto the promoted image's manifest, FROM its staging tag
    `staging-<core7>-<run id>`. `promote` also writes the bare core sha tag, but a rebuild of the SAME
    core commit (a host-relevant MeshWeaver.Plugins change, or an operator's `rebuild`) moves that tag
    to the later manifest. The staging tag stays on the armed manifest and still names its core. Sets
    armed before the pair tag was retired are read through their legacy `<core7>-p<plugins7>` tag.
    Conflicting or missing identities are errors, never guesses (a wrong base narrows the bundle)."""
    if not isinstance(manifests, list):
        raise ValueError("the manifest metadata is not a JSON list")
    best: tuple[int, str, list[str]] | None = None
    for row in manifests:
        tags = row.get("tags") if isinstance(row, dict) else None
        if not isinstance(tags, list):
            continue
        for t in tags:
            n = run_number_of(str(t))
            if n is not None and (best is None or n > best[0]):
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

    Returns {"action", "version", "move_pointers", "control_newest"}:
      * `done` — memex-control already carries `version` (idempotent re-run);
      * `tag`  — write the version tag, and the line pointers when `move_pointers` (never backwards:
                 a NEWER control version already tagged keeps the pointers where they are).
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
    if version in control_tags:
        return {"action": "done", "version": version, "move_pointers": False, "control_newest": newest}
    return {"action": "tag", "version": version,
            "move_pointers": not numbered or n_self >= max(numbered)[0], "control_newest": newest}


# ───────────────────────────────── transport ──────────────────────────────────────────────

class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):  # noqa: D401 — hand the 302 back to the caller
        return None


def http_get(url: str, token: str, raw: bool = False) -> tuple[int, object]:
    """GET over REST. A RAW download (an artifact zip) is a 302 to blob storage, which must be
    fetched WITHOUT the bearer — so the redirect is read here and followed unauthenticated."""
    req = urllib.request.Request(url if url.startswith("http") else f"{API}/{url}", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28"})
    opener = urllib.request.build_opener(_NoRedirect) if raw else urllib.request.build_opener()
    try:
        with opener.open(req, timeout=60) as r:
            body = r.read()
            return r.status, (body if raw else json.loads(body.decode() or "null"))
    except urllib.error.HTTPError as e:
        if raw and e.code in (301, 302, 303, 307, 308) and e.headers.get("Location"):
            try:
                with urllib.request.urlopen(e.headers["Location"], timeout=120) as r:
                    return r.status, r.read()
            except (urllib.error.URLError, OSError) as e2:
                return 0, str(e2)
        return e.code, None
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, str(e)


def read_records(get: Callable[..., tuple[int, object]], core_token: str,
                 above: int = 0, log: Callable[[str], None] = print) -> list[dict]:
    """Promotion records of main-cd runs on main numbered ABOVE `above`, newest first."""
    code, page = get(f"repos/{CORE}/actions/workflows/{WORKFLOW}/runs?branch=main&per_page={RUNS_EXAMINED}", core_token)
    if code != 200 or not isinstance(page, dict):
        raise RuntimeError(f"cannot list {WORKFLOW} runs (HTTP {code}) — refusing to decide on nothing")
    out: list[dict] = []
    for run in page.get("workflow_runs", []):
        number = int(run.get("run_number", 0))
        if number <= above:
            continue
        code, arts = get(f"repos/{CORE}/actions/runs/{run['id']}/artifacts?name={RECORD_ARTIFACT}", core_token)
        if code != 200 or not isinstance(arts, dict):
            log(f"  run #{number}: artifacts unreadable (HTTP {code}) — skipped")
            continue
        found = [a for a in arts.get("artifacts", []) if a.get("name") == RECORD_ARTIFACT and not a.get("expired")]
        if not found:
            continue          # not a promoting run (reconcile, superseded, or still before promote)
        code, blob = get(found[0]["archive_download_url"], core_token, raw=True)  # type: ignore[call-arg]
        if code != 200 or not isinstance(blob, (bytes, bytearray)):
            log(f"  run #{number}: record download failed (HTTP {code}) — skipped")
            continue
        try:
            with zipfile.ZipFile(io.BytesIO(blob)) as z:
                rec = json.loads(z.read(z.namelist()[0]).decode())
        except (zipfile.BadZipFile, IndexError, ValueError, KeyError) as e:
            log(f"  run #{number}: record unreadable ({e}) — skipped")
            continue
        why = check_record(rec)
        if why:
            log(f"  run #{number}: record refused — {why}")
            continue
        rec["run_url"] = run.get("html_url", "")
        rec["run_id"] = run.get("id")
        out.append(rec)
    out.sort(key=lambda r: int(r["run_number"]), reverse=True)
    return out


def read_run_jobs(get: Get, core_token: str, run_id: object) -> list[dict] | None:
    """Every job of one main-cd run (REST, one page of 100 — main-cd has fewer jobs than that, the
    matrix legs included), or None when the run is unreadable."""
    code, page = get(f"repos/{CORE}/actions/runs/{run_id}/jobs?per_page=100&filter=latest", core_token)
    if code != 200 or not isinstance(page, dict):
        return None
    return [j for j in page.get("jobs", []) if isinstance(j, dict)]


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


def control_given_at(jobs: list[dict] | None) -> str | None:
    """When the run's `Deploy control first` job SUCCEEDED (ISO timestamp), else None. Pure."""
    for j in jobs or []:
        if str(j.get("name", "")).startswith(CONTROL_FIRST_JOB_PREFIX) and j.get("conclusion") == "success":
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
    hcode, _ = fetch(f"{url.rstrip('/')}/health")
    out["healthy"] = hcode == 200
    if hcode != 200:
        out["why"] = f"{url}/health answered {hcode}"
    for sha in dict.fromkeys(core_shas):
        code, cmp = get(f"repos/{CORE}/compare/{sha}...{commit}", core_token)
        out["contains"][sha] = (cmp.get("status") in CONTAINED) if code == 200 and isinstance(cmp, dict) else None
    return out


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
    commit = control.get("commit") or ""
    if not commit:
        return "waiting", f"the control instance's running build is unknown ({control.get('why') or 'not read'})"
    contains = (control.get("contains") or {}).get(rec["core_sha"])
    if contains is None:
        return "waiting", f"could not establish whether control's {commit[:9]} contains {rec['core_sha'][:9]}"
    if not contains:
        return "waiting", (f"control runs {commit[:9]}, which does not contain {rec['core_sha'][:9]} yet — "
                           "control first: the fleet is offered a build only after control runs it")
    if not control.get("healthy"):
        return "waiting", (f"control runs {commit[:9]} (contains this set) but /health is not 200 "
                           f"({control.get('why') or 'unhealthy'}) — not offered to the fleet until control is healthy")
    return "green", f"ladder success; control runs {commit[:9]} (contains it) and is healthy"


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


def control_lag(newest: dict | None, control: dict, now: float, bound_minutes: int) -> tuple[str, str]:
    """Policy `control-always-latest`, the PLATFORM half: is the control instance on the newest
    platform build it was given? Pure. Returns (state, sentence): `ok`, `converging` or `lag`.

    `newest` is the newest promoted set whose `Deploy control first` job SUCCEEDED, carrying
    `core_sha`, `v_portal` and `given_at` (epoch seconds the control image was tagged with it).
    `control` is the same reading `judge` uses. RED (`lag`) when control does not contain that
    build and it was given more than `bound_minutes` ago — or when control's running build cannot
    be read at all (an instance that cannot say what it runs cannot be shown to be latest; silence
    is never a pass). Within the bound it is `converging`, said out loud. Control ON the newest build
    but with `/health` not 200 is `lag` too: `judge` offers the fleet nothing from an unhealthy
    control, so that state blocks every later arming and must be red, never an `ok` that closes the
    issue (review on MeshWeaver#6143)."""
    if newest is None:
        return "lag", ("no promoted set among the examined runs has a successful `Deploy control first` job — "
                       "nothing proves control was given a build")
    commit = control.get("commit") or ""
    if not commit:
        return "lag", (f"control's running build cannot be read ({control.get('why') or 'no answer'}) — "
                       f"it cannot be shown to be on {newest['v_portal']}")
    contains = (control.get("contains") or {}).get(newest["core_sha"])
    age_min = int(max(0.0, now - float(newest["given_at"])) // 60)
    if contains is True and not control.get("healthy"):
        return "lag", (f"control runs {commit[:9]}, which contains the newest platform build {newest['v_portal']}, "
                       f"but /health is not 200 ({control.get('why') or 'unhealthy'}) — the fleet is offered nothing "
                       "until control is healthy (policy platform-deploy-control-first)")
    if contains is True:
        return "ok", (f"control runs {commit[:9]}, which contains the newest platform build "
                      f"{newest['v_portal']} ({newest['core_sha'][:9]})")
    if contains is None:
        return "lag", (f"could not establish whether control's {commit[:9]} contains {newest['v_portal']} "
                       f"({newest['core_sha'][:9]}) — not shown to be latest")
    if age_min <= bound_minutes:
        return "converging", (f"control runs {commit[:9]}; {newest['v_portal']} ({newest['core_sha'][:9]}) was given "
                              f"{age_min} min ago, inside the {bound_minutes} min bound")
    return "lag", (f"control runs {commit[:9]}, which does NOT contain the newest platform build {newest['v_portal']} "
                   f"({newest['core_sha'][:9]}) given {age_min} min ago — over the {bound_minutes} min bound "
                   "(policy control-always-latest)")


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
                    choices=("select", "armed-base", "armed-state", "control-first", "control-lag"))
    ap.add_argument("--manifests", type=Path, help="armed-base/armed-state: memex-portal-ai manifest metadata (JSON list)")
    ap.add_argument("--control-manifests", type=Path, help="control-first: memex-control manifest metadata (JSON list)")
    ap.add_argument("--version", default="", help="control-first: THIS run's accepted platform version")
    ap.add_argument("--control-instance", type=Path, default=Path(".github/control-instance.json"),
                    help="select/control-lag: the declaration of which instance is control")
    ap.add_argument("--armed-max", type=int, default=0,
                    help="run number of the newest ARMED set (memex-portal-ai's newest version tag)")
    ap.add_argument("--override", default="", help="arm exactly this set name, platform verdict or not")
    ap.add_argument("--resume", default="",
                    help="the newest armed set whose arming never completed; re-armed when nothing newer is green")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        ap.error("a command is required")
    if a.command == "armed-state":
        if a.manifests is None:
            ap.error("armed-state needs --manifests")
        try:
            st = armed_state(json.loads(a.manifests.read_text()))
        except (OSError, ValueError) as e:
            print(f"::error::the arming cursor cannot be read: {e}")
            return 1
        if st is None:
            print("::error::memex-portal-ai carries no version tag at all — refusing to treat that as 'nothing armed' (it would arm anything)")
            return 1
        rows = {"max": str(st[0]), "version": st[1], "complete": "true" if st[2] else "false"}
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
    if a.command == "armed-base":
        # Offline and pure: the caller reads the registry; this only interprets. Exit 1 = cannot say.
        if a.manifests is None:
            ap.error("armed-base needs --manifests")
        try:
            got = armed_commit(json.loads(a.manifests.read_text()))
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
        try:
            records = read_records(http_get, core_token, above=0)
        except RuntimeError as e:
            print(f"::error::{e}")
            return 1
        newest = None
        for r in records:
            given = control_given_at(read_run_jobs(http_get, core_token, r.get("run_id")))
            if given:
                newest = {"core_sha": r["core_sha"], "v_portal": r["v_portal"], "given_at": _iso_epoch(given)}
                break
        control = read_control(ci["url"], http_get, core_token, [newest["core_sha"]] if newest else [])
        import time
        state, text = control_lag(newest, control, time.time(), int(ci["platformLagBoundMinutes"]))
        rows = {"state": state, "sentence": text, "control": ci["url"], "running": control.get("commit", ""),
                "newest": newest["v_portal"] if newest else ""}
        _write_rows(rows)
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a") as f:
                f.write(f"### control-always-latest — platform: **{state}**\n\n{text}\n")
        print(f"control-always-latest (platform): {state} — {text}")
        return 0
    try:
        above = 0 if a.override else (a.armed_max - 1 if a.resume else a.armed_max)
        records = read_records(http_get, core_token, above=above)
    except RuntimeError as e:
        print(f"::error::{e}")
        return 1
    ladders = {int(r["run_number"]): ladder_of(read_run_jobs(http_get, core_token, r.get("run_id"))) for r in records[:10]}
    control = read_control(ci["url"], http_get, core_token, [r["core_sha"] for r in records[:10]])
    print(f"control {ci['url']}: running {control.get('commit') or 'UNKNOWN'}; "
          f"/health {'200' if control.get('healthy') else 'NOT 200'}{' — ' + control['why'] if control.get('why') else ''}")
    try:
        rec, lines = select(records, ladders, control, a.armed_max, a.override, a.resume)
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    for line in lines:
        print(line)
    write_outputs(rec, lines)
    print(f"ARM {rec['v_portal']} ({rec['key']})" if rec else "nothing to arm")
    return 0


if __name__ == "__main__":
    sys.exit(main())
