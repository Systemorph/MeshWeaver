#!/usr/bin/env python3
"""module-build-ledger.py — the fleet's CI MODULE BUILD LEDGER on the registry portal (Plugins#889, #931).

(The name on this first line is load-bearing: the module-pack lane fetches this file at the
platform pin and refuses a body whose first 400 bytes do not name it.)

WHY THIS EXISTS (maintainer, 2026-09-02: "we should not start the same build multiple times ⇒
coordinate which packages are in progress; track progress through memex")
------------------------------------------------------------------------------------------------
Every satellite calls the same reusable module-pack lane, and until this script every call built
every selected module from scratch — a PR run and the push-to-main run that followed it minutes later
compiled and tested the SAME bytes twice, two concurrent PRs touching the same module built it twice
at once, and a platform release rebuilt 31 bundles whether or not each one's inputs had moved. Nothing
recorded what had been built against what.

The ledger is one MeshNode per BUILD KEY (module-build-key.py: the content address of one module
build — package, moduleVersion, in-repo closure, both image digests, platform ref, recipe) at

    Admin/ModuleBuilds/<key>          nodeType ModuleBuild, content $type ModuleBuildRecord

on the registry portal (memex.meshweaver.cloud), written through its MCP endpoint (JSON-RPC over
HTTP at /mcp, `Authorization: Bearer <mw_ ApiToken>`) as a dedicated CI user holding a partition-admin
grant on exactly that root — never a global admin (Doc/Architecture/AccessControl → "The Admin partition").

THE PROTOCOL
------------
  claim      = CREATE the node. Creation fails on an existing path, so exactly one run holds a key;
               "already exists" is the follower's SUCCESS case — it then reads the holder's record.
               After every create the claimant re-reads the node and holds the key only if the record
               names ITS run: a claim you cannot read back is a claim you do not hold.
  heartbeat  = the holder proves it is alive. A claim whose heartbeat is older than STALE_AFTER (the
               fleet's 45-minute job cap) is dead by construction and may be taken over — a job that
               cannot heartbeat inside its own cap has been killed.
  reuse      = a terminal record (Built/Tested/Published) whose bundle artifact this run can fetch is
               not rebuilt: the pack job downloads that bundle, runs only the phases the record lacks
               (tests if this run needs a verdict, publish if this run publishes), and records them.
  wait       = a fresh, unfinished claim by ANOTHER run: the follower polls (30 s) until the holder
               finishes or goes stale, then reuses or takes over — the same key is never built twice
               at once.
  tolerance  = a Failed record BLOCKS a later run of the same key only when the same inputs give the
               same result: a COMPILE failure blocks; a TEST failure blocks from the second failed
               attempt on (one re-claim, so a flaky suite does not pin the fleet); a cancelled or
               aborted build never blocks. Blocked runs fail with the holder's evidence (run URL,
               phase, failure text) — never silently.

🚨 A read that could not reach a verdict (a 5xx, a timeout, an "Error: …" answer) is UNAVAILABLE, not
"absent": it is retried a bounded number of times honouring Retry-After, and then the lane proceeds AS IF
THERE WERE NO RECORD — the module is BUILT, without coordination, and the job summary says so in yellow.
Never claimed-on-faith (that would be the fault-becomes-fact defect, #2695) and never red: the ledger is
a coordination layer over a build that is correct without it, so its unavailability may cost a duplicate
build and may never cost a green (maintainer, 2026-09-02, after the registry answered 503 to three
Reinsurance bakes — core #3119). Every write command degrades the same way: a `::warning`, exit 0.

USAGE (the lane's steps; every command reads the run identity from GITHUB_* and the endpoint from
MW_LEDGER_URL / MW_LEDGER_TOKEN)
  decide    --keys @keys.json --matrix @matrix.json --publish true|false --lane L --out-matrix F --out-build F
  heartbeat --key K
  record    --key K --status Built --bundle FILE --artifact-name N --retention-days 7 [--version V] [--platform-identity I]
  record    --key K --status Tested [--trx FILE]
  record    --key K --status Published
  record    --key K --status Failed --phase compile|pack|test|publish|workspace [--failure-file F]
  finish    --key K                 the holder's leg ended with a non-terminal status (Built/Tested, no publish)
  release   --key K                 the holder was cancelled: the key becomes reclaimable at once
  get       --key K
  --self-test                       an in-process fake MCP server; every rule above is exercised
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = "Admin/ModuleBuilds"
NODE_TYPE = "ModuleBuild"
CONTENT_TYPE = "ModuleBuildRecord"
STATUSES = ("Claimed", "Built", "Tested", "Published", "Failed")
STALE_AFTER_S = 45 * 60          # the fleet's job cap (check-workflow-timeouts.py)
BLOCKING_TEST_ATTEMPTS = 2       # a test failure blocks from this many failed attempts on
PROTOCOL_VERSION = "2025-06-18"
HTTP_TIMEOUT_S = 60
HTTP_ATTEMPTS = 3
RETRY_DELAY_S = float(os.environ.get("MW_LEDGER_RETRY_DELAY_S", "10"))
FAILURE_TEXT_CAP = 4000
TEST_NAMES_CAP = 50


class LedgerError(RuntimeError):
    """The ledger could not be read or written — unavailable, refused, or malformed. Never 'absent'."""


class ToolError(LedgerError):
    """A tool answered an error string ("Error: …") instead of a result."""


def now_utc() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def iso(t: dt.datetime) -> str:
    return t.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def parse_time(s) -> dt.datetime | None:
    """Tolerant ISO-8601: .NET writes up to 7 fractional digits, Python reads at most 6."""
    if not isinstance(s, str) or not s:
        return None
    m = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?(Z|[+-]\d{2}:\d{2})?", s.strip())
    if not m:
        return None
    frac = (m.group(2) or "")[:7]
    tz = m.group(3) or "Z"
    text = m.group(1) + frac + ("+00:00" if tz == "Z" else tz)
    try:
        t = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    return t if t.tzinfo else t.replace(tzinfo=dt.timezone.utc)


# ── the run this process speaks for ──────────────────────────────────────────────────────────

def run_identity(lane: str | None = None) -> dict:
    env = os.environ
    repo = env.get("GITHUB_REPOSITORY", "")
    run_id = env.get("GITHUB_RUN_ID", "")
    attempt = env.get("GITHUB_RUN_ATTEMPT", "1")
    server = env.get("GITHUB_SERVER_URL", "https://github.com")
    return {
        "repo": repo,
        "runId": run_id,
        "attempt": attempt,
        "url": f"{server}/{repo}/actions/runs/{run_id}/attempts/{attempt}" if repo and run_id else "",
        "event": env.get("GITHUB_EVENT_NAME", ""),
        "lane": lane or env.get("MW_LEDGER_LANE") or None,
    }


def same_run(rec_run: dict | None, me: dict) -> bool:
    if not isinstance(rec_run, dict):
        return False
    if (rec_run.get("repo"), str(rec_run.get("runId")), str(rec_run.get("attempt"))) != \
            (me["repo"], str(me["runId"]), str(me["attempt"])):
        return False
    return rec_run.get("lane") in (None, "", me.get("lane")) or me.get("lane") in (None, "")


# ── MCP over HTTP ────────────────────────────────────────────────────────────────────────────

class Ledger:
    """Speaks JSON-RPC to the portal's /mcp (Streamable HTTP; JSON or SSE answers) with a bearer token."""

    def __init__(self, base_url: str, token: str, say=None):
        if not base_url or not token:
            raise LedgerError("MW_LEDGER_URL and MW_LEDGER_TOKEN are required — the ledger has nowhere to write "
                              "and nothing to write as. Provision the registry portal's ledger token (a mw_ "
                              "ApiToken of the CI user) as the caller's ledger-token secret.")
        self.endpoint = base_url.rstrip("/") + "/mcp"
        self.token = token
        self.session_id: str | None = None
        self._next_id = 0
        self._initialized = False
        self.say = say or (lambda *_: None)
        # Set once the endpoint has exhausted its retries: every later caller answers "unavailable" at
        # once instead of paying the retry budget again for each of 30 modules.
        self.down: str | None = None

    def _headers(self) -> dict:
        h = {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json",
             "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": PROTOCOL_VERSION}
        if self.session_id:
            h["Mcp-Session-Id"] = self.session_id
        return h

    def _post(self, method: str, params: dict, notification: bool = False) -> dict | None:
        body: dict = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            self._next_id += 1
            body["id"] = self._next_id
        data = json.dumps(body).encode("utf-8")
        if self.down:
            raise LedgerError(f"{method}: the ledger is unavailable ({self.down})")
        last = ""
        for attempt in range(1, HTTP_ATTEMPTS + 1):
            req = urllib.request.Request(self.endpoint, data=data, headers=self._headers(), method="POST")
            try:
                with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_S) as resp:
                    sid = resp.headers.get("Mcp-Session-Id")
                    if sid:
                        self.session_id = sid
                    raw = resp.read()
                    ctype = (resp.headers.get("Content-Type") or "").lower()
                    if notification or resp.status == 202 or not raw:
                        return None
                    return self._parse(raw, ctype, body.get("id"))
            except urllib.error.HTTPError as exc:
                text = exc.read().decode("utf-8", "replace")[:500]
                if exc.code in (429, 502, 503, 504) and attempt < HTTP_ATTEMPTS:
                    wait = exc.headers.get("Retry-After")
                    delay = float(wait) if wait and wait.replace(".", "", 1).isdigit() else RETRY_DELAY_S
                    self.say(f"ledger: HTTP {exc.code} from {self.endpoint} (attempt {attempt}) — retrying in {delay:g}s "
                             f"(a retryable fault, NOT a verdict): {text}")
                    time.sleep(min(delay, 60.0))
                    last = f"HTTP {exc.code}: {text}"
                    continue
                if exc.code in (429, 502, 503, 504):
                    self.down = f"HTTP {exc.code} after {HTTP_ATTEMPTS} attempts: {text}"
                    raise LedgerError(f"{method}: the ledger stayed unavailable ({self.down})") from exc
                raise LedgerError(f"{method} → HTTP {exc.code} from {self.endpoint}: {text}") from exc
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                last = str(exc)
                if attempt < HTTP_ATTEMPTS:
                    self.say(f"ledger: {self.endpoint} unreachable (attempt {attempt}: {exc}) — retrying in {RETRY_DELAY_S:g}s")
                    time.sleep(RETRY_DELAY_S)
                    continue
        self.down = f"unreachable after {HTTP_ATTEMPTS} attempts: {last}"
        raise LedgerError(f"{method}: the ledger stayed unavailable ({self.down})")

    @staticmethod
    def _parse(raw: bytes, ctype: str, want_id) -> dict:
        text = raw.decode("utf-8", "replace")
        messages: list[dict] = []
        if "text/event-stream" in ctype:
            for block in text.replace("\r\n", "\n").split("\n\n"):
                payload = "\n".join(line[5:].lstrip() for line in block.split("\n") if line.startswith("data:"))
                if payload.strip():
                    try:
                        messages.append(json.loads(payload))
                    except json.JSONDecodeError:
                        continue
        else:
            try:
                parsed = json.loads(text)
            except json.JSONDecodeError as exc:
                raise LedgerError(f"the ledger answered non-JSON ({ctype or 'no content-type'}): {text[:300]}") from exc
            messages = parsed if isinstance(parsed, list) else [parsed]
        for m in messages:
            if isinstance(m, dict) and m.get("id") == want_id and ("result" in m or "error" in m):
                if "error" in m:
                    err = m["error"]
                    raise LedgerError(f"JSON-RPC error {err.get('code')}: {err.get('message')} {err.get('data') or ''}".strip())
                return m["result"] if isinstance(m["result"], dict) else {"value": m["result"]}
        raise LedgerError(f"no JSON-RPC response for id {want_id} in: {text[:300]}")

    def initialize(self) -> None:
        if self._initialized:
            return
        self._post("initialize", {"protocolVersion": PROTOCOL_VERSION, "capabilities": {},
                                  "clientInfo": {"name": "module-build-ledger", "version": "1"}})
        self._post("notifications/initialized", {}, notification=True)
        self._initialized = True

    def call(self, tool: str, arguments: dict) -> str:
        self.initialize()
        result = self._post("tools/call", {"name": tool, "arguments": arguments}) or {}
        parts = [c.get("text", "") for c in result.get("content", []) if isinstance(c, dict) and c.get("type") == "text"]
        text = "".join(parts)
        if result.get("isError"):
            raise ToolError(text or f"{tool} answered isError with no text")
        return text

    # ── the three tools the ledger uses ──

    def get(self, key: str) -> dict | None:
        """The record's CONTENT, or None when the node is ABSENT. Unavailable/erroring reads RAISE."""
        text = self.call("get", {"path": f"@{ROOT}/{key}"}).strip()
        if text.startswith("Not found"):
            return None
        if text.startswith("Error"):
            raise LedgerError(f"get {ROOT}/{key}: {text[:300]}")
        try:
            node = json.loads(text)
        except json.JSONDecodeError as exc:
            raise LedgerError(f"get {ROOT}/{key} answered non-JSON: {text[:300]}") from exc
        content = node.get("content") if isinstance(node, dict) else None
        if not isinstance(content, dict):
            raise LedgerError(f"get {ROOT}/{key}: the node carries no content object: {text[:300]}")
        return content

    def create(self, key: str, name: str, content: dict) -> str:
        node = {"id": key, "namespace": ROOT, "name": name, "nodeType": NODE_TYPE,
                "content": {"$type": CONTENT_TYPE, **content}}
        text = self.call("create", {"node": json.dumps(node)}).strip()
        if not text.startswith("Created"):
            raise ToolError(text)
        return text

    def patch(self, key: str, content_fields: dict) -> str:
        text = self.call("patch", {"path": f"@{ROOT}/{key}", "fields": json.dumps({"content": content_fields})}).strip()
        if text.startswith("Error") or text.startswith("Not found"):
            raise ToolError(text)
        return text


# ── record helpers ───────────────────────────────────────────────────────────────────────────

def status_of(rec: dict | None) -> str | None:
    if not rec:
        return None
    s = rec.get("status")
    if isinstance(s, int) and 0 <= s < len(STATUSES):
        return STATUSES[s]
    return s if isinstance(s, str) else None


def heartbeat_age_s(rec: dict) -> float:
    t = parse_time(rec.get("heartbeatAt")) or parse_time(rec.get("claimedAt"))
    return float("inf") if t is None else (now_utc() - t).total_seconds()


def summary_of(rec: dict) -> dict:
    """What a re-claim keeps of the previous holder: evidence, not authority."""
    keep = ("status", "phase", "blocking", "attempts", "run", "claimedAt", "heartbeatAt", "finishedAt",
            "failure", "tests", "bundleSha256", "version", "platformIdentity")
    return {k: rec.get(k) for k in keep if rec.get(k) is not None}


def artifact_fetchable(rec: dict, me: dict, say) -> tuple[bool, str]:
    """Can THIS run download the record's bundle artifact? Same repo (GITHUB_TOKEN is repo-scoped) and
    the artifact still exists and is not expired — asked of the API, never assumed from a date."""
    art = rec.get("bundleArtifact")
    if not isinstance(art, dict) or not art.get("name") or not art.get("runId"):
        return False, "the record names no bundle artifact"
    if art.get("repo") != me["repo"]:
        return False, f"the bundle lives in {art.get('repo')}'s run {art.get('runId')} — this run's token reads only {me['repo']}"
    gh = os.environ.get("MW_LEDGER_GH", "gh")
    try:
        proc = subprocess.run([gh, "api", f"repos/{art['repo']}/actions/runs/{art['runId']}/artifacts?name={art['name']}"],
                              capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError) as exc:
        return False, f"gh api failed: {exc}"
    if proc.returncode != 0:
        return False, (f"gh api answered exit {proc.returncode} for run {art['runId']}'s artifacts — "
                       f"{(proc.stderr or proc.stdout).strip()[:200]} (the caller's job needs `permissions: actions: read` to reuse bundles)")
    try:
        found = [a for a in json.loads(proc.stdout).get("artifacts", []) if a.get("name") == art["name"]]
    except json.JSONDecodeError:
        return False, "gh api answered non-JSON"
    live = [a for a in found if not a.get("expired")]
    if not live:
        return False, f"artifact {art['name']} of run {art['runId']} is gone or expired"
    return True, f"artifact {art['name']} of run {art['runId']}"


# ── the decision ─────────────────────────────────────────────────────────────────────────────

def claim_content(entry: dict, key_info: dict, me: dict, attempts: int, previous: dict | None) -> dict:
    inputs = key_info.get("inputs", {})
    content = {
        "key": key_info["key"], "package": entry["package"], "module": entry["module"],
        "moduleVersion": inputs.get("moduleVersion", ""), "platformRef": inputs.get("platformRef", ""),
        "testerDigest": inputs.get("testerDigest") or None, "platformDigest": inputs.get("platformDigest") or None,
        "status": "Claimed", "phase": None, "blocking": False, "attempts": attempts, "run": me,
        "claimedAt": iso(now_utc()), "heartbeatAt": iso(now_utc()), "finishedAt": None,
        "bundleSha256": None, "bundleArtifact": None, "tests": None, "failure": None,
        "previous": previous,
    }
    return content


def decide_one(ledger: Ledger, entry: dict, key_info: dict, me: dict, publishing: bool, say) -> tuple[str, dict]:
    """One pass: ('build'|'reuse'|'wait'|'blocked', detail). 'wait' asks the caller to poll and call again."""
    key = key_info["key"]
    need_test = entry.get("test", True) is not False
    rec = ledger.get(key)
    if rec is None:
        try:
            ledger.create(key, f"{entry['module']} @ {key[:12]}", claim_content(entry, key_info, me, 1, None))
        except ToolError as exc:
            say(f"  {entry['module']}: create did not succeed ({str(exc)[:160]}) — reading who holds {key[:12]}")
        rec = ledger.get(key)
        if rec is None:
            raise LedgerError(f"{entry['module']}: the claim for {key} wrote nothing readable — the create was refused "
                              "for a reason other than 'already exists' (see the message above) and no other run holds it")
    st = status_of(rec)
    if same_run(rec.get("run"), me):
        return "build", {"attempts": rec.get("attempts", 1), "status": st, "takeover": False}
    fresh = heartbeat_age_s(rec) < STALE_AFTER_S
    finished = parse_time(rec.get("finishedAt")) is not None
    holder = (rec.get("run") or {}).get("url") or json.dumps(rec.get("run"))
    if st in ("Claimed", "Built", "Tested") and not finished and fresh:
        return "wait", {"holder": holder, "status": st, "heartbeatAgeS": int(heartbeat_age_s(rec))}
    if st == "Failed" and rec.get("blocking") is True:
        return "blocked", {"holder": holder, "phase": rec.get("phase"), "failure": (rec.get("failure") or "")[:1500],
                           "attempts": rec.get("attempts"), "tests": rec.get("tests")}
    if st in ("Built", "Tested", "Published"):
        ok, why = artifact_fetchable(rec, me, say)
        if ok:
            more_test = need_test and st not in ("Tested", "Published")
            more_publish = publishing and st != "Published"
            if more_test or more_publish:
                # this run completes the record: it becomes the holder for the remaining phases
                ledger.patch(key, {"run": me, "heartbeatAt": iso(now_utc()), "finishedAt": None})
                after = ledger.get(key)
                if after is None or not same_run(after.get("run"), me):
                    return "wait", {"holder": holder, "status": st, "heartbeatAgeS": 0}
            return "reuse", {"holder": holder, "status": st, "needTest": more_test, "needPublish": more_publish,
                             "artifact": rec.get("bundleArtifact"), "bundleSha256": rec.get("bundleSha256"),
                             "platformIdentity": rec.get("platformIdentity"), "source": why}
        say(f"  {entry['module']}: {st} record at {holder} is not reusable — {why}")
    # stale claim, non-blocking failure, or a terminal record whose bundle is gone: take the key over
    attempts = int(rec.get("attempts") or 0) + 1
    ledger.patch(key, {**claim_content(entry, key_info, me, attempts, summary_of(rec))})
    after = ledger.get(key)
    if after is None or not same_run(after.get("run"), me):
        return "wait", {"holder": holder, "status": st, "heartbeatAgeS": 0}
    return "build", {"attempts": attempts, "status": st, "takeover": True, "previous": holder}


def decide(ledger: Ledger, matrix: list[dict], keys: list[dict], me: dict, publishing: bool,
           wait_max_s: float, poll_s: float, say) -> tuple[list[dict], list[str]]:
    """Every entry annotated with a `ledger` object; returns (annotated matrix, blocking problems)."""
    by_module = {k["module"]: k for k in keys}
    pending = list(matrix)
    decided: dict[str, dict] = {}
    problems: list[str] = []
    deadline = time.monotonic() + wait_max_s
    first = True
    def uncoordinated(e: dict, k: dict | None, why: str) -> None:
        # 🚨 The ledger's unavailability costs a duplicate build at worst — never a red. The module is
        # built exactly as it was before the ledger existed, and the summary says so in yellow.
        say(f"  {e['module']}: BUILD without coordination — {why}")
        decided[e["module"]] = {**e, "ledger": {"key": k["key"] if k else "", "decision": "build",
                                                "attempts": None, "unavailable": why[:300]}}

    while pending:
        if not first:
            if time.monotonic() > deadline:
                for e in pending:
                    uncoordinated(e, by_module.get(e["module"]),
                                  f"another run still holds this key after {int(wait_max_s)}s — neither finished nor "
                                  "stale; building anyway rather than holding this run hostage (a duplicate build, not a red)")
                break
            time.sleep(poll_s)
        first = False
        still: list[dict] = []
        for e in pending:
            k = by_module.get(e["module"])
            if k is None:
                uncoordinated(e, None, "no build key was computed for it")
                continue
            if ledger.down:
                uncoordinated(e, k, f"the ledger is unavailable ({ledger.down})")
                continue
            try:
                verdict, detail = decide_one(ledger, e, k, me, publishing, say)
            except LedgerError as exc:
                uncoordinated(e, k, f"the ledger is unavailable ({str(exc)[:200]})")
                continue
            if verdict == "wait":
                say(f"  {e['module']}: WAITING — {detail['status']} by {detail['holder']} (heartbeat {detail['heartbeatAgeS']}s ago)")
                still.append(e)
                continue
            if verdict == "blocked":
                problems.append(f"{e['module']}: key {k['key'][:12]}… FAILED in phase '{detail.get('phase')}' at "
                                f"{detail['holder']} (attempt {detail.get('attempts')}) and the same inputs give the same "
                                f"result:\n{detail.get('failure') or '(no failure text recorded)'}")
                decided[e["module"]] = {**e, "ledger": {"key": k["key"], "decision": "blocked", **detail}}
                continue
            decided[e["module"]] = {**e, "ledger": {"key": k["key"], "decision": verdict, **detail}}
            say(f"  {e['module']}: {verdict.upper()} — key {k['key'][:12]}… " + (
                f"(reusing {detail['status']} bundle from {detail['holder']}; tests {'needed' if detail['needTest'] else 'recorded'}, "
                f"publish {'needed' if detail['needPublish'] else 'not needed'})" if verdict == "reuse"
                else f"(attempt {detail['attempts']}{', taken over from ' + detail['previous'] if detail.get('takeover') else ''})"))
        pending = still
    out = [decided[e["module"]] for e in matrix if e["module"] in decided]
    return out, problems


# ── transitions ──────────────────────────────────────────────────────────────────────────────

def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_trx(path: Path) -> dict:
    """{passed, failed, names[]} from a VSTest .trx — names are the FAILED tests, capped."""
    tree = ET.parse(path)
    root = tree.getroot()
    counters = None
    failed_names: list[str] = []
    for el in root.iter():
        tag = el.tag.split("}")[-1]
        if tag == "Counters":
            counters = el.attrib
        elif tag == "UnitTestResult" and el.attrib.get("outcome") == "Failed":
            failed_names.append(el.attrib.get("testName", "?"))
    passed = int((counters or {}).get("passed", 0))
    failed = int((counters or {}).get("failed", 0))
    return {"passed": passed, "failed": failed, "names": sorted(failed_names)[:TEST_NAMES_CAP]}


def holder_or_warn(ledger: Ledger, key: str, me: dict, say) -> dict | None:
    rec = ledger.get(key)
    if rec is None:
        say(f"::warning::ledger record {ROOT}/{key} does not exist — nothing to write")
        return None
    if not same_run(rec.get("run"), me):
        say(f"::warning::this run does not hold key {key[:12]}… (holder: {(rec.get('run') or {}).get('url')}) — not writing")
        return None
    return rec


def record(ledger: Ledger, a: argparse.Namespace, me: dict, say) -> int:
    rec = holder_or_warn(ledger, a.key, me, say)
    if rec is None:
        return 0
    now = iso(now_utc())
    fields: dict = {"status": a.status, "heartbeatAt": now}
    if a.version:
        fields["version"] = a.version
    if a.platform_identity:
        fields["platformIdentity"] = a.platform_identity
    if a.status == "Built":
        if not a.bundle or not a.artifact_name:
            print("::error::record --status Built needs --bundle FILE and --artifact-name NAME", file=sys.stderr)
            return 2
        fields["bundleSha256"] = sha256_file(Path(a.bundle))
        fields["bundleArtifact"] = {"repo": me["repo"], "runId": me["runId"], "name": a.artifact_name,
                                    "expiresAt": iso(now_utc() + dt.timedelta(days=a.retention_days))}
        fields["phase"] = None
    elif a.status == "Tested":
        if a.trx and Path(a.trx).is_file():
            fields["tests"] = parse_trx(Path(a.trx))
        else:
            fields["tests"] = {"passed": 0, "failed": 0, "names": []}
            say("::warning::record --status Tested without a readable --trx — the verdict carries no counts")
    elif a.status == "Published":
        fields["finishedAt"] = now
    elif a.status == "Failed":
        if not a.phase:
            print("::error::record --status Failed needs --phase", file=sys.stderr)
            return 2
        attempts = int(rec.get("attempts") or 1)
        blocking = a.phase == "compile" or (a.phase == "test" and attempts >= BLOCKING_TEST_ATTEMPTS)
        text = ""
        if a.failure_file and Path(a.failure_file).is_file():
            text = Path(a.failure_file).read_text(encoding="utf-8", errors="replace")[-FAILURE_TEXT_CAP:]
        fields.update({"phase": a.phase, "blocking": blocking, "failure": text or f"failed in {a.phase}", "finishedAt": now})
        if a.trx and Path(a.trx).is_file():
            fields["tests"] = parse_trx(Path(a.trx))
    else:
        print(f"::error::unknown status {a.status}", file=sys.stderr)
        return 2
    ledger.patch(a.key, fields)
    say(f"ledger: {a.key[:12]}… → {a.status}" + (f" ({a.phase}, blocking={fields.get('blocking')})" if a.status == "Failed" else ""))
    return 0


def simple_patch(ledger: Ledger, key: str, me: dict, fields: dict, say, label: str) -> int:
    if holder_or_warn(ledger, key, me, say) is None:
        return 0
    ledger.patch(key, fields)
    say(f"ledger: {key[:12]}… {label}")
    return 0


# ── self-test ────────────────────────────────────────────────────────────────────────────────

_FAKE_SERVER_SRC = r'''
import json, sys, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STORE = {}
FLAGS = {"sse": False, "fail503": 0, "create_refuses": True}

def merge(target, patch):
    for k, v in patch.items():
        if v is None:
            target.pop(k, None)
        elif isinstance(v, dict) and isinstance(target.get(k), dict):
            merge(target[k], v)
        else:
            target[k] = v

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, status, payload, sse=False, headers=None):
        body = b""
        if payload is not None:
            if sse:
                body = ("event: message\ndata: " + json.dumps(payload) + "\n\n").encode()
            else:
                body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "text/event-stream" if sse else "application/json")
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_POST(self):
        if self.path == "/__ctl":
            n = int(self.headers.get("Content-Length", 0)); FLAGS.update(json.loads(self.rfile.read(n) or b"{}"))
            return self._send(200, {"ok": True})
        if self.path == "/__store":
            n = int(self.headers.get("Content-Length", 0)); body = json.loads(self.rfile.read(n) or b"{}")
            for path, content in body.items():
                if content is None: STORE.pop(path, None)
                else: STORE[path] = content
            return self._send(200, {"ok": True})
        if self.headers.get("Authorization") != "Bearer mw_test":
            return self._send(401, {"error": "unauthorized"})
        if FLAGS["fail503"] > 0:
            FLAGS["fail503"] -= 1
            return self._send(503, {"error": "token validation unavailable"}, headers={"Retry-After": "0"})
        n = int(self.headers.get("Content-Length", 0)); req = json.loads(self.rfile.read(n))
        method = req.get("method"); rid = req.get("id")
        if method == "notifications/initialized":
            return self._send(202, None)
        if method == "initialize":
            return self._send(200, {"jsonrpc": "2.0", "id": rid, "result": {"protocolVersion": "2025-06-18", "capabilities": {}, "serverInfo": {"name": "fake"}}})
        name = req["params"]["name"]; args = req["params"]["arguments"]
        if name == "get":
            path = args["path"].lstrip("@")
            text = json.dumps({"path": path, "content": STORE[path]}) if path in STORE else "Not found: " + path
        elif name == "create":
            node = json.loads(args["node"]); path = node["namespace"] + "/" + node["id"]
            if path in STORE and FLAGS["create_refuses"]:
                text = "Error creating node: A node already exists at " + path
            else:
                STORE[path] = node["content"]; text = "Created: " + path
        elif name == "patch":
            path = args["path"].lstrip("@"); fields = json.loads(args["fields"])
            if path not in STORE: text = "Error: node not found at " + path
            else: merge(STORE[path], fields.get("content", {})); text = "Patched: " + path
        else:
            text = "Error: unknown tool " + name
        sse = FLAGS["sse"]; FLAGS["sse"] = not sse
        self._send(200, {"jsonrpc": "2.0", "id": rid, "result": {"content": [{"type": "text", "text": text}], "isError": False}}, sse=sse)

srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
print(srv.server_address[1], flush=True)
srv.serve_forever()
'''

_STUB_GH = '''#!/usr/bin/env python3
import json, os, sys
if os.environ.get("STUB_GH_FAIL") == "1":
    sys.stderr.write("HTTP 403: Resource not accessible by integration\\n"); sys.exit(1)
name = sys.argv[-1].split("name=")[-1]
print(json.dumps({"artifacts": [{"name": name, "expired": os.environ.get("STUB_GH_EXPIRED") == "1"}]}))
'''

_TRX = '''<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Acme.Alpha.Test.Thing.Works" outcome="Passed" />
    <UnitTestResult testName="Acme.Alpha.Test.Thing.Breaks" outcome="Failed" />
  </Results>
  <ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" /></ResultSummary>
</TestRun>
'''


def self_test() -> int:
    import tempfile
    import threading
    failures: list[str] = []
    ran = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    quiet = lambda *a: None
    global RETRY_DELAY_S
    RETRY_DELAY_S = 0.01   # the self-test pays no real retry delays; the CLI children read the env below
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "fake.py").write_text(_FAKE_SERVER_SRC, encoding="utf-8")
        gh = root / "gh"
        gh.write_text(_STUB_GH, encoding="utf-8")
        gh.chmod(0o755)
        os.environ["MW_LEDGER_GH"] = str(gh)
        server = subprocess.Popen([sys.executable, str(root / "fake.py")], stdout=subprocess.PIPE, text=True)
        try:
            port = int((server.stdout.readline() or "0").strip())
            url = f"http://127.0.0.1:{port}"

            def ctl(**flags):
                urllib.request.urlopen(urllib.request.Request(f"{url}/__ctl", data=json.dumps(flags).encode(),
                                                              headers={"Content-Type": "application/json"}, method="POST")).read()

            def store(**nodes):
                urllib.request.urlopen(urllib.request.Request(f"{url}/__store", data=json.dumps(nodes).encode(),
                                                              headers={"Content-Type": "application/json"}, method="POST")).read()

            def run(repo="Systemorph/Acme", run_id="100", attempt="1", event="pull_request", lane="l1"):
                os.environ.update({"GITHUB_REPOSITORY": repo, "GITHUB_RUN_ID": run_id, "GITHUB_RUN_ATTEMPT": attempt,
                                   "GITHUB_EVENT_NAME": event, "GITHUB_SERVER_URL": "https://github.com"})
                return run_identity(lane)

            entry = {"package": "Alpha", "module": "Acme.Alpha", "project": "src/Acme.Alpha/Acme.Alpha.csproj", "test": True}
            key = "k" * 64
            kinfo = {"module": "Acme.Alpha", "key": key, "inputs": {"moduleVersion": "mv1", "platformRef": "abc",
                                                                       "testerDigest": "sha256:t", "platformDigest": "sha256:p"}}
            L = lambda: Ledger(url, "mw_test", quiet)

            print("auth + transport:")
            try:
                Ledger(url, "mw_wrong", quiet).get(key)
                check("a wrong token is a hard error, never 'absent'", False, "no error raised")
            except LedgerError as exc:
                check("a wrong token is a hard error, never 'absent'", "401" in str(exc), str(exc)[:80])
            ctl(fail503=1)
            check("a 503 + Retry-After is retried, then answered (unavailable is not a verdict)", L().get(key) is None)

            print("the claim IS the mutex:")
            me = run()
            v, d = decide_one(L(), entry, kinfo, me, False, quiet)
            check("an absent key is claimed and built (attempt 1)", v == "build" and d["attempts"] == 1, f"{v} {d}")
            rec = L().get(key)
            check("the record names the claiming run and carries the key's inputs",
                  rec is not None and same_run(rec["run"], me) and rec["moduleVersion"] == "mv1"
                  and rec["status"] == "Claimed" and rec["$type"] == CONTENT_TYPE, json.dumps(rec)[:200])
            check("SSE and JSON answers both parse (the fake alternates)", L().get(key) is not None)
            other = run(run_id="200")
            v, d = decide_one(L(), entry, kinfo, other, False, quiet)
            check("a second run sees the fresh claim and WAITS (never builds the same key twice)", v == "wait" and "100" in d["holder"], f"{v} {d}")
            v, d = decide_one(L(), entry, kinfo, me, False, quiet)
            check("the holder itself re-deciding is still 'build' (idempotent within the run)", v == "build" and not d["takeover"], f"{v} {d}")

            print("transitions, as the holder:")
            bundle = root / "b.nupkg"
            bundle.write_bytes(b"bundle-bytes")
            ns = argparse.Namespace(key=key, status="Built", bundle=str(bundle), artifact_name="module-bundle-Acme.Alpha",
                                    retention_days=7, version="1.2.3", platform_identity="s1234", trx=None, phase=None, failure_file=None)
            record(L(), ns, me, quiet)
            rec = L().get(key)
            check("Built records sha256, the artifact locator, version and the PLATFORM identity",
                  rec["status"] == "Built" and rec["bundleSha256"] == hashlib.sha256(b"bundle-bytes").hexdigest()
                  and rec["bundleArtifact"]["runId"] == "100" and rec["version"] == "1.2.3" and rec["platformIdentity"] == "s1234", json.dumps(rec)[:300])
            trx = root / "r.trx"
            trx.write_text(_TRX, encoding="utf-8")
            record(L(), argparse.Namespace(key=key, status="Tested", trx=str(trx), version=None, platform_identity=None,
                                           bundle=None, artifact_name=None, retention_days=7, phase=None, failure_file=None), me, quiet)
            rec = L().get(key)
            check("Tested carries counts and the FAILED names from the trx",
                  rec["status"] == "Tested" and rec["tests"] == {"passed": 1, "failed": 1, "names": ["Acme.Alpha.Test.Thing.Breaks"]}, json.dumps(rec.get("tests")))
            record(L(), argparse.Namespace(key=key, status="Tested", trx=None, version=None, platform_identity=None, bundle=None,
                                           artifact_name=None, retention_days=7, phase=None, failure_file=None), other, quiet)
            check("a run that does not hold the key cannot write it", L().get(key)["run"]["runId"] == "100")
            v, d = decide_one(L(), entry, kinfo, other, False, quiet)
            check("Tested but not FINISHED still makes a follower wait (the holder may be publishing)", v == "wait", f"{v} {d}")
            simple_patch(L(), key, me, {"finishedAt": iso(now_utc())}, quiet, "finished")

            print("reuse:")
            v, d = decide_one(L(), entry, kinfo, other, False, quiet)
            check("a finished Tested record with a fetchable artifact is REUSED with nothing left to do",
                  v == "reuse" and d["needTest"] is False and d["needPublish"] is False and d["artifact"]["name"] == "module-bundle-Acme.Alpha", f"{v} {d}")
            pub = run(run_id="300", event="push")
            v, d = decide_one(L(), entry, kinfo, pub, True, quiet)
            check("a PUBLISHING run reuses the tested bundle and only publishes — and becomes the holder for that",
                  v == "reuse" and d["needPublish"] is True and d["needTest"] is False and L().get(key)["run"]["runId"] == "300", f"{v} {d}")
            record(L(), argparse.Namespace(key=key, status="Published", trx=None, version=None, platform_identity=None, bundle=None,
                                           artifact_name=None, retention_days=7, phase=None, failure_file=None), pub, quiet)
            rec = L().get(key)
            check("Published is terminal (finishedAt set)", rec["status"] == "Published" and rec.get("finishedAt"))
            v, d = decide_one(L(), entry, kinfo, run(run_id="400", event="push"), True, quiet)
            check("push of an already-Published key: reuse, nothing to publish (the #889 baseline)",
                  v == "reuse" and d["needPublish"] is False, f"{v} {d}")
            v, d = decide_one(L(), {**entry, "test": False}, kinfo, run(run_id="401"), False, quiet)
            check("a floor-only entry (test=false) reuses without needing tests", v == "reuse" and d["needTest"] is False, f"{v} {d}")
            os.environ["STUB_GH_EXPIRED"] = "1"
            v, d = decide_one(L(), entry, kinfo, run(run_id="500", event="push"), True, quiet)
            check("an EXPIRED artifact is not reusable: the key is taken over and rebuilt (attempt 2, previous kept)",
                  v == "build" and d["takeover"] and d["attempts"] == 2 and L().get(key)["previous"]["status"] == "Published", f"{v} {d}")
            os.environ.pop("STUB_GH_EXPIRED", None)
            simple_patch(L(), key, run(run_id="500", event="push"), {"status": "Tested", "finishedAt": iso(now_utc()),
                                                                   "bundleArtifact": {"repo": "Systemorph/Acme", "runId": "500", "name": "module-bundle-Acme.Alpha"}}, quiet, "x")
            os.environ["STUB_GH_FAIL"] = "1"
            v, d = decide_one(L(), entry, kinfo, run(run_id="600"), False, quiet)
            check("a token that cannot read artifacts (403) means BUILD, loudly — never a silent reuse", v == "build" and d["takeover"], f"{v} {d}")
            os.environ.pop("STUB_GH_FAIL", None)
            store(**{f"{ROOT}/{key}": {**L().get(key), "bundleArtifact": {"repo": "Systemorph/Other", "runId": "1", "name": "x"},
                                      "status": "Tested", "finishedAt": iso(now_utc())}})
            v, d = decide_one(L(), entry, kinfo, run(run_id="601"), False, quiet)
            check("a bundle in ANOTHER repo's run is not fetchable with this token → build", v == "build", f"{v} {d}")

            print("staleness:")
            old = iso(now_utc() - dt.timedelta(seconds=STALE_AFTER_S + 60))
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="700"), 3, None), "heartbeatAt": old, "claimedAt": old}})
            v, d = decide_one(L(), entry, kinfo, run(run_id="701"), False, quiet)
            check("a claim whose heartbeat is older than the job cap is taken over", v == "build" and d["takeover"] and d["attempts"] == 4, f"{v} {d}")
            simple_patch(L(), key, run(run_id="701"), {"heartbeatAt": iso(now_utc())}, quiet, "hb")
            v, d = decide_one(L(), entry, kinfo, run(run_id="702"), False, quiet)
            check("…and a heartbeat makes it fresh again", v == "wait", f"{v} {d}")

            print("tolerance:")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="800"), 1, None)}})
            fail = root / "fail.txt"
            fail.write_text("error CS0246: The type or namespace name 'Foo' could not be found\n", encoding="utf-8")
            record(L(), argparse.Namespace(key=key, status="Failed", phase="compile", failure_file=str(fail), trx=None, version=None,
                                           platform_identity=None, bundle=None, artifact_name=None, retention_days=7), run(run_id="800"), quiet)
            v, d = decide_one(L(), entry, kinfo, run(run_id="801"), False, quiet)
            check("a COMPILE failure blocks the same key, with the holder's evidence",
                  v == "blocked" and "CS0246" in d["failure"] and d["phase"] == "compile", f"{v} {d}")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="810"), 1, None)}})
            record(L(), argparse.Namespace(key=key, status="Failed", phase="test", failure_file=None, trx=str(trx), version=None,
                                           platform_identity=None, bundle=None, artifact_name=None, retention_days=7), run(run_id="810"), quiet)
            check("the first TEST failure does not block", L().get(key)["blocking"] is False)
            v, d = decide_one(L(), entry, kinfo, run(run_id="811"), False, quiet)
            check("…so the next run re-claims it (attempt 2)", v == "build" and d["attempts"] == 2, f"{v} {d}")
            record(L(), argparse.Namespace(key=key, status="Failed", phase="test", failure_file=None, trx=str(trx), version=None,
                                           platform_identity=None, bundle=None, artifact_name=None, retention_days=7), run(run_id="811"), quiet)
            v, d = decide_one(L(), entry, kinfo, run(run_id="812"), False, quiet)
            check("the second TEST failure blocks, naming the failed tests",
                  v == "blocked" and d["tests"]["names"] == ["Acme.Alpha.Test.Thing.Breaks"], f"{v} {d}")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="820"), 1, None)}})
            record(L(), argparse.Namespace(key=key, status="Failed", phase="workspace", failure_file=None, trx=None, version=None,
                                           platform_identity=None, bundle=None, artifact_name=None, retention_days=7), run(run_id="820"), quiet)
            v, d = decide_one(L(), entry, kinfo, run(run_id="821"), False, quiet)
            check("a workspace abort (another module's error) never blocks", v == "build", f"{v} {d}")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="830"), 1, None)}})
            simple_patch(L(), key, run(run_id="830"), {"status": "Failed", "phase": "cancelled", "blocking": False,
                                                       "finishedAt": iso(now_utc()), "failure": "cancelled"}, quiet, "released")
            v, d = decide_one(L(), entry, kinfo, run(run_id="831"), False, quiet)
            check("a released (cancelled) claim is reclaimable at once", v == "build", f"{v} {d}")

            print("decide over a matrix, with a holder that finishes while we wait:")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="900"), 1, None)}})
            k2 = "m" * 64
            kinfo2 = {**kinfo, "module": "Acme.Beta", "key": k2}
            entry2 = {**entry, "module": "Acme.Beta", "package": "Beta"}

            def finish_later():
                time.sleep(0.6)
                store(**{f"{ROOT}/{key}": {**L().get(key), "status": "Tested", "finishedAt": iso(now_utc()),
                                          "bundleArtifact": {"repo": "Systemorph/Acme", "runId": "900", "name": "module-bundle-Acme.Alpha"}}})
            threading.Thread(target=finish_later, daemon=True).start()
            out, problems = decide(L(), [entry, entry2], [kinfo, kinfo2], run(run_id="901"), False, 10, 0.2, quiet)
            by = {e["module"]: e["ledger"] for e in out}
            check("the waited-for key is reused once its holder finishes; the free key is claimed and built",
                  not problems and by["Acme.Alpha"]["decision"] == "reuse" and by["Acme.Beta"]["decision"] == "build", f"{problems} {by}")
            store(**{f"{ROOT}/{key}": {**claim_content(entry, kinfo, run(run_id="950"), 1, None)}})
            out, problems = decide(L(), [entry], [kinfo], run(run_id="951"), False, 0.5, 0.2, quiet)
            check("a holder that neither finishes nor goes stale within the budget ⇒ build anyway, flagged (never a red)",
                  not problems and out[0]["ledger"]["decision"] == "build" and "still holds" in out[0]["ledger"]["unavailable"],
                  f"{problems} {out}")
        finally:
            server.kill()
            server.wait()

        print("the ledger is DOWN — a coordination layer may cost a duplicate build, never a green:")
        dead = Ledger(url, "mw_test", quiet)
        t0 = time.monotonic()
        out, problems = decide(dead, [entry, entry2], [kinfo, kinfo2], run(run_id="960"), True, 10, 0.2, quiet)
        check("an unreachable ledger ⇒ every module is BUILT without coordination, none blocked, no exception",
              not problems and [e["ledger"]["decision"] for e in out] == ["build", "build"]
              and all("unavailable" in e["ledger"] for e in out), f"{problems} {out}")
        check("…and the retry budget is paid ONCE, not once per module",
              dead.down is not None and time.monotonic() - t0 < 5, f"down={dead.down} took {time.monotonic() - t0:.1f}s")
        rc = record(Ledger(url, "mw_test", quiet), argparse.Namespace(key=key, status="Published", trx=None, version=None,
                    platform_identity=None, bundle=None, artifact_name=None, retention_days=7, phase=None, failure_file=None),
                    run(run_id="960"), quiet) if False else None
        try:
            simple_patch(Ledger(url, "mw_test", quiet), key, run(run_id="960"), {"heartbeatAt": iso(now_utc())}, quiet, "hb")
            check("a write against a dead ledger raises LedgerError (main() turns it into a ::warning, exit 0)", False, "no error")
        except LedgerError:
            check("a write against a dead ledger raises LedgerError (main() turns it into a ::warning, exit 0)", True)
        del rc
        env = {**os.environ, "MW_LEDGER_URL": url, "MW_LEDGER_TOKEN": "mw_test", "MW_LEDGER_RETRY_DELAY_S": "0.01"}
        proc = subprocess.run([sys.executable, str(Path(__file__).resolve()), "heartbeat", "--key", key],
                              capture_output=True, text=True, env=env, timeout=120)
        check("the CLI: a write to a dead ledger exits 0 with a ::warning, never 1",
              proc.returncode == 0 and "::warning" in proc.stderr, f"exit={proc.returncode} {proc.stderr[-200:]}")
        mfile = root / "m.json"
        mfile.write_text(json.dumps([entry]), encoding="utf-8")
        kfile = root / "k.json"
        kfile.write_text(json.dumps([kinfo]), encoding="utf-8")
        proc = subprocess.run([sys.executable, str(Path(__file__).resolve()), "decide", "--keys", f"@{kfile}", "--matrix", f"@{mfile}",
                               "--publish", "true", "--out-matrix", str(root / "om.json"), "--out-build", str(root / "ob.json")],
                              capture_output=True, text=True, env=env, timeout=120)
        got = json.loads((root / "om.json").read_text(encoding="utf-8")) if (root / "om.json").is_file() else []
        check("the CLI: decide against a dead ledger exits 0 and hands the lane a full BUILD matrix",
              proc.returncode == 0 and got and got[0]["ledger"]["decision"] == "build" and got[0]["ledger"].get("unavailable"),
              f"exit={proc.returncode} {proc.stderr[-200:]} {got}")
        proc = subprocess.run([sys.executable, str(Path(__file__).resolve()), "decide", "--keys", f"@{kfile}", "--matrix", f"@{mfile}",
                               "--publish", "true", "--out-matrix", str(root / "om2.json"), "--out-build", str(root / "ob2.json")],
                              capture_output=True, text=True, env={**env, "MW_LEDGER_TOKEN": ""}, timeout=120)
        got = json.loads((root / "om2.json").read_text(encoding="utf-8")) if (root / "om2.json").is_file() else []
        check("the CLI: decide with NO token configured exits 0, builds everything, and says why",
              proc.returncode == 0 and got and got[0]["ledger"].get("unavailable") and "MW_LEDGER_TOKEN" in proc.stderr,
              f"exit={proc.returncode} {proc.stderr[-200:]}")

    if failures:
        print(f"\n::error title=module-build-ledger self-test failed::{len(failures)} case(s) — this script decides "
              "what is NOT rebuilt and who builds; a wrong answer is a silent under-build or a double build.")
        return 1
    print(f"\n✓ module-build-ledger self-test: {ran} cases green — auth, transport (JSON + SSE, 503 retry), the claim "
          "mutex, every transition, reuse (tested / publish-only / floor / expired / 403 / foreign repo), staleness, "
          "the tolerance rules, and a matrix decision with a waiting follower.")
    return 0


# ── CLI ──────────────────────────────────────────────────────────────────────────────────────

def load_json_arg(raw: str):
    if raw.startswith("@"):
        raw = Path(raw[1:]).read_text(encoding="utf-8")
    return json.loads(raw)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("command", nargs="?", choices=["decide", "heartbeat", "record", "finish", "release", "get"])
    p.add_argument("--key")
    p.add_argument("--keys", help="JSON list of {module, key, inputs} (module-build-key.py --modules), or @file")
    p.add_argument("--matrix", help="the selected matrix entries (JSON or @file)")
    p.add_argument("--publish", default="false")
    p.add_argument("--lane")
    p.add_argument("--out-matrix")
    p.add_argument("--out-build")
    p.add_argument("--wait-max", type=float, default=2400.0, help="seconds a follower waits on a live holder")
    p.add_argument("--poll", type=float, default=30.0)
    p.add_argument("--status", choices=["Built", "Tested", "Published", "Failed"])
    p.add_argument("--phase")
    p.add_argument("--bundle")
    p.add_argument("--artifact-name")
    p.add_argument("--retention-days", type=int, default=7)
    p.add_argument("--version")
    p.add_argument("--platform-identity")
    p.add_argument("--trx")
    p.add_argument("--failure-file")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    a = p.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        p.error("a command or --self-test is required")

    say = lambda *parts: print(*parts, file=sys.stderr, flush=True)
    me = run_identity(a.lane)

    def summary(lines: list[str]) -> None:
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
                fh.write("\n".join(lines) + "\n")

    try:
        ledger = Ledger(os.environ.get("MW_LEDGER_URL", ""), os.environ.get("MW_LEDGER_TOKEN", ""), say)
        if a.command == "decide":
            if not (a.keys and a.matrix and a.out_matrix and a.out_build):
                p.error("decide needs --keys, --matrix, --out-matrix and --out-build")
            matrix = load_json_arg(a.matrix)
            keys = load_json_arg(a.keys)
            publishing = str(a.publish).lower() == "true"
            say(f"ledger: deciding {len(matrix)} module(s) as {me['url']} (publish={publishing})")
            out, problems = decide(ledger, matrix, keys, me, publishing, a.wait_max, a.poll, say)
            build = [e for e in out if e["ledger"]["decision"] == "build"]
            Path(a.out_matrix).write_text(json.dumps(out), encoding="utf-8")
            Path(a.out_build).write_text(json.dumps(build), encoding="utf-8")
            lines = ["### Module build ledger", "",
                     f"{len(build)} of {len(matrix)} selected module(s) will be BUILT; "
                     f"{sum(1 for e in out if e['ledger']['decision'] == 'reuse')} reused from an earlier run.", ""]
            for e in out:
                lg = e["ledger"]
                if lg["decision"] == "reuse":
                    lines.append(f"- `{e['module']}` — **reused** ({lg['status']}) from {lg['holder']}"
                                 + (" — tests still run" if lg["needTest"] else "") + (" — published by this run" if lg["needPublish"] else ""))
                elif lg["decision"] == "build" and lg.get("unavailable"):
                    lines.append(f"- 🟡 `{e['module']}` — **build WITHOUT coordination**: {lg['unavailable']}")
                elif lg["decision"] == "build":
                    lines.append(f"- `{e['module']}` — **build** (attempt {lg['attempts']}"
                                 + (f", taken over from {lg.get('previous')}" if lg.get("takeover") else "") + f") key `{lg['key'][:12]}…`")
                else:
                    lines.append(f"- `{e['module']}` — **BLOCKED** by {lg['holder']} ({lg.get('phase')})")
            for pr in problems:
                lines.append(f"- 🚨 {pr.splitlines()[0]}")
            summary(lines)
            if problems:
                for pr in problems:
                    print(f"::error title=module build ledger::{pr}", file=sys.stderr)
                return 1
            return 0
        if not a.key:
            p.error(f"{a.command} needs --key")
        if a.command == "get":
            print(json.dumps(ledger.get(a.key), indent=2))
            return 0
        if a.command == "heartbeat":
            return simple_patch(ledger, a.key, me, {"heartbeatAt": iso(now_utc())}, say, "heartbeat")
        if a.command == "finish":
            return simple_patch(ledger, a.key, me, {"finishedAt": iso(now_utc()), "heartbeatAt": iso(now_utc())}, say, "finished")
        if a.command == "release":
            return simple_patch(ledger, a.key, me, {"status": "Failed", "phase": "cancelled", "blocking": False,
                                                    "failure": "the holding run was cancelled", "finishedAt": iso(now_utc()),
                                                    "heartbeatAt": iso(now_utc())}, say, "released (cancelled)")
        if a.command == "record":
            if not a.status:
                p.error("record needs --status")
            return record(ledger, a, me, say)
    except LedgerError as exc:
        # 🚨 Never red on the ledger's account. A decide that cannot even construct or reach the ledger
        # builds every selected module without coordination; a write that fails is a warning.
        why = str(exc)[:400]
        print(f"::warning title=module build ledger unavailable::{why}", file=sys.stderr)
        summary(["### Module build ledger", "", f"🟡 **unavailable** — {why}", "",
                 "Every selected module is built without coordination (a duplicate build at worst, never a red)."
                 if a.command == "decide" else f"`{a.command}` for key `{(a.key or '')[:12]}…` was not recorded."])
        if a.command == "decide" and a.matrix and a.out_matrix and a.out_build:
            matrix = load_json_arg(a.matrix)
            keys = {k.get("module"): k for k in (load_json_arg(a.keys) if a.keys else [])}
            out = [{**e, "ledger": {"key": (keys.get(e.get("module")) or {}).get("key", ""), "decision": "build",
                                    "attempts": None, "unavailable": why[:300]}} for e in matrix]
            Path(a.out_matrix).write_text(json.dumps(out), encoding="utf-8")
            Path(a.out_build).write_text(json.dumps(out), encoding="utf-8")
        return 0
    return 2


if __name__ == "__main__":
    sys.exit(main())
