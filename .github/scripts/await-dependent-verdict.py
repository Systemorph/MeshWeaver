#!/usr/bin/env python3
"""Wait for MeshWeaver.Plugins' verdict on THIS core candidate, and finish with it.

    python3 .github/scripts/await-dependent-verdict.py --key K --candidate SHA --base SHA \
        --deadline-minutes 40          # GITHUB_TOKEN = a READ token on MeshWeaver.Plugins
    python3 .github/scripts/await-dependent-verdict.py --self-test

The waiting half of the `Dependent suites (MeshWeaver.Plugins)` gate in `dotnet-test.yml`
(policy `dependent-suites-gate`; Doc/Architecture/CrossRepoPairGate § "The dependent's suites run
against the candidate"). The job before it sent `repository_dispatch core-candidate-suites` to
MeshWeaver.Plugins; that repository's `core-candidate.yml` builds its reachable suites against the
candidate from source, re-runs only what failed at the candidate's first parent, and writes its
verdict as a commit message at `refs/core-candidate/<key>`. This script polls that ref over REST
(never GraphQL — AGENTS.md), once a minute, and exits with the verdict.

🚨 IT NEVER PASSES ON SILENCE. No ref by the deadline → RED ("the dependent did not answer"). A
verdict for a different key, candidate or base → RED (it is about something else). A malformed
verdict → RED. The only green is `conclusion: success` for exactly this key, candidate and base.
A transport failure while polling is retried until the deadline, and then it is the silence case —
never a pass.

🔒 This repository is PUBLIC and this log is public. The verdict is printed as it came: counts, one
sentence and the private run's link — Plugins writes nothing more into it, by construction.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

REPO = "Systemorph/MeshWeaver.Plugins"
API = "https://api.github.com"
# 🔒 The ONLY verdict fields that ever reach this PUBLIC log. The verdict is read from a private
# repository; whatever else it might carry is dropped, not printed.
PUBLIC_COUNTS = ("selected", "universe", "legs", "drift", "preExisting", "missingEvidence")


def public_view(verdict: dict) -> dict:
    counts = verdict.get("counts") if isinstance(verdict.get("counts"), dict) else {}
    return {"conclusion": verdict.get("conclusion"), "candidate": verdict.get("candidate"),
            "base": verdict.get("base"), "key": verdict.get("key"),
            "counts": {k: counts.get(k) for k in PUBLIC_COUNTS if isinstance(counts.get(k), int)},
            "summary": verdict.get("summary") if isinstance(verdict.get("summary"), str) else None,
            "run": verdict.get("run") if isinstance(verdict.get("run"), str) else None}


def validate(verdict: object, key: str, candidate: str, base: str) -> tuple[bool, str]:
    """(passes, the sentence to print). Pure — the self-test drives it."""
    if not isinstance(verdict, dict):
        return False, "the verdict is not a JSON object — it cannot be read as an answer"
    if verdict.get("schema") != 1:
        return False, f"unknown verdict schema {verdict.get('schema')!r} — refusing to guess what it means"
    for field, want in (("key", key), ("candidate", candidate), ("base", base)):
        if verdict.get(field) != want:
            return False, (f"the verdict at this key names {field}={verdict.get(field)!r}, not {want!r} — "
                           "it is about a different measurement")
    conclusion = verdict.get("conclusion")
    summary, run, counts = verdict.get("summary"), verdict.get("run"), verdict.get("counts")
    # A green must carry its evidence: a sentence, the run it rests on, and the counts. A "success"
    # without them is an empty answer, and an empty answer is not a pass.
    if not (isinstance(summary, str) and summary.strip()):
        return False, "the verdict carries no summary sentence — refusing a green without its evidence"
    if not (isinstance(run, str) and run.startswith(f"https://github.com/{REPO}/actions/runs/")):
        return False, f"the verdict's run link {run!r} is not a {REPO} Actions run — refusing a green that cites nothing"
    if conclusion == "success" and not (isinstance(counts, dict) and all(isinstance(counts.get(k), int) for k in PUBLIC_COUNTS)):
        return False, f"the verdict's counts {counts!r} are not the integers {PUBLIC_COUNTS} — refusing a green without a denominator"
    if conclusion == "success":
        return True, f"✅ MeshWeaver.Plugins passes against this candidate: {summary} — {run}"
    return False, (f"🚨 MeshWeaver.Plugins reports '{conclusion}' against this candidate: {summary}. "
                   f"The failing suites are named in its run (private): {run}")


def _get(path: str, token: str) -> tuple[int, object]:
    req = urllib.request.Request(f"{API}/{path}", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, None


def poll(key: str, token: str, deadline: float, interval: int = 60, quiet: bool = False) -> tuple[object | None, str]:
    """(the verdict JSON, or None with the last reason it was not there)."""
    last = "never polled"
    while True:
        try:
            code, ref = _get(f"repos/{REPO}/git/ref/core-candidate/{key}", token)
            if code == 200 and isinstance(ref, dict):
                sha = ref["object"]["sha"]
                code, commit = _get(f"repos/{REPO}/git/commits/{sha}", token)
                if code == 200 and isinstance(commit, dict):
                    try:
                        return json.loads(commit["message"]), "found"
                    except (KeyError, ValueError) as e:
                        return {"malformed": str(e)}, "found"
                last = f"the verdict commit {sha} did not read (HTTP {code})"
            elif code == 404:
                last = "no verdict yet"
            else:
                last = f"HTTP {code} reading the verdict ref"
        except (urllib.error.URLError, OSError, KeyError, TypeError) as e:
            last = f"transport: {e}"
        if time.time() + interval > deadline:
            return None, last
        if not quiet:
            print(f"  {time.strftime('%H:%M:%S', time.gmtime())}Z — {last}; next look in {interval} s", flush=True)
        time.sleep(interval)


def self_test() -> int:
    failures = 0
    K, C, B = "123-1", "c" * 40, "b" * 40

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    counts = {"selected": 5, "universe": 83, "legs": 1, "drift": 0, "preExisting": 0, "missingEvidence": 0}
    good = {"schema": 1, "key": K, "candidate": C, "base": B, "conclusion": "success", "summary": "5 of 83",
            "run": f"https://github.com/{REPO}/actions/runs/1", "counts": counts}
    ok, _ = validate(good, K, C, B)
    check("a success for exactly this key, candidate and base passes", ok)
    for field, value in (("key", "999-1"), ("candidate", "d" * 40), ("base", "e" * 40)):
        ok, text = validate({**good, field: value}, K, C, B)
        check(f"a verdict naming another {field} is RED", not ok and field in text, text)
    ok, _ = validate({**good, "conclusion": "failure"}, K, C, B)
    check("a failure verdict is RED", not ok)
    ok, _ = validate({**good, "conclusion": "cancelled"}, K, C, B)
    check("anything but 'success' is RED (a cancelled dependent run is not a pass)", not ok)
    ok, _ = validate({**good, "schema": 2}, K, C, B)
    check("an unknown schema is RED", not ok)
    ok, _ = validate(["not", "an", "object"], K, C, B)
    check("a non-object verdict is RED", not ok)
    ok, _ = validate({"malformed": "x"}, K, C, B)
    check("a malformed verdict is RED", not ok)
    for field in ("summary", "run", "counts"):
        ok, text = validate({k: v for k, v in good.items() if k != field}, K, C, B)
        check(f"a 'success' WITHOUT its {field} is RED (a green must carry its evidence)", not ok, text)
    ok, _ = validate({**good, "run": "https://example.com/x"}, K, C, B)
    check("a run link that is not a Plugins Actions run is RED", not ok)
    view = public_view({**good, "failingTests": ["Secret.Test.Name"], "counts": {**counts, "names": ["x"]}})
    check("the public view drops every field and count that is not allow-listed",
          "failingTests" not in view and "names" not in view["counts"] and "Secret" not in json.dumps(view), json.dumps(view))
    # The poll loop itself, against a scripted API: silence by the deadline must come back as NO
    # verdict (main maps that to exit 1), and a ref that appears must be read through its commit.
    global _get
    real = _get
    try:
        _get = lambda path, token: (404, None)
        v, why = poll(K, "t", deadline=time.time() + 0.5, interval=0, quiet=True)
        check("silence by the deadline returns no verdict, with the reason", v is None and "no verdict" in why, why)
        calls = iter([(500, None), (200, {"object": {"sha": "s"}}), (200, {"message": json.dumps(good)})])
        _get = lambda path, token: next(calls)
        v, _ = poll(K, "t", deadline=time.time() + 5, interval=0, quiet=True)
        check("a transient 500 is retried, then the verdict is read from the ref's commit message", v == good, str(v))
        _get = lambda path, token: (200, {"object": {"sha": "s"}}) if "/ref/" in path else (200, {"message": "not json"})
        v, _ = poll(K, "t", deadline=time.time() + 5, interval=0, quiet=True)
        check("an unparseable message comes back as a malformed verdict, which validate reds",
              not validate(v, K, C, B)[0], str(v))
    finally:
        _get = real
    print(f"await-dependent-verdict self-test: {failures} failure(s)")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true", dest="self_test")
    ap.add_argument("--key")
    ap.add_argument("--candidate")
    ap.add_argument("--base")
    ap.add_argument("--deadline-minutes", type=int, default=40)
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not (a.key and a.candidate and a.base):
        ap.error("--key, --candidate and --base are required")
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        print("::error::GITHUB_TOKEN is empty — the verdict ref cannot be read; refusing to wait on nothing")
        return 1
    deadline = time.time() + a.deadline_minutes * 60
    print(f"waiting up to {a.deadline_minutes} min for {REPO} refs/core-candidate/{a.key} "
          f"(candidate {a.candidate[:9]}, base {a.base[:9]})", flush=True)
    verdict, why = poll(a.key, token, deadline)
    if verdict is None:
        print(f"::error::MeshWeaver.Plugins did not answer within {a.deadline_minutes} min ({why}). "
              "Silence is not a pass: the candidate is unverified. Its run is under "
              f"https://github.com/{REPO}/actions/workflows/core-candidate.yml")
        return 1
    ok, text = validate(verdict, a.key, a.candidate, a.base)
    print(text if ok else f"::error::{text}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            view = public_view(verdict) if isinstance(verdict, dict) else {}
            f.write(f"### Dependent suites (MeshWeaver.Plugins)\n\n{text}\n\n```json\n{json.dumps(view, indent=1)}\n```\n")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
