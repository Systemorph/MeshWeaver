#!/usr/bin/env python3
"""ghrest — the ONE GitHub REST client the `.github/scripts` share.

WHY ONE. Seven scripts carried their own REST client: two byte-identical `gh api` wrappers (the pin
checks), a `gh api -i` status parser, a `gh api` JSON reader, and four `urllib` clients that each
decided on their own whether the bearer token may follow a redirect. Two decided right
(`add_unredirected_header`), one decided right only for its raw downloads (a no-redirect opener plus
an unauthenticated manual follow), and the rest never decided — and urllib's default answer is to
FORWARD it. That is the defect class one module removes by construction.

THE REDIRECT RULE (measured, not assumed — `--self-test` proves both halves on two local ports):
  * urllib's `HTTPRedirectHandler.redirect_request` copies EVERY header set with `add_header` or the
    `Request(headers=…)` argument onto the redirected request — to whatever host the `Location` names
    (CPython 3.12–3.14). An artifact `…/zip` or a job `…/logs` read answers a 302 to signed blob
    storage, so a naive client hands the token to a host that is not GitHub.
  * Only `add_unredirected_header` headers stay behind — on EVERY redirect, same host included.
  * Here: the token is sent as an ordinary header and STRIPPED whenever a redirect leaves the
    request's ORIGIN (scheme, host, port). A same-origin redirect (a renamed repository's 301 on
    api.github.com) keeps its credential; signed storage never receives it. Once stripped it stays
    stripped for every later hop.

Two families, kept apart on purpose:
  * the `gh` CLI (`gh_cli`, `gh_cli_status`, `gh_json`, `gh_bytes`) — for scripts whose credential
    and pagination are `gh`'s (`GH_TOKEN`, `--paginate --jq`). The CLI's own HTTP client is Go's,
    which drops `Authorization` on a redirect to another host; nothing here adds a header to it.
  * plain REST over urllib (`request`, `get`, `post`, `paginate`, `download_artifact`) — for scripts
    that must not depend on `gh`'s output policy (it refused escape-bearing job logs, #4534).

Every helper RAISES what the transport raised (`urllib.error.HTTPError` / `URLError`,
`subprocess.CalledProcessError`) — the caller owns its own error text, exit codes and retry policy;
none of that is decided here.

IMPORTING IT. A script beside this file does
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import ghrest
so it resolves whether the script is run directly or loaded by path from another script. A lane
that fetches a script ONE FILE AT A TIME (`mw-core-file`) must fetch this file into the same
directory — `CiRestBudgetGuard` holds every `node-repo-*.yml` lane to that.

  ghrest.py --self-test
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import NoReturn

API = "https://api.github.com"
JSON_ACCEPT = "application/vnd.github+json"
API_VERSION = "2022-11-28"


# ───────────────────────────────────────── reporting ─────────────────────────────────────────

def die(headline: str, lines: list[str]) -> NoReturn:
    """A RED with its reasons: one `::error::` annotation, the detail lines indented, exit 1."""
    print(f"::error::{headline}")
    for line in lines:
        print(f"  {line}")
    sys.exit(1)


# ───────────────────────────────────────── `gh` CLI ──────────────────────────────────────────

def gh_cli(path: str, paginate: bool = False, jq: str | None = None) -> tuple[int, str, str]:
    """`gh api` GET → (returncode, stdout, stderr). With `paginate` + `jq`, gh applies the filter to
    every page — `--paginate` alone emits one JSON document PER PAGE, which no splitter recovers."""
    cmd = ["gh", "api", "-H", f"X-GitHub-Api-Version: {API_VERSION}", path]
    if jq:
        cmd += ["--jq", jq]
    if paginate:
        cmd.insert(2, "--paginate")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    return proc.returncode, proc.stdout, proc.stderr


# 🚨 A PRIVATE REPOSITORY'S NAME IS PRIVATE TEXT (policy `issue-text-name-scan`, Plugins#4364).
# This repository is public, so are its Actions logs, and a fleet lane that discovers the org's
# repositories prints their names — a client's own repository is named after the client. Every
# repository the installation reports as anything but `private: false` therefore has its name
# registered as a log mask the moment it is discovered, BEFORE any caller can print it.
#
# The one exception is this set: PRIVATE repositories whose names this public repository already
# publishes (its AGENTS.md and docs name them as the fleet's own). It is an allowlist, so it fails
# CLOSED — a repository missing from it is masked, never printed. Add a name here only when the
# name is already public; a client's name never belongs here.
PRIVATE_NAMES_ALREADY_PUBLIC = frozenset({
    "Systemorph/Memex",
    "Systemorph/MeshWeaver.Plugins",
    "Systemorph/MeshWeaver.Deploy",
    "Systemorph/MeshWeaver.Crm",
    "Systemorph/MeshWeaver.Education",
    "Systemorph/MeshWeaver.Reinsurance",
    "Systemorph/MeshWeaver.SocialMedia",
    "Systemorph/MeshWeaver.Manufacturing",
    "Systemorph/MeshWeaver.FundReporting",
    "Systemorph/MeshWeaver.Feedback",
})

# One line per repository: the full name, a tab, and the `private` flag as jq renders it.
INSTALLATION_REPO_JQ = '.repositories[] | "\\(.full_name)\\t\\(.private)"'


def parse_installation_repos(out: str) -> list[tuple[str, bool]]:
    """`INSTALLATION_REPO_JQ` output → [(full name, is public)]. Pure.

    PUBLIC ONLY ON AN EXPLICIT `false`: a line with no flag, a `null`, or anything this parser does
    not recognise reads as PRIVATE — so an answer that lost its visibility masks the name instead
    of printing it."""
    rows: dict[str, bool] = {}
    for line in out.splitlines():
        if not line.strip():
            continue
        name, _, flag = line.partition("\t")
        name = name.strip()
        if name:
            rows[name] = rows.get(name, True) and flag.strip() == "false"
    return sorted(rows.items())


def log_masks_for(repos: list[tuple[str, bool]]) -> list[str]:
    """The log masks discovery registers: the full AND the bare name of every repository that is
    not public and not in `PRIVATE_NAMES_ALREADY_PUBLIC` — a lane prints both spellings. Pure."""
    masks: set[str] = set()
    for name, public in repos:
        if public or name in PRIVATE_NAMES_ALREADY_PUBLIC:
            continue
        masks.add(name)
        bare = name.split("/", 1)[-1]
        if bare:
            masks.add(bare)
    return sorted(masks)


def register_log_masks(values: list[str]) -> None:
    """`::add-mask::` for each value, on GitHub Actions only (a local run's terminal is not
    published). The runner masks a value in every LATER line of the job, so this runs before the
    names are returned to anything that could print them."""
    if os.environ.get("GITHUB_ACTIONS") != "true":
        return
    for value in values:
        print(f"::add-mask::{value}", flush=True)


def installation_repo_names() -> tuple[int, list[str], str]:
    """Every repository the App installation reaches (needs an INSTALLATION token) →
    (returncode, sorted unique full names, stderr). The caller decides what an error or an empty
    fleet means — both must be RED, never "nothing to check".

    🚨 Masks every private name it returns (`log_masks_for`) before returning it."""
    rc, out, err = gh_cli("/installation/repositories", paginate=True, jq=INSTALLATION_REPO_JQ)
    repos = parse_installation_repos(out)
    register_log_masks(log_masks_for(repos))
    return rc, [name for name, _ in repos], err


def gh_cli_status(method: str, path: str, fields: dict | None = None,
                  error: type[Exception] = RuntimeError) -> tuple[int, object]:
    """`gh api -X METHOD -i` → (HTTP status, parsed JSON or None). A response with no status line or
    a non-JSON body raises `error` — never folded into an answer."""
    cmd = ["gh", "api", "-X", method, "-i", path]
    for k, v in (fields or {}).items():
        cmd += ["-f", f"{k}={v}"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    # `-i` puts the status line first; the body follows the blank line.
    head, _, body = r.stdout.replace("\r\n", "\n").partition("\n\n")
    status_line = head.split("\n", 1)[0] if head else ""
    try:
        status = int(status_line.split()[1])
    except (IndexError, ValueError):
        raise error(f"gh api {method} {path}: no HTTP status (rc={r.returncode}): {r.stderr.strip()}")
    try:
        parsed = json.loads(body) if body.strip() else None
    except json.JSONDecodeError:
        raise error(f"gh api {method} {path}: HTTP {status}, body is not JSON")
    return status, parsed


def gh_json(path: str, timeout: float = 60) -> object:
    """`gh api PATH` → parsed JSON; a non-zero exit raises `subprocess.CalledProcessError`."""
    out = subprocess.run(["gh", "api", path], capture_output=True, text=True, timeout=timeout, check=True)
    return json.loads(out.stdout)


def gh_bytes(path: str, timeout: float = 60) -> bytes:
    """`gh api PATH` → raw bytes (an artifact zip); a non-zero exit raises CalledProcessError."""
    return subprocess.run(["gh", "api", path], capture_output=True, timeout=timeout, check=True).stdout


# ─────────────────────────────────────── REST over urllib ─────────────────────────────────────

def _origin(url: str) -> tuple[str, str, int | None]:
    parts = urllib.parse.urlsplit(url)
    scheme = (parts.scheme or "").lower()
    port = parts.port or {"http": 80, "https": 443}.get(scheme)
    return scheme, (parts.hostname or "").lower(), port


class _CredentialDroppingRedirect(urllib.request.HTTPRedirectHandler):
    """Follows redirects exactly as urllib does, except that `Authorization` never leaves the
    request's origin."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None and _origin(new.full_url) != _origin(req.full_url):
            new.remove_header("Authorization")
        return new


def urlopen(req: urllib.request.Request, timeout: float = 60):
    """`urllib.request.urlopen`, with the redirect rule above. Use it for ANY bearer-carrying
    request, GitHub's or not."""
    return urllib.request.build_opener(_CredentialDroppingRedirect()).open(req, timeout=timeout)


def request(path: str, token: str | None = None, *, method: str | None = None,
            data: bytes | None = None, accept: str = JSON_ACCEPT, headers: dict | None = None,
            timeout: float = 60, user_agent: str = "meshweaver-ci", api: str = API):
    """Open a GitHub REST resource and return the live response (a context manager). `path` is
    relative to `api` (leading slash optional) or a full URL (a `Link: rel="next"`, an
    `archive_download_url`). Raises HTTPError / URLError."""
    url = path if path.startswith(("http://", "https://")) else f"{api.rstrip('/')}/{path.lstrip('/')}"
    hdrs = {"Accept": accept, "X-GitHub-Api-Version": API_VERSION, "User-Agent": user_agent}
    hdrs.update(headers or {})
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    # The token is attached ONLY when the resolved URL is on `api`'s origin. A full URL (a
    # `Link: rel="next"`, an `archive_download_url`) that names another origin goes out without it:
    # the redirect handler guards hops, and this guards the FIRST request — otherwise `paginate()`
    # would re-attach the token to an off-origin next-page URL a redirect had just stripped it from.
    if token and _origin(url) == _origin(api):
        req.add_header("Authorization", f"Bearer {token}")
    return urlopen(req, timeout=timeout)


def _json(raw: bytes) -> object:
    return json.loads(raw.decode("utf-8")) if raw.strip() else None


def get(path: str, token: str | None = None, **kw) -> object:
    """GET → parsed JSON (an empty body is None)."""
    with request(path, token, **kw) as response:
        return _json(response.read())


def post(path: str, token: str | None = None, payload: object = None, **kw) -> object:
    """POST a JSON body (or an empty one) → parsed JSON (an empty body is None)."""
    data = b"" if payload is None else json.dumps(payload).encode("utf-8")
    extra = dict(kw.pop("headers", None) or {})
    if payload is not None:
        extra.setdefault("Content-Type", "application/json")
    with request(path, token, method="POST", data=data, headers=extra, **kw) as response:
        return _json(response.read())


class PageLimitExceeded(Exception):
    """A listing has more pages than the caller allowed. Refused, never truncated."""


class NotAList(ValueError):
    """A listing page decoded to something other than a JSON array."""


def next_link(header: str | None) -> str | None:
    for part in (header or "").split(","):
        bits = part.split(";")
        if len(bits) >= 2 and any(b.strip() == 'rel="next"' for b in bits[1:]):
            return bits[0].strip().lstrip("<").rstrip(">")
    return None


def paginate(path: str, token: str | None = None, max_pages: int = 50, **kw) -> list:
    """Every item of a JSON-array listing, following `Link: rel="next"` to the LAST page. A page that
    is not a list raises NotAList; more than `max_pages` raises PageLimitExceeded."""
    items: list = []
    url: str | None = path
    for _ in range(max_pages):
        with request(url, token, **kw) as response:  # type: ignore[arg-type]
            page = _json(response.read())
            url = next_link(response.headers.get("Link"))
        if not isinstance(page, list):
            raise NotAList(f"{path.split('?')[0]} did not answer a list")
        items.extend(page)
        if not url:
            return items
    raise PageLimitExceeded(f"{path.split('?')[0]} has more than {max_pages} pages")


class TooLarge(Exception):
    """A download exceeded the caller's byte cap. Refused, never truncated."""


def download_artifact(repo_or_url: str, artifact_id: int | str | None = None, token: str | None = None,
                      max_bytes: int | None = None, timeout: float = 120, **kw) -> bytes:
    """An Actions artifact's zip — `repo` + `artifact_id`, or its `archive_download_url`. GitHub
    answers a 302 to signed blob storage; the token stays on GitHub's side of it."""
    path = repo_or_url if artifact_id is None else f"repos/{repo_or_url}/actions/artifacts/{artifact_id}/zip"
    with request(path, token, timeout=timeout, **kw) as response:
        raw = response.read() if max_bytes is None else response.read(max_bytes + 1)
    if max_bytes is not None and len(raw) > max_bytes:
        raise TooLarge(f"{path} exceeds {max_bytes} bytes")
    return raw


# ───────────────────────────────────────── self-test ─────────────────────────────────────────

def self_test() -> int:
    """Two local servers on two ports = two origins. Origin A answers 302 to origin B; B records
    whether Authorization arrived. Negative control: a naive urllib Request (`add_header`) through
    the stock opener DOES forward it — so a green here measures the handler, not a server that
    cannot see headers."""
    import http.server
    import threading

    seen: dict[str, str | None] = {}
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        print(f"  {'ok  ' if ok else 'FAIL'}  {name}")
        if not ok:
            failures.append(name)

    def serve(handler_cls):
        srv = http.server.HTTPServer(("127.0.0.1", 0), handler_cls)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        return srv

    class Storage(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            seen["B" + self.path] = self.headers.get("Authorization")
            if self.path == "/list-off-2":
                body = b"[2]"
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            body = b"PK-zip-bytes"
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    storage = serve(Storage)
    b_url = f"http://127.0.0.1:{storage.server_address[1]}"

    class Api(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _send(self, code: int, body: bytes = b"", hdrs: dict | None = None) -> None:
            self.send_response(code)
            for k, v in (hdrs or {}).items():
                self.send_header(k, v)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            seen["A" + self.path] = self.headers.get("Authorization")
            port = self.server.server_address[1]
            if self.path.endswith("/zip"):
                self._send(302, hdrs={"Location": f"{b_url}/signed/blob.zip?sig=1"})
            elif self.path == "/moved":
                self._send(301, hdrs={"Location": f"http://127.0.0.1:{port}/here"})
            elif self.path == "/hop":
                self._send(302, hdrs={"Location": f"{b_url}/bounce"})
            elif self.path == "/list?page=1":
                self._send(200, b'[1, 2]', {"Link": f'<http://127.0.0.1:{port}/list?page=2>; rel="next"'})
            elif self.path == "/list?page=2":
                self._send(200, b'[3]')
            elif self.path == "/list-off?page=1":
                # the next page lives on ANOTHER origin (storage) — its Link must not carry the token
                self._send(200, b'[1]', {"Link": f'<{b_url}/list-off-2>; rel="next"'})
            elif self.path == "/endless":
                self._send(200, b'[0]', {"Link": f'<http://127.0.0.1:{port}/endless>; rel="next"'})
            else:
                self._send(200, b'{"ok": true}', {"Content-Type": "application/json"})

        def do_POST(self):
            seen["A-POST" + self.path] = self.headers.get("Authorization")
            got = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            if self.path == "/post-hop":
                # urllib follows a 301/302/303 answer to a POST as a GET — with the headers.
                self._send(303, hdrs={"Location": f"{b_url}/after-post"})
                return
            self._send(201, json.dumps({"got": json.loads(got or b"null")}).encode("utf-8"))

    api = serve(Api)
    a_url = f"http://127.0.0.1:{api.server_address[1]}"
    try:
        # ── the negative control: what urllib does on its own ──
        naive = urllib.request.Request(f"{a_url}/repos/o/r/actions/artifacts/1/zip")
        naive.add_header("Authorization", "Bearer naive-token")
        with urllib.request.urlopen(naive, timeout=10) as r:
            r.read()
        check("NEGATIVE CONTROL: a naive add_header request through stock urllib FORWARDS the token "
              "to the other origin", seen.get("B/signed/blob.zip?sig=1") == "Bearer naive-token")
        seen.clear()
        naive_post = urllib.request.Request(f"{a_url}/post-hop", data=b"{}", method="POST",
                                            headers={"Authorization": "Bearer naive-post"})
        with urllib.request.urlopen(naive_post, timeout=10) as r:
            r.read()
        check("NEGATIVE CONTROL: a naive POST answered 303 forwards Request(headers=…) to the other origin",
              seen.get("B/after-post") == "Bearer naive-post")
        seen.clear()
        unred =urllib.request.Request(f"{a_url}/repos/o/r/actions/artifacts/1/zip")
        unred.add_unredirected_header("Authorization", "Bearer unred-token")
        with urllib.request.urlopen(unred, timeout=10) as r:
            r.read()
        check("control: add_unredirected_header does NOT forward it (what the safe copies relied on)",
              seen.get("B/signed/blob.zip?sig=1") is None and
              seen.get("A/repos/o/r/actions/artifacts/1/zip") == "Bearer unred-token")
        seen.clear()

        # ── ghrest ──
        blob = download_artifact("o/r", 1, token="secret", api=a_url)
        check("download_artifact follows the 302 and returns the blob", blob == b"PK-zip-bytes")
        check("the API origin received the token", seen.get("A/repos/o/r/actions/artifacts/1/zip") == "Bearer secret")
        check("the storage origin received NO Authorization", "B/signed/blob.zip?sig=1" in seen
              and seen["B/signed/blob.zip?sig=1"] is None)
        try:
            download_artifact("o/r", 1, token="secret", api=a_url, max_bytes=4)
            check("a download over the cap is refused", False)
        except TooLarge:
            check("a download over the cap is refused", True)
        seen.clear()
        check("a same-origin redirect is followed", get("/moved", "secret", api=a_url) == {"ok": True})
        check("...and KEEPS the token on its own origin", seen.get("A/here") == "Bearer secret")
        seen.clear()
        with request("/hop", "secret", api=a_url) as r:
            r.read()
        check("a cross-origin GET redirect drops the token", seen.get("B/bounce", "absent") is None)
        with request("/post-hop", "secret", method="POST", data=b"{}", api=a_url) as r:
            r.read()
        check("a POST answered 303 to another origin is followed WITHOUT the token",
              seen.get("A-POST/post-hop") == "Bearer secret" and seen.get("B/after-post", "absent") is None)
        mesh = urllib.request.Request(f"{a_url}/post-hop", data=b"{}", method="POST",
                                      headers={"Authorization": "Bearer mesh-token"})
        with urlopen(mesh, timeout=10) as r:
            r.read()
        check("urlopen applies the same rule to a hand-built non-GitHub request",
              seen.get("B/after-post", "absent") is None)
        check("paginate follows Link to the last page", paginate("/list?page=1", "secret", api=a_url) == [1, 2, 3])
        seen.clear()
        with request(f"{b_url}/signed/blob.zip?sig=1", "secret", api=a_url) as r:
            r.read()
        check("an absolute URL on ANOTHER origin gets NO token on its first request",
              seen.get("B/signed/blob.zip?sig=1", "absent") is None)
        seen.clear()
        check("paginate follows a Link onto another origin",
              paginate("/list-off?page=1", "secret", api=a_url) == [1, 2])
        check("...the API page carried the token, the off-origin next page did NOT",
              seen.get("A/list-off?page=1") == "Bearer secret" and seen.get("B/list-off-2", "absent") is None)
        try:
            paginate("/endless", "secret", api=a_url, max_pages=3)
            check("paginate refuses past max_pages", False)
        except PageLimitExceeded:
            check("paginate refuses past max_pages", True)
        check("post sends the JSON body and returns the parsed answer",
              post("/x", "secret", {"a": 1}, api=a_url) == {"got": {"a": 1}}
              and seen.get("A-POST/x") == "Bearer secret")
        check("origin compares scheme, host and port",
              _origin("https://api.github.com/x") == _origin("https://API.github.com:443/y")
              and _origin("https://api.github.com/x") != _origin("http://api.github.com/x")
              and _origin("http://h:1/") != _origin("http://h:2/"))
        check("next_link reads rel=next among other rels",
              next_link('<https://a/1>; rel="prev", <https://a/3>; rel="next"') == "https://a/3"
              and next_link('<https://a/1>; rel="last"') is None)

        # ── private repository names never reach a public log (Plugins#4364) ──
        listing = ("acme/Public\tfalse\n"
                   "acme/Acme.Client\ttrue\n"
                   "Systemorph/Memex\ttrue\n"
                   "acme/NoFlag\n"
                   "acme/Nulled\tnull\n")
        repos = parse_installation_repos(listing)
        check("discovery returns every repository, public or not",
              [n for n, _ in repos] == ["Systemorph/Memex", "acme/Acme.Client", "acme/NoFlag",
                                        "acme/Nulled", "acme/Public"])
        masks = log_masks_for(repos)
        check("a private repository is masked by its full AND its bare name",
              "acme/Acme.Client" in masks and "Acme.Client" in masks)
        check("FAIL CLOSED: a repository whose visibility is missing or null is masked",
              {"acme/NoFlag", "NoFlag", "acme/Nulled", "Nulled"} <= set(masks))
        check("control: a public repository is not masked",
              "acme/Public" not in masks and "Public" not in masks)
        check("control: a private repository whose name is already public is not masked",
              "Systemorph/Memex" not in masks and "Memex" not in masks)
        check("NEGATIVE CONTROL: the old full-name-only listing carries no visibility, so every "
              "name is masked rather than printed",
              set(log_masks_for(parse_installation_repos("acme/Acme.Client\nacme/Public\n")))
              >= {"acme/Acme.Client", "acme/Public"})
        import contextlib
        import io
        saved = os.environ.get("GITHUB_ACTIONS")
        try:
            os.environ["GITHUB_ACTIONS"] = "true"
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed):
                register_log_masks(masks)
            check("on Actions every mask is registered as ::add-mask::",
                  printed.getvalue().splitlines() == [f"::add-mask::{m}" for m in masks])
            os.environ["GITHUB_ACTIONS"] = "false"
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed):
                register_log_masks(masks)
            check("off Actions nothing is printed", printed.getvalue() == "")
            # THE DOOR ITSELF: discovery registers the masks BEFORE it hands a name to a caller.
            os.environ["GITHUB_ACTIONS"] = "true"
            real_gh_cli = globals()["gh_cli"]
            asked: list[str | None] = []

            def fake_gh_cli(path, paginate=False, jq=None):
                asked.append(jq)
                return 0, listing, ""
            globals()["gh_cli"] = fake_gh_cli
            try:
                printed = io.StringIO()
                with contextlib.redirect_stdout(printed):
                    rc, names, _ = installation_repo_names()
            finally:
                globals()["gh_cli"] = real_gh_cli
            check("installation_repo_names asks for each repository's visibility",
                  asked == [INSTALLATION_REPO_JQ])
            check("installation_repo_names returns the full names",
                  rc == 0 and names == [n for n, _ in repos])
            check("installation_repo_names masks the private names before returning them",
                  "::add-mask::acme/Acme.Client" in printed.getvalue().splitlines()
                  and "::add-mask::Acme.Client" in printed.getvalue().splitlines()
                  and "::add-mask::acme/Public" not in printed.getvalue())
        finally:
            if saved is None:
                os.environ.pop("GITHUB_ACTIONS", None)
            else:
                os.environ["GITHUB_ACTIONS"] = saved
    finally:
        api.shutdown()
        storage.shutdown()
        api.server_close()
        storage.server_close()
    print(f"ghrest: {'FAILED: ' + '; '.join(failures) if failures else 'all cases passed'}")
    return 1 if failures else 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    print(__doc__)
    sys.exit(2)
