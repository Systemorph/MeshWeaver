#!/usr/bin/env python3
"""build-executor.py — who runs this CI run's heavy legs: a standing build host, or GitHub Actions.

MeshWeaver.Plugins `Hosting/StandingBuildHosts` §7 H7; platform reference Doc/Architecture/BuildHosts
("Every repository on the hosts"). Called ONCE per run, where the run is admitted (a repository's
`admission` job, or the `node-repo-build-executor.yml` lane), it answers `executor=host|actions`:

  1. the repository must DECLARE its stages (`.github/build-stages.json`, with at least one stage) —
     a repository that declares none cannot be run by a host;
  2. the operations instance must SAY host: `GET <api>/queue?channel=<c>&repo=<name>` answers
     `"executor": "host"` (the instance's `Hosting:Builds:Executor`, narrowed per repository by
     `Hosting:Builds:HostRepos`); an instance that does not answer, answers anything else, or is an
     older build that names no executor at all, is `actions`;
  3. the run must be QUEUED: one signed `build-request` (the org pair BUILD_WEBHOOK_URL /
     BUILD_WEBHOOK_SECRET — the same inbox and HMAC the CI queue's `ci-enqueue` uses) is POSTed and
     answered 2xx with its signature verified. Only then is the answer `host`.

🚨 FAIL TOWARD TODAY'S BEHAVIOUR. Every failure above answers `actions` — the heavy legs run on
GitHub exactly as they did before this script existed — and SAYS WHY in the job summary and a
::warning:: (a failure, not a deliberate `actions`). Nothing is skipped silently: with `host`, every
heavy leg is replaced by a follow job whose verdict the required context carries
(`build-follow.yml`), and with `actions` nothing changes. A request that reached the queue while the
answer was lost can at worst make a host build a run Actions also builds — wasted minutes, never a
lost verdict.

The base the host merges onto is the admission's: `MAIN_SHA` (policy `suites-test-fresh-merge`)
rides as the request's `ref.baseSha`, so the host tests the same merged tree the Actions legs would.

    python3 .github/scripts/build-executor.py --self-test
    python3 .github/scripts/build-executor.py        # reads the environment the workflow sets

Stdlib only; HMAC in Python. Exit 0 always on a decision (host or actions); exit 2 only on a
malformed input this script itself was handed (a value no workflow could have meant).
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Callable, Optional

HOST = "host"
ACTIONS = "actions"
STAGES_FILE = ".github/build-stages.json"
TRUNK_EVENTS = ("push", "schedule", "workflow_dispatch", "repository_dispatch")
QUEUE_TIMEOUT_SECONDS = 15
POST_TIMEOUT_SECONDS = 30

# (status, body-text) — status 0 is a transport failure, the text its reason.
Get = Callable[[str], tuple[int, str]]
Post = Callable[[str, bytes, dict], tuple[int, str]]


class BadInput(Exception):
    pass


@dataclass
class Inputs:
    queue_url: str = ""          # the inbox: https://<instance>/api/hooks/Hosting/PlatformBuilds
    secret: str = ""
    build_api: str = ""          # optional override (vars.MW_BUILD_API); default <inbox origin>/api/builds
    repo: str = ""               # owner/name
    sha: str = ""
    event: str = ""
    ref_name: str = ""
    default_branch: str = "main"
    pr: Optional[int] = None
    base_ref: str = ""
    main_sha: str = ""
    run_id: int = 0
    run_attempt: int = 1
    run_url: str = ""
    requested_by: str = ""
    change_mode: str = ""
    channel: str = "latest"
    stages_file: str = STAGES_FILE


@dataclass
class Decision:
    executor: str
    api_url: str
    why: str
    lines: list[str] = field(default_factory=list)   # workflow-command lines to print


def api_url_of(i: Inputs) -> str:
    """The build API base: the explicit override, else the inbox's origin + /api/builds. '' = none."""
    raw = (i.build_api or "").strip().rstrip("/")
    if not raw and i.queue_url:
        p = urllib.parse.urlparse(i.queue_url.strip())
        raw = f"{p.scheme}://{p.netloc}/api/builds" if p.scheme and p.netloc else ""
    if not raw:
        return ""
    p = urllib.parse.urlparse(raw)
    host = (p.hostname or "").lower()
    local = p.scheme == "http" and host in ("localhost", "127.0.0.1", "::1")
    if "@" in p.netloc or not ((p.scheme == "https" and host) or local):
        return ""
    return raw


def declared_stages(path: str) -> tuple[list[str], str]:
    """The stage names the repository declares, or ([], why not)."""
    try:
        with open(path) as fh:
            doc = json.load(fh)
    except FileNotFoundError:
        return [], f"the repository declares no stages ({path} is absent)"
    except (OSError, ValueError) as e:
        return [], f"{path} is unreadable ({type(e).__name__})"
    stages = doc.get("stages") if isinstance(doc, dict) else None
    names = [s.get("name") for s in stages if isinstance(s, dict)] if isinstance(stages, list) else []
    names = [n for n in names if isinstance(n, str) and n]
    return (names, "") if names else ([], f"{path} declares no stage")


def is_trunk(i: Inputs) -> bool:
    return i.event in TRUNK_EVENTS and i.ref_name == i.default_branch


def request_body(i: Inputs) -> dict:
    """The `build-request` — the shape `BuildRequest.Parse` reads (Hosting/Build/Source/BuildEvents.cs)."""
    if i.event == "pull_request" and i.pr:
        ref = {"kind": "pullRequest", "name": i.ref_name, "number": i.pr}
        if i.base_ref:
            ref["baseRef"] = i.base_ref
        if re.fullmatch(r"[0-9a-f]{40}", i.main_sha or ""):
            ref["baseSha"] = i.main_sha
    else:
        ref = {"kind": "branch", "name": i.ref_name}
    body = {"event": "build-request", "repo": i.repo.split("/")[-1], "sha": i.sha, "ref": ref,
            "isTrunk": is_trunk(i), "runId": i.run_id, "runUrl": i.run_url, "runAttempt": i.run_attempt}
    if i.requested_by:
        body["requestedBy"] = i.requested_by
    if i.change_mode:
        body["changeMode"] = i.change_mode
    return body


def sign(secret: str, payload: bytes) -> str:
    return "sha256=" + hmac.new(secret.encode(), payload, hashlib.sha256).hexdigest()


def decide(i: Inputs, get: Get, post: Post) -> Decision:
    """The executor for this run. Pure but for the two injected calls."""
    if not re.fullmatch(r"[0-9a-f]{40}", i.sha or ""):
        raise BadInput(f"HEAD_SHA {i.sha!r} is not a 40-hex commit")
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", i.channel or ""):
        raise BadInput(f"CHANNEL {i.channel!r} is not a channel name")

    def actions(why: str, warn: bool = True) -> Decision:
        lines = [f"::warning title=Heavy legs on Actions::{why} — the heavy legs run on GitHub Actions (today's behaviour)"] if warn else []
        return Decision(ACTIONS, api, why, lines)

    api = api_url_of(i)
    stages, no_stages = declared_stages(i.stages_file)
    if not stages:
        return actions(no_stages, warn=False)
    if not api:
        return actions("no build API is configured (vars.MW_BUILD_API, or an https BUILD_WEBHOOK_URL to derive it from)", warn=False)
    repo_name = i.repo.split("/")[-1]
    status, text = get(f"{api}/queue?" + urllib.parse.urlencode({"channel": i.channel, "repo": repo_name}))
    if status != 200:
        return actions(f"the build queue did not answer (GET {api}/queue: {'transport: ' + text if status == 0 else f'HTTP {status}'})")
    try:
        answer = json.loads(text)
    except ValueError:
        return actions("the build queue's answer is not JSON")
    executor = answer.get("executor") if isinstance(answer, dict) else None
    if executor is None:
        return actions("the build queue names no executor (an instance older than H7)")
    if not isinstance(executor, str) or executor.strip().lower() != HOST:
        return actions(f"the instance executes {repo_name} on {executor!s:.40}", warn=False)
    if not i.secret or not i.queue_url:
        return actions("the instance says host, but this context has no webhook pair (BUILD_WEBHOOK_URL / BUILD_WEBHOOK_SECRET) to queue the run with")
    payload = json.dumps(request_body(i), separators=(",", ":")).encode()
    status, text = post(i.queue_url, payload, {"Content-Type": "application/json", "X-Hub-Signature-256": sign(i.secret, payload),
                                               "User-Agent": "meshweaver-build-executor"})
    if not 200 <= status < 300:
        return actions(f"the build-request was not accepted ({'transport: ' + text if status == 0 else f'HTTP {status}'})")
    try:
        verdict = json.loads(text).get("signature") if text.strip() else None
    except (ValueError, AttributeError):
        verdict = None
    if verdict != "verified":
        return actions(f"the inbox stored the build-request but did not verify its signature (signature: {verdict!r})")
    return Decision(HOST, api, f"the instance runs {repo_name}'s stages ({', '.join(stages)}) on build-{i.channel}; the run is queued", [
        f"::notice title=Heavy legs on a build host::{repo_name}@{i.sha[:12]} is queued on build-{i.channel} — every heavy leg is followed from the host ({', '.join(stages)})"])


def _http_get(url: str) -> tuple[int, str]:
    req = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "meshweaver-build-executor"})
    try:
        with urllib.request.urlopen(req, timeout=QUEUE_TIMEOUT_SECONDS) as r:
            return r.status, r.read(65536).decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, ""
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, type(e).__name__


def _http_post(url: str, payload: bytes, headers: dict) -> tuple[int, str]:
    req = urllib.request.Request(url, data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=POST_TIMEOUT_SECONDS) as r:
            return r.status, r.read(65536).decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, ""
    except (urllib.error.URLError, OSError, ValueError) as e:
        return 0, type(e).__name__


def inputs_from_env(e: dict) -> Inputs:
    def num(name: str, default: int = 0) -> int:
        v = (e.get(name) or "").strip()
        if not v:
            return default
        if not v.isdigit():
            raise BadInput(f"{name} {v!r} is not a number")
        return int(v)
    pr = num("PR_NUMBER")
    return Inputs(queue_url=e.get("QUEUE_URL", "").strip(), secret=e.get("QUEUE_SECRET", ""), build_api=e.get("BUILD_API", "").strip(),
                  repo=e.get("REPO", "").strip(), sha=e.get("HEAD_SHA", "").strip().lower(), event=e.get("EVENT", "").strip(),
                  ref_name=e.get("REF_NAME", "").strip(), default_branch=(e.get("DEFAULT_BRANCH") or "main").strip(),
                  pr=pr or None, base_ref=e.get("BASE_REF", "").strip(), main_sha=e.get("MAIN_SHA", "").strip().lower(),
                  run_id=num("RUN_ID"), run_attempt=num("RUN_ATTEMPT", 1), run_url=e.get("RUN_URL", "").strip(),
                  requested_by=e.get("REQUESTED_BY", "").strip(), change_mode=e.get("CHANGE_MODE", "").strip(),
                  channel=(e.get("CHANNEL") or "latest").strip(), stages_file=(e.get("STAGES_FILE") or STAGES_FILE).strip())


def emit(d: Decision, env: dict) -> None:
    for line in d.lines:
        print(line, flush=True)
    print(f"executor={d.executor} — {d.why}", flush=True)
    if env.get("GITHUB_OUTPUT"):
        with open(env["GITHUB_OUTPUT"], "a") as f:
            f.write(f"executor={d.executor}\napi-url={d.api_url}\n")
    if env.get("GITHUB_STEP_SUMMARY"):
        with open(env["GITHUB_STEP_SUMMARY"], "a") as f:
            f.write(f"- 🏗️ Heavy legs: **{d.executor}** — {d.why.replace(chr(10), ' ')}\n")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    try:
        d = decide(inputs_from_env(dict(os.environ)), _http_get, _http_post)
    except BadInput as e:
        print(f"::error title=Build executor::{e}")
        return 2
    emit(d, dict(os.environ))
    return 0


def self_test() -> int:
    import tempfile
    failures: list[str] = []

    def check(name: str, ok: bool, detail: object = "") -> None:
        if not ok:
            failures.append(f"{name}: {detail}")

    tmp = tempfile.mkdtemp(prefix="build-executor-")
    stages = os.path.join(tmp, "build-stages.json")
    with open(stages, "w") as fh:
        json.dump({"stages": [{"name": "compile-check"}, {"name": "gate"}]}, fh)
    base = Inputs(queue_url="https://control.example.com/api/hooks/Hosting/PlatformBuilds", secret="s3cret",
                  repo="Systemorph/MeshWeaver.Plugins", sha="a" * 40, event="pull_request", ref_name="feat/x", pr=7,
                  base_ref="main", main_sha="b" * 40, run_id=42, run_attempt=1, run_url="https://github.com/r/1",
                  requested_by="octo", stages_file=stages)

    def with_(**kw) -> Inputs:
        return Inputs(**{**base.__dict__, **kw})

    calls: list[tuple] = []

    def get_answer(status: int, body: object) -> Get:
        def g(url: str) -> tuple[int, str]:
            calls.append(("GET", url))
            return status, body if isinstance(body, str) else json.dumps(body)
        return g

    def post_answer(status: int, body: object) -> Post:
        def p(url: str, payload: bytes, headers: dict) -> tuple[int, str]:
            calls.append(("POST", url, payload, headers))
            return status, body if isinstance(body, str) else json.dumps(body)
        return p

    host_q = get_answer(200, {"channel": "latest", "claimable": 0, "running": 0, "demand": 0, "executor": "host"})
    ok_post = post_answer(202, {"signature": "verified"})

    # 1. host: the instance says host, the request is queued and verified
    calls.clear()
    d = decide(base, host_q, ok_post)
    check("host/answered", d.executor == HOST and d.api_url == "https://control.example.com/api/builds", d)
    check("host/asks for this repository on this channel", calls[0][1] == "https://control.example.com/api/builds/queue?channel=latest&repo=MeshWeaver.Plugins", calls)
    payload, headers = calls[1][2], calls[1][3]
    check("host/the request is signed with the pair's secret", headers["X-Hub-Signature-256"] == sign("s3cret", payload), headers)
    body = json.loads(payload)
    check("host/the request is a build-request for the head, with the run id the follower's OIDC binds to",
          body["event"] == "build-request" and body["sha"] == "a" * 40 and body["repo"] == "MeshWeaver.Plugins" and body["runId"] == 42, body)
    check("host/a PR request names the PR and the ADMISSION's main as its base (one merged tree)",
          body["ref"] == {"kind": "pullRequest", "name": "feat/x", "number": 7, "baseRef": "main", "baseSha": "b" * 40} and body["isTrunk"] is False, body)
    # 2. trunk: a push to main is trunk; a push elsewhere is not
    tb = request_body(with_(event="push", ref_name="main", pr=None))
    check("trunk/a push to main", tb["isTrunk"] is True and tb["ref"] == {"kind": "branch", "name": "main"}, tb)
    check("trunk/a push to a branch is not trunk", request_body(with_(event="push", ref_name="feat/y", pr=None))["isTrunk"] is False)
    check("trunk/a merge group is not trunk", request_body(with_(event="merge_group", ref_name="main", pr=None))["isTrunk"] is False)
    # 3. every failure answers actions, said
    cases = [
        ("no stages declared", with_(stages_file=os.path.join(tmp, "absent.json")), host_q, ok_post, "declares no stages", False),
        ("no build API", with_(queue_url="", build_api=""), host_q, ok_post, "no build API", False),
        ("plain-http API refused", with_(build_api="http://control.example.com/api/builds", queue_url=""), host_q, ok_post, "no build API", False),
        ("credentials in the URL refused", with_(build_api="https://user@control.example.com/api/builds"), host_q, ok_post, "no build API", False),
        ("queue unreachable", base, get_answer(0, "URLError"), ok_post, "did not answer", True),
        ("queue 503", base, get_answer(503, ""), ok_post, "HTTP 503", True),
        ("queue not JSON", base, get_answer(200, "<html>"), ok_post, "not JSON", True),
        ("an older instance names no executor", base, get_answer(200, {"channel": "latest", "demand": 0}), ok_post, "names no executor", True),
        ("the instance says actions", base, get_answer(200, {"executor": "actions"}), ok_post, "executes MeshWeaver.Plugins on actions", False),
        ("an unknown executor is actions", base, get_answer(200, {"executor": "ftp"}), ok_post, "on ftp", False),
        ("no webhook pair", with_(secret=""), host_q, ok_post, "no webhook pair", True),
        ("request refused", base, host_q, post_answer(401, ""), "HTTP 401", True),
        ("request transport failure", base, host_q, post_answer(0, "TimeoutError"), "transport", True),
        ("signature not verified", base, host_q, post_answer(202, {"signature": "unsigned"}), "did not verify", True),
        ("empty inbox answer", base, host_q, post_answer(200, ""), "did not verify", True),
    ]
    for name, inp, g, p, phrase, warned in cases:
        calls.clear()
        d = decide(inp, g, p)
        check(f"actions/{name}", d.executor == ACTIONS and phrase in d.why, d)
        check(f"actions/{name}: {'warned' if warned else 'deliberate, no warning'}", bool(d.lines) == warned and all("::warning" in l for l in d.lines), d.lines)
    calls.clear()
    decide(with_(stages_file=os.path.join(tmp, "absent.json")), host_q, ok_post)
    check("actions/a repository with no stages asks nothing and queues nothing", calls == [], calls)
    calls.clear()
    decide(base, get_answer(200, {"executor": "actions"}), ok_post)
    check("actions/an actions answer queues nothing", [c[0] for c in calls] == ["GET"], calls)
    # 4. the override wins over the derived base
    d = decide(with_(build_api="https://ops.example.com/api/builds/"), host_q, ok_post)
    check("api/the explicit base wins, trailing slash dropped", d.api_url == "https://ops.example.com/api/builds", d)
    # 5. malformed inputs are refused, never guessed
    for bad in (with_(sha="abc"), with_(channel="Latest Channel")):
        try:
            decide(bad, host_q, ok_post)
            check("input/refused", False, bad)
        except BadInput:
            pass
    try:
        inputs_from_env({"RUN_ID": "12x", "HEAD_SHA": "a" * 40})
        check("input/a non-numeric run id is refused", False)
    except BadInput:
        pass
    # 6. outputs and summary
    out, summ = os.path.join(tmp, "out"), os.path.join(tmp, "summary")
    emit(Decision(HOST, "https://control.example.com/api/builds", "queued", []), {"GITHUB_OUTPUT": out, "GITHUB_STEP_SUMMARY": summ})
    check("emit/outputs", open(out).read() == "executor=host\napi-url=https://control.example.com/api/builds\n", open(out).read())
    check("emit/summary says which and why", "**host** — queued" in open(summ).read())
    for f in failures:
        print(f"SELF-TEST FAILED: {f}")
    if failures:
        return 1
    print("build-executor self-test: all cases passed (host when declared + said + queued and verified; actions — said — on every failure: "
          "no stages, no API, unsafe URL, unreachable, 503, not JSON, no executor, actions, unknown, no pair, refused, unverified; the "
          "request's shape, trunk, the admission's base, the signature, outputs)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
