#!/usr/bin/env python3
"""scan-issue-text-names.py — client names in issue and pull-request TEXT (and in named committed files), checked against the CRM.

(The name on this first line is load-bearing, like every central guard script here: a lane that
fetches this file checks its first 400 bytes name it.)

WHY (MeshWeaver.Plugins#2845, policy `issue-text-name-scan`)
-----------------------------------------------------------
A public repository publishes more than its tree: issue titles and bodies, issue comments,
pull-request titles and descriptions, review comments and review bodies. GitHub keeps their edit
history, and agents and bots write much of that text. This scanner covers that text. Four rules,
all decided by the maintainer:

  * DETERMINISTIC. It reads text over REST, splits it into lines and submits them. No model runs
    anywhere, here or in the check.
  * EVERY AUTHOR, bots included. Nothing is skipped by who wrote it.
  * THE TERMS COME FROM THE CRM. This script holds no names. It submits the text as a
    `Governance/NameCheck` with `source: issue-text` to the instance that owns the CRM (the same
    `NAME_CHECK_URL` / `NAME_CHECK_TOKEN` the committed-text check uses). The watcher there matches
    the text against the same ground truth.
  * FINDINGS ONLY. It never edits or deletes anything. The instance answers `Filed` and sends every
    finding to its private review and ONE masked triage item. It never returns a hit to the caller,
    because anyone can write a comment on a public repository and a verdict would turn the check
    back into an oracle (Governance `ClientNameGate` → "Issue and pull-request text").

Each line names its item by REST reference: `issues/<n>`, `issues/comments/<id>`,
`pulls/comments/<id>` or `pulls/<n>/reviews/<id>`. The instance proves that every line is in the item
it names, and that `runId` is a live run of this repository, before it reads the CRM.

FAIL CLOSED. No URL or token, an unreachable or refusing endpoint, a timeout, a `NotChecked`
answer, or a VERDICT where `Filed` was due (an instance whose Governance package predates #2845) all
FAIL the run (exit 1). The script prints counts only, never a line's text.

COMMITTED FILES (`--commit-files`, policy `committed-text-names-from-crm`)
-------------------------------------------------------------------------
The same caller checks NAMED files of the commit a CI run is on — e.g. a private repository's
publicly-bound tree (Systemorph/Memex: mesh/Home, mesh/Provider, Memex.Website, README, AGENTS,
docs/). It reads each file from `HEAD` (never the working tree), submits every non-blank line plus
one path entry (line 0) per file as `Governance/NameCheck` requests with `source: commit`, the full
`sha` and `runId`. The instance proves every line is in that commit before it reads the CRM, and
answers a VERDICT (`Pass`/`Fail`, hits masked as `client#n`). The term list never leaves the
instance; this script prints `file:line` per hit and nothing else — never the line, the term or its
kind. Any answer but an all-`Pass` is red, and so is a pathspec that selects no file (a guard whose
subject moved checks nothing).

USAGE
-----
  scan-issue-text-names.py --repo Systemorph/MeshWeaver --since-hours 26
  scan-issue-text-names.py --repo Systemorph/Memex --commit-files mesh/Home docs README.md --exclude mesh/Deployments
  scan-issue-text-names.py --self-test
Environment: NAME_CHECK_URL, NAME_CHECK_TOKEN (secrets); GITHUB_TOKEN (read issues/pulls — issue-text
mode only); GITHUB_RUN_ID and GITHUB_RUN_ATTEMPT (set by Actions).
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

NAMESPACE = "Governance/NameChecks"
NODE_TYPE = "Governance/NameCheck"
CONTENT_TYPE = "NameCheckContent"
SOURCE = "issue-text"
SOURCE_COMMIT = "commit"
PROTOCOL_VERSION = "2025-06-18"
CHUNK_LINES = 2000                 # the watcher's cap is 5,000 lines per node
CHUNK_CHARS = 1_000_000
MAX_LINE_CHARS = 1_000_000         # sent WHOLE or refused, never cut
HTTP_TIMEOUT_S = 60
HTTP_ATTEMPTS = 3
RETRY_DELAY_S = float(os.environ.get("NAME_CHECK_RETRY_DELAY_S", "5"))
POLL_S = float(os.environ.get("NAME_CHECK_POLL_S", "3"))
ANSWER_TIMEOUT_S = float(os.environ.get("NAME_CHECK_TIMEOUT_S", "300"))
STATUS_NAMES = {1: "Requested", 2: "Pass", 3: "Fail", 4: "NotChecked", 5: "Filed"}
GITHUB_API = os.environ.get("GITHUB_API_URL", "https://api.github.com")
MAX_PAGES = 400                    # 40,000 items per listing; a longer listing is refused, never truncated


class NotChecked(RuntimeError):
    """The scan could not be completed. Never a pass."""


# ── collect (pure parts) ─────────────────────────────────────────────────────────────────────

def lines_of(ref: str, title: str | None, body: str | None) -> list[dict]:
    """One entry per non-blank line of the item's published text, numbered over ``title + "\\n" +
    body`` (the text the instance reads back), each trimmed. A trimmed line is still a substring of
    that text, which is what the instance proves. Pure."""
    text = (title or "") + "\n" + (body or "")
    out = []
    for i, raw in enumerate(text.split("\n"), start=1):
        line = raw.rstrip("\r").strip()
        if not line:
            continue
        if len(line) > MAX_LINE_CHARS:
            raise NotChecked(f"{ref}:{i} is longer than {MAX_LINE_CHARS} characters — refused, never cut")
        out.append({"file": ref, "line": i, "text": line})
    return out


def chunks(lines: list[dict], size: int = CHUNK_LINES, chars: int = CHUNK_CHARS) -> list[list[dict]]:
    out, cur, used = [], [], 0
    for item in lines:
        n = len(item["text"])
        if cur and (len(cur) >= size or used + n > chars):
            out.append(cur)
            cur, used = [], 0
        cur.append(item)
        used += n
    if cur:
        out.append(cur)
    return out


def node_id(repo: str, run_id: int, attempt: str, index: int, prefix: str = "issue-text") -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "-", repo.split("/")[-1]).strip("-") or "repo"
    return f"{prefix}-{slug}-{run_id}-{re.sub(r'[^0-9]+', '', attempt) or '1'}-{index}"


# ── committed files (pure parts + git) ───────────────────────────────────────────────────────

def file_lines(path: str, text: str | None) -> list[dict]:
    """One path entry (line 0, text = the path — a path can name a client too) and, unless the file
    is binary (``text`` None), one entry per non-blank line, numbered over the file, trimmed. A
    trimmed line is a substring of the file, which is what the instance proves. Pure."""
    out = [{"file": path, "line": 0, "text": path}]
    if text is None:
        return out
    for i, raw in enumerate(text.split("\n"), start=1):
        line = raw.rstrip("\r").strip()
        if not line:
            continue
        if len(line) > MAX_LINE_CHARS:
            raise NotChecked(f"{path}:{i} is longer than {MAX_LINE_CHARS} characters — refused, never cut")
        out.append({"file": path, "line": i, "text": line})
    return out


def decode_file(path: str, data: bytes) -> str | None:
    """The file's text as the instance reads it back (a UTF-8 BOM dropped), or None for a binary file
    (a NUL in its first 8 KiB). A text file that is not UTF-8 is NotChecked: its lines could not be
    proven, and skipping it would pass it unread. Pure."""
    if b"\0" in data[:8192]:
        return None
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        raise NotChecked(f"{path} is neither binary nor UTF-8 — refused, never skipped") from exc


def _git(root: str, *args: str) -> bytes:
    try:
        return subprocess.check_output(["git", *args], cwd=root, stderr=subprocess.PIPE)
    except (subprocess.CalledProcessError, OSError) as exc:
        raise NotChecked(f"git {args[0]} failed ({type(exc).__name__})") from exc


def committed_files(root: str, pathspecs: list[str], excludes: list[str]) -> list[tuple[str, bytes]]:
    """(path, bytes) of every regular file under ``pathspecs`` in the commit at ``HEAD`` — read from
    the commit, never the working tree, so what is submitted is what the instance proves — minus the
    paths at or under an ``excludes`` prefix. Symbolic links and submodules are not files of the
    commit and are left out."""
    return [f for spec in pathspecs for f in _committed_files_one(root, spec, excludes)]


def committed_files_each(root: str, pathspecs: list[str], excludes: list[str]) -> tuple[list[tuple[str, bytes]], list[str]]:
    """(the deduplicated union of every pathspec's files, the pathspecs that select NOTHING after the
    excludes). Each pathspec is checked on its own: a named tree that moved away must not hide behind
    another that still holds files — that guard would check one tree and report both."""
    seen: dict[str, bytes] = {}
    empty = []
    for spec in pathspecs:
        files = _committed_files_one(root, spec, excludes)
        if not files:
            empty.append(spec)
        for path, data in files:
            seen.setdefault(path, data)
    return sorted(seen.items()), empty


def _committed_files_one(root: str, pathspec: str, excludes: list[str]) -> list[tuple[str, bytes]]:
    listing = _git(root, "ls-tree", "-r", "-z", "--full-tree", "HEAD", "--", pathspec).decode("utf-8")
    out = []
    for entry in filter(None, listing.split("\0")):
        meta, path = entry.split("\t", 1)
        mode, kind, _obj = meta.split(" ")
        if kind != "blob" or mode not in ("100644", "100755"):
            continue
        if any(path == e.rstrip("/") or path.startswith(e.rstrip("/") + "/") for e in excludes):
            continue
        out.append((path, _git(root, "cat-file", "blob", f"HEAD:{path}")))
    return out


# Stands in for a path that itself names a client (a line-0 hit), in every position printed.
WITHHELD_PATH = "<path withheld: it names a client>"


def commit_verdict(answers: list[dict]) -> tuple[int, str, list[str]]:
    """(exit code, one line, ``file:line`` per hit) for the answered chunks of a COMMIT request. Only
    all-``Pass`` is green. A hit is printed as its position alone: never the term (even masked), its
    kind or the line. Pure."""
    statuses = [status_of(a) for a in answers]
    if any(s == "NotChecked" for s in statuses):
        reason = next((a.get("reason") for a in answers if status_of(a) == "NotChecked"), None) or "no reason given"
        return 1, f"NOT CHECKED — the instance refused at least one chunk: {reason}", []
    if any(s == "Fail" for s in statuses):
        hits = {(str(h.get("file")), str(h.get("line"))) for a in answers if status_of(a) == "Fail"
                for h in (a.get("hits") or [])}
        # 🚨 A line-0 hit means the PATH itself names a client: that path is never printed, at any
        # position, or the check would publish the very name it found.
        named = sorted({f for f, ln in hits if ln == "0"})
        alias = {f: f"{WITHHELD_PATH}#{i + 1}" for i, f in enumerate(named)}
        where = sorted(f"{alias.get(f, f)}:{ln}" for f, ln in hits)
        return 1, f"FAIL — {len(where)} position(s) in publicly-bound files name a client (names are not printed)", where
    if statuses and all(s == "Pass" for s in statuses):
        checked = sum(int(a.get("linesChecked") or 0) for a in answers)
        return 0, f"checked — {checked} line(s) in {len(answers)} request(s), no client name", []
    return 1, f"NOT CHECKED — unexpected answer(s) to a commit request: {sorted(set(statuses))}", []


def status_of(content: dict) -> str:
    s = content.get("status", "Requested")
    return STATUS_NAMES.get(s, str(s)) if isinstance(s, int) else str(s)


def verdict(answers: list[dict]) -> tuple[int, str]:
    """(exit code, one line) for the answered chunks. Only all-``Filed`` is green. Pure."""
    statuses = [status_of(a) for a in answers]
    if any(s == "NotChecked" for s in statuses):
        reason = next((a.get("reason") for a in answers if status_of(a) == "NotChecked"), None) or "no reason given"
        return 1, f"NOT CHECKED — the instance refused at least one chunk: {reason}"
    if any(s in ("Pass", "Fail") for s in statuses):
        return 1, ("NOT CHECKED — the instance answered a VERDICT to issue text, so it does not know `source: issue-text` "
                   "(its Governance package predates MeshWeaver.Plugins#2845); nothing about the verdict is printed")
    if statuses and all(s == "Filed" for s in statuses):
        checked = sum(int(a.get("linesChecked") or 0) for a in answers)
        return 0, f"checked — {checked} line(s) in {len(answers)} request(s); any finding is in the instance's private review and triage"
    return 1, f"NOT CHECKED — unexpected answer(s): {sorted(set(statuses))}"


# ── GitHub (REST) ────────────────────────────────────────────────────────────────────────────

def _next_link(header: str | None) -> str | None:
    for part in (header or "").split(","):
        m = re.search(r'<([^>]+)>\s*;\s*rel="next"', part)
        if m:
            return m.group(1)
    return None


def gh_list(path: str, token: str) -> list[dict]:
    """Every page of a REST listing, or NotChecked — a partial listing would read as 'nothing there'."""
    url = f"{GITHUB_API}/{path}"
    items: list[dict] = []
    for _ in range(MAX_PAGES):
        req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}",
                                                   "Accept": "application/vnd.github+json",
                                                   "X-GitHub-Api-Version": "2022-11-28",
                                                   "User-Agent": "scan-issue-text-names"})
        try:
            with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_S) as resp:
                page = json.loads(resp.read().decode("utf-8"))
                nxt = _next_link(resp.headers.get("Link"))
        except (urllib.error.URLError, TimeoutError, OSError, json.JSONDecodeError) as exc:
            raise NotChecked(f"GitHub listing {path.split('?')[0]} could not be read ({type(exc).__name__})") from exc
        if not isinstance(page, list):
            raise NotChecked(f"GitHub listing {path.split('?')[0]} did not answer a list")
        items.extend(page)
        if not nxt:
            return items
        url = nxt
    raise NotChecked(f"GitHub listing {path.split('?')[0]} has more than {MAX_PAGES} pages — refused, never truncated")


def collect(repo: str, since: str, token: str, lister=gh_list) -> list[dict]:
    """Every line of every item updated since ``since`` (ISO-8601 Z): issues and pull requests (title +
    body), issue comments, review comments, and EVERY non-blank review body of every pull request
    updated in the window. A review's ``submitted_at`` does not move when its body is edited, so
    filtering on it would miss an edited review; the pull request's own ``updated_at`` does.
    ``lister`` is the REST listing, a parameter so the self-test runs with no network."""
    q = urllib.parse.quote(since)
    out: list[dict] = []
    issues = lister(f"repos/{repo}/issues?state=all&since={q}&per_page=100", token)
    for it in issues:
        out += lines_of(f"issues/{it['number']}", it.get("title"), it.get("body"))
    for c in lister(f"repos/{repo}/issues/comments?since={q}&per_page=100", token):
        out += lines_of(f"issues/comments/{c['id']}", None, c.get("body"))
    for c in lister(f"repos/{repo}/pulls/comments?since={q}&per_page=100", token):
        out += lines_of(f"pulls/comments/{c['id']}", None, c.get("body"))
    for it in issues:
        if "pull_request" not in it:
            continue
        for r in lister(f"repos/{repo}/pulls/{it['number']}/reviews?per_page=100", token):
            if (r.get("body") or "").strip():
                out += lines_of(f"pulls/{it['number']}/reviews/{r['id']}", None, r.get("body"))
    return out


# ── MCP over HTTP (cut from core #5852's check-client-names.py to what this needs) ───────────

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
                    call = f"{method} {params.get('name')}" if method == "tools/call" else method
                    try:
                        return self._parse(raw, (resp.headers.get("Content-Type") or "").lower(), body.get("id"))
                    except NotChecked as exc:
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
                                      "clientInfo": {"name": "scan-issue-text-names", "version": "1"}})
            self._post("notifications/initialized", {}, notification=True)
            self._ready = True
        result = self._post("tools/call", {"name": tool, "arguments": arguments}) or {}
        text = "".join(c.get("text", "") for c in result.get("content", [])
                       if isinstance(c, dict) and c.get("type") == "text")
        if result.get("isError"):
            raise NotChecked(f"{tool} was refused by the check endpoint")
        return text


GRANT_REMEDY = ("the build's service user needs the namecheck.caller.grant on the CRM-owning instance "
                "(Governance ClientNameGate → 'Granting a caller')")


# Anchored refusal formats, matched at the START of the instance's answer only — the rest of a refusal
# may echo the submitted (user-written) text, so a substring anywhere proves nothing (#6409 review).
UNKNOWN_MEMBER = re.compile(r"^(?:Error: )?refused (?:create|patch) of [^\s:]+: unknown content member\(s\) ((?:'[^'\n]*'(?:, )?)+) for type ")
# The `create` verb answers a refused write as "Error: creating node: <the validator's message>"
# (MeshOperations.Create), so the access refusal arrives behind that prefix. Without it the one
# refusal this lane actually meets (measured on run 38115962221: the instance logged "Access denied:
# Create permission required for node 'Governance/NameChecks/…'") was reported as unclassified.
ACCESS_DENIED = re.compile(r"^(?:Error: )?(?:creating node: )?Access denied: Create permission required for node '"
                           + re.escape(NAMESPACE) + r"/")


def refusal_reason(text: str) -> str:
    """Why a create was refused, CLASSIFIED by an anchored prefix — never the instance's text itself,
    which this public log must not carry (a refusal can echo the request). Pure."""
    t = (text or "").strip()
    m = UNKNOWN_MEMBER.match(t)
    if m and "'source'" in m.group(1):
        return ("the instance does not know the issue-text request shape — its Governance package predates "
                "0.11 (MeshWeaver.Plugins#3257); it reaches the instance with the next Governance publication")
    if ACCESS_DENIED.match(t):
        return ("the instance refused it for access: the caller holds no Create on " + NAMESPACE + " — "
                + GRANT_REMEDY)
    return ("the instance refused it for a reason this script does not classify (the instance's log names it); "
            "if it is an access refusal, " + GRANT_REMEDY)


def request_content(repo: str, run_id: int, part: list[dict], sha: str | None = None) -> dict:
    """The request's content: issue text by default; with ``sha``, lines of that commit. Pure."""
    if sha is None:
        return {"$type": CONTENT_TYPE, "repo": repo, "source": SOURCE, "runId": run_id, "lines": part}
    return {"$type": CONTENT_TYPE, "repo": repo, "source": SOURCE_COMMIT, "sha": sha, "runId": run_id, "lines": part}


def submit_and_wait(mesh: Mesh, repo: str, run_id: int, attempt: str, parts: list[list[dict]],
                    sha: str | None = None) -> list[dict]:
    ids = []
    what, prefix = ("issue text", "issue-text") if sha is None else ("committed files", "commit")
    for i, part in enumerate(parts):
        nid = node_id(repo, run_id, attempt, i, prefix)
        node = {"id": nid, "namespace": NAMESPACE, "name": f"{repo} {what} ({i + 1}/{len(parts)})",
                "nodeType": NODE_TYPE, "content": request_content(repo, run_id, part, sha)}
        text = mesh.call("create", {"node": json.dumps(node)}).strip()
        if not text.startswith("Created"):
            raise NotChecked("the check request was not created — " + refusal_reason(text))
        ids.append(nid)
    answers: dict[str, dict] = {}
    deadline = time.monotonic() + ANSWER_TIMEOUT_S
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
                raise NotChecked(f"no answer within {ANSWER_TIMEOUT_S:g}s — is the name-check watcher installed on the CRM-owning instance?")
            time.sleep(POLL_S)
    return [answers[n] for n in ids]


def run(repo: str, since_hours: float) -> int:
    url, token = os.environ.get("NAME_CHECK_URL", ""), os.environ.get("NAME_CHECK_TOKEN", "")
    gh_token = os.environ.get("GITHUB_TOKEN", "")
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    missing = [n for n, v in (("NAME_CHECK_URL", url), ("NAME_CHECK_TOKEN", token), ("GITHUB_TOKEN", gh_token)) if not v]
    if missing:
        print(f"::error::issue-text name scan NOT CHECKED — missing {', '.join(missing)}")
        return 1
    if not run_id.isdigit() or int(run_id) <= 0:
        print("::error::issue-text name scan NOT CHECKED — GITHUB_RUN_ID is not set; the instance proves a live run of this repository")
        return 1
    since = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=since_hours)).strftime("%Y-%m-%dT%H:%M:%SZ")
    try:
        lines = collect(repo, since, gh_token)
        print(f"issue-text name scan: {repo} since {since} — {len(lines)} line(s) from "
              f"{len({l['file'] for l in lines})} item(s)")
        if not lines:
            print("  nothing was published in the window — nothing to check")
            return 0
        answers = submit_and_wait(Mesh(url, token), repo, int(run_id), os.environ.get("GITHUB_RUN_ATTEMPT", "1"), chunks(lines))
    except NotChecked as exc:
        print(f"::error::issue-text name scan NOT CHECKED — {exc}")
        return 1
    code, line = verdict(answers)
    print(("::error::" if code else "  ") + f"issue-text name scan {line}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Issue-text name scan — {repo}\n\n{'❌' if code else '✅'} {line}\n")
    return code


def run_commit(repo: str, pathspecs: list[str], excludes: list[str], root: str = ".") -> int:
    """Check the named files of the commit at ``HEAD`` against the CRM. Red unless every chunk is
    ``Pass``; prints ``file:line`` per hit and nothing else about it."""
    url, token = os.environ.get("NAME_CHECK_URL", ""), os.environ.get("NAME_CHECK_TOKEN", "")
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    missing = [n for n, v in (("NAME_CHECK_URL", url), ("NAME_CHECK_TOKEN", token)) if not v]
    if missing:
        print(f"::error::committed-file name check NOT CHECKED — missing {', '.join(missing)}")
        return 1
    if not run_id.isdigit() or int(run_id) <= 0:
        print("::error::committed-file name check NOT CHECKED — GITHUB_RUN_ID is not set; the instance proves a live run of this repository")
        return 1
    try:
        sha = _git(root, "rev-parse", "HEAD").decode("ascii").strip()
        files, empty = committed_files_each(root, pathspecs, excludes)
        if empty:
            raise NotChecked(f"the pathspec(s) {empty} select no file at HEAD — a guard whose subject moved checks nothing")
        lines: list[dict] = []
        binary = 0
        for path, data in files:
            text = decode_file(path, data)
            binary += text is None
            lines += file_lines(path, text)
        print(f"committed-file name check: {repo}@{sha[:12]} — {len(lines)} line(s) from {len(files)} file(s) "
              f"({binary} binary: path only)")
        answers = submit_and_wait(Mesh(url, token), repo, int(run_id), os.environ.get("GITHUB_RUN_ATTEMPT", "1"),
                                  chunks(lines), sha=sha)
    except NotChecked as exc:
        print(f"::error::committed-file name check NOT CHECKED — {exc}")
        return 1
    code, line, where = commit_verdict(answers)
    for pos in where:
        path, _, num = pos.rpartition(":")
        if path.startswith(WITHHELD_PATH):
            # No file= annotation: GitHub would print the path beside it.
            print(f"::error::a client name appears in a file whose path itself names a client ({path}, line {num}; 0 = the path itself) — not printed")
            continue
        print(f"::error file={path},line={max(int(num) if num.isdigit() else 0, 1)}::a client name appears here (line {num}; 0 = the path itself) — not printed")
    print(("::error::" if code else "  ") + f"committed-file name check {line}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Committed-file name check — {repo}\n\n{'❌' if code else '✅'} {line}\n")
            f.writelines(f"- `{pos}`\n" for pos in where)
    return code


# ── self-test ────────────────────────────────────────────────────────────────────────────────

def self_test() -> int:
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    store: dict[str, dict] = {}
    created: list[dict] = []
    mode: dict = {"answer": "Filed"}

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
            req = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
            rid, method = req.get("id"), req.get("method")
            if method == "notifications/initialized":
                return self._send(None, 202)
            if method == "initialize":
                return self._send({"jsonrpc": "2.0", "id": rid, "result": {"protocolVersion": PROTOCOL_VERSION}})
            name, args = req["params"]["name"], req["params"]["arguments"]
            if name == "create":
                node = json.loads(args["node"])
                store[node["namespace"] + "/" + node["id"]] = node["content"]
                text = "Created: " + node["id"]
            else:
                path = args["path"].lstrip("@")
                c = store[path]
                if c.get("lines"):
                    created.append(c)
                    c = {**c, "status": mode["answer"], "linesChecked": len(c["lines"]), "lines": [],
                         "hits": mode.get("hits", []),
                         "reason": "provenance not established" if mode["answer"] == "NotChecked" else None}
                    store[path] = c
                text = json.dumps({"path": path, "content": c})
            self._send({"jsonrpc": "2.0", "id": rid, "result": {"content": [{"type": "text", "text": text}]}})

    srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{srv.server_address[1]}"
    global RETRY_DELAY_S, POLL_S, ANSWER_TIMEOUT_S
    RETRY_DELAY_S, POLL_S, ANSWER_TIMEOUT_S = 0.01, 0.01, 2
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        print(("✓ " if ok else "✗ ") + name)
        if not ok:
            failures.append(name)

    got = lines_of("issues/12", "Rollout plan", "first\r\n\r\n  second line  \r\n")
    check("lines: numbered over title + body as the instance reads it, blanks skipped, trimmed",
          got == [{"file": "issues/12", "line": 1, "text": "Rollout plan"},
                  {"file": "issues/12", "line": 2, "text": "first"},
                  {"file": "issues/12", "line": 4, "text": "second line"}])
    joined = "Rollout plan" + "\n" + "first\r\n\r\n  second line  \r\n"
    check("lines: every submitted line is a substring of the text the instance proves against",
          all(l["text"] in joined for l in got))
    check("lines: a comment (no title) starts at line 2, after the empty title",
          lines_of("issues/comments/3", None, "hi")[0]["line"] == 2)
    check("chunks split at the size", [len(c) for c in chunks([{"text": ""}] * 5, 2)] == [2, 2, 1])

    pages = {
        "issues?": [{"number": 7, "title": "T7", "body": "b7"},
                    {"number": 8, "title": "PR8", "body": None, "pull_request": {}}],
        "issues/comments?": [{"id": 70, "body": "c70"}],
        "pulls/comments?": [{"id": 80, "body": "r80"}],
        "pulls/8/reviews?": [{"id": 81, "body": "review body", "submitted_at": "2026-10-09T10:00:00Z"},
                             {"id": 82, "body": "", "submitted_at": "2026-10-09T10:00:00Z"},
                             {"id": 83, "body": "old review", "submitted_at": "2026-01-01T00:00:00Z"}],
    }
    asked: list[str] = []

    def fake(path, _token):
        asked.append(path)
        for key, value in pages.items():
            if path.split("/", 3)[3].startswith(key):
                return value
        raise AssertionError(path)

    refs = sorted({l["file"] for l in collect("o/r", "2026-10-09T00:00:00Z", "t", fake)})
    check("collect: issues, PRs, issue comments, review comments and every non-blank review body of an updated PR",
          refs == ["issues/7", "issues/8", "issues/comments/70", "pulls/8/reviews/81", "pulls/8/reviews/83", "pulls/comments/80"])
    check("🚨 collect: a review SUBMITTED before the window (its body possibly edited since) is still scanned",
          "pulls/8/reviews/83" in refs)
    check("collect: reviews are listed only for pull requests", not any("pulls/7/" in a for a in asked))

    mesh = Mesh(url, "mw_test")
    parts = chunks(lines_of("issues/1", "a", "b"))
    check("🚨 an all-Filed answer is green", verdict(submit_and_wait(mesh, "o/r", 42, "1", parts))[0] == 0)
    mode["answer"] = "NotChecked"
    code, line = verdict(submit_and_wait(mesh, "o/r", 43, "1", parts))
    check("🚨 NotChecked is RED and carries the instance's reason", code == 1 and "provenance" in line)
    mode["answer"] = "Fail"
    code, line = verdict(submit_and_wait(mesh, "o/r", 44, "1", parts))
    check("🚨 a VERDICT where Filed was due is RED (an instance that predates #2845), and prints no hit",
          code == 1 and "predates" in line and "client#" not in line)
    mode["answer"] = "Pass"
    check("NEGATIVE CONTROL: a Pass is not a green either — only Filed is",
          verdict(submit_and_wait(mesh, "o/r", 45, "1", parts))[0] == 1)
    check("node ids share a run key, so the instance files ONE triage item per run",
          node_id("Systemorph/MeshWeaver", 9, "2", 0).rsplit("-", 1)[0] == node_id("Systemorph/MeshWeaver", 9, "2", 3).rsplit("-", 1)[0])
    check("refusal: an unknown 'source' member names the Governance version, not the grant",
          "predates" in refusal_reason("Error: refused create of Governance/NameChecks/x-0: unknown content member(s) 'source' for type NameCheckContent (NodeType 'Governance/NameCheck')."))
    check("🚨 refusal: 'member' or 'permission' echoed from issue text classifies as NOTHING",
          "does not classify" in refusal_reason("Error: create failed: line 'source member of permission' rejected")
          and "does not classify" in refusal_reason("Error: refused create of x: unknown content member(s) 'other' for type T"))
    denied = "Access denied: Create permission required for node 'Governance/NameChecks/issue-text-r-1-1-0'"
    check("🚨 refusal: an access refusal AS THE create VERB ANSWERS IT names the grant",
          all("refused it for access" in refusal_reason(p + denied) and "namecheck.caller.grant" in refusal_reason(p + denied)
              for p in ("Error: creating node: ", "Error: ", "")))
    check("NEGATIVE CONTROL: a Create refusal on ANOTHER namespace, or one only quoted later in the text, is not classified as the grant",
          "does not classify" in refusal_reason("Error: creating node: Access denied: Create permission required for node 'Other/x'")
          and "does not classify" in refusal_reason("Error: creating node: line rejected: " + denied))
    check("🚨 refusal: the instance's text is never echoed", "SECRETWORD" not in refusal_reason("Error: SECRETWORD rejected"))

    # ── committed files ──
    got = file_lines("docs/a.md", "# Title\r\n\r\n  body line  \n")
    check("commit lines: a path entry (line 0) first, then non-blank lines numbered over the file, trimmed",
          got == [{"file": "docs/a.md", "line": 0, "text": "docs/a.md"},
                  {"file": "docs/a.md", "line": 1, "text": "# Title"},
                  {"file": "docs/a.md", "line": 3, "text": "body line"}])
    check("commit lines: a binary file is submitted by its path only", file_lines("x.png", None) == [{"file": "x.png", "line": 0, "text": "x.png"}])
    check("decode: a UTF-8 BOM is dropped, so line 1 is a substring of what the instance reads",
          decode_file("a", b"\xef\xbb\xbfhi\n") == "hi\n")
    check("decode: a NUL in the first 8 KiB is binary", decode_file("a", b"PNG\0\1") is None)
    try:
        decode_file("a", b"\xff\xfe bad")
        check("🚨 decode: non-UTF-8 text is NotChecked, never skipped", False)
    except NotChecked:
        check("🚨 decode: non-UTF-8 text is NotChecked, never skipped", True)

    import tempfile
    with tempfile.TemporaryDirectory() as repo_dir:
        def g(*a):
            subprocess.check_output(["git", *a], cwd=repo_dir, stderr=subprocess.STDOUT)
        g("init", "-q")
        for rel, body in (("docs/a.md", b"one\n"), ("docs/sub/b.md", b"two\n"), ("mesh/Home/c.md", b"three\n"),
                          ("mesh/Deployments/d.json", b"{}\n"), ("other/e.md", b"four\n")):
            p = os.path.join(repo_dir, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as f:
                f.write(body)
        g("add", "-A")
        g("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "t")
        with open(os.path.join(repo_dir, "docs/a.md"), "wb") as f:
            f.write(b"uncommitted edit\n")
        files = dict(committed_files(repo_dir, ["docs", "mesh"], ["mesh/Deployments"]))
        check("commit files: the named pathspecs, recursively, minus the excluded prefix",
              sorted(files) == ["docs/a.md", "docs/sub/b.md", "mesh/Home/c.md"])
        check("🚨 commit files: read from HEAD, never the working tree (what the instance proves)",
              files.get("docs/a.md") == b"one\n")
        check("NEGATIVE CONTROL: an exclude prefix is a path prefix, not a substring",
              "mesh/Home/c.md" in dict(committed_files(repo_dir, ["mesh"], ["mesh/Ho"])))
        union, empty = committed_files_each(repo_dir, ["docs", "moved-away"], [])
        check("🚨 commit files: a pathspec that selects nothing is named even when another still selects files",
              empty == ["moved-away"] and sorted(dict(union)) == ["docs/a.md", "docs/sub/b.md"])
        union, empty = committed_files_each(repo_dir, ["docs", "docs/sub"], [])
        check("NEGATIVE CONTROL: overlapping pathspecs both select files and the union is deduplicated",
              empty == [] and [p for p, _ in union] == ["docs/a.md", "docs/sub/b.md"])
        check("commit files: a pathspec emptied by the excludes counts as selecting nothing",
              committed_files_each(repo_dir, ["mesh/Deployments"], ["mesh/Deployments"])[1] == ["mesh/Deployments"])

    sha = "a" * 40
    parts = chunks(file_lines("docs/a.md", "one\n"))
    content = request_content("o/r", 46, parts[0], sha)
    check("commit request: source 'commit', the full sha and the run id",
          content["source"] == "commit" and content["sha"] == sha and content["runId"] == 46)
    mode.update(answer="Pass", hits=[])
    created.clear()
    check("🚨 commit: an all-Pass answer is green",
          commit_verdict(submit_and_wait(mesh, "o/r", 46, "1", parts, sha=sha))[0] == 0)
    check("commit: the node submitted carries source 'commit' and an id of its own run key",
          created and created[-1].get("source") == "commit" and created[-1].get("sha") == sha)
    mode.update(answer="Fail", hits=[{"file": "docs/a.md", "line": 1, "column": 1, "term": "client#1", "kind": "name"}])
    code, line, where = commit_verdict(submit_and_wait(mesh, "o/r", 47, "1", parts, sha=sha))
    check("🚨 commit: a Fail is RED and reports file:line only — no term, no kind",
          code == 1 and where == ["docs/a.md:1"] and "client#" not in line + "".join(where) and "name" not in "".join(where))
    mode.update(answer="Fail", hits=[{"file": "docs/acme-x.md", "line": 0, "column": 1, "term": "client#1", "kind": "name"},
                                    {"file": "docs/acme-x.md", "line": 3, "column": 1, "term": "client#1", "kind": "name"},
                                    {"file": "docs/b.md", "line": 2, "column": 1, "term": "client#1", "kind": "name"}])
    code, line, where = commit_verdict(submit_and_wait(mesh, "o/r", 50, "1", parts, sha=sha))
    check("🚨 commit: a path that names a client (line-0 hit) is withheld at EVERY position, content hits included",
          code == 1 and "acme" not in line + "".join(where)
          and sorted(where) == sorted([f"{WITHHELD_PATH}#1:0", f"{WITHHELD_PATH}#1:3", "docs/b.md:2"]))
    check("NEGATIVE CONTROL: a path with only content hits is still printed",
          "docs/b.md:2" in where)
    mode.update(answer="NotChecked", hits=[])
    check("🚨 commit: NotChecked is RED", commit_verdict(submit_and_wait(mesh, "o/r", 48, "1", parts, sha=sha))[0] == 1)
    mode.update(answer="Filed")
    check("NEGATIVE CONTROL: commit: a Filed answer is not a green — only Pass is",
          commit_verdict(submit_and_wait(mesh, "o/r", 49, "1", parts, sha=sha))[0] == 1)
    check("commit and issue-text node ids never collide",
          node_id("o/r", 1, "1", 0, "commit") != node_id("o/r", 1, "1", 0))

    saved = {k: os.environ.pop(k, None) for k in ("NAME_CHECK_URL", "NAME_CHECK_TOKEN")}
    try:
        # The fail-closed path prints a real ::error:: annotation; swallow it here so a GREEN self-test
        # does not paint a red annotation on the run that executes it.
        import contextlib, io
        with contextlib.redirect_stdout(io.StringIO()):
            rc = run("o/r", 1)
            rc_commit = run_commit("o/r", ["docs"], [])
        check("🚨 no URL/token FAILS closed", rc == 1)
        check("🚨 commit mode: no URL/token FAILS closed", rc_commit == 1)
    finally:
        for k, v in saved.items():
            if v is not None:
                os.environ[k] = v
    srv.shutdown()
    if failures:
        print(f"::error::scan-issue-text-names.py self-test: {len(failures)} case(s) failed")
        return 1
    print("self-test: every case behaved")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--repo", help="owner/name — the repository this run belongs to (the instance proves it)")
    ap.add_argument("--since-hours", type=float, default=26.0,
                    help="scan items updated in this many hours (default 26: a daily schedule with overlap)")
    ap.add_argument("--commit-files", nargs="+", metavar="PATHSPEC",
                    help="check these files of the commit at HEAD (source: commit) instead of issue text")
    ap.add_argument("--exclude", action="append", default=[], metavar="PREFIX",
                    help="with --commit-files: leave out paths at or under this prefix (repeatable)")
    ap.add_argument("--root", default=".", help="with --commit-files: the repository checkout (default: cwd)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.repo:
        ap.error("--repo is required")
    if args.commit_files:
        return run_commit(args.repo, args.commit_files, args.exclude, args.root)
    if args.exclude:
        ap.error("--exclude needs --commit-files")
    if args.since_hours <= 0:
        ap.error("--since-hours must be positive")
    return run(args.repo, args.since_hours)


if __name__ == "__main__":
    sys.exit(main())
