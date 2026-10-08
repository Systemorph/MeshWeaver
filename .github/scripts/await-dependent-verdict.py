#!/usr/bin/env python3
"""Wait for MeshWeaver.Plugins' verdict on THIS core candidate, and finish with it.

    python3 .github/scripts/await-dependent-verdict.py --key K --candidate SHA --base SHA \
        --deadline-minutes 40          # GITHUB_TOKEN = a READ token on MeshWeaver.Plugins
    python3 .github/scripts/await-dependent-verdict.py --self-test

The waiting half of the `Dependent suites (MeshWeaver.Plugins)` gate in `dotnet-test.yml`
(policy `dependent-suites-affected-gate`; Doc/Architecture/CrossRepoPairGate § "The dependent's suites run
against the candidate"). The job before it sent `repository_dispatch core-candidate-suites` to
MeshWeaver.Plugins; that repository's `core-candidate.yml` builds its reachable suites against the
candidate from source, re-runs only what failed at the candidate's first parent, and writes its
verdict as a commit message at `refs/core-candidate/<key>`. This script polls that ref over REST
(never GraphQL — AGENTS.md), once a minute, and exits with the verdict.

🔁 A LATE VERDICT STILL DECIDES. When this window closes without a verdict the job is red — and when
the candidate answers later, MeshWeaver.Plugins re-runs this job (scripts/core-candidate-requester.py
there), whose first look finds the verdict (Doc/Architecture/CrossRepoPairGate § "A late verdict
still decides").

🚨 IT NEVER PASSES ON SILENCE. No ref by the deadline → RED ("the dependent did not answer"). A
verdict for a different key, candidate or base → RED (it is about something else). A malformed
verdict → RED. The only green is `conclusion: success` for exactly this key, candidate and base.
A transport failure while polling is retried until the deadline, and then it is the silence case —
never a pass.

🔒 This repository is PUBLIC and this log is public. The verdict is printed as it came: counts, one
sentence and the private run's link — Plugins writes nothing more into it, by construction.

🚦 WHILE IT WAITS, IT SAYS WHERE THE CANDIDATE IS (the QUEUE CENSUS, every `--census-every` seconds and
once more at the deadline). "No verdict in time" alone could not tell a dependent that is slow from one
that never got a runner, and the second was the whole story on 2026-10-08: core #6320's control leg sat
queued behind 48 legs of nine other candidates while its waiter ran out. The census reads only Actions
METADATA of MeshWeaver.Plugins' `core-candidate.yml` runs (`actions: read` on the same App token): this
candidate's run and its jobs' states, the candidate runs created BEFORE it that are still live (its queue
position), and how many Plugins runs are queued in total. Run ids, run keys (`core-candidate <core run
id>-<attempt>`) and job states — never a job's log, a suite or a test. And it is never a verdict: a
census that cannot be read says so and changes nothing about the exit code.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

REPO = "Systemorph/MeshWeaver.Plugins"
API = "https://api.github.com"
# 🔒 The ONLY verdict fields that ever reach this PUBLIC log. The verdict is read from a private
# repository; whatever else it might carry is dropped, not printed.
PUBLIC_COUNTS = ("selected", "universe", "legs", "drift", "preExisting", "missingEvidence")
# 🚦 SILENCE IS NOT A VERDICT, AND IT GETS ITS OWN EXIT CODE. Still red — never a pass — but
# distinguishable from a verdict that says the candidate broke something: the workflow maps this
# code to its own failing step, and the merge-queue steward reads THAT step as infrastructure (the
# dependent's legs never got a runner inside the deadline — measured 2026-09-27: legs that ran
# 5–14 min waited 20–50 min on a shared FIFO label), re-queueing on evidence, capped per head.
# Every other red (a failure verdict, a mismatched or malformed one, no token) stays exit 1.
EXIT_NO_VERDICT = 3


# Per REALM (a package id the public registry already lists, or `hosts`): only these integer/verdict
# fields, so the realms a change breaks can be NAMED in `Breaks-plugins:` without a suite or test name.
PUBLIC_REALM_FIELDS = ("conclusion", "suites", "drift", "missingEvidence", "meshTestsUnmeasured")
EXTRA_COUNTS = ("notSelected", "meshTestsUnmeasured")
_REALM = re.compile(r"^[A-Za-z0-9_.-]{1,80}$")


def public_view(verdict: dict) -> dict:
    counts = verdict.get("counts") if isinstance(verdict.get("counts"), dict) else {}
    realms = counts.get("realms") if isinstance(counts.get("realms"), dict) else {}
    view_counts = {k: counts.get(k) for k in PUBLIC_COUNTS + EXTRA_COUNTS if isinstance(counts.get(k), int)}
    view_counts["realms"] = {
        r: {f: v.get(f) for f in PUBLIC_REALM_FIELDS if isinstance(v.get(f), (int, str)) and not isinstance(v.get(f), bool)}
        for r, v in realms.items() if isinstance(r, str) and _REALM.match(r) and isinstance(v, dict)}
    plugins = verdict.get("pluginsSha")
    return {"conclusion": verdict.get("conclusion"), "candidate": verdict.get("candidate"),
            "base": verdict.get("base"), "key": verdict.get("key"),
            "pluginsSha": plugins if isinstance(plugins, str) and len(plugins) == 40 else None,
            "counts": view_counts,
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


WORKFLOW = "core-candidate.yml"
_LIVE = ("queued", "in_progress", "waiting", "pending", "requested")
# 🔒 A Plugins job name reaches this PUBLIC log only if it is one of core-candidate.yml's own fixed
# shapes; anything else (a future matrix value, a renamed job) is printed as `<job>`. The API makes no
# promise about what a name contains, so the boundary is an allow-list, never a hope.
_PUBLIC_JOB = re.compile(r"^(?:Admitted by the CI queue \(gate tier\)|Select the suites the candidate can reach"
                         r"|Plan the control arm \(only what the candidate did not pass\)|Verdict for core"
                         r"|(?:Candidate|Control) / (?:candidate|control) \((?:leg \d{1,3}/\d{1,3}|build once for \d{1,3} legs)\))$")


def public_job(name: str) -> str:
    return name if _PUBLIC_JOB.match(name or "") else "<job>"


def census(key: str, token: str) -> dict:
    """Where is the candidate run for this key, and what is ahead of it? Metadata only (see the module
    docstring). Never raises: an unreadable census comes back as {"error": …}."""
    try:
        code, page = _get(f"repos/{REPO}/actions/workflows/{WORKFLOW}/runs?per_page=100", token)
        if code != 200 or not isinstance(page, dict):
            return {"error": f"HTTP {code} listing {WORKFLOW} runs"}
        runs = page.get("workflow_runs") or []
        mine = next((r for r in runs if r.get("name") == f"core-candidate {key}"), None)
        live = [r for r in runs if r.get("status") in _LIVE]
        code, queued_total = _get(f"repos/{REPO}/actions/runs?status=queued&per_page=1", token)
        queued_runs = queued_total.get("total_count") if code == 200 and isinstance(queued_total, dict) else None
        if mine is None:
            return {"found": False, "live": len(live), "pluginsQueuedRuns": queued_runs}
        ahead = sorted((r for r in live if r.get("id") != mine.get("id")
                        and (r.get("created_at") or "") < (mine.get("created_at") or "")),
                       key=lambda r: r.get("created_at") or "")
        code, jobs = _get(f"repos/{REPO}/actions/runs/{mine['id']}/jobs?filter=latest&per_page=100", token)
        jobs = (jobs or {}).get("jobs") or [] if code == 200 and isinstance(jobs, dict) else None
        out = {"found": True, "run": mine.get("html_url"), "status": mine.get("status"),
               "conclusion": mine.get("conclusion"), "ahead": [r.get("name", "").removeprefix("core-candidate ") for r in ahead],
               "position": len(ahead) + 1, "live": len(live), "pluginsQueuedRuns": queued_runs}
        if jobs is None:
            out["jobsError"] = f"HTTP {code} reading the run's jobs"
        else:
            out["queued"] = sorted(public_job(j.get("name", "")) for j in jobs if j.get("status") in ("queued", "waiting", "pending"))
            out["running"] = sorted(public_job(j.get("name", "")) for j in jobs if j.get("status") == "in_progress")
            out["done"] = sum(1 for j in jobs if j.get("status") == "completed")
        return out
    except (urllib.error.URLError, OSError, KeyError, TypeError, ValueError) as e:
        return {"error": f"transport: {e}"}


def _names(names: list, most: int = 3) -> str:
    return ", ".join(names[:most]) + (f" and {len(names) - most} more" if len(names) > most else "")


def describe(c: dict) -> str:
    """One sentence about the census, for the log while waiting."""
    if "error" in c:
        return f"queue census unavailable ({c['error']}) — the wait goes on regardless"
    if not c.get("found"):
        return (f"no {WORKFLOW} run for this key yet ({c.get('live')} candidate run(s) live, "
                f"{c.get('pluginsQueuedRuns')} Plugins run(s) queued in total)")
    jobs = (f"{len(c['running'])} job(s) running, {len(c['queued'])} waiting for a runner, {c['done']} done"
            if "queued" in c else c.get("jobsError", "jobs unknown"))
    ahead = f"behind {len(c['ahead'])} older live candidate run(s) ({_names(c['ahead'])})" if c["ahead"] else "first in line among the candidate runs"
    return (f"candidate run {c['status']}: {jobs}; queue position {c['position']} of {c['live']} — {ahead}; "
            f"{c.get('pluginsQueuedRuns')} Plugins run(s) queued in total — {c['run']}")


def timeout_sentence(c: dict, minutes: int) -> str:
    """WHY there is no verdict, as precisely as the census allows. Still red, whatever it says."""
    if "error" in c:
        return f"no verdict within {minutes} min, and the queue census could not be read ({c['error']})"
    if not c.get("found"):
        return (f"no verdict within {minutes} min and NO {WORKFLOW} run carries this key — the request started "
                f"nothing visible ({c.get('pluginsQueuedRuns')} Plugins run(s) queued in total)")
    behind = f"queued behind {len(c['ahead'])} candidate run(s)" + (f": {_names(c['ahead'], 5)}" if c["ahead"] else "")
    if c.get("status") == "completed":
        return f"the candidate run finished ({c.get('conclusion')}) without a verdict this waiter could read — {c['run']}"
    if "queued" not in c:
        return f"no verdict within {minutes} min ({behind}; its jobs could not be read: {c.get('jobsError')}) — {c['run']}"
    if not c["queued"] and not c["running"] and not c["done"] and c.get("status") in _LIVE:
        return (f"no runner within {minutes} min ({behind}) — the candidate run itself never started: it is "
                f"'{c.get('status')}' with no job materialised yet (admission / concurrency) — "
                f"{c.get('pluginsQueuedRuns')} Plugins run(s) queued in total — {c['run']}")
    if c["queued"] and not c["running"]:
        return (f"no runner within {minutes} min ({behind}) — waiting for a runner: {_names(c['queued'])} — "
                f"{c.get('pluginsQueuedRuns')} Plugins run(s) queued in total — {c['run']}")
    return (f"the candidate was still running at the deadline ({len(c['running'])} job(s) running: {_names(c['running'])}; "
            f"{len(c['queued'])} waiting for a runner; {behind}) — {c['run']}")


def _get(path: str, token: str) -> tuple[int, object]:
    req = urllib.request.Request(f"{API}/{path}", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, None


def poll(key: str, token: str, deadline: float, interval: int = 60, quiet: bool = False,
         census_every: int = 0) -> tuple[object | None, str]:
    """(the verdict JSON, or None with the last reason it was not there). With `census_every` > 0 the
    queue census is printed that often — information only, it never decides anything."""
    last = "never polled"
    next_census = 0.0
    while True:
        if census_every > 0 and time.time() >= next_census:
            next_census = time.time() + census_every
            if not quiet:
                print(f"  {time.strftime('%H:%M:%S', time.gmtime())}Z — {describe(census(key, token))}", flush=True)
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
    view = public_view({**good, "counts": {**counts, "realms": {
        "AI": {"conclusion": "failure", "drift": 1, "suites": 2, "failingSuite": "Secret.Test"},
        "bad realm name with spaces": {"conclusion": "success"}}}})
    check("per-realm fields are allow-listed too: realm names and integers ride, a suite name never does",
          view["counts"]["realms"] == {"AI": {"conclusion": "failure", "suites": 2, "drift": 1}}
          and "Secret" not in json.dumps(view), json.dumps(view))
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
        # 🔁 A RE-RUN (MeshWeaver.Plugins re-runs this job when its verdict lands after the window):
        # the verdict is ALREADY published, so the first look must return it — not after an interval.
        # `time.sleep` is replaced by a recorder, so a regression that waits before its first look
        # FAILS this check at once instead of sleeping the job into its cap.
        _get = lambda path, token: (200, {"object": {"sha": "s"}}) if "/ref/" in path else (200, {"message": json.dumps(good)})
        slept: list[float] = []
        real_sleep = time.sleep
        time.sleep = slept.append
        try:
            v, _ = poll(K, "t", deadline=time.time() + 5, interval=1, quiet=True)
        finally:
            time.sleep = real_sleep
        check("a re-run finds an already-published verdict on its FIRST look (no interval waited)",
              v == good and slept == [], f"{v}, sleeps requested: {slept}")
        # Negative control: the recorder DOES see a wait when the first look finds nothing.
        looks = iter([(404, None), (200, {"object": {"sha": "s"}}), (200, {"message": json.dumps(good)})])
        _get = lambda path, token: next(looks)
        slept.clear()
        time.sleep = slept.append
        try:
            v, _ = poll(K, "t", deadline=time.time() + 5, interval=1, quiet=True)
        finally:
            time.sleep = real_sleep
        check("negative control: a first look that finds nothing IS followed by a recorded wait",
              v == good and slept == [1], f"{v}, sleeps requested: {slept}")
        _get = lambda path, token: (200, {"object": {"sha": "s"}}) if "/ref/" in path else (200, {"message": "not json"})
        v, _ = poll(K, "t", deadline=time.time() + 5, interval=0, quiet=True)
        check("an unparseable message comes back as a malformed verdict, which validate reds",
              not validate(v, K, C, B)[0], str(v))
        # 🚦 THE CENSUS — what the deadline says, and that it never turns silence into a pass.
        def api(listing, jobs, queued_total=7):
            def f(path, token):
                if "/jobs" in path:
                    return (200, {"jobs": jobs}) if jobs is not None else (403, None)
                if "status=queued" in path:
                    return 200, {"total_count": queued_total}
                return (200, {"workflow_runs": listing}) if listing is not None else (403, None)
            return f
        mine = {"id": 9, "name": f"core-candidate {K}", "status": "queued", "created_at": "2026-10-08T16:30:13Z",
                "html_url": f"https://github.com/{REPO}/actions/runs/9"}
        older = [{"id": i, "name": f"core-candidate {i}-1", "status": "queued", "created_at": f"2026-10-08T16:2{i}:00Z"} for i in (1, 2, 3)]
        newer = {"id": 8, "name": "core-candidate 8-1", "status": "queued", "created_at": "2026-10-08T17:00:00Z"}
        done = {"id": 7, "name": "core-candidate 7-1", "status": "completed", "created_at": "2026-10-08T16:00:00Z"}
        _get = api([newer, mine, *older, done], [{"name": "Candidate / candidate (leg 1/8)", "status": "completed"},
                                                 {"name": "Control / control (leg 1/1)", "status": "queued"}])
        c = census(K, "t")
        check("the census finds this key's run and counts ONLY older LIVE runs as ahead (position 4)",
              c.get("found") and c["position"] == 4 and len(c["ahead"]) == 3 and c["live"] == 5, json.dumps(c))
        text = timeout_sentence(c, 42)
        check("a candidate with jobs waiting and none running times out as 'no runner within 42 min (queued behind 3 …)'",
              text.startswith("no runner within 42 min (queued behind 3 candidate run(s)") and "Control / control" in text, text)
        _get = api([mine], [{"name": "Candidate / candidate (leg 2/8)", "status": "in_progress"}])
        text = timeout_sentence(census(K, "t"), 42)
        check("a candidate still RUNNING at the deadline says so — not 'no runner'", text.startswith("the candidate was still running"), text)
        _get = api([mine, *older], [])
        text = timeout_sentence(census(K, "t"), 42)
        check("a QUEUED run with no job materialised says the run never started — not 'still running'",
              text.startswith("no runner within 42 min (queued behind 3") and "never started" in text, text)
        _get = api([mine], [{"name": "portal-hosts (Secret.Customer.Test)", "status": "queued"},
                            {"name": "Candidate / candidate (build once for 8 legs)", "status": "queued"}])
        text = timeout_sentence(census(K, "t"), 42)
        check("a job name outside core-candidate.yml's fixed shapes is printed as <job>, never verbatim",
              "Secret" not in text and "<job>" in text and "build once for 8 legs" in text, text)
        _get = api([newer], [])
        text = timeout_sentence(census(K, "t"), 42)
        check("no run carrying this key says the request started nothing visible", "NO core-candidate.yml run carries this key" in text, text)
        _get = api(None, None)
        c = census(K, "t")
        check("an unreadable census (no actions: read) is reported, never raised", "error" in c and "403" in c["error"], json.dumps(c))
        check("…and the timeout still says there is no verdict", timeout_sentence(c, 42).startswith("no verdict within 42 min"))
        _get = api([mine, *older], [{"name": "Candidate / candidate (leg 1/8)", "status": "queued"}])
        v, _ = poll(K, "t", deadline=time.time() + 0.5, interval=0, quiet=True, census_every=1)
        # The poll above read the census api for the verdict ref too, which 403s — silence.
        check("NEGATIVE CONTROL: with the census on, silence is still NO verdict (the census never passes anything)", v is None, str(v))
        check("the public sentences carry run keys and job states, never a suite or test name",
              "Test" not in describe(census(K, "t")).replace("Tests", ""), describe(census(K, "t")))
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
    ap.add_argument("--out", help="write the PUBLIC view of the verdict here (for the break-declaration check)")
    ap.add_argument("--census-every", type=int, default=300, dest="census_every",
                    help="seconds between queue-census lines while waiting (0 = none)")
    ap.add_argument("--no-verdict-out", dest="no_verdict_out",
                    help="on silence, write WHY (the deadline's census sentence) here, for the no-verdict step")
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
    verdict, why = poll(a.key, token, deadline, census_every=a.census_every)
    if verdict is None:
        reason = timeout_sentence(census(a.key, token), a.deadline_minutes)
        print(f"::error::MeshWeaver.Plugins did not answer within {a.deadline_minutes} min ({why}): {reason}. "
              "Silence is not a pass: the candidate is unverified. Its run is under "
              f"https://github.com/{REPO}/actions/workflows/core-candidate.yml")
        if a.no_verdict_out:
            with open(a.no_verdict_out, "w", encoding="utf-8") as f:
                f.write(reason + "\n")
        return EXIT_NO_VERDICT
    ok, text = validate(verdict, a.key, a.candidate, a.base)
    print(text if ok else f"::error::{text}")
    # The verdict is written whatever it says (only a verdict ABOUT this key/candidate/base): a red one
    # is what check-plugins-break-declaration.py reads to decide whether a DECLARED break excuses it.
    about_this = isinstance(verdict, dict) and all(verdict.get(f) == w for f, w in
                                                   (("key", a.key), ("candidate", a.candidate), ("base", a.base)))
    if a.out and about_this and verdict.get("schema") == 1:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(public_view(verdict), f, indent=1)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            view = public_view(verdict) if isinstance(verdict, dict) else {}
            f.write(f"### Dependent suites (MeshWeaver.Plugins)\n\n{text}\n\n```json\n{json.dumps(view, indent=1)}\n```\n")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
