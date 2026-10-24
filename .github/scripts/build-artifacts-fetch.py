#!/usr/bin/env python3
"""build-artifacts-fetch.py — bring a host-executed run's artifacts to the module publish lane, by receipt.

H9 of the standing build hosts (MeshWeaver.Plugins `Hosting/StandingBuildHosts` §2, §7a; platform
reference Doc/Architecture/BuildHosts). On a build host, every module bundle (and its
`<module>.publication.json` record) a stage produces is uploaded by the HOST to the shared CI
artifact store, its stored hash read back, and only then reported as a receipt
`{"kind":"artifact","unit","key","sha256","bytes"}`. The instance serves those receipts at

    GET {api}/{repo}/{sha}/artifacts?channel=<c>          (GitHub OIDC — the same gate as the log)

This script reads them, fetches every receipt's object with core's `ci-artifact-store.py get`
(which verifies the hash in the locator), RE-HASHES what landed against the receipt, and writes the
files into --out exactly as the module pack lane stages them (`<bundle>` + `<module>.publication.json`
side by side), so the caller uploads --out as `module-publication-<lane>-host` and the UNCHANGED
`node-repo-module-publish.yml` collects it with every other staged publication.

    build-artifacts-fetch.py --api-url https://control.example.com/api/builds --store file:/ci-artifacts \
        --out "$RUNNER_TEMP/staged" [--repo R] [--sha <head>] [--channel latest] [--stage modules]
    build-artifacts-fetch.py --self-test

RED, never green, on: no receipt at all (a host-executed run that handed over nothing is not an empty
publication), a receipt whose fetched bytes hash or size differ, a receipt whose unit escapes --out,
an unreadable endpoint (403/404/5xx), and a missing OIDC grant.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.parse
from typing import Callable

HERE = os.path.dirname(os.path.abspath(__file__))
SHA = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")


def _follow_module():
    """build-follow.py's transport and OIDC request — one implementation, loaded from beside this file."""
    spec = importlib.util.spec_from_file_location("build_follow", os.path.join(HERE, "build-follow.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class Red(Exception):
    """A refusal with its reason."""


def receipts_of(body: object, stage: str = "") -> list[dict]:
    rows = body.get("artifacts") if isinstance(body, dict) else body
    if not isinstance(rows, list):
        raise Red("the artifacts answer carries no list of receipts")
    out = []
    for r in rows:
        if not isinstance(r, dict):
            raise Red(f"a receipt is not an object: {str(r)[:80]}")
        if stage and r.get("stage") not in (stage, None):
            continue
        unit, key, sha, size = r.get("unit"), r.get("key"), r.get("sha256"), r.get("bytes")
        if not isinstance(unit, str) or "/" not in unit or not isinstance(key, str) or not key \
                or not isinstance(sha, str) or not HEX64.match(sha) or not isinstance(size, int) or size < 0:
            raise Red(f"a receipt is malformed (unit, key, sha256, bytes): {json.dumps(r)[:160]}")
        out.append({"stage": r.get("stage"), "unit": unit, "key": key, "sha256": sha, "bytes": size})
    return out


def fetch_all(receipts: list[dict], out: str, store: str, get: Callable[[str, str], tuple[int, str]]) -> list[str]:
    """Fetch every receipt's object into `out` under its file name, verify sha256 and size against
    the RECEIPT (not only the locator), and return the written names."""
    if not receipts:
        raise Red("the run handed over NO artifacts — a host-executed run with nothing to publish is a "
                  "missing publication, never an empty one")
    os.makedirs(out, exist_ok=True)
    written = []
    for r in receipts:
        name = r["unit"].rsplit("/", 1)[1]
        if not name or name in (".", "..") or os.sep in name:
            raise Red(f"receipt unit {r['unit']!r} names no file")
        dest = os.path.join(out, name)
        if os.path.exists(dest):
            raise Red(f"two receipts name the same file {name!r}")
        rc, msg = get(r["key"], dest)
        if rc != 0 or not os.path.isfile(dest):
            raise Red(f"ci-artifact-store get {r['unit']} failed (exit {rc}): {msg.strip()[-200:]}")
        h = hashlib.sha256()
        with open(dest, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest() != r["sha256"] or os.path.getsize(dest) != r["bytes"]:
            raise Red(f"{r['unit']} landed as sha256 {h.hexdigest()[:12]}… / {os.path.getsize(dest)} bytes, but the "
                      f"receipt attests {r['sha256'][:12]}… / {r['bytes']} bytes — refusing to stage it")
        written.append(name)
    return written


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--api-url", default="")
    ap.add_argument("--repo", default="")
    ap.add_argument("--sha", default="")
    ap.add_argument("--channel", default="latest")
    ap.add_argument("--stage", default="")
    ap.add_argument("--audience", default="")
    ap.add_argument("--store", default="")
    ap.add_argument("--expect-store-id", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--probe", default="", help="a probe run's id (the claim's `probe`)")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = dict(os.environ)
    try:
        bf = _follow_module()
        repo = (a.repo or env.get("GITHUB_REPOSITORY", "").split("/", 1)[-1]).strip()
        sha = (a.sha or env.get("GITHUB_SHA", "")).strip().lower()
        if not a.api_url.startswith("https://"):
            raise Red(f"--api-url {a.api_url!r} is not https — refusing to send an OIDC token there")
        if not repo or "/" in repo or not SHA.match(sha) or not a.store or not a.out:
            raise Red("--repo (bare), --sha (40 hex), --store and --out are required")
        p = urllib.parse.urlparse(a.api_url)
        audience = a.audience or f"{p.scheme}://{p.netloc}"
        http = bf.Http()
        try:
            token = bf.oidc_token(http, audience, env)
        except bf.Failure as f:
            raise Red(str(f))
        url = f"{a.api_url.rstrip('/')}/{urllib.parse.quote(repo, safe='')}/{sha}/artifacts?" \
              + urllib.parse.urlencode({"channel": a.channel, **({"probe": a.probe} if a.probe.strip() else {})})
        status, body = http.get(url, {"Authorization": f"Bearer {token}", "Accept": "application/json"})
        if status != 200:
            raise Red(f"the build API answered HTTP {status} for {repo}@{sha[:12]}'s artifacts (403: the token's run "
                      f"is not this run's; 404: no host-executed run)")
        store_script = os.path.join(HERE, "ci-artifact-store.py")

        def get(locator: str, dest: str) -> tuple[int, str]:
            cmd = [sys.executable, store_script, "get", "--store", a.store, "--locator", locator, "--out", dest]
            if a.expect_store_id:
                cmd += ["--expect-store-id", a.expect_store_id]
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
            return r.returncode, (r.stdout or "") + (r.stderr or "")
        names = fetch_all(receipts_of(body, a.stage), a.out, a.store, get)
    except Red as r:
        print(f"::error title=Host artifacts::{r}", flush=True)
        return 1
    for n in names:
        print(f"staged {n} (hash and size verified against its receipt)")
    return 0


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        if not cond:
            failures.append(f"{name}: {detail}")

    tmp = tempfile.mkdtemp(prefix="artifacts-fetch-")
    blobs = {"k1": b"bundle-bytes", "k2": b'{"module":"Store"}'}

    def receipt(unit, key, data):
        return {"stage": "modules", "unit": unit, "key": key, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}

    def fake_get(locator, dest):
        open(dest, "wb").write(blobs[locator])
        return 0, ""
    rs = receipts_of({"artifacts": [receipt("Store/Store-1.4.2.zip", "k1", blobs["k1"]),
                                    receipt("Store/Store.publication.json", "k2", blobs["k2"])]})
    names = fetch_all(rs, os.path.join(tmp, "a"), "file:/x", fake_get)
    check("fetch/both files staged side by side", sorted(names) == ["Store-1.4.2.zip", "Store.publication.json"]
          and open(os.path.join(tmp, "a", "Store-1.4.2.zip"), "rb").read() == b"bundle-bytes", names)
    for why, rows, getter in (
            ("no receipts", [], fake_get),
            ("wrong hash", [dict(receipt("Store/S.zip", "k1", blobs["k1"]), sha256="0" * 64)], fake_get),
            ("wrong size", [dict(receipt("Store/S.zip", "k1", blobs["k1"]), bytes=1)], fake_get),
            ("store refused", [receipt("Store/S.zip", "k1", blobs["k1"])], lambda l, d: (1, "manifest unknown")),
            ("duplicate name", [receipt("A/S.zip", "k1", blobs["k1"]), receipt("B/S.zip", "k1", blobs["k1"])], fake_get)):
        try:
            fetch_all(rows, os.path.join(tmp, why.replace(" ", "-")), "file:/x", getter)
            check(f"fetch/{why} is RED", False, "staged")
        except Red:
            pass
    for bad in ({"artifacts": [{"unit": "noslash", "key": "k", "sha256": "0" * 64, "bytes": 1}]},
                {"artifacts": [{"unit": "a/b", "key": "", "sha256": "0" * 64, "bytes": 1}]},
                {"artifacts": [{"unit": "a/b", "key": "k", "sha256": "short", "bytes": 1}]}, {"nope": 1}):
        try:
            receipts_of(bad)
            check(f"receipts/refuses {bad}", False, "accepted")
        except Red:
            pass
    check("receipts/a stage filter keeps that stage's receipts",
          len(receipts_of([receipt("A/x", "k", b"x"), dict(receipt("B/y", "k", b"y"), stage="gate")], "gate")) == 1)
    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-artifacts-fetch self-test: all cases passed (staging side by side, receipt hash + size, no receipts, "
          "a refused fetch, duplicate names, malformed receipts, the stage filter)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
