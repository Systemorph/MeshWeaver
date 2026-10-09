#!/usr/bin/env python3
"""scan-issue-text-names.py — client names in issue and pull-request TEXT, checked against the CRM, findings filed.

(The name on this first line is load-bearing, like every central guard script here: a lane that
fetches this file checks its first 400 bytes name it.)

WHY (MeshWeaver.Plugins#2845, policy `issue-text-name-scan`)
-----------------------------------------------------------
The client-name check covers committed files. A public repository publishes more than its tree:
issue titles and bodies, issue comments, pull-request titles and descriptions, review comments and
review bodies. GitHub keeps their edit history, and agents and bots write much of that text. This
scanner covers that text. Four rules, all decided by the maintainer:

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

USAGE
-----
  scan-issue-text-names.py --repo Systemorph/MeshWeaver --since-hours 26
  scan-issue-text-names.py --self-test
Environment: NAME_CHECK_URL, NAME_CHECK_TOKEN (secrets); GITHUB_TOKEN (read issues/pulls);
GITHUB_RUN_ID and GITHUB_RUN_ATTEMPT (set by Actions).
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
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


def node_id(repo: str, run_id: int, attempt: str, index: int) -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "-", repo.split("/")[-1]).strip("-") or "repo"
    return f"issue-text-{slug}-{run_id}-{re.sub(r'[^0-9]+', '', attempt) or '1'}-{index}"


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


def submit_and_wait(mesh: Mesh, repo: str, run_id: int, attempt: str, parts: list[list[dict]]) -> list[dict]:
    ids = []
    for i, part in enumerate(parts):
        nid = node_id(repo, run_id, attempt, i)
        node = {"id": nid, "namespace": NAMESPACE, "name": f"{repo} issue text ({i + 1}/{len(parts)})",
                "nodeType": NODE_TYPE,
                "content": {"$type": CONTENT_TYPE, "repo": repo, "source": SOURCE, "runId": run_id, "lines": part}}
        text = mesh.call("create", {"node": json.dumps(node)}).strip()
        if not text.startswith("Created"):
            raise NotChecked("the check request was not created — the build's service user needs the "
                             "namecheck.caller.grant on the CRM-owning instance (Governance ClientNameGate → 'Granting a caller')")
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


# ── self-test ────────────────────────────────────────────────────────────────────────────────

def self_test() -> int:
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    store: dict[str, dict] = {}
    mode = {"answer": "Filed"}

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
                    c = {**c, "status": mode["answer"], "linesChecked": len(c["lines"]), "lines": [],
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
    saved = {k: os.environ.pop(k, None) for k in ("NAME_CHECK_URL", "NAME_CHECK_TOKEN")}
    try:
        check("🚨 no URL/token FAILS closed", run("o/r", 1) == 1)
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
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.repo:
        ap.error("--repo is required")
    if args.since_hours <= 0:
        ap.error("--since-hours must be positive")
    return run(args.repo, args.since_hours)


if __name__ == "__main__":
    sys.exit(main())
