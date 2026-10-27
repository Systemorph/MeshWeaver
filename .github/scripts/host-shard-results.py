#!/usr/bin/env python3
"""host-shard-results.py — bring one host shard's results tree into a GitHub-hosted job, BY ITS RECEIPTS.

H9 of the standing build hosts (Doc/Architecture/BuildHosts → "Core's own route"). A host
`test-shard-<i>` stage leaves the `test` job's results tree under
`$MW_BUILD_ARTIFACTS/testResults-shard<i>/` (deploy/core-tests/core-tests.py: `test/**/*.trx`,
`test/test-results.log`, `collected-logs/**`); after the stage SUCCEEDS the host uploads every file
to its artifact store, reads it back, and only then reports one receipt per file
`{"stage","unit","key","sha256","bytes"}`.

Core is a PUBLIC repository and runs NO job on the self-hosted pool and holds no handle on the
runners' shared store, so this script fetches everything from the build API over HTTPS with the
run's own GitHub OIDC token (build-follow.py's request — the same gate as the stage log and the
receipts):

    GET {api}/{repo}/{sha}/artifacts?channel=<c>              the receipts (build-artifacts-fetch.py's reader)
    GET {api}/{repo}/{sha}/artifacts/{sha256}?channel=<c>     one receipt's bytes, by its content hash

and writes the tree under --out with its relative paths PRESERVED (two `.trx` of one name in two
projects must not collide), so the caller uploads --out as `testResults-shard<i>-attempt<a>` and
collect-results consolidates a host shard exactly as an Actions one.

    host-shard-results.py --api-url https://control.example.com/api/builds --sha <head> --shard 3 \
        --out "$GITHUB_WORKSPACE/results" [--repo MeshWeaver] [--channel latest]
    host-shard-results.py --self-test

RED, never green, naming what is wrong, on:
  * no receipt for the shard's tree (a RED host shard hands nothing over — its own red is the verdict);
  * a receipt whose unit escapes the tree (`..`, absolute, control characters) or lies outside its shape
    (a FOREIGN file: not a `.trx` under test/, not test/test-results.log, not under collected-logs/);
  * two receipts naming one path with different content;
  * no test/test-results.log, or one carrying no `[CI] <project> exit=<n>` marker (not the `test` job's tree);
  * a fetch the API refuses (401/403/404/5xx, a redirect), a body larger or smaller than its receipt
    (TRUNCATED), or bytes whose sha256 differs from the receipt;
  * an --out that already holds anything, or a written tree that is not EXACTLY the receipts' set.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import os
import re
import shutil
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable

HERE = os.path.dirname(os.path.abspath(__file__))
SHA = re.compile(r"^[0-9a-f]{40}$")
REPO = re.compile(r"^[A-Za-z0-9._-]+$")
MARKER = re.compile(r"^\[CI\] \S+ exit=\d+", re.MULTILINE)
LOG = "test/test-results.log"
CHUNK = 1 << 20


class Red(Exception):
    """A refusal with its reason."""


def _load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _inert(text: str) -> str:
    """A message inside an `::error::` command: one line, workflow-command data escaped."""
    return (text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            .replace("##[", "##​[").replace("::", ":​:"))


def allowed(rel: str) -> bool:
    """The `test` job's artifact shape (its upload-artifact `path:` list), nothing else."""
    return (rel == LOG
            or (rel.startswith("test/") and rel.endswith(".trx"))
            or rel.startswith("collected-logs/"))


def plan(receipts: list[dict], shard: int) -> dict[str, dict]:
    """The shard's tree as {relative path: receipt}, from receipts already shape-checked by
    build-artifacts-fetch.receipts_of. Units outside `testResults-shard<i>/` are other units (a
    host hands every file over after each stage) and are not this tree's."""
    prefix = f"testResults-shard{shard}/"
    tree: dict[str, dict] = {}
    for r in receipts:
        unit = r["unit"]
        if not unit.startswith(prefix):
            continue
        rel = unit[len(prefix):]
        parts = rel.split("/")
        if (not rel or rel.startswith("/") or "\\" in rel or any(p in ("", ".", "..") for p in parts)
                or any(ord(ch) < 32 or ch == "\x7f" for ch in rel)):
            raise Red(f"receipt unit {unit!r} escapes or malforms the results tree")
        if not allowed(rel):
            raise Red(f"receipt unit {unit!r} is FOREIGN to the `test` job's results tree "
                      f"(only test/**/*.trx, {LOG} and collected-logs/**)")
        if rel in tree:
            if (tree[rel]["sha256"], tree[rel]["bytes"]) == (r["sha256"], r["bytes"]):
                continue
            raise Red(f"two receipts name {unit!r} with different content")
        tree[rel] = r
    if not tree:
        raise Red(f"shard {shard} handed back NO results (no receipt under {prefix}) — a host shard that ran "
                  "hands its tree over after it succeeds; nothing to consolidate is never a pass")
    if LOG not in tree:
        raise Red(f"shard {shard} handed back no {LOG} — its tree is not the `test` job's")
    return tree


def fetch_tree(tree: dict[str, dict], out: str, get: Callable[[str, str, int], None]) -> list[str]:
    """Write every receipt's bytes to out/<rel>, each verified against its receipt (sha256, size);
    then hold the written tree to EXACTLY the receipts' set."""
    if os.path.exists(out) and (not os.path.isdir(out) or os.listdir(out)):
        raise Red(f"--out {out} already holds files — the written tree must be exactly the receipts'")
    os.makedirs(out, exist_ok=True)
    root = os.path.realpath(out)
    for rel, r in sorted(tree.items()):
        dest = os.path.join(root, *rel.split("/"))
        if not os.path.realpath(dest).startswith(root + os.sep):
            raise Red(f"{rel!r} resolves outside --out")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        get(r["sha256"], dest, r["bytes"])
        h = hashlib.sha256()
        with open(dest, "rb") as fh:
            for chunk in iter(lambda: fh.read(CHUNK), b""):
                h.update(chunk)
        size = os.path.getsize(dest)
        if size != r["bytes"]:
            raise Red(f"{rel} landed as {size} bytes but its receipt attests {r['bytes']} — TRUNCATED or padded")
        if h.hexdigest() != r["sha256"]:
            raise Red(f"{rel} landed as sha256 {h.hexdigest()[:12]}… but its receipt attests {r['sha256'][:12]}…")
    written = sorted(os.path.relpath(os.path.join(d, f), root).replace(os.sep, "/")
                     for d, _, fs in os.walk(root) for f in fs)
    if written != sorted(tree):
        extra = sorted(set(written) - set(tree))
        missing = sorted(set(tree) - set(written))
        raise Red(f"the written tree is not the receipts' set (extra {extra[:5]}, missing {missing[:5]})")
    with open(os.path.join(root, *LOG.split("/")), encoding="utf-8", errors="replace") as fh:
        if not MARKER.search(fh.read()):
            raise Red(f"{LOG} carries no `[CI] <project> exit=<n>` marker — not the `test` job's results")
    return written


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect would carry the OIDC bearer to wherever it points — refuse it instead."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def api_getter(base: str, token: str, channel: str,
               opener: urllib.request.OpenerDirector | None = None) -> Callable[[str, str, int], None]:
    """GET {base}/{sha256}?channel=… with the bearer, streamed to dest, reading at most bytes+1 so an
    oversized body is detected without being swallowed whole."""
    op = opener or urllib.request.build_opener(_NoRedirect)

    def get(sha256: str, dest: str, size: int) -> None:
        url = f"{base}/{sha256}?" + urllib.parse.urlencode({"channel": channel})
        req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}",
                                                   "Accept": "application/octet-stream"}, method="GET")
        try:
            with op.open(req, timeout=120) as resp, open(dest, "wb") as fh:
                if resp.status != 200:
                    raise Red(f"the build API answered HTTP {resp.status} for object {sha256[:12]}…")
                left = size + 1
                while left > 0:
                    chunk = resp.read(min(CHUNK, left))
                    if not chunk:
                        break
                    fh.write(chunk)
                    left -= len(chunk)
        except urllib.error.HTTPError as e:
            raise Red(f"the build API answered HTTP {e.code} for object {sha256[:12]}… "
                      "(404: it serves no object for that receipt — GET {repo}/{sha}/artifacts/{sha256}; "
                      "403: the token's run is not this run's; 3xx: a redirect is refused)")
        except (urllib.error.URLError, OSError) as e:
            raise Red(f"fetching object {sha256[:12]}… failed: {e}")
    return get


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--api-url", default="")
    ap.add_argument("--repo", default="")
    ap.add_argument("--sha", default="")
    ap.add_argument("--shard", default="")
    ap.add_argument("--channel", default="latest")
    ap.add_argument("--audience", default="")
    ap.add_argument("--out", default="")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = dict(os.environ)
    try:
        repo = (a.repo or env.get("GITHUB_REPOSITORY", "").split("/", 1)[-1]).strip()
        sha = (a.sha or env.get("GITHUB_SHA", "")).strip().lower()
        if not a.api_url.startswith("https://"):
            raise Red(f"--api-url {a.api_url!r} is not https — refusing to send an OIDC token there "
                      "(empty: the admission named no build API)")
        if not REPO.match(repo) or not SHA.match(sha) or not a.shard.isdigit() or not a.out:
            raise Red("--repo (bare), --sha (40 hex), --shard (a number) and --out are required")
        shard = int(a.shard)
        bf = _load("build_follow", "build-follow.py")
        fetch = _load("build_artifacts_fetch", "build-artifacts-fetch.py")
        p = urllib.parse.urlparse(a.api_url)
        http = bf.Http()
        try:
            token = bf.oidc_token(http, a.audience or f"{p.scheme}://{p.netloc}", env)
        except bf.Failure as f:
            raise Red(str(f))
        base = f"{a.api_url.rstrip('/')}/{urllib.parse.quote(repo, safe='')}/{sha}/artifacts"
        status, body = http.get(f"{base}?" + urllib.parse.urlencode({"channel": a.channel}),
                                {"Authorization": f"Bearer {token}", "Accept": "application/json"})
        if status != 200:
            raise Red(f"the build API answered HTTP {status} for {repo}@{sha[:12]}'s receipts (403: the token's run "
                      f"is not this run's; 404: no host-executed run)")
        try:
            receipts = fetch.receipts_of(body, f"test-shard-{shard}")
        except fetch.Red as e:
            raise Red(str(e))
        written = fetch_tree(plan(receipts, shard), a.out, api_getter(base, token, a.channel))
    except Red as r:
        print(f"::error title=Host shard results::{_inert(str(r))}", flush=True)
        return 1
    print(f"shard {a.shard}: {len(written)} result file(s) fetched over HTTPS, each verified against its receipt → {a.out}")
    return 0


def self_test() -> int:
    fails: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}")
        if not cond:
            fails.append(f"{name}: {detail}")

    fetch = _load("build_artifacts_fetch", "build-artifacts-fetch.py")
    tmp = tempfile.mkdtemp(prefix="host-shard-results-")
    log = b"[CI] MeshWeaver.Data.Test exit=0 elapsed=12s\n"
    trx_a, trx_b, trace = b"<TestRun>a</TestRun>", b"<TestRun>b</TestRun>", b"TEST_START x\nTEST_END x\n"
    blobs = {hashlib.sha256(b).hexdigest(): b for b in (log, trx_a, trx_b, trace)}

    def rc(unit: str, data: bytes, stage: str = "test-shard-1") -> dict:
        return {"stage": stage, "unit": unit, "key": "file:/x#sha256=" + hashlib.sha256(data).hexdigest(),
                "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}

    good = [rc("testResults-shard1/test/test-results.log", log),
            rc("testResults-shard1/test/A/bin/Release/net10.0/TestResults/R.trx", trx_a),
            rc("testResults-shard1/test/B/bin/Release/net10.0/TestResults/R.trx", trx_b),
            rc("testResults-shard1/collected-logs/_meshweaver-test-trace.log", trace),
            rc("Store/Store.zip", trx_a, "build")]

    def serve(sha256: str, dest: str, size: int) -> None:
        with open(dest, "wb") as fh:
            fh.write(blobs[sha256])

    def run(rows: list[dict], getter=serve, shard: int = 1, name: str = "") -> list[str]:
        out = os.path.join(tmp, name or str(len(os.listdir(tmp))))
        return fetch_tree(plan(fetch.receipts_of(rows, f"test-shard-{shard}"), shard), out, getter)

    def red(rows: list[dict], getter=serve, shard: int = 1) -> bool:
        try:
            run(rows, getter, shard)
            return False
        except (Red, fetch.Red):
            return True

    # The positive control FIRST: a check that refuses everything would pass every negative below.
    try:
        got = run(good, name="good")
        check("a complete tree is fetched with its paths PRESERVED (two R.trx do not collide)",
              got == sorted(["test/test-results.log", "test/A/bin/Release/net10.0/TestResults/R.trx",
                             "test/B/bin/Release/net10.0/TestResults/R.trx",
                             "collected-logs/_meshweaver-test-trace.log"]), got)
        check("the bytes are the receipt's",
              open(os.path.join(tmp, "good", "test", "B", "bin", "Release", "net10.0", "TestResults", "R.trx"), "rb").read() == trx_b)
    except Red as e:
        check("a complete tree is fetched", False, e)
    check("a receipt repeated by a later stage with the SAME content is one file",
          not red(good + [dict(good[1])]))
    check("RED: no receipt at all (a red host shard handed nothing over)", red([]))
    check("RED: only another shard's tree", red([dict(r, unit=r["unit"].replace("shard1", "shard2")) for r in good[:4]]))
    check("RED: the receipts are another STAGE's", red([dict(r, stage="test-shard-2") for r in good]))
    check("RED: no test/test-results.log (not the `test` job's tree)", red(good[1:]))
    check("RED: a test-results.log with no exit marker", red(
        [rc("testResults-shard1/test/test-results.log", b"hello\n")] + good[1:4],
        lambda s, d, n: open(d, "wb").write(b"hello\n") and None))
    check("RED: a FOREIGN file in the tree (a dll is not a result)",
          red(good + [rc("testResults-shard1/test/A/bin/Release/net10.0/A.dll", trx_a)]))
    check("RED: a FOREIGN file at the tree's root", red(good + [rc("testResults-shard1/notes.txt", trx_a)]))
    for bad in ("testResults-shard1/../etc/passwd", "testResults-shard1/test//x.trx", "testResults-shard1/test/./x.trx",
                "testResults-shard1/collected-logs/x\n::stop-commands::m", "testResults-shard1/collected-logs/a\\b"):
        check(f"RED: an escaping or malformed unit {bad!r}", red(good + [rc(bad, trx_a)]))
    check("RED: one path, two contents", red(good + [rc(good[1]["unit"], trx_b)]))
    check("RED: TRUNCATED — the body is shorter than the receipt",
          red(good, lambda s, d, n: open(d, "wb").write(blobs[s][:-1]) and None))
    check("RED: padded — the body is longer than the receipt",
          red(good, lambda s, d, n: open(d, "wb").write(blobs[s] + b"x") and None))
    check("RED: the bytes hash differently from the receipt",
          red([dict(good[0])] + [dict(good[1], sha256=hashlib.sha256(trx_b).hexdigest())] + good[2:4],
              lambda s, d, n: open(d, "wb").write(trx_a if s == hashlib.sha256(trx_b).hexdigest() else blobs[s]) and None))

    def refuse(s, d, n):
        raise Red("the build API answered HTTP 404")
    check("RED: the API refuses an object", red(good, refuse))

    def extra(s, d, n):
        serve(s, d, n)
        open(os.path.join(os.path.dirname(d), "smuggled.trx"), "wb").write(b"x")
    check("RED: an EXTRA file appears beside the receipts' set", red(good, extra))
    occupied = os.path.join(tmp, "occupied")
    os.makedirs(occupied)
    open(os.path.join(occupied, "stale.trx"), "wb").write(b"x")
    try:
        fetch_tree(plan(fetch.receipts_of(good, "test-shard-1"), 1), occupied, serve)
        check("RED: an --out that already holds files", False, "accepted")
    except Red:
        check("RED: an --out that already holds files", True)
    check("the shape: trx under test/, the log, collected-logs — nothing else",
          allowed(LOG) and allowed("test/x/y.trx") and allowed("collected-logs/d/x.dmp")
          and not allowed("test/x.dll") and not allowed("x.trx") and not allowed("test/test-results.txt"))

    # The HTTPS getter, end to end against a local server: right bytes accepted, a short and a long
    # body and a redirect refused, the bearer sent.
    import http.server
    import threading
    seen: list[str] = []

    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            seen.append(self.headers.get("Authorization", ""))
            key = self.path.split("?")[0].rsplit("/", 1)[-1]
            if self.path.startswith("/redirect/"):
                self.send_response(302)
                self.send_header("Location", "http://127.0.0.1:1/elsewhere")
                self.end_headers()
                return
            data = blobs.get(key)
            if data is None:
                self.send_response(404)
                self.end_headers()
                return
            if self.path.startswith("/long/"):
                data = data + b"tail"
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *a):
            pass
    srv = http.server.HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{srv.server_address[1]}"
    try:
        check("https getter: a full tree over HTTP(S) is accepted", not red(good, api_getter(base + "/ok", "tok", "latest")))
        check("https getter: the run's bearer is sent", seen and all(s == "Bearer tok" for s in seen), seen)
        check("https getter: a body LONGER than the receipt is RED", red(good, api_getter(base + "/long", "tok", "latest")))
        check("https getter: a redirect is RED (the bearer never follows it)", red(good, api_getter(base + "/redirect", "tok", "latest")))
        check("https getter: an object the API does not serve is RED",
              red([dict(good[0], sha256="f" * 64)] + good[1:4], api_getter(base + "/ok", "tok", "latest")))
    finally:
        srv.shutdown()
    def quiet(args: list[str]) -> int:
        import contextlib
        import io
        with contextlib.redirect_stdout(io.StringIO()):
            return main(args)
    check("usage: a non-https api-url is RED (no OIDC token sent in clear)",
          quiet(["--api-url", "http://x", "--sha", "a" * 40, "--repo", "R", "--shard", "1", "--out", tmp]) == 1)
    check("usage: an empty api-url is RED", quiet(["--api-url", "", "--sha", "a" * 40, "--repo", "R", "--shard", "1", "--out", tmp]) == 1)
    check("usage: a non-numeric shard is RED",
          quiet(["--api-url", "https://x", "--sha", "a" * 40, "--repo", "R", "--shard", "x", "--out", tmp]) == 1)
    shutil.rmtree(tmp, ignore_errors=True)
    if fails:
        for f in fails:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("host-shard-results self-test: every case behaved (accepts a complete tree with paths preserved; refuses "
          "none, another shard's or stage's, no log, no marker, foreign, escaping, conflicting, truncated, padded, "
          "mis-hashed, refused, extra, occupied out, redirect)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
