#!/usr/bin/env python3
"""build-follow.py — stream one build stage's log from a standing build host into the Actions log.

The FOLLOW half of the standing build hosts (MeshWeaver.Plugins `Hosting/StandingBuildHosts` §4.2,
platform reference Doc/Architecture/BuildHosts). While GitHub Actions is still the front door, a
heavy leg that no longer compiles on the runner is replaced by ONE thin job per stage that runs this
script: it reads the stage log the operations instance keeps for the run and prints it, so the
GitHub log of a run shows build and test progress line by line, and it exits with the STAGE's
verdict — so the required context names do not change while a repository migrates.

    build-follow.py --api-url https://control.example.com/api/builds --stage modules \
                    [--repo MeshWeaver.Plugins] [--sha <40 hex>] [--channel latest] \
                    [--timeout-minutes 40] [--audience https://control.example.com]
    build-follow.py --self-test

THE WIRE (§7a): `GET {api}/{repo}/{sha}/stages/{stage}/log?after=<seq>&channel=<c>` with the run's
GitHub OIDC token as the bearer, answering
    {"lines":[{"seq":N,"text":"…"}], "next":N, "skipped":N, "state":"running|succeeded|failed|…",
     "summary":"…"}
The endpoint answers ONLY for the `repository` and `sha` the token's claims name (403 otherwise),
so no secret is stored in any repository and the log is readable by exactly the run that asked.

THE RULES, each of which is a way this step could otherwise read green on no evidence:
  * exit 0 ONLY on state `succeeded`. Every other terminal state — failed, cancelled, skipped,
    timed out, anything new — is exit 1, naming it. A skipped stage is not a passed one.
  * a TIMEOUT is RED. No verdict within --timeout-minutes means the run is NOT judged; the job
    never times out into a green or into GitHub's own cancellation (which reads as neither).
  * an UNREADABLE endpoint is RED, naming why: 403 at once (the token's claims do not name this
    run — retrying cannot change that), 401 after one token refresh, 404 after a grace period
    (the run may not be registered yet when the follow job starts), and transport errors or 5xx
    after a bounded run of consecutive failures.
  * a `skipped` gap is printed LOUDLY as a warning with the count: the host keeps the newest 2,000
    lines, and a follower whose cursor fell behind is told how many it missed — never a silent gap.
  * the verdict is read only after the log is DRAINED: a terminal state is acted on once a poll
    returns no further lines, so the last error lines are printed before the exit.
  * workflow commands from the log are passed through ONLY for `::error`, `::warning` and
    `::notice` (an annotation on the PR's Files tab). Every other `::` line — `::add-mask::`,
    `::stop-commands::`, `::group::`, `::set-output` … — is NEUTRALISED by a leading space: the log
    is text a build wrote, and a build must not be able to steer the runner that prints it.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable, Optional

POLL_SECONDS = 10
# How long a 404 is tolerated: the follow job can start before the build-request it follows has
# been registered as a run node (the PR workflow posts the request and the follow jobs together).
NOT_FOUND_GRACE_SECONDS = 300
# Consecutive transport errors / 5xx tolerated before the endpoint counts as unreadable (≈ 1 min).
MAX_CONSECUTIVE_FAILURES = 6
# GitHub's OIDC tokens are short-lived; a fresh one is requested well inside their lifetime.
TOKEN_REFRESH_SECONDS = 240
# The job is hard-cut at 45 minutes (check-workflow-timeouts.py); the follower must decide first.
MAX_TIMEOUT_MINUTES = 43

GREEN = "succeeded"
# States that mean "not decided yet". Anything else is terminal — and only GREEN is a pass.
PENDING = {"", "queued", "pending", "claimed", "running", "waiting", "staging", "requested"}
PASSTHROUGH = re.compile(r"^::(error|warning|notice)( [^:]*)?::")
SHA = re.compile(r"^[0-9a-f]{40}$")


class Http:
    """The transport. Returns (status, parsed JSON or None); status 0 is a transport failure."""

    def get(self, url: str, headers: dict) -> tuple[int, object]:
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                body = r.read().decode() or "null"
                return r.status, json.loads(body)
        except urllib.error.HTTPError as e:
            return e.code, None
        except (urllib.error.URLError, OSError, ValueError) as e:
            return 0, str(e)


def oidc_token(http: Http, audience: str, env: dict) -> str:
    """The run's GitHub OIDC token for `audience`. Needs `permissions: id-token: write`."""
    url = env.get("ACTIONS_ID_TOKEN_REQUEST_URL", "")
    bearer = env.get("ACTIONS_ID_TOKEN_REQUEST_TOKEN", "")
    if not url or not bearer:
        raise Failure(
            "no OIDC token can be requested: ACTIONS_ID_TOKEN_REQUEST_URL / _TOKEN are not set. "
            "The calling job must grant `permissions: id-token: write` (a reusable workflow can only "
            "narrow its caller's grant)")
    sep = "&" if "?" in url else "?"
    status, body = http.get(f"{url}{sep}audience={urllib.parse.quote(audience, safe='')}",
                            {"Authorization": f"bearer {bearer}", "Accept": "application/json"})
    if status != 200 or not isinstance(body, dict) or not body.get("value"):
        raise Failure(f"GitHub refused the OIDC token request (HTTP {status}) for audience {audience!r}")
    return str(body["value"])


class Failure(Exception):
    """A RED with a reason. Never caught into a pass."""


def neutralise(text: str) -> str:
    """A log line as it may be printed: annotations pass, every other workflow command is defused."""
    text = text.replace("\r", "")
    if text.startswith("::") and not PASSTHROUGH.match(text):
        return " " + text
    return text


def follow(*, api_url: str, repo: str, sha: str, stage: str, channel: str, timeout_minutes: float,
           audience: str, http: Http, env: dict, out: Callable[[str], None],
           clock: Callable[[], float] = time.monotonic,
           sleep: Callable[[float], None] = time.sleep,
           poll_seconds: float = POLL_SECONDS) -> int:
    """Follow one stage to its verdict. Returns the exit code (0 only for `succeeded`)."""
    base = api_url.rstrip("/")
    stage_path = urllib.parse.quote(stage, safe="")
    query = {"channel": channel or "latest"}
    started = clock()
    deadline = started + timeout_minutes * 60
    cursor: Optional[int] = None
    token = oidc_token(http, audience, env)
    token_at = clock()
    refreshed_after_401 = False
    failures = 0
    first_404: Optional[float] = None
    state = ""
    summary = ""
    out(f"::group::build/{stage} — on build-{query['channel']} ({repo}@{sha[:12]})")
    try:
        while True:
            if clock() > deadline:
                raise Failure(
                    f"no verdict for {stage} within {timeout_minutes:g} minutes (last state "
                    f"{state or 'none'}) — the run is NOT judged green; read the run node on the "
                    f"operations instance")
            if clock() - token_at > TOKEN_REFRESH_SECONDS:
                token = oidc_token(http, audience, env)
                token_at = clock()
            q = dict(query)
            if cursor is not None:
                q["after"] = str(cursor)
            url = f"{base}/{urllib.parse.quote(repo, safe='')}/{sha}/stages/{stage_path}/log?" \
                  + urllib.parse.urlencode(q)
            status, body = http.get(url, {"Authorization": f"Bearer {token}",
                                          "Accept": "application/json"})
            if status == 403:
                raise Failure(
                    f"the build API refused this run's OIDC token (HTTP 403): it answers only for the "
                    f"repository and sha the token's claims name — Systemorph/{repo} at {sha}. A "
                    f"different sha (a PR head instead of the merge commit the token names) or a "
                    f"wrong audience ({audience}) reads exactly like this")
            if status == 401:
                if refreshed_after_401:
                    raise Failure(f"the build API refused a FRESH OIDC token too (HTTP 401) — the "
                                  f"audience {audience!r} is not the instance's Hosting:Builds:OidcAudience")
                refreshed_after_401 = True
                token = oidc_token(http, audience, env)
                token_at = clock()
                continue
            if status == 404:
                first_404 = clock() if first_404 is None else first_404
                if clock() - first_404 > NOT_FOUND_GRACE_SECONDS:
                    raise Failure(
                        f"the build API knows no stage {stage!r} for {repo}@{sha} on channel "
                        f"{query['channel']} (HTTP 404 for {NOT_FOUND_GRACE_SECONDS // 60} minutes) — "
                        f"was the build-request posted, and does the repository declare this stage?")
                sleep(poll_seconds)
                continue
            if status != 200 or not isinstance(body, dict):
                failures += 1
                if failures >= MAX_CONSECUTIVE_FAILURES:
                    why = body if status == 0 else f"HTTP {status}"
                    raise Failure(f"the build API is unreadable: {failures} consecutive failed reads "
                                  f"(last: {why}) — the stage's verdict cannot be known, so it is RED")
                sleep(poll_seconds)
                continue
            failures = 0
            first_404 = None
            refreshed_after_401 = False

            skipped = int(body.get("skipped") or 0)
            if skipped > 0:
                out(f"::warning title=Log gap::{skipped} line(s) of {stage} were NOT shown — this "
                    f"follower fell behind the host's 2,000-line window; the run node holds the "
                    f"verdict, the annotations and the newest lines")
            lines = body.get("lines") or []
            for line in lines:
                if isinstance(line, dict):
                    out(neutralise(str(line.get("text", ""))))
            nxt = body.get("next")
            if isinstance(nxt, int):
                cursor = nxt
            elif lines and isinstance(lines[-1], dict) and isinstance(lines[-1].get("seq"), int):
                cursor = lines[-1]["seq"]
            state = str(body.get("state") or "").strip().lower()
            summary = str(body.get("summary") or "").strip()
            if state in PENDING:
                if lines:
                    continue        # more may be waiting — read again before sleeping
                sleep(poll_seconds)
                continue
            if lines:
                continue            # terminal, but drain the log before acting on the verdict
            break
    finally:
        out("::endgroup::")

    took = clock() - started
    if state == GREEN:
        out(f"{stage}: succeeded on build-{query['channel']} in {took / 60:.1f} min"
            + (f" — {summary}" if summary else ""))
        return 0
    raise Failure(f"{stage} ended {state!r} on build-{query['channel']}"
                  + (f" — {summary}" if summary else "")
                  + ("" if state == "failed" else " (only `succeeded` is a pass)"))


def resolve_args(a: argparse.Namespace, env: dict) -> argparse.Namespace:
    a.repo = (a.repo or env.get("GITHUB_REPOSITORY", "").split("/", 1)[-1]).strip()
    a.sha = (a.sha or env.get("GITHUB_SHA", "")).strip().lower()
    a.channel = (a.channel or "latest").strip()
    if not a.api_url.startswith("https://") and not a.api_url.startswith("http://localhost"):
        raise Failure(f"--api-url {a.api_url!r} is not an https URL — refusing to send an OIDC token there")
    if not a.repo or "/" in a.repo:
        raise Failure(f"--repo {a.repo!r} must be the bare repository name (the API path is /{{repo}}/…)")
    if not SHA.match(a.sha):
        raise Failure(f"--sha {a.sha!r} is not a full 40-hex commit")
    # A stage is ONE path segment (its Check Run is build/<name>); `build/<name>` is accepted and
    # normalised, so a caller may pass either the stage or its Check Run name.
    a.stage = (a.stage or "").strip()
    if a.stage.startswith("build/"):
        a.stage = a.stage[len("build/"):]
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", a.stage):
        raise Failure(f"--stage {a.stage!r} is not a stage name (one segment [a-z0-9][a-z0-9._-]*, "
                      f"as .github/build-stages.json declares it)")
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", a.channel):
        raise Failure(f"--channel {a.channel!r} is not a channel name")
    if not (0 < a.timeout_minutes <= MAX_TIMEOUT_MINUTES):
        raise Failure(f"--timeout-minutes {a.timeout_minutes:g} must be in (0, {MAX_TIMEOUT_MINUTES}] — the "
                      f"job is hard-cut at 45 minutes and the follower must reach its own verdict first")
    if not a.audience:
        p = urllib.parse.urlparse(a.api_url)
        a.audience = f"{p.scheme}://{p.netloc}"
    return a


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--api-url", default="")
    ap.add_argument("--repo", default="")
    ap.add_argument("--sha", default="")
    ap.add_argument("--stage", default="")
    ap.add_argument("--channel", default="latest")
    ap.add_argument("--timeout-minutes", type=float, default=40)
    ap.add_argument("--audience", default="")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = dict(os.environ)
    try:
        a = resolve_args(a, env)
        code = follow(api_url=a.api_url, repo=a.repo, sha=a.sha, stage=a.stage, channel=a.channel,
                      timeout_minutes=a.timeout_minutes, audience=a.audience, http=Http(), env=env,
                      out=lambda s: print(s, flush=True))
    except Failure as f:
        print(f"::error title=Build follow::{f}", flush=True)
        summary(env, f"- ❌ `{a.stage}`: {f}")
        return 1
    summary(env, f"- ✅ `{a.stage}` succeeded on build-{a.channel}")
    return code


def summary(env: dict, line: str) -> None:
    path = env.get("GITHUB_STEP_SUMMARY")
    if path:
        try:
            with open(path, "a") as fh:
                fh.write(line + "\n")
        except OSError:
            pass


# ───────────────────────────────── self-test ──────────────────────────────────────────────

class _Clock:
    def __init__(self) -> None:
        self.t = 1000.0

    def now(self) -> float:
        return self.t

    def sleep(self, s: float) -> None:
        self.t += s


class _FakeHttp(Http):
    """Answers scripted (status, body) pairs for log reads; records every log URL it was asked."""

    def __init__(self, answers: list, clock: _Clock, token_status: int = 200) -> None:
        self.answers = list(answers)
        self.urls: list[str] = []
        self.token_requests = 0
        self.clock = clock
        self.token_status = token_status

    def get(self, url: str, headers: dict) -> tuple[int, object]:
        if url.startswith("https://token.example/"):
            self.token_requests += 1
            return (self.token_status, {"value": f"tok{self.token_requests}"} if self.token_status == 200 else None)
        self.urls.append(url)
        self.clock.t += 0.1
        if not self.answers:
            return 200, {"lines": [], "next": None, "skipped": 0, "state": "running", "summary": ""}
        return self.answers.pop(0)


def _run(answers, *, timeout=10.0, token_status=200):
    clock = _Clock()
    http = _FakeHttp(answers, clock, token_status)
    out: list[str] = []
    env = {"ACTIONS_ID_TOKEN_REQUEST_URL": "https://token.example/t?x=1",
           "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "req"}
    try:
        code = follow(api_url="https://control.example.com/api/builds", repo="MeshWeaver.Plugins",
                      sha="a" * 40, stage="modules", channel="latest", timeout_minutes=timeout,
                      audience="https://control.example.com", http=http, env=env, out=out.append,
                      clock=clock.now, sleep=clock.sleep)
        err = None
    except Failure as f:
        code, err = 1, str(f)
    return code, err, out, http


def _page(lines, nxt, state, skipped=0, summary=""):
    return 200, {"lines": [{"seq": s, "text": t} for s, t in lines], "next": nxt,
                 "skipped": skipped, "state": state, "summary": summary}


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        if not cond:
            failures.append(f"{name}: {detail}")

    # 1. The cursor advances: the second read asks after=<next of the first>, and lines print once.
    code, err, out, http = _run([
        _page([(1, "[00:00:03] compose host ready"), (2, "Store cache hit")], 2, "running"),
        _page([(3, "Hosting compiled 15.2s")], 3, "running"),
        _page([], 3, "succeeded", summary="112/112 units"),
    ])
    check("cursor/exit", code == 0 and err is None, (code, err))
    check("cursor/first read has no after", "after=" not in http.urls[0], http.urls[0])
    check("cursor/second read after=2", "after=2" in http.urls[1], http.urls[1])
    check("cursor/third read after=3", "after=3" in http.urls[2], http.urls[2])
    check("cursor/channel on every read", all("channel=latest" in u for u in http.urls), http.urls)
    check("cursor/each line once", sum("Store cache hit" in o for o in out) == 1, out)
    check("cursor/stage is one path segment", "/stages/modules/log?" in http.urls[0], http.urls[0])
    check("cursor/grouped", out[0].startswith("::group::build/modules") and "::endgroup::" in out, out)

    # 2. A skipped gap is LOUD, with the count.
    code, err, out, _ = _run([_page([(2050, "late line")], 2050, "running", skipped=48),
                              _page([], 2050, "succeeded")])
    check("skipped/warned", any(o.startswith("::warning") and "48 line(s)" in o for o in out), out)
    check("skipped/still green on success", code == 0, (code, err))

    # 3. Verdicts: failed is 1, and the drain prints the last lines BEFORE acting on the verdict.
    code, err, out, http = _run([
        _page([(1, "::error file=Hosting/Build/Source/BuildApi.cs,line=41::CS0246 X")], 1, "failed"),
        _page([], 1, "failed", summary="1 error"),
    ])
    check("failed/exit 1", code == 1 and err and "failed" in err, (code, err))
    check("failed/annotation passed through",
          "::error file=Hosting/Build/Source/BuildApi.cs,line=41::CS0246 X" in out, out)
    check("failed/drained before the verdict", len(http.urls) == 2, http.urls)
    for st in ("cancelled", "skipped", "something-new"):
        code, err, _, _ = _run([_page([], 0, st)])
        check(f"verdict/{st} is red", code == 1 and err and st in err, (code, err))

    # 4. Timeout is RED, never a green and never left to GitHub's cancellation.
    code, err, out, _ = _run([], timeout=1.0)
    check("timeout/red", code == 1 and err and "no verdict" in err and "NOT judged green" in err, (code, err))
    check("timeout/group closed", out[-1] == "::endgroup::", out)

    # 5. 403 is RED at once (no retry can change a claims mismatch).
    code, err, _, http = _run([(403, None), _page([], 0, "succeeded")])
    check("403/red", code == 1 and err and "403" in err, (code, err))
    check("403/no retry", len(http.urls) == 1, http.urls)

    # 6. 401 refreshes the token ONCE, then a second 401 is red.
    code, err, _, http = _run([(401, None), _page([], 0, "succeeded")])
    check("401/refresh then green", code == 0 and http.token_requests == 2, (code, err, http.token_requests))
    code, err, _, _ = _run([(401, None), (401, None)])
    check("401/twice red", code == 1 and err and "FRESH" in err, (code, err))

    # 7. Transient failures are tolerated; a run of them is red (unreadable endpoint, named).
    code, err, _, _ = _run([(503, None), (0, "connection reset"), _page([], 0, "succeeded")])
    check("transient/tolerated", code == 0, (code, err))
    code, err, _, _ = _run([(503, None)] * MAX_CONSECUTIVE_FAILURES, timeout=40)
    check("unreadable/red", code == 1 and err and "unreadable" in err, (code, err))

    # 8. 404 is tolerated for the grace period (the run may not be registered yet), then red.
    code, err, _, _ = _run([(404, None), (404, None), _page([], 0, "succeeded")])
    check("404/grace", code == 0, (code, err))
    code, err, _, _ = _run([(404, None)] * 40, timeout=40)
    check("404/red after grace", code == 1 and err and "404" in err, (code, err))

    # 9. A build cannot steer the runner: only annotations pass; other workflow commands are defused.
    check("neutralise/add-mask", neutralise("::add-mask::x") == " ::add-mask::x")
    check("neutralise/stop-commands", neutralise("::stop-commands::tok").startswith(" "))
    check("neutralise/endgroup", neutralise("::endgroup::").startswith(" "))
    check("neutralise/warning passes", neutralise("::warning::w") == "::warning::w")
    check("neutralise/plain text", neutralise("compiled 3/4") == "compiled 3/4")

    # 10. No OIDC grant is a named red, never a silent anonymous read.
    try:
        oidc_token(Http(), "aud", {})
        check("oidc/missing grant", False, "no Failure raised")
    except Failure as f:
        check("oidc/missing grant", "id-token: write" in str(f), f)
    code, err, _, _ = _run([], token_status=403)
    check("oidc/refused is red", code == 1 and err and "OIDC token request" in err, (code, err))

    # 11. Argument guards.
    def args(**kw):
        ns = argparse.Namespace(api_url="https://control.example.com/api/builds", repo="R", sha="b" * 40,
                                stage="x", channel="latest", timeout_minutes=40, audience="")
        for k, v in kw.items():
            setattr(ns, k, v)
        return ns
    check("args/audience derived", resolve_args(args(), {}).audience == "https://control.example.com")
    check("args/build/ prefix normalised", resolve_args(args(stage="build/modules"), {}).stage == "modules")
    for bad in (dict(api_url="http://evil.example/api"), dict(sha="abc"), dict(timeout_minutes=50),
                dict(repo="Systemorph/R"), dict(channel="Bad Channel"), dict(stage="a/b")):
        try:
            resolve_args(args(**bad), {})
            check(f"args/refuses {bad}", False, "accepted")
        except Failure:
            pass

    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-follow self-test: all cases passed (cursor, skipped gap, verdicts, timeout, 403, 401, "
          "transient/unreadable, 404 grace, command neutralisation, OIDC grant, argument guards)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
