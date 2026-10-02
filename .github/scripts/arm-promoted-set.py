#!/usr/bin/env python3
"""arm-promoted-set.py — THE ONE PROMOTION GATE: which promoted set may the fleet roll to?

    python3 .github/scripts/arm-promoted-set.py select --armed-max N [--override SET] [--resume SET]
    python3 .github/scripts/arm-promoted-set.py armed-state --manifests portal.json   # the cursor
    python3 .github/scripts/arm-promoted-set.py pending            # MeshWeaver.Plugins' poller
    python3 .github/scripts/arm-promoted-set.py armed-base --manifests portal.json   # promote's record base
    python3 .github/scripts/arm-promoted-set.py control-follow --manifests portal.json --control-manifests control.json
    python3 .github/scripts/arm-promoted-set.py --self-test

Policy `one-promotion-gate` (register: Doc/Architecture/PolicyNotProse). Design of record:
Doc/Architecture/OnePromotionGate.

WHAT A SET IS. main-cd builds core main HEAD together with MeshWeaver.Plugins main HEAD as of its
`gate` job (the pair tag `<core7>-p<plugins7>` on memex-portal-ai names both). `promote` tags the
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
  * whose MeshWeaver.Plugins dependent-suites verdict for exactly that PAIR is `success` — the
    verdict `core-candidate.yml` writes at `refs/core-candidate/pair-<core7>-p<plugins7>` in
    MeshWeaver.Plugins, validated field by field (key, candidate, base, and the Plugins commit);
  * or the one set named by `--override` (a workflow_dispatch carrying its reason) — an
    instruction, so a set that is not promoted is a RED, never a substitution.

A set whose verdict is absent is WAITING, one whose verdict is red is REFUSED: neither is armed,
and a newer set with a green verdict supersedes both. Silence is never a pass.

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
several core merges (the batch window; `pending` supersedes older pairs instead of queueing them), and
every one of them reaches the fleet when the set is armed. So the record's `base` — the commit the
Plugins run diffs from and re-runs its control arm at, and the one the verdict is pinned to — is the
core commit of the newest ARMED set: `base..candidate` is then exactly the merges the fleet has not
seen. `armed-base` resolves it from memex-portal-ai's manifests (the armed version tag shares its
manifest with a core sha or pair tag). The record says which rule produced `base` in
`base_kind`:

  * `armed`         — the newest armed set's commit (the normal case);
  * `first-parent`  — no set has ever been armed, or the candidate IS the armed set: nothing wider
                      exists to measure, said out loud;
  * `unresolved`    — the armed set could not be read. The record keeps the first parent so CI still
                      promotes, but `select` REFUSES to arm it and `pending` does not measure it: a
                      narrower bundle than the truth would arm merges nobody measured. The next
                      promoted set tries again.

A record written before this rule carries no `base_kind` and is judged as before (legacy, first
parent), so a set promoted in the transition is never stranded.

`pending` is the other consumer's half: MeshWeaver.Plugins' `promotion-candidate.yml` asks it for
the newest promoted-but-unarmed pair that has NO verdict yet, and runs its suites against it. So a
burst of merges coalesces: the poller always takes the newest pair, and the older ones are simply
superseded — never queued.

"Does armed set X contain core commit C / Plugins commit P?" is answered by ANCESTRY against the
record's two commits — Memex `scripts/image-contains.py`, which reads the same record.

API budget: one runs page, one artifact listing per run examined (+1 download per record), two
reads per verdict ref. REST only (AGENTS.md: never GraphQL).
"""
from __future__ import annotations

import argparse
import importlib.util
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
PLUGINS = "Systemorph/MeshWeaver.Plugins"
API = "https://api.github.com"
WORKFLOW = "main-cd.yml"
RECORD_ARTIFACT = "promotion-record"
RUNS_EXAMINED = 40
RECORD_KEYS = ("run_number", "core_sha", "base", "plugins_sha", "short", "plugins_short", "staging",
               "v_portal", "v_migration", "v_plugin", "key")
SHA = re.compile(r"^[0-9a-f]{40}$")
SHORT_SHA_TAG = re.compile(r"^[0-9a-f]{7}$")
PAIR_TAG = re.compile(r"^([0-9a-f]{7})-p[0-9a-f]{7}$")
BASE_KINDS = ("armed", "first-parent", "unresolved")
SET_NAME = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?[.-]ci\.(\d+)$")

# The waiter's validation is THE definition of a valid verdict — imported, never restated.
_spec = importlib.util.spec_from_file_location(
    "await_dependent_verdict", Path(__file__).with_name("await-dependent-verdict.py"))
_adv = importlib.util.module_from_spec(_spec)  # type: ignore[arg-type]
_spec.loader.exec_module(_adv)  # type: ignore[union-attr]
validate_verdict = _adv.validate

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

    The version tag is written by `arm` onto the promoted image's manifest. `promote` writes both a
    core sha tag and a core/Plugins pair tag, but a later pair for the SAME core moves the sha tag
    to that later manifest. The pair tag remains on the armed manifest and still names its core.
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
    if len(shas) != 1:
        raise ValueError(f"the newest armed manifest {best[1]} names {len(shas)} distinct core commits "
                         f"({best[2]}) — cannot name its core commit")
    return best[1], next(iter(shas))


def control_follow(portal_manifests: object, control_manifests: object) -> dict:
    """What the CONTROL image (`memex-control`) must be told about the newest ARMED set. Pure.

    The control image is the same build as memex-portal-ai under another repository name
    (`-p:MemexControlImage=true`), so it follows the SAME promotion gate: `control-promote` writes
    only identity tags (the core sha and the `<core7>-p<plugins7>` pair), and a version tag — the
    thing a Continuous record with `updatePattern: 3.0.0-ci*` rolls to — and the line pointers are
    written only for a set `arm` armed on memex-portal-ai. The pair tag is the join: the armed
    version shares its manifest with exactly one pair tag, and the accepted control image of that
    pair carries the same pair tag (the bare sha tag is re-pointed by a rebuild, the pair is not).

    Returns {"action", "version", "pair", "move_pointers", "control_newest"}:
      * `none`    — nothing was ever armed;
      * `done`    — memex-control already carries the armed version;
      * `tag`     — its accepted image for the armed pair exists: tag the version (and the line
                    pointers when `move_pointers` — never backwards past a newer control version);
      * `missing` — no accepted control image for the armed pair (its control lane failed or has not
                    finished): the control image stays where it is, and the caller SAYS so.
    An armed manifest naming zero or several pairs is a ValueError — never a guess."""
    got = armed_commit(portal_manifests, require_sha=False)
    if got is None:
        return {"action": "none", "version": "", "pair": "", "move_pointers": False, "control_newest": ""}
    version = got[0]
    armed_tags: list[str] = []
    for row in portal_manifests:  # armed_commit validated it is a list
        t = row.get("tags") if isinstance(row, dict) else None
        if isinstance(t, list) and version in t:
            armed_tags = [str(x) for x in t]
            break
    pairs = sorted({t for t in armed_tags if PAIR_TAG.match(t)})
    if len(pairs) != 1:
        raise ValueError(f"the armed manifest {version} names {len(pairs)} pair tags ({armed_tags}) — "
                         f"cannot name the control image that belongs to it")
    if not isinstance(control_manifests, list):
        raise ValueError("the memex-control manifest metadata is not a JSON list")
    control_tags: set[str] = set()
    for row in control_manifests:
        t = row.get("tags") if isinstance(row, dict) else None
        if isinstance(t, list):
            control_tags.update(str(x) for x in t)
    numbered = [(n, t) for t in control_tags if (n := run_number_of(t)) is not None]
    newest = max(numbered)[1] if numbered else ""
    n_self = run_number_of(version) or 0
    if version in control_tags:
        action = "done"
    elif pairs[0] in control_tags:
        action = "tag"
    else:
        action = "missing"
    return {"action": action, "version": version, "pair": pairs[0],
            "move_pointers": action == "tag" and (not numbered or n_self >= max(numbered)[0]),
            "control_newest": newest}


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
        out.append(rec)
    out.sort(key=lambda r: int(r["run_number"]), reverse=True)
    return out


def read_verdict(get: Get, plugins_token: str, key: str) -> tuple[object | None, str]:
    code, ref = get(f"repos/{PLUGINS}/git/ref/core-candidate/{key}", plugins_token)
    if code == 404:
        return None, "no verdict yet"
    if code != 200 or not isinstance(ref, dict):
        return None, f"verdict ref unreadable (HTTP {code})"
    code, commit = get(f"repos/{PLUGINS}/git/commits/{ref['object']['sha']}", plugins_token)
    if code != 200 or not isinstance(commit, dict):
        return None, f"verdict commit unreadable (HTTP {code})"
    try:
        return json.loads(commit.get("message", "")), "found"
    except ValueError as e:
        return {"malformed": str(e)}, "found"


def judge(rec: dict, verdict: object | None, why: str) -> tuple[str, str]:
    """(state, sentence): state is `green`, `waiting` or `refused`. Pure."""
    if rec.get("base_kind") == "unresolved":
        return "refused", ("its bundle base could not be resolved when it was promoted (the newest armed set was "
                           "unreadable), so its verdict would cover fewer merges than the fleet would receive")
    if verdict is None:
        return ("waiting" if why == "no verdict yet" else "refused"), why
    ok, text = validate_verdict(verdict, rec["key"], rec["core_sha"], rec["base"])
    if ok and isinstance(verdict, dict) and verdict.get("pluginsSha") != rec["plugins_sha"]:
        return "refused", (f"the verdict tested Plugins {str(verdict.get('pluginsSha'))[:9]}, not the image's "
                           f"{rec['plugins_short']} — it is about a different pair")
    return ("green" if ok else "refused"), text


def select(records: list[dict], verdicts: dict[str, tuple[object | None, str]], armed_max: int,
           override: str = "", resume: str = "") -> tuple[dict | None, list[str]]:
    """(the record to arm or None, the report lines). Pure — the self-test drives it."""
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
        lines.append(f"OVERRIDE: arming {hit['v_portal']} ({hit['key']}) WITHOUT a dependent-suites verdict, as instructed")
        return hit, lines
    for rec in records:
        n = int(rec["run_number"])
        if n <= armed_max:
            lines.append(f"{rec['v_portal']} ({rec['key']}): not newer than the armed ci.{armed_max} — never backwards")
            continue
        state, text = judge(rec, *verdicts.get(rec["key"], (None, "not read")))
        lines.append(f"{rec['v_portal']} ({rec['key']}): {state} — {text}")
        if state == "green":
            return rec, lines
    if resume:
        # Nothing newer is green, and the newest ARMED set never completed its sequence (no
        # `arm-complete-<version>` marker): re-arm exactly it. Its verdict was green when it was
        # armed; the resume re-runs the idempotent tag writes and the event, and decides nothing new.
        hit = next((r for r in records if r["v_portal"] == resume), None)
        if hit is None:
            # No promotion record to resume from: a set armed by the pre-gate `promote` (which wrote
            # the pointers and the event itself, in the same job, before any marker existed), or one
            # whose record aged out of the window. Said out loud — never red, because a red here
            # would hold EVERY later arming run on a set that cannot be resumed at all.
            lines.append(f"RESUME NOT POSSIBLE: {resume} carries no `arm-complete` marker and has no promotion record "
                         f"among the newest {RUNS_EXAMINED} main-cd runs (armed before the marker existed, or aged out) "
                         "— the next newer green set completes the line")
            return None, lines
        lines.append(f"RESUME: {resume} ({hit['key']}) was armed but its sequence never completed — re-arming it")
        return hit, lines
    return None, lines


def pending(records: list[dict], verdicts: dict[str, tuple[object | None, str]], armed_max: int) -> dict | None:
    """The newest promoted, unarmed pair with NO verdict at all — what the Plugins poller runs. Pure.
    Only the NEWEST unarmed record is ever a candidate: an older one is superseded by construction."""
    newer = [r for r in records if int(r["run_number"]) > armed_max]
    if not newer:
        return None
    top = newer[0]
    if top.get("base_kind") == "unresolved":
        return None
    v, why = verdicts.get(top["key"], (None, "not read"))
    return top if v is None and why == "no verdict yet" else None


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

    counts = {"selected": 5, "universe": 83, "legs": 1, "drift": 0, "preExisting": 0, "missingEvidence": 0}

    def green(r: dict, **kw) -> tuple[object, str]:
        v = {"schema": 1, "key": r["key"], "candidate": r["core_sha"], "base": r["base"], "conclusion": "success",
             "summary": "5 of 83", "run": f"https://github.com/{PLUGINS}/actions/runs/1", "counts": counts,
             "pluginsSha": r["plugins_sha"]}
        v.update(kw)
        return v, "found"

    a, b, c = rec(9459, "a", "1"), rec(9460, "c", "2"), rec(9461, "d", "3")
    records = [c, b, a]
    check("a well-formed record is accepted", check_record(a) is None, str(check_record(a)))
    check("a record whose key is not its pair tag is refused", check_record({**a, "key": "pair-x"}) is not None)
    check("a record whose version does not carry its run number is refused",
          check_record({**a, "v_portal": "3.0.0-ci.1"}) is not None)
    check("a record with a short core sha is refused", check_record({**a, "core_sha": "abc"}) is not None)
    check("a record without its base is refused (the verdict is pinned to a base)",
          check_record({k: v for k, v in a.items() if k != "base"}) is not None)

    # latest green wins; a newer WAITING set is not armed, and does not hold back an older green one
    chosen, lines = select(records, {b["key"]: green(b), c["key"]: (None, "no verdict yet")}, armed_max=9458)
    check("the newest set with a green verdict is armed; a newer waiting one is not", chosen is b, "\n".join(lines))
    # red is refused, and the next older green is taken
    chosen, _ = select(records, {c["key"]: green(c, conclusion="failure"), b["key"]: green(b)}, armed_max=9458)
    check("a red verdict is refused and the next older green set is armed", chosen is b)
    # never backwards
    chosen, lines = select(records, {a["key"]: green(a)}, armed_max=9460)
    check("a green set OLDER than the armed one is never armed (never backwards)", chosen is None, "\n".join(lines))
    # silence is not a pass
    chosen, _ = select(records, {}, armed_max=0)
    check("no verdict anywhere arms nothing (silence is not a pass)", chosen is None)
    # a verdict about a different Plugins commit is refused
    chosen, lines = select([b], {b["key"]: green(b, pluginsSha="9" * 40)}, armed_max=0)
    check("a verdict that tested a different Plugins commit is refused", chosen is None, "\n".join(lines))
    chosen, _ = select([b], {b["key"]: green(b, candidate="e" * 40)}, armed_max=0)
    check("a verdict about a different core commit is refused", chosen is None)
    # override
    chosen, lines = select(records, {}, armed_max=9460, override="3.0.0-ci.9459")
    check("an override arms exactly the named set, verdict or not", chosen is a and "OVERRIDE" in lines[0])
    try:
        select(records, {}, armed_max=0, override="3.0.0-ci.1")
        check("an override naming no promoted set is RED", False)
    except ValueError:
        check("an override naming no promoted set is RED, never a substitution", True)
    try:
        select(records, {}, armed_max=0, override="3.0.1-ci.9459")
        check("an override naming the right RUN on another version line is RED", False)
    except ValueError:
        check("an override naming the right RUN on another version line is RED (exact version, never the run number)", True)
    # resume — the cursor moved at phase C but the sequence never finished
    chosen, lines = select(records, {}, armed_max=9460, resume="3.0.0-ci.9460")
    check("resume: nothing newer green → the incomplete newest armed set is re-armed", chosen is b and "RESUME" in lines[-1],
          "\n".join(lines))
    chosen, _ = select(records, {c["key"]: green(c)}, armed_max=9460, resume="3.0.0-ci.9460")
    check("resume: a NEWER green set still wins (arming it completes the line past the incomplete one)", chosen is c)
    chosen, lines = select(records, {}, armed_max=9460, resume="3.0.0-ci.9000")
    check("resume: an incomplete set with NO record (armed before the marker existed) is SAID, not red — "
          "a red would hold every later arming", chosen is None and "RESUME NOT POSSIBLE" in lines[-1], "\n".join(lines))
    # pending (the Plugins poller)
    check("pending: the newest unarmed pair without a verdict is returned",
          pending(records, {c["key"]: (None, "no verdict yet")}, armed_max=9458) is c)
    check("pending: a verdict ref that could not be READ is not 'no verdict' — nothing is re-run on doubt",
          pending(records, {c["key"]: (None, "verdict ref unreadable (HTTP 502)")}, armed_max=9458) is None)
    check("pending: a newest pair that HAS a verdict is not re-run (red or green)",
          pending(records, {c["key"]: green(c, conclusion="failure")}, armed_max=9458) is None)
    check("pending: an older pair is superseded — only the newest is ever a candidate",
          pending(records, {c["key"]: green(c)}, armed_max=9458) is None)
    check("pending: nothing newer than the armed set means nothing to run",
          pending(records, {}, armed_max=9461) is None)
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
    u = {**c, "base_kind": "unresolved"}
    check("a record with an unknown base_kind is refused", check_record({**a, "base_kind": "nope"}) is not None)
    check("an `armed` and a legacy (no base_kind) record are both accepted",
          check_record({**a, "base_kind": "armed"}) is None and check_record(a) is None)
    chosen, lines = select([u, b], {u["key"]: green(u), b["key"]: green(b)}, armed_max=9458)
    check("an UNRESOLVED base is never armed, even green — the next older green set is taken", chosen is b, "\n".join(lines))
    check("pending: an UNRESOLVED newest record is not measured (the next promotion retries)",
          pending([u, b, a], {u["key"]: (None, "no verdict yet")}, armed_max=9458) is None)
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
    armed_p = [{"tags": ["3.0.0-ci.9598", "0d14493", "0d14493-pc791b44", "arm-complete-3.0.0-ci.9598"]},
               {"tags": ["3.0.0-ci.9590", "5145540-pa5ef923"]}]
    cf = control_follow(armed_p, [{"tags": ["0d14493", "0d14493-pc791b44", "main"]},
                                  {"tags": ["3.0.0-ci.9590", "5145540", "3-latest"]}])
    check("control-follow: the accepted control image of the ARMED pair is tagged with its version",
          cf["action"] == "tag" and cf["version"] == "3.0.0-ci.9598" and cf["pair"] == "0d14493-pc791b44"
          and cf["move_pointers"] is True, str(cf))
    cf = control_follow(armed_p, [{"tags": ["3.0.0-ci.9598", "0d14493-pc791b44"]}])
    check("control-follow: already carrying the armed version is DONE (idempotent)", cf["action"] == "done", str(cf))
    cf = control_follow(armed_p, [{"tags": ["0d14493", "0d14493-p1111111"]}, {"tags": ["3.0.0-ci.9590"]}])
    check("control-follow: a control image of ANOTHER pair of the same core is not the armed one (MISSING)",
          cf["action"] == "missing" and cf["control_newest"] == "3.0.0-ci.9590", str(cf))
    cf = control_follow(armed_p, [{"tags": ["0d14493-pc791b44"]}, {"tags": ["3.0.0-ci.9601"]}])
    check("control-follow: never moves the line pointers backwards past a newer control version",
          cf["action"] == "tag" and cf["move_pointers"] is False, str(cf))
    check("control-follow: nothing armed is NONE", control_follow([{"tags": ["main"]}], [])["action"] == "none")
    try:
        control_follow([{"tags": ["3.0.0-ci.9598", "0d14493"]}], [])
        check("control-follow: an armed manifest with no pair tag is RED", False)
    except ValueError:
        check("control-follow: an armed manifest with no pair tag is RED, never a guess", True)
    print(f"arm-promoted-set self-test: {failures} failure(s)")
    return 1 if failures else 0


# ───────────────────────────────── main ───────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("command", nargs="?", choices=("select", "pending", "armed-base", "armed-state", "control-follow"))
    ap.add_argument("--manifests", type=Path, help="armed-base: memex-portal-ai manifest metadata (JSON list)")
    ap.add_argument("--control-manifests", type=Path, help="control-follow: memex-control manifest metadata (JSON list)")
    ap.add_argument("--armed-max", type=int, default=0,
                    help="run number of the newest ARMED set (memex-portal-ai's newest version tag)")
    ap.add_argument("--override", default="", help="arm exactly this set name, verdict or not")
    ap.add_argument("--resume", default="",
                    help="the newest armed set whose arming never completed; re-armed when nothing newer is green")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        ap.error("a command is required")
    if a.command == "armed-state":
        try:
            st = armed_state(json.loads(a.manifests.read_text())) if a.manifests else None
        except (OSError, ValueError) as e:
            print(f"::error::the arming cursor cannot be read: {e}")
            return 1
        if a.manifests is None:
            ap.error("armed-state needs --manifests")
        if st is None:
            print("::error::memex-portal-ai carries no version tag at all — refusing to treat that as 'nothing armed' (it would arm anything)")
            return 1
        rows = {"max": str(st[0]), "version": st[1], "complete": "true" if st[2] else "false"}
        out = os.environ.get("GITHUB_OUTPUT")
        if out:
            with open(out, "a") as f:
                f.writelines(f"{k}={v}\n" for k, v in rows.items())
        print(json.dumps(rows))
        return 0
    if a.command == "control-follow":
        if a.manifests is None or a.control_manifests is None:
            ap.error("control-follow needs --manifests and --control-manifests")
        try:
            rows = control_follow(json.loads(a.manifests.read_text()), json.loads(a.control_manifests.read_text()))
        except (OSError, ValueError) as e:
            print(f"::error::the control image's arming cannot be decided: {e}")
            return 1
        out = os.environ.get("GITHUB_OUTPUT")
        if out:
            with open(out, "a") as f:
                f.writelines(f"{k}={str(v).lower() if isinstance(v, bool) else v}\n" for k, v in rows.items())
        print(json.dumps(rows))
        return 0
    if a.command == "armed-base":
        # Offline and pure: the caller reads the registry; this only interprets. Exit 1 = cannot say.
        try:
            got = armed_commit(json.loads(a.manifests.read_text())) if a.manifests else None
        except (OSError, ValueError) as e:
            print(f"::error::the newest armed set cannot be named: {e}")
            return 1
        if a.manifests is None:
            ap.error("armed-base needs --manifests")
        rows = {"armed_version": got[0], "armed_short": got[1]} if got else {"armed_version": "", "armed_short": ""}
        out = os.environ.get("GITHUB_OUTPUT")
        if out:
            with open(out, "a") as f:
                f.writelines(f"{k}={v}\n" for k, v in rows.items())
        print(json.dumps(rows))
        return 0
    core_token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN", "")
    plugins_token = os.environ.get("PLUGINS_TOKEN") or core_token
    if not core_token:
        print("::error::GH_TOKEN is empty — the promotion records cannot be read; refusing to decide on nothing")
        return 1
    try:
        above = 0 if a.override else (a.armed_max - 1 if a.resume else a.armed_max)
        records = read_records(http_get, core_token, above=above)
    except RuntimeError as e:
        print(f"::error::{e}")
        return 1
    verdicts = {r["key"]: read_verdict(http_get, plugins_token, r["key"]) for r in records[:10]}
    if a.command == "pending":
        rec = pending(records, verdicts, a.armed_max)
        out = os.environ.get("GITHUB_OUTPUT")
        rows = {"due": "true" if rec else "false"}
        if rec:
            rows.update({k: str(rec[k]) for k in RECORD_KEYS})
        if out:
            with open(out, "a") as f:
                f.writelines(f"{k}={v}\n" for k, v in rows.items())
        print(json.dumps(rows, indent=1))
        return 0
    try:
        rec, lines = select(records, verdicts, a.armed_max, a.override, a.resume)
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
