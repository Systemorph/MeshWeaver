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
                 whose only grant is create + read on that namespace);
    3. read      each node back until the watcher there has answered: ``Pass``, ``Fail`` with
                 ``client#n`` at file:line:column, or ``NotChecked``;
    4. report    one ``::error`` annotation per hit — the masked term and the kind that matched,
                 never a name, never the line's text.

🚨 FAIL CLOSED. No URL, no token, an unreachable or refusing endpoint, a timeout or a
``NotChecked`` answer is "not checked", and "not checked" FAILS (exit 1) — an unchecked diff is
never a clean one. ``--report-only`` (private repositories) downgrades it to a warning.

Inputs (environment): ``NAME_CHECK_URL`` — base URL of the CRM-owning instance;
``NAME_CHECK_TOKEN`` — the build's ``mw_`` token. Both are secrets.

    check-client-names.py --base origin/main          # a pull request
    check-client-names.py                             # the whole tree
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
MAX_TEXT = 2000               # a longer line is cut; a name in the first 2,000 characters is still seen
MAX_FILE_BYTES = 2_000_000
HTTP_TIMEOUT_S = 60
HTTP_ATTEMPTS = 3
RETRY_DELAY_S = float(os.environ.get("NAME_CHECK_RETRY_DELAY_S", "5"))
POLL_S = float(os.environ.get("NAME_CHECK_POLL_S", "3"))
ANSWER_TIMEOUT_S = float(os.environ.get("NAME_CHECK_TIMEOUT_S", "180"))
STATUS_NAMES = {1: "Requested", 2: "Pass", 3: "Fail", 4: "NotChecked"}


class NotChecked(RuntimeError):
    """The check could not be performed. Never a pass."""


# ── collect ──────────────────────────────────────────────────────────────────────────────────

def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True,
                          text=True, encoding="utf-8", errors="replace").stdout


def parse_diff(diff: str) -> list[dict]:
    """Added lines of a unified diff (``-U0``) as ``{file, line, text}``, plus one line-0 entry per
    added or renamed path — a path can name a client too. Pure."""
    out: list[dict] = []
    path: str | None = None
    old: str | None = None
    line = 0
    for raw in diff.splitlines():
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
        m = re.match(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", raw)
        if m:
            line = int(m.group(1))
            continue
        if path is None:
            continue
        if raw.startswith("+"):
            text = raw[1:]
            if text.strip():
                out.append({"file": path, "line": line, "text": text[:MAX_TEXT]})
            line += 1
        elif raw.startswith(" "):
            line += 1
    return out


def collect_diff(root: Path, base: str) -> list[dict]:
    diff = git(root, "diff", "--no-color", "--no-ext-diff", "-U0", "--find-renames", f"{base}...HEAD")
    return parse_diff(diff)


def collect_tree(root: Path) -> list[dict]:
    out: list[dict] = []
    for rel in git(root, "ls-files", "-z").split("\0"):
        if not rel:
            continue
        out.append({"file": rel, "line": 0, "text": rel})
        p = root / rel
        try:
            if not p.is_file() or p.stat().st_size > MAX_FILE_BYTES:
                continue
            data = p.read_bytes()
        except OSError:
            continue
        if b"\0" in data[:8192]:
            continue
        for i, text in enumerate(data.decode("utf-8", "replace").splitlines(), start=1):
            if text.strip():
                out.append({"file": rel, "line": i, "text": text[:MAX_TEXT]})
    return out


def chunks(lines: list[dict], size: int = CHUNK_LINES) -> list[list[dict]]:
    return [lines[i:i + size] for i in range(0, len(lines), size)] or []


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
                    return self._parse(raw, (resp.headers.get("Content-Type") or "").lower(), body.get("id"))
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


def submit_and_wait(mesh: Mesh, repo: str, sha: str, run: str, parts: list[list[dict]]) -> list[dict]:
    """Creates one node per chunk, then reads each back until answered. Returns the answered
    contents. Raises NotChecked for anything short of an answer."""
    ids = []
    for i, part in enumerate(parts):
        nid = node_id(repo, sha, run, i)
        node = {"id": nid, "namespace": NAMESPACE, "name": f"{repo}@{sha[:12]} ({i + 1}/{len(parts)})",
                "nodeType": NODE_TYPE,
                "content": {"$type": CONTENT_TYPE, "repo": repo, "sha": sha, "lines": part}}
        text = mesh.call("create", {"node": json.dumps(node)}).strip()
        if not text.startswith("Created"):
            raise NotChecked("the check request was not created (the build's service user may lack create on "
                             f"{NAMESPACE})")
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
                raise NotChecked(f"no answer within {ANSWER_TIMEOUT_S:g}s — is the name-check watcher installed "
                                 "on the CRM-owning instance?")
            time.sleep(POLL_S)
    return [answers[n] for n in ids]


# ── report ───────────────────────────────────────────────────────────────────────────────────

def esc(s: str) -> str:
    return s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A").replace(",", "%2C").replace(":", "%3A")


def report(answers: list[dict], say=None) -> tuple[str, list[dict]]:
    """Folds the answers: ('NotChecked', []) wins over ('Fail', hits) over ('Pass', []). Hits are
    re-numbered across chunks so one client keeps one number in this run. Pure but for ``say``."""
    for a in answers:
        if status_of(a) == "NotChecked" or status_of(a) not in ("Pass", "Fail"):
            raise NotChecked(f"the instance answered {status_of(a)}: {a.get('reason') or 'no reason given'}")
    say = say or print
    hits: list[dict] = []
    renumber: dict[tuple[int, str], str] = {}
    for i, a in enumerate(answers):
        for h in a.get("hits") or []:
            key = (i, str(h.get("term")))
            if key not in renumber:
                # Chunks number independently; a new number per (chunk, term) keeps the mask honest.
                renumber[key] = f"client#{len(renumber) + 1}"
            hits.append({**h, "term": renumber[key]})
    for h in hits:
        f, line, col = h.get("file", ""), int(h.get("line") or 0), int(h.get("column") or 1)
        what = f"{h['term']} ({h.get('kind', 'name')})"
        if line <= 0:
            say(f"::error file={esc(f)}::The path names a client of ours: {what}. Rename it with a neutral placeholder.")
        else:
            say(f"::error file={esc(f)},line={line},col={col}::A client of ours is named here: {what}. "
                "Replace it with a neutral placeholder (AGENTS.md, 'Confidential terms').")
    return ("Fail" if hits else "Pass"), hits


def summary(status: str, hits: list[dict], lines: int, reason: str | None = None) -> str:
    out = ["## No client names", ""]
    if status == "Pass":
        out.append(f"✅ {lines} line(s) checked against the CRM on the CRM-owning instance; no client named.")
    elif status == "Fail":
        clients = len({h['term'] for h in hits})
        out.append(f"❌ {len(hits)} hit(s) naming {clients} client(s) in {lines} line(s). Terms are masked; "
                   "the CRM decides who is a client.")
        out += ["", "| File | Line | Col | Term | Kind |", "|---|---|---|---|---|"]
        out += [f"| `{h.get('file')}` | {h.get('line')} | {h.get('column')} | {h['term']} | {h.get('kind')} |" for h in hits[:200]]
    else:
        out.append(f"⛔ Not checked: {reason}. An unchecked diff is not a clean one.")
    return "\n".join(out) + "\n"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--base", help="check the added lines of BASE...HEAD; omit to check the whole tree")
    ap.add_argument("--root", default=".")
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "local/repo"))
    ap.add_argument("--report-only", action="store_true", help="private repositories: warn instead of failing")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()

    root = Path(a.root).resolve()
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    lines: list[dict] = []
    try:
        sha = git(root, "rev-parse", "HEAD").strip()
        lines = collect_diff(root, a.base) if a.base else collect_tree(root)
        if not lines:
            print("No added text to check.")
            status, hits = "Pass", []
        else:
            url, token = os.environ.get("NAME_CHECK_URL", ""), os.environ.get("NAME_CHECK_TOKEN", "")
            if not url or not token:
                missing = [n for n, v in (("NAME_CHECK_URL", url), ("NAME_CHECK_TOKEN", token)) if not v]
                raise NotChecked("missing " + ", ".join(missing) + " — the build's service user on the CRM-owning "
                                 "instance and its endpoint (Settings → Secrets → Actions)")
            run = f"{os.environ.get('GITHUB_RUN_ID', str(int(time.time())))}-{os.environ.get('GITHUB_RUN_ATTEMPT', '1')}"
            answers = submit_and_wait(Mesh(url, token), a.repo, sha, run, chunks(lines))
            status, hits = report(answers)
    except NotChecked as exc:
        level = "warning" if a.report_only else "error"
        print(f"::{level}::No client names — NOT CHECKED: {exc}")
        if step_summary:
            Path(step_summary).open("a").write(summary("NotChecked", [], len(lines), str(exc)))
        return 0 if a.report_only else 1
    if step_summary:
        Path(step_summary).open("a").write(summary(status, hits, len(lines)))
    print(f"No client names: {status} — {len(lines)} line(s), {len(hits)} hit(s).")
    return 1 if status == "Fail" and not a.report_only else 0


# ── self-test: a fake CRM-owning instance whose "watcher" knows one synthetic client ─────────

def self_test() -> int:
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    import tempfile

    store: dict[str, dict] = {}
    flags = {"answer": True, "not_checked": False}
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
            if name == "create":
                node = json.loads(args["node"])
                store[node["namespace"] + "/" + node["id"]] = node["content"]
                text = "Created: " + node["id"]
            else:
                path = args["path"].lstrip("@")
                c = store.get(path)
                if c is None:
                    text = "Not found"
                else:
                    if flags["answer"] and c.get("lines"):
                        if flags["not_checked"]:
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
            os.environ.update({"GITHUB_STEP_SUMMARY": str(r / "summary.md"), "GITHUB_RUN_ID": str(len(store))})
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
        rc, out = run(ok_env)
        check("the whole tree is checked without --base, and fails too", rc == 1)
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
        flags["not_checked"], flags["answer"] = False, False
        rc, out = run(ok_env, "--base", "HEAD~1")
        check("🚨 no answer in time FAILS closed", rc == 1 and "no answer within" in out)
        rc, out = run(ok_env, "--base", "HEAD~1", "--report-only")
        check("--report-only downgrades not-checked to a warning", rc == 0 and "::warning::" in out)
    srv.shutdown()
    print(f"\n{'FAILED' if failures else 'OK'}: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
