#!/usr/bin/env python3
"""arm-promoted-set.py — THE ONE PROMOTION GATE: which promoted set may the fleet roll to?

    python3 .github/scripts/arm-promoted-set.py select --armed-max N [--override SET]
    python3 .github/scripts/arm-promoted-set.py pending            # MeshWeaver.Plugins' poller
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
    return None


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
    if verdict is None:
        return ("waiting" if why == "no verdict yet" else "refused"), why
    ok, text = validate_verdict(verdict, rec["key"], rec["core_sha"], rec["base"])
    if ok and isinstance(verdict, dict) and verdict.get("pluginsSha") != rec["plugins_sha"]:
        return "refused", (f"the verdict tested Plugins {str(verdict.get('pluginsSha'))[:9]}, not the image's "
                           f"{rec['plugins_short']} — it is about a different pair")
    return ("green" if ok else "refused"), text


def select(records: list[dict], verdicts: dict[str, tuple[object | None, str]], armed_max: int,
           override: str = "") -> tuple[dict | None, list[str]]:
    """(the record to arm or None, the report lines). Pure — the self-test drives it."""
    lines: list[str] = []
    if override:
        n = run_number_of(override)
        if n is None:
            raise ValueError(f"--override {override!r} is not a set name X.Y.Z[-pre]-ci.N")
        hit = next((r for r in records if int(r["run_number"]) == n), None)
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
    return None, lines


def pending(records: list[dict], verdicts: dict[str, tuple[object | None, str]], armed_max: int) -> dict | None:
    """The newest promoted, unarmed pair with NO verdict at all — what the Plugins poller runs. Pure.
    Only the NEWEST unarmed record is ever a candidate: an older one is superseded by construction."""
    newer = [r for r in records if int(r["run_number"]) > armed_max]
    if not newer:
        return None
    top = newer[0]
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
    print(f"arm-promoted-set self-test: {failures} failure(s)")
    return 1 if failures else 0


# ───────────────────────────────── main ───────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("command", nargs="?", choices=("select", "pending"))
    ap.add_argument("--armed-max", type=int, default=0,
                    help="run number of the newest ARMED set (memex-portal-ai's newest version tag)")
    ap.add_argument("--override", default="", help="arm exactly this set name, verdict or not")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        ap.error("a command is required")
    core_token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN", "")
    plugins_token = os.environ.get("PLUGINS_TOKEN") or core_token
    if not core_token:
        print("::error::GH_TOKEN is empty — the promotion records cannot be read; refusing to decide on nothing")
        return 1
    try:
        records = read_records(http_get, core_token, above=0 if a.override else a.armed_max)
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
        rec, lines = select(records, verdicts, a.armed_max, a.override)
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
