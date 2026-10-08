#!/usr/bin/env python3
"""No client names — the build side of the confidentiality check.

This repository is PUBLIC. A client name, a client person or a client identifier (host, tenant or
app GUID, admin handle) must not land in it. WHO is a client is decided by the CRM, and the CRM is
never read from here (maintainer, 2026-09-28: "build system should not have access to crm"). So
this script holds NO terms at all. It sends the text to check to the instance that owns the CRM
and reads back a masked verdict:

    1. collect   the PR diff's added lines and changed paths (``--base``), or every tracked text
                 file (no ``--base``), each with file:line;
    2. submit    one ``Governance/NameCheck`` node per chunk under ``Governance/NameChecks``, over
                 that instance's MCP endpoint, as the build's OWN service user (a ``mw_`` token
                 whose only grant is create + read on that namespace). Each request names the
                 repository, the full 40-character commit and the Actions run that sends it
                 (``runId`` = ``GITHUB_RUN_ID``): the instance proves all three through its own
                 GitHub App before it reads the CRM, so it never answers for text that is not in
                 a live build's commit (MeshWeaver.Plugins#2785);
    3. read      each node back until the watcher there has answered: ``Pass``, ``Fail`` with
                 ``client#n`` at file:line:column, or ``NotChecked``;
    4. report    one ``::error`` annotation per hit — the masked term and the kind that matched,
                 never a name, never the line's text.

🚨 FAIL CLOSED. No URL, no token, an unreachable or refusing endpoint, a timeout or a
``NotChecked`` answer is "not checked", and "not checked" FAILS (exit 1) — an unchecked diff is
never a clean one. ``--report-only`` (private repositories) downgrades it to a warning.

Inputs (environment): ``NAME_CHECK_URL`` — base URL of the CRM-owning instance;
``NAME_CHECK_TOKEN`` — the build's ``mw_`` token. Both are secrets. ``GITHUB_RUN_ID`` — set by
GitHub Actions; without it there is nothing the instance can prove, so the check is not run.

    check-client-names.py --base origin/main          # a pull request
    check-client-names.py                             # the whole tree
    check-client-names.py --unchecked warn            # a private repository: a hit fails, "not checked" warns
    check-client-names.py --self-test                 # both arms, against a fake instance
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

NAMESPACE = "Governance/NameChecks"
NODE_TYPE = "Governance/NameCheck"
CONTENT_TYPE = "NameCheckContent"
PROTOCOL_VERSION = "2025-06-18"
CHUNK_LINES = 2000            # the watcher's cap is 5,000 per node
CHUNK_CHARS = 1_000_000       # …and a request carries at most this much text (a longer line goes alone)
# Lines are sent WHOLE: a name after any cut would never reach the matcher while the line counted as
# checked. A line longer than this is refused as not checked, never truncated.
MAX_LINE_CHARS = 1_000_000
HTTP_TIMEOUT_S = 60
HTTP_ATTEMPTS = 3
RETRY_DELAY_S = float(os.environ.get("NAME_CHECK_RETRY_DELAY_S", "5"))
POLL_S = float(os.environ.get("NAME_CHECK_POLL_S", "3"))
ANSWER_TIMEOUT_S = float(os.environ.get("NAME_CHECK_TIMEOUT_S", "180"))
STATUS_NAMES = {1: "Requested", 2: "Pass", 3: "Fail", 4: "NotChecked"}
FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
# What a refused CREATE means and who fixes it. The caller's grant is an access-control change, so
# the build never makes it: it is the governed standard `namecheck.caller.grant` on the CRM-owning
# instance (input: the token's service identity, `svc-…`; signed by a global admin who is not the
# proposer, or by the standard's maintainer on their own proposal, so one admin can do it alone),
# which writes `Governance/NameChecks/_Access/{svc}_Access` with the NameCheckCaller role.
GRANT_REMEDY = ("the build's service user may not create on " + NAMESPACE + ". Owed by a global admin, never "
                "by the build: propose a Governance/Activity under Governance/Activities with standard "
                "'namecheck.caller.grant' and inputs.service = the token's service identity (svc-…), signed by "
                "a global admin other than the proposer, or by the standard's maintainer on their own proposal; it writes " + NAMESPACE + "/_Access/{svc}_Access with role NameCheckCaller")


class NotChecked(RuntimeError):
    """The check could not be performed. Never a pass. ``hits`` are the masked hits of the chunks
    that DID answer, and ``failed`` says whether any of them answered ``Fail`` (with or without hit
    details): a known rejection is reported, and fails, whatever else went unchecked."""

    def __init__(self, message: str, hits: list[dict] | None = None, failed: bool = False):
        super().__init__(message)
        self.hits: list[dict] = hits or []
        self.failed: bool = failed or bool(self.hits)


# ── collect ──────────────────────────────────────────────────────────────────────────────────

def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True,
                          text=True, encoding="utf-8", errors="replace").stdout


def entry(file: str, line: int, text: str) -> dict:
    """One line to check, WHOLE. A line too long to send is refused, never cut: a cut line would be
    counted as checked while its tail never reached the matcher."""
    if len(text) > MAX_LINE_CHARS:
        raise NotChecked(f"{file}:{line} is a {len(text)}-character line, longer than the {MAX_LINE_CHARS} the check "
                         "sends — exclude the file with a pathspec or break the line")
    return {"file": file, "line": line, "text": text}


def parse_diff(diff: str) -> list[dict]:
    """Added lines of a unified diff (``-U0``) as ``{file, line, text}``, plus one line-0 entry per
    added or renamed path — a path can name a client too. Pure."""
    out: list[dict] = []
    path: str | None = None
    old: str | None = None
    line = 0
    # How much of the open hunk's body is still to come, from its `@@` header. While either is
    # positive the line IS body: an added line whose text starts `++ ` (raw `+++ …`) or a deleted
    # one starting `-- ` (raw `--- …`) is content, never a file header. Files that embed unified
    # diffs (fixtures, a page about this very gate) are exactly where that happens.
    old_left = new_left = 0
    for raw in diff.splitlines():
        if old_left > 0 or new_left > 0:
            if raw.startswith("\\"):          # "\ No newline at end of file"
                continue
            if raw.startswith("-") and old_left > 0:
                old_left -= 1
                continue
            if raw.startswith("+") and new_left > 0:
                new_left -= 1
                text = raw[1:]
                if path is not None and text.strip():
                    out.append(entry(path, line, text))
                line += 1
                continue
            if raw.startswith(" ") and old_left > 0 and new_left > 0:
                old_left -= 1
                new_left -= 1
                line += 1
                continue
            old_left = new_left = 0             # a body shorter than its header: read on as headers
        if raw.startswith("--- "):
            source = raw[4:]
            old = None if source == "/dev/null" else (source[2:] if source.startswith("a/") else source)
            continue
        if raw.startswith("+++ "):
            target = raw[4:]
            path = None if target == "/dev/null" else (target[2:] if target.startswith("b/") else target)
            if path is not None and path != old:
                out.append({"file": path, "line": 0, "text": path})
            continue
        if raw.startswith("diff --git") or raw.startswith("Binary files"):
            continue
        m = re.match(r"^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", raw)
        if m:
            old_left = int(m.group(1)) if m.group(1) is not None else 1
            line = int(m.group(2))
            new_left = int(m.group(3)) if m.group(3) is not None else 1
    return out


def collect_diff(root: Path, base: str, paths: list[str] | None = None) -> list[dict]:
    # BASE against HEAD directly (not BASE...HEAD): needs only the two commits, never a merge base, so a
    # shallow checkout plus one `git fetch --depth=1 origin <base>` is enough. On a PR's merge-commit
    # checkout and on a push (before → after) that is exactly the change; only ADDED lines are read.
    diff = git(root, "diff", "--no-color", "--no-ext-diff", "-U0", "--find-renames", base, "HEAD",
               "--", *(paths or []))
    out = parse_diff(diff)
    # The NEW paths, independently of textual hunks: a pure rename, a new empty file and a new binary
    # file carry no `---`/`+++` header, so the patch alone would never name them.
    seen = {e["file"] for e in out if e["line"] == 0}
    for rel in git(root, "diff", "--no-color", "--no-ext-diff", "--name-only", "-z", "--find-renames",
                   "--diff-filter=ACR", base, "HEAD", "--", *(paths or [])).split("\0"):
        if rel and rel not in seen:
            seen.add(rel)
            out.append(entry(rel, 0, rel))
    return out


def collect_tree(root: Path, paths: list[str] | None = None) -> list[dict]:
    out: list[dict] = []
    for rel in git(root, "ls-files", "-z", "--", *(paths or [])).split("\0"):
        if not rel:
            continue
        out.append(entry(rel, 0, rel))
        p = root / rel
        if not p.is_file():
            continue
        # Read incrementally and WHOLE, whatever the size: a large text file is checked like any other.
        try:
            with p.open("rb") as fh:
                if b"\0" in fh.read(8192):
                    continue                                  # binary: its path is checked above
                fh.seek(0)
                for i, raw in enumerate(fh, start=1):
                    text = raw.decode("utf-8", "replace").rstrip("\r\n")
                    if text.strip():
                        out.append(entry(rel, i, text))
        except OSError as exc:
            raise NotChecked(f"{rel} could not be read ({type(exc).__name__})") from exc
    return out


def chunks(lines: list[dict], size: int | None = None, chars: int | None = None) -> list[list[dict]]:
    """At most ``size`` lines and ``chars`` characters of text per request; a single longer line
    goes in a request of its own."""
    size, chars = size or CHUNK_LINES, chars or CHUNK_CHARS
    out: list[list[dict]] = []
    cur: list[dict] = []
    used = 0
    for item in lines:
        n = len(item.get("text") or "")
        if cur and (len(cur) >= size or used + n > chars):
            out.append(cur)
            cur, used = [], 0
        cur.append(item)
        used += n
    if cur:
        out.append(cur)
    return out


# ── MCP over HTTP (the ledger's client, cut to what this needs) ──────────────────────────────

class Mesh:
    def __init__(self, base_url: str, token: str):
        self.endpoint = base_url.rstrip("/") + "/mcp"
        self.token = token
        self.session_id: str | None = None
        self._id = 0
        self._ready = False

    def _headers(self) -> dict:
        h = {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json",
             "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": PROTOCOL_VERSION}
        if self.session_id:
            h["Mcp-Session-Id"] = self.session_id
        return h

    def _post(self, method: str, params: dict, notification: bool = False) -> dict | None:
        body: dict = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            self._id += 1
            body["id"] = self._id
        data = json.dumps(body).encode("utf-8")
        last = ""
        for attempt in range(1, HTTP_ATTEMPTS + 1):
            req = urllib.request.Request(self.endpoint, data=data, headers=self._headers(), method="POST")
            try:
                with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_S) as resp:
                    sid = resp.headers.get("Mcp-Session-Id")
                    if sid:
                        self.session_id = sid
                    raw = resp.read()
                    if notification or resp.status == 202 or not raw:
                        return None
                    try:
                        return self._parse(raw, (resp.headers.get("Content-Type") or "").lower(), body.get("id"))
                    except NotChecked as exc:
                        # Name the CALL that was refused: "JSON-RPC error -32602" alone does not say whether
                        # the handshake, the create or the read-back failed (#5852's runs could not tell).
                        call = f"{method} {params.get('name')}" if method == "tools/call" else method
                        raise NotChecked(f"{call}: {exc}") from exc
            except urllib.error.HTTPError as exc:
                last = f"HTTP {exc.code}"
                if exc.code in (429, 502, 503, 504) and attempt < HTTP_ATTEMPTS:
                    time.sleep(RETRY_DELAY_S)
                    continue
                raise NotChecked(f"{method}: the check endpoint answered {last}") from exc
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                last = type(exc).__name__
                if attempt < HTTP_ATTEMPTS:
                    time.sleep(RETRY_DELAY_S)
                    continue
        raise NotChecked(f"{method}: the check endpoint is unreachable ({last} after {HTTP_ATTEMPTS} attempts)")

    @staticmethod
    def _parse(raw: bytes, ctype: str, want_id) -> dict:
        text = raw.decode("utf-8", "replace")
        messages: list = []
        if "text/event-stream" in ctype:
            for block in text.replace("\r\n", "\n").split("\n\n"):
                payload = "\n".join(l[5:].lstrip() for l in block.split("\n") if l.startswith("data:"))
                if payload.strip():
                    try:
                        messages.append(json.loads(payload))
                    except json.JSONDecodeError:
                        continue
        else:
            try:
                parsed = json.loads(text)
            except json.JSONDecodeError as exc:
                raise NotChecked("the check endpoint answered non-JSON") from exc
            messages = parsed if isinstance(parsed, list) else [parsed]
        for m in messages:
            if isinstance(m, dict) and m.get("id") == want_id and ("result" in m or "error" in m):
                if "error" in m:
                    raise NotChecked(f"JSON-RPC error {m['error'].get('code')}")
                return m["result"] if isinstance(m["result"], dict) else {"value": m["result"]}
        raise NotChecked("no JSON-RPC response from the check endpoint")

    def call(self, tool: str, arguments: dict) -> str:
        if not self._ready:
            self._post("initialize", {"protocolVersion": PROTOCOL_VERSION, "capabilities": {},
                                      "clientInfo": {"name": "check-client-names", "version": "1"}})
            self._post("notifications/initialized", {}, notification=True)
            self._ready = True
        result = self._post("tools/call", {"name": tool, "arguments": arguments}) or {}
        text = "".join(c.get("text", "") for c in result.get("content", [])
                       if isinstance(c, dict) and c.get("type") == "text")
        if result.get("isError"):
            raise NotChecked(f"{tool} was refused by the check endpoint")
        return text


# ── submit and read ──────────────────────────────────────────────────────────────────────────

def node_id(repo: str, sha: str, run: str, index: int) -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "-", repo.split("/")[-1]).strip("-") or "repo"
    return f"{slug}-{sha[:12]}-{re.sub(r'[^A-Za-z0-9]+', '-', run)}-{index}"


def status_of(content: dict) -> str:
    s = content.get("status", "Requested")
    return STATUS_NAMES.get(s, str(s)) if isinstance(s, int) else str(s)


def provenance(sha: str, run_id: str | None) -> int:
    """The run id the instance will prove, or NotChecked. The instance refuses a request without
    a full 40-character commit or a positive ``runId`` before reading anything, so sending one would
    only turn a clear local message into a remote ``NotChecked``. Pure."""
    if not FULL_SHA.match(sha or ""):
        raise NotChecked(f"HEAD is not a full 40-character commit id ({len(sha or '')} characters) — the "
                         "instance verifies every line against that commit")
    if not (run_id or "").isdigit() or int(run_id) <= 0:
        raise NotChecked("GITHUB_RUN_ID is not set — the instance answers only for a live GitHub Actions run of "
                         "this repository, which it proves through its own GitHub App, so this check runs in CI only")
    return int(run_id)


def submit_and_wait(mesh: Mesh, repo: str, sha: str, run_id: int, run: str, parts: list[list[dict]]) -> list[dict]:
    """Creates one node per chunk, then reads each back until answered. Returns the answered
    contents. Raises NotChecked for anything short of an answer."""
    ids = []
    for i, part in enumerate(parts):
        nid = node_id(repo, sha, run, i)
        node = {"id": nid, "namespace": NAMESPACE, "name": f"{repo}@{sha[:12]} ({i + 1}/{len(parts)})",
                "nodeType": NODE_TYPE,
                "content": {"$type": CONTENT_TYPE, "repo": repo, "sha": sha, "runId": run_id, "lines": part}}
        try:
            text = mesh.call("create", {"node": json.dumps(node)}).strip()
        except NotChecked as exc:
            if "unreachable" in str(exc) or "HTTP 401" in str(exc):
                raise                                         # not a grant question: the endpoint or the token
            raise NotChecked(f"{exc} — {GRANT_REMEDY}") from exc
        if not text.startswith("Created"):
            raise NotChecked(f"the check request was not created — {GRANT_REMEDY}")
        ids.append(nid)
    answers: dict[str, dict] = {}
    deadline = time.monotonic() + ANSWER_TIMEOUT_S
    try:
        while len(answers) < len(ids):
            for nid in ids:
                if nid in answers:
                    continue
                text = mesh.call("get", {"path": f"@{NAMESPACE}/{nid}"}).strip()
                try:
                    content = (json.loads(text) or {}).get("content") or {}
                except json.JSONDecodeError:
                    content = {}
                if status_of(content) not in ("Requested", ""):
                    answers[nid] = content
            if len(answers) < len(ids):
                if time.monotonic() > deadline:
                    raise NotChecked(f"no answer within {ANSWER_TIMEOUT_S:g}s — is the name-check watcher installed "
                                     "on the CRM-owning instance?")
                time.sleep(POLL_S)
    except NotChecked as exc:
        # A later read failing or timing out must not discard what the chunks that DID answer found:
        # annotate them now and carry their hits (and any rejection) on the refusal.
        done = [answers[n] for n in ids if n in answers]
        try:
            verdict, hits = report(done)
            failed = verdict == "Fail"
        except NotChecked as partial:
            hits, failed = partial.hits, partial.failed
        raise NotChecked(str(exc), hits, failed) from exc
    return [answers[n] for n in ids]


# ── report ───────────────────────────────────────────────────────────────────────────────────

def esc(s: str) -> str:
    """A workflow-command PROPERTY value (``file=``)."""
    return s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A").replace(",", "%2C").replace(":", "%3A")


def esc_data(s: str) -> str:
    """A workflow-command MESSAGE: a newline in it would start a second command."""
    return s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def kind_of(h: dict) -> str:
    """The hit's kind is the instance's word (``name``, ``alias``, ``host``, …) and goes into a
    public log, so it is cut to a plain token: nothing in it can forge a command or a table cell."""
    return re.sub(r"[^A-Za-z0-9 _-]+", "", str(h.get("kind") or "name"))[:40] or "name"


def cell(value) -> str:
    """One markdown table cell from a remote-provided value."""
    return re.sub(r"[\r\n|`<>]+", " ", str(value if value is not None else ""))[:300]


# What a not-checked answer says about itself, by status. The instance's own ``reason`` is NOT
# printed: this log is public and the reason is free text from another process. It stays on the
# request node, where the people who may read the CRM can read it.
def unchecked_message(answers: list[dict]) -> str:
    states = sorted({status_of(a) if status_of(a) in STATUS_NAMES.values() else "an unknown status"
                     for a in answers if status_of(a) not in ("Pass", "Fail")})
    n = sum(1 for a in answers if status_of(a) not in ("Pass", "Fail"))
    return (f"the instance answered {', '.join(states)} for {n} of {len(answers)} request(s) — the reason is on "
            f"the request node(s) under {NAMESPACE} on the CRM-owning instance")


def report(answers: list[dict], say=None) -> tuple[str, list[dict]]:
    """Folds the answers into ('Fail', hits) or ('Pass', []), and raises NotChecked — carrying the
    hits — when any chunk was not answered Pass or Fail. The hits of the chunks that DID answer are
    annotated FIRST, so one unanswered chunk never hides a name another chunk found. Hits are
    re-numbered per (chunk, term): each chunk numbers its clients independently, so the same
    ``client#n`` from two chunks need not be the same client, and one client seen in two chunks
    gets two numbers. Pure but for ``say``."""
    say = say or print
    hits: list[dict] = []
    renumber: dict[tuple[int, str], str] = {}
    # A `Fail` is enforced by its STATUS: an empty or missing `hits` array only means there are no
    # positions to annotate, never that the instance's rejection may read as a pass.
    rejected_without_detail = 0
    rejected = False
    for i, a in enumerate(answers):
        if status_of(a) != "Fail":
            continue
        rejected = True
        if not a.get("hits"):
            rejected_without_detail += 1
        for h in a.get("hits") or []:
            key = (i, str(h.get("term")))
            if key not in renumber:
                # Chunks number independently; a new number per (chunk, term) keeps the mask honest.
                renumber[key] = f"client#{len(renumber) + 1}"
            hits.append({**h, "term": renumber[key]})
    for h in hits:
        f, line, col = h.get("file", ""), int(h.get("line") or 0), int(h.get("column") or 1)
        what = esc_data(f"{h['term']} ({kind_of(h)})")
        if line <= 0:
            say(f"::error file={esc(f)}::The path names a client of ours: {what}. Rename it with a neutral placeholder.")
        else:
            say(f"::error file={esc(f)},line={line},col={col}::A client of ours is named here: {what}. "
                "Replace it with a neutral placeholder (AGENTS.md, 'Confidential terms').")
    if rejected_without_detail:
        say(f"::error::The instance answered Fail for {rejected_without_detail} request(s) without naming where — the "
            f"positions are on the request node(s) under {NAMESPACE} on the CRM-owning instance.")
    if any(status_of(a) not in ("Pass", "Fail") for a in answers):
        raise NotChecked(unchecked_message(answers), hits, rejected)
    return ("Fail" if rejected else "Pass"), hits


def summary(status: str, hits: list[dict], lines: int, reason: str | None = None, failed: bool = False) -> str:
    out = ["## No client names", ""]
    if status == "Pass":
        out.append(f"✅ {lines} line(s) checked against the CRM on the CRM-owning instance; no client named.")
    elif status == "Fail":
        clients = len({h['term'] for h in hits})
        out.append(f"❌ {len(hits)} hit(s) naming {clients} client(s) in {lines} line(s). Terms are masked; "
                   "the CRM decides who is a client.")
        if not hits:
            out.append("The instance rejected the change without naming where; the positions are on the request node(s).")
    else:
        out.append(f"⛔ Not checked: {cell(reason)}. An unchecked diff is not a clean one.")
        if hits or failed:
            out += ["", f"❌ The part that WAS checked was rejected ({len(hits)} hit(s)). Terms are masked."]
    if hits:
        out += ["", "| File | Line | Col | Term | Kind |", "|---|---|---|---|---|"]
        out += [f"| {cell(h.get('file'))} | {cell(h.get('line'))} | {cell(h.get('column'))} | {cell(h['term'])} | {kind_of(h)} |"
                for h in hits[:200]]
    return "\n".join(out) + "\n"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--base", help="check the added lines of BASE...HEAD; omit to check the whole tree")
    ap.add_argument("--root", default=".")
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "local/repo"))
    ap.add_argument("--report-only", action="store_true", help="private repositories: warn instead of failing")
    ap.add_argument("--unchecked", choices=("fail", "warn"), default="fail",
                    help="what 'not checked' does: fail (default, public repositories) or warn (a private repository "
                         "whose Dependabot PRs cannot reach the secret) — a HIT still fails either way")
    ap.add_argument("--path", action="append", default=[], metavar="PATHSPEC",
                    help="limit the check to these git pathspecs (repeatable; ':(exclude)x' excludes). Default: everything")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()

    root = Path(a.root).resolve()
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    lines: list[dict] = []
    try:
        sha = git(root, "rev-parse", "HEAD").strip()
        lines = collect_diff(root, a.base, a.path) if a.base else collect_tree(root, a.path)
        if not lines:
            print("No added text to check.")
            status, hits = "Pass", []
        else:
            url, token = os.environ.get("NAME_CHECK_URL", ""), os.environ.get("NAME_CHECK_TOKEN", "")
            if not url or not token:
                missing = [n for n, v in (("NAME_CHECK_URL", url), ("NAME_CHECK_TOKEN", token)) if not v]
                raise NotChecked("missing " + ", ".join(missing) + " — the build's service user on the CRM-owning "
                                 "instance and its endpoint (Settings → Secrets → Actions)")
            run_id = provenance(sha, os.environ.get("GITHUB_RUN_ID"))
            run = f"{run_id}-{os.environ.get('GITHUB_RUN_ATTEMPT', '1')}"
            answers = submit_and_wait(Mesh(url, token), a.repo, sha, run_id, run, chunks(lines))
            status, hits = report(answers)
    except NotChecked as exc:
        lenient = a.report_only or a.unchecked == "warn"
        level = "warning" if lenient else "error"
        print(f"::{level}::No client names — NOT CHECKED: {esc_data(str(exc))}")
        if step_summary:
            Path(step_summary).open("a").write(summary("NotChecked", exc.hits, len(lines), str(exc), exc.failed))
        # A HIT (or a rejection) in the part that was checked fails exactly as it would alone:
        # `--unchecked warn` forgives "not checked", never a known client name. Only --report-only forgives a hit.
        if exc.failed and not a.report_only:
            print(f"No client names: Fail — {len(exc.hits)} hit(s) in the part that was checked.")
            return 1
        return 0 if lenient else 1
    if step_summary:
        Path(step_summary).open("a").write(summary(status, hits, len(lines)))
    print(f"No client names: {status} — {len(lines)} line(s), {len(hits)} hit(s).")
    return 1 if status == "Fail" and not a.report_only else 0


# ── self-test: a fake CRM-owning instance whose "watcher" knows one synthetic client ─────────

def self_test() -> int:
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    import tempfile

    store: dict[str, dict] = {}
    sent: list[dict] = []
    flags = {"answer": True, "not_checked": False, "rpc_error": False, "get_error_file": None}
    term = re.compile(r"(?<![A-Za-z0-9])zorblax(?![a-z0-9])", re.I)

    class H(BaseHTTPRequestHandler):
        def log_message(self, *_): pass

        def _send(self, payload, status=200):
            body = json.dumps(payload).encode() if payload is not None else b""
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            if self.headers.get("Authorization") != "Bearer mw_test":
                return self._send({"error": "unauthorized"}, 401)
            req = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
            rid, method = req.get("id"), req.get("method")
            if method == "notifications/initialized":
                return self._send(None, 202)
            if method == "initialize":
                return self._send({"jsonrpc": "2.0", "id": rid, "result": {"protocolVersion": PROTOCOL_VERSION}})
            name, args = req["params"]["name"], req["params"]["arguments"]
            if name == "create" and flags.get("no_grant"):
                return self._send({"jsonrpc": "2.0", "id": rid, "result": {"content": [
                    {"type": "text", "text": "Access denied: secret detail never printed"}]}})
            if name == "create" and flags["rpc_error"]:
                return self._send({"jsonrpc": "2.0", "id": rid,
                                   "error": {"code": -32602, "message": "secret detail never printed"}})
            if name == "create":
                node = json.loads(args["node"])
                store[node["namespace"] + "/" + node["id"]] = node["content"]
                sent.append(node["content"])
                text = "Created: " + node["id"]
            else:
                path = args["path"].lstrip("@")
                c = store.get(path)
                if c is not None and any(l["file"] == flags["get_error_file"] for l in c.get("lines") or []):
                    return self._send({"jsonrpc": "2.0", "id": rid,
                                       "error": {"code": -32603, "message": "read failed"}})
                if c is None:
                    text = "Not found"
                else:
                    if flags["answer"] and c.get("lines"):
                        unproven = not (isinstance(c.get("runId"), int) and c["runId"] > 0
                                        and FULL_SHA.match(str(c.get("sha") or "")))
                        if unproven or flags["not_checked"] or any(l["file"] == flags.get("not_checked_file") for l in c["lines"]):
                            c = {**c, "status": "NotChecked", "reason": "ground truth empty", "lines": []}
                        else:
                            hits = [{"file": l["file"], "line": l["line"], "column": m.start() + 1,
                                     "term": "client#1", "kind": "name"}
                                    for l in c["lines"] for m in term.finditer(l["text"])]
                            c = {**c, "status": 3 if hits else 2, "hits": hits, "lines": []}
                        store[path] = c
                    text = json.dumps({"path": path, "content": c})
            self._send({"jsonrpc": "2.0", "id": rid, "result": {"content": [{"type": "text", "text": text}]}})

    srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{srv.server_address[1]}"
    global RETRY_DELAY_S, POLL_S, ANSWER_TIMEOUT_S
    RETRY_DELAY_S, POLL_S, ANSWER_TIMEOUT_S = 0.01, 0.01, 0.3
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        print(("✓ " if ok else "✗ ") + name)
        if not ok:
            failures.append(name)

    diff = "diff --git a/d.md b/d.md\n--- a/d.md\n+++ b/d.md\n@@ -3,0 +4,2 @@\n+hello Zorblax AG\n+\n@@ -9 +11 @@\n-x\n+ok\n"
    parsed = parse_diff(diff)
    check("diff: added lines with their new line numbers, blank lines skipped, no entry for an unchanged path",
          parsed == [{"file": "d.md", "line": 4, "text": "hello Zorblax AG"},
                     {"file": "d.md", "line": 11, "text": "ok"}])
    check("diff: a NEW or RENAMED path is checked as text too",
          parse_diff("--- /dev/null\n+++ b/n.md\n@@ -0,0 +1 @@\n+x\n")[0] == {"file": "n.md", "line": 0, "text": "n.md"}
          and parse_diff("--- a/o.md\n+++ b/p.md\n")[0]["file"] == "p.md")
    check("diff: a deleted file contributes nothing", parse_diff("--- a/x\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n") == [])
    check("chunks split at the size", [len(c) for c in chunks([{}] * 5, 2)] == [2, 2, 1])
    check("chunks split at the text budget too; an over-budget line goes alone",
          [len(c) for c in chunks([{"text": "a" * 4}, {"text": "b" * 4}, {"text": "c" * 9}, {"text": "d"}], 10, 8)]
          == [2, 1, 1])
    long_line = "x" * 4500 + " zorblax"
    check("🚨 a long line is sent WHOLE: a name past character 2,000 reaches the matcher",
          parse_diff(f"--- a/m.js\n+++ b/m.js\n@@ -1 +1 @@\n-a\n+{long_line}\n")[0]["text"] == long_line)
    global MAX_LINE_CHARS
    saved_max, MAX_LINE_CHARS = MAX_LINE_CHARS, 10
    try:
        parse_diff("--- a/m.js\n+++ b/m.js\n@@ -1 +1 @@\n-a\n+" + "y" * 11 + "\n")
        refused = False
    except NotChecked as exc:
        refused = "m.js:1" in str(exc) and "y" * 11 not in str(exc)
    MAX_LINE_CHARS = saved_max
    check("🚨 a line longer than the limit is NOT CHECKED (never cut), naming file:line and not its text", refused)
    said_fail: list[str] = []
    check("🚨 a Fail with an EMPTY hits array is a Fail, never a Pass",
          report([{"status": "Fail", "hits": []}], said_fail.append)[0] == "Fail"
          and report([{"status": 3}], said_fail.append)[0] == "Fail"
          and any("without naming where" in x for x in said_fail))
    try:
        report([{"status": "Fail"}, {"status": "NotChecked"}], said_fail.append)
        carried = False
    except NotChecked as exc:
        carried = exc.failed and not exc.hits
    check("🚨 …and a hit-less Fail beside a NotChecked chunk is carried as a rejection", carried)

    # A file that EMBEDS a unified diff: its added lines start `+++ `/`--- ` once git prefixes them.
    embedded = ("diff --git a/doc.md b/doc.md\n--- a/doc.md\n+++ b/doc.md\n@@ -5,2 +5,4 @@\n"
                "--- a/old.txt\n-- gone\n+++ b/zorblax.txt\n+-- kept\n+@@ -1 +1 @@\n+after\n"
                "diff --git a/e.md b/e.md\n--- a/e.md\n+++ b/e.md\n@@ -1,0 +2 @@\n+next file\n")
    check("diff: an added line that LOOKS like a file header is content, on its own file and line",
          parse_diff(embedded) == [{"file": "doc.md", "line": 5, "text": "++ b/zorblax.txt"},
                                   {"file": "doc.md", "line": 6, "text": "-- kept"},
                                   {"file": "doc.md", "line": 7, "text": "@@ -1 +1 @@"},
                                   {"file": "doc.md", "line": 8, "text": "after"},
                                   {"file": "e.md", "line": 2, "text": "next file"}])
    check("diff: '\\ No newline at end of file' is not content",
          parse_diff("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n\\ No newline at end of file\n")
          == [{"file": "x", "line": 1, "text": "b"}])

    # One chunk answered Fail, the next NotChecked: the hit is annotated BEFORE the refusal.
    said: list[str] = []
    mixed = [{"status": "Fail", "hits": [{"file": "a.md", "line": 2, "column": 3, "term": "client#1", "kind": "name"}]},
             {"status": "NotChecked", "reason": "SECRET-REASON zorblax"}]
    try:
        report(mixed, said.append)
        raised = None
    except NotChecked as exc:
        raised = exc
    check("🚨 a NotChecked chunk does not hide another chunk's hit: annotated first, carried on the refusal",
          raised is not None and len(raised.hits) == 1 and any("file=a.md,line=2,col=3::" in x for x in said))
    check("🚨 the instance's free-text reason is never printed",
          raised is not None and "SECRET-REASON" not in str(raised) and "NotChecked" in str(raised)
          and "SECRET-REASON" not in summary("NotChecked", raised.hits, 3, str(raised)))
    said.clear()
    report([{"status": "Fail", "hits": [{"file": "a.md", "line": 1, "column": 1, "term": "client#1",
                                         "kind": "name\n::error::forged|`x`"}]}], said.append)
    check("a remote `kind` cannot forge a workflow command or a table cell",
          len(said) == 1 and "\n" not in said[0] and "::error::forged" not in said[0] and "(nameerrorforgedx)" in said[0]
          and "forged|" not in summary("Fail", [{"file": "a|b\n", "line": 1, "column": 1, "term": "client#1",
                                                 "kind": "k|`"}], 1))
    said.clear()
    _, two = report([{"status": "Fail", "hits": [{"term": "client#1", "file": "a", "line": 1}]},
                     {"status": "Fail", "hits": [{"term": "client#1", "file": "b", "line": 1}]}], said.append)
    check("chunks number independently: the same client#n from two chunks gets two numbers",
          [h["term"] for h in two] == ["client#1", "client#2"])

    with tempfile.TemporaryDirectory() as tmp:
        r = Path(tmp)
        subprocess.run(["git", "init", "-q", "-b", "main", str(r)], check=True)
        for k, v in (("user.email", "t@example.com"), ("user.name", "t")):
            subprocess.run(["git", "-C", str(r), "config", k, v], check=True)
        (r / "a.md").write_text("clean\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "base"], check=True)
        (r / "a.md").write_text("clean\nwritten for zorblax, a client\n")
        (r / "zorblax-notes.md").write_text("fine\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "head"], check=True)

        def run(env: dict, *extra: str) -> tuple[int, str]:
            saved = {k: os.environ.get(k) for k in ("NAME_CHECK_URL", "NAME_CHECK_TOKEN", "GITHUB_STEP_SUMMARY", "GITHUB_RUN_ID")}
            os.environ.update({"GITHUB_STEP_SUMMARY": str(r / "summary.md"), "GITHUB_RUN_ID": str(len(store) + 1)})
            for k in ("NAME_CHECK_URL", "NAME_CHECK_TOKEN"):
                os.environ.pop(k, None)
            os.environ.update(env)
            out: list[str] = []
            import builtins
            real = builtins.print
            builtins.print = lambda *p, **_: out.append(" ".join(str(x) for x in p))
            try:
                rc = main(["--root", str(r), "--repo", "Acme/Demo", *extra])
            finally:
                builtins.print = real
                for k, v in saved.items():
                    if v is None:
                        os.environ.pop(k, None)
                    else:
                        os.environ[k] = v
            return rc, "\n".join(out)

        ok_env = {"NAME_CHECK_URL": url, "NAME_CHECK_TOKEN": "mw_test"}
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("a hit fails the check", rc == 1)
        check("…annotated at file:line:col with the MASKED term", "file=a.md,line=2,col=13::" in out and "client#1" in out)
        check("…and the path hit is reported on the path", "file=zorblax-notes.md::The path names" in out)
        check("…and no line text is printed", "written for" not in out)
        head = subprocess.run(["git", "-C", str(r), "rev-parse", "HEAD"], check=True, capture_output=True,
                              text=True).stdout.strip()
        check("🚨 every request carries the full 40-character commit and the run's id (the instance proves both)",
              bool(sent) and all(c.get("sha") == head and len(c["sha"]) == 40 and isinstance(c.get("runId"), int)
                                 and c["runId"] > 0 for c in sent))
        before = len(sent)
        rc, out = run({**ok_env, "GITHUB_RUN_ID": ""}, "--base", "HEAD~1")
        check("🚨 no GITHUB_RUN_ID FAILS closed, names it, and sends nothing",
              rc == 1 and "GITHUB_RUN_ID" in out and len(sent) == before)
        try:
            provenance(head[:12], "7")
            short_refused = False
        except NotChecked as exc:
            short_refused = "40-character" in str(exc)
        check("🚨 an abbreviated commit id is refused before anything is sent", short_refused)
        rc, out = run(ok_env)
        check("the whole tree is checked without --base, and fails too", rc == 1)
        rc, out = run(ok_env, "--base", "HEAD~1", "--path", "docs", "--path", ":(exclude)a.md")
        check("pathspecs limit what is checked (nothing under docs/, a.md excluded)", rc == 0)
        (r / "a.md").write_text("clean\n")
        (r / "zorblax-notes.md").unlink()
        subprocess.run(["git", "-C", str(r), "commit", "-qam", "fix"], check=True)
        rc, out = run(ok_env, "--base", "HEAD~2")
        check("a clean diff passes", rc == 0 and "Pass" in out)
        rc, out = run({}, "--base", "HEAD~1")
        check("🚨 no URL/token: an empty diff is still a pass (nothing to send)", rc == 0)
        (r / "b.md").write_text("new text\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "more"], check=True)
        rc, out = run({}, "--base", "HEAD~1")
        check("🚨 no URL/token with text to check FAILS closed, naming both", rc == 1 and "NAME_CHECK_URL" in out and "NAME_CHECK_TOKEN" in out)
        rc, out = run({"NAME_CHECK_URL": url, "NAME_CHECK_TOKEN": "mw_wrong"}, "--base", "HEAD~1")
        check("🚨 a refused token FAILS closed", rc == 1 and "NOT CHECKED" in out)
        rc, out = run({"NAME_CHECK_URL": "http://127.0.0.1:9", "NAME_CHECK_TOKEN": "mw_test"}, "--base", "HEAD~1")
        check("🚨 an unreachable endpoint FAILS closed", rc == 1 and "unreachable" in out)
        flags["not_checked"] = True
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("🚨 a NotChecked answer FAILS closed", rc == 1 and "NotChecked" in out)
        flags["not_checked"], flags["rpc_error"] = False, True
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("🚨 a JSON-RPC error FAILS closed, naming the refused call and not its message",
              rc == 1 and "tools/call create: JSON-RPC error -32602" in out and "secret detail" not in out)
        flags["rpc_error"], flags["no_grant"] = False, True
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("🚨 a refused create (no grant) FAILS closed and names the governed grant to propose",
              rc == 1 and "::error::" in out and "namecheck.caller.grant" in out and "NameCheckCaller" in out
              and "secret detail" not in out)
        rc, out = run(ok_env, "--base", "HEAD~1", "--report-only")
        check("…and --report-only (private repositories) still prints the remedy, as a warning",
              rc == 0 and "::warning::" in out and "namecheck.caller.grant" in out)
        flags["no_grant"] = False
        flags["not_checked"], flags["answer"] = False, False
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("🚨 no answer in time FAILS closed", rc == 1 and "no answer within" in out)
        rc, out = run(ok_env, "--base", "HEAD~1", "--report-only")
        check("--report-only downgrades not-checked to a warning", rc == 0 and "::warning::" in out)
        rc, out = run({}, "--base", "HEAD~1", "--unchecked", "warn")
        check("--unchecked warn: a missing secret warns", rc == 0 and "::warning::" in out)
        flags["answer"] = True
        (r / "b.md").write_text("new text\nzorblax\n")
        subprocess.run(["git", "-C", str(r), "commit", "-qam", "hit"], check=True)
        rc, out = run(ok_env, "--base", "HEAD~1", "--unchecked", "warn")
        check("--unchecked warn: a HIT still fails", rc == 1 and "client#1" in out)
        global CHUNK_LINES
        saved_chunk, CHUNK_LINES = CHUNK_LINES, 1
        flags["not_checked_file"] = "c.md"
        (r / "c.md").write_text("unanswerable\n")
        (r / "b.md").write_text("new text\nzorblax\nzorblax again\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "mixed"], check=True)
        rc, out = run(ok_env, "--base", "HEAD~1", "--unchecked", "warn")
        check("🚨 --unchecked warn: a hit in one chunk FAILS though another chunk was not checked",
              rc == 1 and "file=b.md,line=3" in out and "::warning::" in out and "ground truth empty" not in out)
        flags["not_checked_file"], flags["get_error_file"] = None, "c.md"
        rc, out = run(ok_env, "--base", "HEAD~1", "--unchecked", "warn")
        check("🚨 a READ failure after another chunk answered keeps that chunk's hit: annotated, and it FAILS",
              rc == 1 and "file=b.md,line=3" in out and "JSON-RPC error -32603" in out and "read failed" not in out)
        flags["get_error_file"] = None
        CHUNK_LINES = saved_chunk

        # Paths with no textual hunk: a pure rename, a new EMPTY file and a new BINARY file.
        (r / "c.md").write_text("plain\n")
        (r / "b.md").write_text("new text\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "clean again"], check=True)
        subprocess.run(["git", "-C", str(r), "mv", "c.md", "zorblax-renamed.md"], check=True)
        (r / "zorblax-empty.txt").write_text("")
        (r / "zorblax.bin").write_bytes(b"\0\1\2binary")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "paths only"], check=True)
        got = {e["file"] for e in collect_diff(r, "HEAD~1") if e["line"] == 0}
        check("🚨 a pure rename, a new empty file and a new binary file are each checked by PATH",
              {"zorblax-renamed.md", "zorblax-empty.txt", "zorblax.bin"} <= got)
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("…and a change of only such paths FAILS on them",
              rc == 1 and "file=zorblax-renamed.md::The path names" in out and "file=zorblax.bin::The path names" in out
              and "file=zorblax-empty.txt::The path names" in out)

        # A tracked text file larger than any old size cap is read whole in a whole-tree check.
        (r / "big.txt").write_text(("filler line\n" * 200_000) + "tail zorblax\n")
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(r), "commit", "-qm", "big"], check=True)
        check("🚨 a large text file is read WHOLE in a whole-tree check (the name on its last line is sent)",
              any(e["file"] == "big.txt" and e["line"] == 200_001 and "zorblax" in e["text"] for e in collect_tree(r)))
    srv.shutdown()
    print(f"\n{'FAILED' if failures else 'OK'}: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
