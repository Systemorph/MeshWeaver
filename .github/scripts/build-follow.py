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
The endpoint answers ONLY for the token's `repository` and the Actions run recorded on the build
(`run_id`; 403 otherwise), so no secret is stored in any repository and the log is readable by
exactly the run that asked. `--sha` is the commit the build is keyed by — the PR HEAD, never the
merge commit (the lane passes `github.event.pull_request.head.sha || github.sha`).

THE RULES, each of which is a way this step could otherwise read green on no evidence:
  * exit 0 ONLY on state `succeeded`. Every other terminal state — failed, `not-run` (the run
    ended before this stage started), cancelled, skipped, timed out, anything new — is exit 1,
    naming it. A skipped or never-run stage is not a passed one.
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
  * the RUN decides when the follow ends, not the stage: while `runTerminal` is false the follower
    keeps polling even after its stage's verdict (the run can restart the stage — a lost host, a
    re-queue — and the restart is printed), and at `runTerminal` it exits with the stage's FINAL
    `state` (`succeeded` → 0, anything else → 1). If the follow WINDOW (--timeout-minutes, inside the
    45-minute job cap) ends first with the stage's verdict already in, it exits with that verdict and
    a warning that the stage's Check Run is the authority — a run may be longer than one job (its
    budget is the host's, up to 720 min); a stage still pending at the window's end is RED.
  * PROGRESS is printed even when the log is quiet: while the stage is pending or running, the
    page's one-line `heartbeat` (stage, elapsed, units/tests/errors, what is left, the run's
    done/total, percent and estimate — the control instance's run board, Hosting/BuildService.md
    "Watching a run") is printed at most every HEARTBEAT_SECONDS, marked `⏱ ` so it is never a
    command. An instance that serves no heartbeat gets none printed — nothing is invented here.
  * the STEP SUMMARY carries the run's stage board (the page's `board` — the SAME markdown as the
    `build` Check Run's summary) and a link to the run's live page on the operations instance
    (`<origin>/<runPath>`), on a pass and on a red alike.
  * workflow commands from the log are passed through ONLY for `::error`, `::warning` and
    `::notice` (an annotation on the PR's Files tab), RE-COMPOSED from validated parts. Every other
    command-shaped physical line — `::add-mask::`, `::stop-commands::`, `::group::`, `##[…]`, after
    any leading whitespace — is made inert by a visible `› ` marker (the runner trims whitespace
    before it reads a command, so a space would defuse nothing): the log is text a build wrote, and
    a build must not be able to steer the runner that prints it.
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
# The heartbeat's cadence — BuildBoard.HeartbeatEvery on the operations instance.
HEARTBEAT_SECONDS = 30
HEARTBEAT_MARK = "⏱ "
# The step summary's board is bounded well inside GitHub's 1 MiB per step.
MAX_BOARD_CHARS = 60_000
# The run's page path as the instance names it: Hosting/Builds/<repo>/<id> — anything else is not linked.
RUN_PATH = re.compile(r"^Hosting/Builds/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")

GREEN = "succeeded"
# States that mean "not decided yet". Anything else is terminal — and only GREEN is a pass.
PENDING = {"", "queued", "pending", "claimed", "running", "waiting", "staging", "requested"}
# An annotation as the LOG may carry it: `::error|warning|notice[ k=v,…]::message`.
ANNOTATION = re.compile(r"^::(?P<sev>error|warning|notice)(?:\s+(?P<props>[^:]*))?::(?P<msg>.*)$")
ANNOTATION_PROPS = ("file", "line", "col", "endLine", "endColumn", "title")
# The marker that makes any other command-shaped line inert. The runner trims leading WHITESPACE
# before it looks for `::` (and for the legacy `##[…]` syntax), so a space defuses nothing; a
# visible, non-whitespace first character does.
INERT = "› "
# The runner's legacy parser finds `##[` ANYWHERE in a line, so the delimiter itself is broken
# wherever it occurs in emitted text (a zero-width space between the `#` and the `[`).
LEGACY = "##["
LEGACY_BROKEN = "##\u200b["
SHA = re.compile(r"^[0-9a-f]{40}$")
STAGE = re.compile(r"^[a-z0-9][a-z0-9._-]*$")


def aggregate_check(channel: str) -> str:
    """The run's AGGREGATE Check Run: `build` on latest, `build-<channel>` otherwise — no slash, so
    it can never collide with a stage's check (a stage named `verdict` is `build/verdict`)."""
    return "build" if channel == "latest" else f"build-{channel}"


def stage_check(stage: str, channel: str) -> str:
    """A stage's Check Run: `build/<stage>` on latest, `build-<channel>/<stage>` otherwise."""
    return f"{aggregate_check(channel)}/{stage}"


def stage_of(name: str, channel: str) -> str:
    """The stage a `--stage` value names: a bare stage, or its check name on THIS channel. The
    aggregate check is not a stage — following it is refused, never matched to a stage."""
    name = (name or "").strip()
    if name == aggregate_check(channel) or re.fullmatch(r"build(-[a-z0-9][a-z0-9._-]*)?", name):
        raise Failure(f"--stage {name!r} names an AGGREGATE check, not a stage — follow a stage (e.g. modules)")
    prefix = aggregate_check(channel) + "/"
    if name.startswith(prefix):
        name = name[len(prefix):]
    elif "/" in name:
        raise Failure(f"--stage {name!r} is not a stage of channel {channel} (its checks are {prefix}<stage>)")
    if not STAGE.match(name):
        raise Failure(f"--stage {name!r} is not a stage name (one segment [a-z0-9][a-z0-9._-]*, "
                      f"as .github/build-stages.json declares it)")
    return name


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """🚨 The transport never follows a redirect (MeshWeaver.Plugins#4514). Every call it makes
    carries a bearer — the run's OIDC token on a build-API read, the runtime's request token on the
    OIDC mint — and urllib's default handler re-sends the request, Authorization header included, to
    whatever the 3xx names: another host, or an https→http downgrade. The `https://` check on
    --api-url covers the first hop only. Returning None makes urllib raise the 3xx as an HTTPError,
    which `Http.get` answers as its status, before any byte reaches the target — the same rule as
    MeshWeaver.Plugins `scripts/build-probe.py`. `follow` turns a 3xx into a RED at once."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def is_redirect(status: int) -> bool:
    """Whether a transport status is a redirect the transport refused to follow. Pure."""
    return 300 <= status < 400


class Http:
    """The transport. Returns (status, parsed JSON or None); status 0 is a transport failure, and a
    3xx is a redirect it refused to follow (see _NoRedirect) — every caller reads it as a refusal."""

    def __init__(self) -> None:
        self._opener = urllib.request.build_opener(_NoRedirect)

    def get(self, url: str, headers: dict) -> tuple[int, object]:
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with self._opener.open(req, timeout=30) as r:
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


def _escape_data(v: str) -> str:
    return v.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A").replace(LEGACY, LEGACY_BROKEN)


def _escape_prop(v: str) -> str:
    return _escape_data(v).replace(":", "%3A").replace(",", "%2C")


def one_line(text: str) -> str:
    """A server-supplied string (a stage's summary or state) as ONE inert physical line: line breaks
    collapse to " ⏎ " and every legacy `##[` is broken, so it can neither start a second line that
    carries a command nor be a command where it is printed at a line's start."""
    flat = " ⏎ ".join(p.strip() for p in str(text).replace("\r\n", "\n").replace("\r", "\n").split("\n") if p.strip())
    flat = flat.replace(LEGACY, LEGACY_BROKEN)
    return INERT + flat if flat.startswith("::") else flat


def neutralise(text: str) -> list[str]:
    """The physical lines a log entry may be printed as. An annotation is RE-COMPOSED from its
    validated parts (severity, the known properties, the message) — never echoed — and every other
    line that the runner could read as a command (`::…`, `##[…]`, after any leading whitespace) is
    made inert by a visible marker. A build must not be able to steer the runner that prints its log
    (`::add-mask::`, `::stop-commands::`, `::set-output`, `##[group]`, …)."""
    out: list[str] = []
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        stripped = line.lstrip()
        m = ANNOTATION.match(stripped)
        if m:
            props = []
            for part in (m.group("props") or "").split(","):
                k, sep, v = part.strip().partition("=")
                if sep and k in ANNOTATION_PROPS and v:
                    if k in ("line", "col", "endLine", "endColumn") and not v.isdigit():
                        continue
                    props.append(f"{k}={_escape_prop(v)}")
            head = f"::{m.group('sev')}" + (" " + ",".join(props) if props else "")
            out.append(f"{head}::{_escape_data(m.group('msg'))}")
        elif stripped.startswith("::"):
            out.append(INERT + stripped.replace(LEGACY, LEGACY_BROKEN))
        else:
            out.append(line.replace(LEGACY, LEGACY_BROKEN))
    return out


def follow(*, api_url: str, repo: str, sha: str, stage: str, channel: str, timeout_minutes: float,
           audience: str, http: Http, env: dict, out: Callable[[str], None], probe: str = "",
           clock: Callable[[], float] = time.monotonic,
           sleep: Callable[[float], None] = time.sleep,
           poll_seconds: float = POLL_SECONDS,
           heartbeat_seconds: float = HEARTBEAT_SECONDS,
           report: Optional[dict] = None) -> int:
    """Follow one stage to its verdict. Returns the exit code (0 only for `succeeded`). `report`, when
    given, receives the newest `board` and `run_path` the instance served (for the step summary)."""
    base = api_url.rstrip("/")
    stage_path = urllib.parse.quote(stage, safe="")
    query = {"channel": channel or "latest", **({"probe": probe} if probe else {})}
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
    last_beat: Optional[float] = None
    report = report if report is not None else {}
    out(f"::group::{stage_check(stage, query['channel'])} — on build-{query['channel']} ({repo}@{sha[:12]})")
    try:
        while True:
            if clock() > deadline:
                if state and state not in PENDING:
                    # The follower's window (inside the 45-minute job cap) ended with the stage's
                    # verdict IN but the run still going (a later stage, or a run longer than the
                    # window). Exit with that verdict, and say the Check Run is the authority: a
                    # later restart of this stage changes it there, not here.
                    out(f"::warning title=Run still going::the follow window ({timeout_minutes:g} min) ended with "
                        f"{stage} {state} while the run is not terminal — exiting with that verdict; the "
                        f"stage's Check Run on the commit is the authority if the run restarts it")
                    break
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
                    f"the build API refused this run's OIDC token (HTTP 403): it answers only for "
                    f"Systemorph/{repo} and the Actions run recorded on the build ({repo}@{sha[:12]}, "
                    f"channel {query['channel']}). A build-request this workflow run did not post, a "
                    f"merge-commit sha where the PR head was meant, or a wrong audience ({audience}) "
                    f"reads exactly like this")
            if is_redirect(status):
                raise Failure(f"the build API answered a redirect (HTTP {status}) — refused, never followed: the "
                              f"OIDC bearer is never sent on to a redirect target. Is --api-url the build API's "
                              f"own host?")
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
                    for printable in neutralise(str(line.get("text", ""))):
                        out(printable)
            nxt = body.get("next")
            if isinstance(nxt, int):
                cursor = nxt
            elif lines and isinstance(lines[-1], dict) and isinstance(lines[-1].get("seq"), int):
                cursor = lines[-1]["seq"]
            new_state = one_line(str(body.get("state") or "")).strip().lower()
            summary = one_line(str(body.get("summary") or "")).strip()
            # A stage's verdict is NOT the end: the RUN can still restart it (a host lost, a re-queue
            # after a platform move). The follower ends only when the run is terminal — and exits
            # with the stage's FINAL state then. A page without `runTerminal` (an older instance)
            # keeps the old rule: the stage's terminal state ends the follow.
            has_run = "runTerminal" in body
            run_terminal = body.get("runTerminal") is True
            run_state = str(body.get("runState") or "").strip()
            if state and state not in PENDING and new_state in PENDING:
                out(f"::notice title=Stage restarted::{stage} was {state} and is {new_state or 'pending'} again — "
                    f"the run restarted it (run {run_state or 'running'}); following on")
            state = new_state
            board = body.get("board")
            if isinstance(board, str) and board.strip():
                report["board"] = board
            run_path = body.get("runPath")
            if isinstance(run_path, str) and RUN_PATH.match(run_path):
                report["run_path"] = run_path
            heartbeat = body.get("heartbeat")
            if isinstance(heartbeat, str) and heartbeat.strip() and state in PENDING \
                    and (last_beat is None or clock() - last_beat >= heartbeat_seconds):
                out(HEARTBEAT_MARK + one_line(heartbeat))
                last_beat = clock()
            if lines:
                continue                # drain the log before deciding anything
            if has_run:
                if run_terminal:
                    break
                sleep(poll_seconds)
                continue
            if state in PENDING:
                sleep(poll_seconds)
                continue
            break
    finally:
        out("::endgroup::")

    took = clock() - started
    if state == GREEN:
        out(f"{stage}: succeeded on build-{query['channel']} in {took / 60:.1f} min"
            + (f" — {summary}" if summary else ""))
        return 0
    if state == "not-run":
        raise Failure(f"{stage} did NOT RUN on build-{query['channel']}: the run ended before this stage started"
                      + (f" — {summary}" if summary else "") + " (a stage that never ran is not a pass)")
    raise Failure(f"{stage} ended {state!r} on build-{query['channel']}"
                  + (f" — {summary}" if summary else "")
                  + ("" if state == "failed" else " (only `succeeded` is a pass)"))


def resolve_args(a: argparse.Namespace, env: dict) -> argparse.Namespace:
    a.repo = (a.repo or env.get("GITHUB_REPOSITORY", "").split("/", 1)[-1]).strip()
    a.sha = (a.sha or env.get("GITHUB_SHA", "")).strip().lower()
    a.channel = (a.channel or "latest").strip()
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", a.channel):
        raise Failure(f"--channel {a.channel!r} is not a channel name")
    parsed = urllib.parse.urlparse(a.api_url)
    host = (parsed.hostname or "").lower()
    local = parsed.scheme == "http" and host in ("localhost", "127.0.0.1", "::1") and "@" not in parsed.netloc
    if not ((parsed.scheme == "https" and host) or local):
        raise Failure(f"--api-url {a.api_url!r} is not an https URL (plain http only to localhost) — "
                      f"refusing to send an OIDC token there")
    if not a.repo or "/" in a.repo:
        raise Failure(f"--repo {a.repo!r} must be the bare repository name (the API path is /{{repo}}/…)")
    if not SHA.match(a.sha):
        raise Failure(f"--sha {a.sha!r} is not a full 40-hex commit")
    # A stage is ONE path segment; its Check Run is build/<stage> (latest) or build-<channel>/<stage>,
    # either accepted and normalised. The aggregate (`build` / `build-<channel>`) is refused.
    a.stage = stage_of(a.stage, a.channel)
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
    ap.add_argument("--timeout-minutes", type=float, default=MAX_TIMEOUT_MINUTES)
    ap.add_argument("--audience", default="")
    ap.add_argument("--probe", default="", help="a probe run's id (the claim's `probe`); rides on every log read")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = dict(os.environ)
    report: dict = {}
    try:
        a = resolve_args(a, env)
        code = follow(api_url=a.api_url, repo=a.repo, sha=a.sha, stage=a.stage, channel=a.channel,
                      timeout_minutes=a.timeout_minutes, audience=a.audience, http=Http(), env=env, probe=a.probe.strip(),
                      out=lambda s: print(s, flush=True), report=report)
    except Failure as f:
        print(f"::error title=Build follow::{_escape_data(one_line(str(f)))}", flush=True)
        summary(env, f"- ❌ `{a.stage}`: {one_line(str(f))}" + summary_block(a.api_url, report))
        return 1
    summary(env, f"- ✅ `{a.stage}` succeeded on build-{a.channel}" + summary_block(a.api_url, report))
    return code


def page_url(api_url: str, run_path: str) -> str:
    """The run's live page on the operations instance — the API's origin + the run's node path — or ''
    when the path is not a run path or the API is not https (plain http only to localhost)."""
    if not RUN_PATH.match(run_path or ""):
        return ""
    p = urllib.parse.urlparse(api_url or "")
    host = (p.hostname or "").lower()
    local = p.scheme == "http" and host in ("localhost", "127.0.0.1", "::1")
    if "@" in p.netloc or not ((p.scheme == "https" and host) or local):
        return ""
    return f"{p.scheme}://{p.netloc}/{run_path}"


def summary_block(api_url: str, report: dict) -> str:
    """What the step summary adds below the verdict line: the run's live page and its stage board as the
    instance last served it (the `build` Check Run's markdown). Empty when the instance served neither."""
    parts: list[str] = []
    url = page_url(api_url, str(report.get("run_path") or ""))
    if url:
        parts.append(f"\n  - 📡 Live progress of the run (stage board, what is left, log tail): {url}")
    board = str(report.get("board") or "").replace(LEGACY, LEGACY_BROKEN)
    if board.strip():
        if len(board) > MAX_BOARD_CHARS:
            board = board[:MAX_BOARD_CHARS - 1] + "…"
        parts.append("\n\n<details open><summary>Stage board — as of this job's last read</summary>\n\n"
                     + board.rstrip() + "\n\n</details>\n")
    return "".join(parts)


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


def _run_with(answers, *, heartbeat_seconds=HEARTBEAT_SECONDS, report=None):
    clock = _Clock()
    http = _FakeHttp(answers, clock)
    out: list[str] = []
    env = {"ACTIONS_ID_TOKEN_REQUEST_URL": "https://token.example/t?x=1", "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "req"}
    code = follow(api_url="https://control.example.com/api/builds", repo="MeshWeaver.Plugins", sha="a" * 40, stage="modules",
                  channel="latest", timeout_minutes=10.0, audience="https://control.example.com", http=http, env=env,
                  out=out.append, clock=clock.now, sleep=clock.sleep, heartbeat_seconds=heartbeat_seconds, report=report)
    return code, None, out, http


def _page(lines, nxt, state, skipped=0, summary=""):
    return 200, {"lines": [{"seq": s, "text": t} for s, t in lines], "next": nxt,
                 "skipped": skipped, "state": state, "summary": summary}


def _redirect_cases(check) -> None:
    """The redirect refusal, against the real Http transport over loopback, with its control."""
    import http.server
    import threading

    seen: list[str] = []

    class Target(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            seen.append(self.headers.get("Authorization") or "")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"ok": true}')

    target = http.server.HTTPServer(("127.0.0.1", 0), Target)
    target_url = f"http://127.0.0.1:{target.server_address[1]}/stolen"

    class Redirector(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            self.send_response(302)
            self.send_header("Location", target_url)
            self.end_headers()

    redirector = http.server.HTTPServer(("127.0.0.1", 0), Redirector)
    servers = [target, redirector]
    for s in servers:
        threading.Thread(target=s.serve_forever, daemon=True).start()
    try:
        # Control: the target itself answers, and records the bearer it was sent — so an empty
        # `seen` below is a refusal, never a recorder that cannot record.
        status, body = Http().get(target_url, {"Authorization": "Bearer control"})
        check("redirect/control: the recorder records a direct call", status == 200 and seen == ["Bearer control"],
              (status, body, seen))
        seen.clear()
        status, body = Http().get(f"http://127.0.0.1:{redirector.server_address[1]}/api/builds?after=1",
                                  {"Authorization": "Bearer secret-oidc"})
        check("redirect/a cross-origin 302 is answered as its 3xx, never followed", status == 302 and is_redirect(status),
              (status, body))
        check("redirect/🚨 no bearer reaches the redirect target", seen == [], seen)
    finally:
        for s in servers:
            s.shutdown()
            s.server_close()

    # An https→http downgrade: the handler builds no follow-up request, so urllib raises the 3xx.
    req = urllib.request.Request("https://control.example.com/api/builds", headers={"Authorization": "Bearer x"})
    check("redirect/an https→http downgrade builds no follow-up request",
          _NoRedirect().redirect_request(req, None, 301, "Moved", {}, "http://control.example.com/api/builds") is None,
          "followed")

    # The follower: a 3xx is RED at once — one read, no retry, no token sent anywhere else.
    code, err, _out, fhttp = _run([(302, None)])
    check("redirect/follow: a 3xx is RED at once, naming the redirect",
          code == 1 and err is not None and "redirect (HTTP 302)" in err and len(fhttp.urls) == 1, (code, err, fhttp.urls))


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
    check("cursor/no probe on a normal run", all("probe=" not in u for u in http.urls), http.urls)
    clock_p = _Clock()
    http_p = _FakeHttp([_page([], 0, "succeeded")], clock_p)
    follow(api_url="https://control.example.com/api/builds", repo="R", sha="a" * 40, stage="modules", channel="latest",
           timeout_minutes=5, audience="https://control.example.com", http=http_p, out=lambda o: None, probe="p-123",
           env={"ACTIONS_ID_TOKEN_REQUEST_URL": "https://token.example/t?x=1", "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "r"},
           clock=clock_p.now, sleep=clock_p.sleep)
    check("probe/a probe run's log reads carry &probe=<id>", http_p.urls and all("probe=p-123" in u for u in http_p.urls), http_p.urls)
    check("cursor/each line once", sum("Store cache hit" in o for o in out) == 1, out)
    check("cursor/stage is one path segment", "/stages/modules/log?" in http.urls[0], http.urls[0])
    check("cursor/grouped", out[0].startswith("::group::build/modules") and "::endgroup::" in out, out)

    # 1b. The RUN decides when following ends: a stage verdict while the run is still running keeps
    #     the follower going, a restart after success is followed, and the final state at runTerminal
    #     is the exit.
    def _rpage(lines, nxt, state, run_state, terminal, summary=""):
        st, body = _page(lines, nxt, state, summary=summary)
        return st, dict(body, runState=run_state, runTerminal=terminal)
    code, err, out, http = _run([
        _rpage([(1, "compiled")], 1, "succeeded", "running", False),
        _rpage([], 1, "succeeded", "running", False),
        _rpage([], 1, "succeeded", "succeeded", True),
    ])
    check("run/succeeded but the run still running keeps polling", code == 0 and len(http.urls) == 3, (code, err, http.urls))
    code, err, out, _ = _run([
        _rpage([(1, "compiled")], 1, "succeeded", "running", False),
        _rpage([(2, "host lost — re-queued")], 2, "queued", "running", False),
        _rpage([(3, "CS0246 on the new set")], 3, "failed", "running", False),
        _rpage([], 3, "failed", "failed", True, summary="1 error"),
    ])
    check("run/a reset after success is followed, and the FINAL state is the exit",
          code == 1 and err and "failed" in err and any("Stage restarted" in o for o in out), (code, err, out))
    code, err, _, _ = _run([_rpage([], 0, "not-run", "failed", True)])
    check("run/not-run at a terminal run is red", code == 1 and err and "did NOT RUN" in err, (code, err))
    code, err, out, _ = _run([_rpage([], 0, "succeeded", "running", False)] * 200, timeout=1.0)
    check("run/window ends with the stage's verdict in: exit with it, and warn the Check Run is the authority",
          code == 0 and any("Run still going" in o for o in out), (code, err, out))
    code, err, _, _ = _run([_rpage([], 0, "failed", "running", False)] * 200, timeout=1.0)
    check("run/window ends with the stage failed: red", code == 1 and err and "failed" in err, (code, err))
    code, err, _, _ = _run([_rpage([], 0, "running", "running", False)], timeout=1.0)
    check("run/window ends with the stage still pending: a timeout, not a green", code == 1 and err and "no verdict" in err, (code, err))

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
    code, err, out, _ = _run([_page([], 0, "not-run", summary="stage compile-check failed first")])
    check("verdict/not-run is a terminal RED, said as such", code == 1 and err and "did NOT RUN" in err
          and "compile-check failed first" in err, (code, err))
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

    # 9. A build cannot steer the runner: annotations are re-composed, every other command is inert —
    #    a leading SPACE defuses nothing (the runner trims whitespace before it reads `::`).
    for evil in ("::add-mask::x", "  ::add-mask::x", "\t::stop-commands::tok", "::endgroup::", "##[group]x",
                 "   ##[error]x", "::set-output name=a::b"):
        printed = neutralise(evil)
        check(f"neutralise/inert: {evil!r}", all(not p.lstrip().startswith(("::", "##[")) for p in printed), printed)
    check("neutralise/a second physical line cannot carry a command",
          all(not p.lstrip().startswith("::") for p in neutralise("ok\n::add-mask::secret")[1:]), neutralise("ok\n::add-mask::secret"))
    check("neutralise/annotation re-composed", neutralise("::error file=a/B.cs,line=41::CS0246 X") ==
          ["::error file=a/B.cs,line=41::CS0246 X"], neutralise("::error file=a/B.cs,line=41::CS0246 X"))
    check("neutralise/unknown annotation properties dropped",
          neutralise("::warning file=a.cs,line=x,evil=1::w") == ["::warning file=a.cs::w"], neutralise("::warning file=a.cs,line=x,evil=1::w"))
    check("neutralise/indented annotation still an annotation", neutralise("  ::notice::n") == ["::notice::n"])
    # the runner's LEGACY parser: `##[` anywhere in a line is a command — prefixed, embedded, or
    # inside an annotation's message — so no emitted line may contain the delimiter at all.
    for evil in ("› ##[add-mask]secret", "build ok ##[add-mask]secret", "##[set-output name=a;]b",
                 "::error file=a.cs::x ##[add-mask]y", "  text ##[group]g", "::add-mask::x ##[error]e"):
        printed = neutralise(evil)
        check(f"neutralise/no legacy delimiter survives: {evil!r}", all(LEGACY not in p for p in printed), printed)
    check("neutralise/plain text", neutralise("compiled 3/4") == ["compiled 3/4"])
    # a TERMINAL summary (e.g. a hand-over failure naming a stage-created filename) is one inert line
    evil_summary = "hand-over failed: Store/bundle\n::stop-commands::tok\n##[group]x\r::add-mask::y"
    code, err, out, _ = _run([_page([], 0, "failed", summary=evil_summary)])
    printed = [o for o in out] + [err or ""]
    check("summary/one line: no command can start a line it reaches",
          all(not ln.lstrip().startswith(("::stop", "::add", "##[")) for o in printed for ln in o.split("\n")) and "\n" not in (err or ""), printed)
    check("summary/no legacy delimiter survives", all(LEGACY not in o for o in printed), printed)
    code, _, out, _ = _run([_page([], 0, "succeeded", summary="ok\n::add-mask::z")])
    check("summary/the green line is one inert line too", code == 0 and not any(o.startswith("::add") for o in out) and all("\n" not in o for o in out), out)
    check("summary/one_line keeps plain text", one_line("3 errors") == "3 errors" and one_line("::x").startswith(INERT))

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
    check("args/a stable stage check normalised", resolve_args(args(stage="build-stable/gate", channel="stable"), {}).stage == "gate")
    # The aggregate and the stage checks are DISJOINT: a follower waiting on the aggregate never
    # matches a stage's check, even a stage named like the old aggregate (`verdict`).
    for ch in ("latest", "stable"):
        agg = aggregate_check(ch)
        check(f"names/the aggregate of {ch} carries no slash", "/" not in agg and agg == ("build" if ch == "latest" else "build-stable"))
        for st in ("verdict", "modules", "build", "gate"):
            check(f"names/{agg} never equals {stage_check(st, ch)}", stage_check(st, ch) != agg and stage_check(st, ch).startswith(agg + "/"))
        for name in (agg, "build", "build-stable"):
            try:
                stage_of(name, ch)
                check(f"names/following the aggregate {name!r} on {ch} is refused", False, "matched a stage")
            except Failure:
                pass
    try:
        stage_of("build-stable/gate", "latest")
        check("names/another channel's stage check is refused", False, "matched")
    except Failure:
        pass
    check("names/a stage named verdict is a stage", stage_of("build/verdict", "latest") == "verdict")
    check("args/http to localhost allowed", resolve_args(args(api_url="http://localhost:8080/api/builds"), {}).api_url.startswith("http://localhost"))
    for bad in (dict(api_url="http://evil.example/api"), dict(api_url="http://localhost.example.com/api/builds"),
                dict(api_url="http://localhost@remote.example.com/api/builds"), dict(sha="abc"), dict(timeout_minutes=50),
                dict(repo="Systemorph/R"), dict(channel="Bad Channel"), dict(stage="a/b")):
        try:
            resolve_args(args(**bad), {})
            check(f"args/refuses {bad}", False, "accepted")
        except Failure:
            pass

    # 9. PROGRESS: the heartbeat is printed while the stage runs — at once, then at most every
    #    HEARTBEAT_SECONDS whatever the poll rate — inert, and only what the instance served.
    def _hpage(state, beat, lines=(), board=None, run_path=None):
        st, body = _page(list(lines), None, state)
        body = dict(body, heartbeat=beat)
        if board is not None:
            body["board"] = board
        if run_path is not None:
            body["runPath"] = run_path
        return st, body
    running = [_hpage("running", f"▶️ modules: running 00:00:{i:02d} · 3 units left | run 0/2 stages · 10 %") for i in range(7)]
    code, err, out, http = _run(running + [_hpage("succeeded", "✅ modules: succeeded", board="| | stage |\n|---|---|\n| ✅ | modules |",
                                                   run_path="Hosting/Builds/MeshWeaver.Plugins/" + "a" * 40)])
    beats = [o for o in out if o.startswith(HEARTBEAT_MARK)]
    # Seven running reads 10 s apart (≈ 60 s): a beat at the first read, then one per 30 s — three, never seven.
    check("heartbeat/cadence", len(beats) == 3, beats)
    check("heartbeat/first beat at once, carrying the instance's line", beats and "3 units left" in beats[0] and "run 0/2 stages" in beats[0], beats)
    check("heartbeat/none after the stage's verdict", not any("succeeded" in b for b in beats), beats)
    # The negative control for the cadence: with no throttle every running read would beat — the counter can fail.
    _, _, out0, _ = _run_with(running + [_hpage("succeeded", "x")], heartbeat_seconds=0)
    check("heartbeat/control: unthrottled, every running read beats", sum(o.startswith(HEARTBEAT_MARK) for o in out0) == 7, out0)
    # An instance that serves no heartbeat (older) gets none printed — nothing is invented.
    _, _, out_old, _ = _run([_page([], None, "running")] * 4 + [_page([], None, "succeeded")])
    check("heartbeat/an older instance prints none", not any(o.startswith(HEARTBEAT_MARK) for o in out_old), out_old)
    # A heartbeat cannot be a command: it starts with the mark and the legacy delimiter is broken.
    _, _, out_cmd, _ = _run([_hpage("running", "::error::x ##[group]g"), _hpage("succeeded", "")])
    cmd = [o for o in out_cmd if o.startswith(HEARTBEAT_MARK)]
    check("heartbeat/inert", cmd == [HEARTBEAT_MARK + INERT + "::error::x " + LEGACY_BROKEN + "group]g"], cmd)

    # 10. The step summary: the run's live page and the board the instance served.
    report: dict = {}
    _run_with([_hpage("running", "b", board="| | stage |", run_path="Hosting/Builds/R/" + "a" * 40),
               _hpage("succeeded", "", run_path="../../evil")], report=report)
    check("summary/the newest valid run path is kept (a malformed one is ignored)", report.get("run_path") == "Hosting/Builds/R/" + "a" * 40, report)
    block = summary_block("https://control.example.com/api/builds", report)
    check("summary/links the run's page", "https://control.example.com/Hosting/Builds/R/" + "a" * 40 in block, block)
    check("summary/carries the board", "| | stage |" in block and "<details open>" in block, block)
    check("summary/no page link off a non-run path", page_url("https://control.example.com/api/builds", "https://evil.example/x") == "", "linked")
    check("summary/no page link off plain http", page_url("http://evil.example/api/builds", "Hosting/Builds/R/x") == "", "linked")
    check("summary/nothing served → nothing added", summary_block("https://control.example.com/api/builds", {}) == "", "added")

    # 🚨 The REAL transport never carries a bearer across a redirect (MeshWeaver.Plugins#4514).
    # Two loopback servers on different ports are two origins: the first answers 302 to the
    # second, which records every Authorization header that reaches it.
    _redirect_cases(check)

    if failures:
        for f in failures:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("build-follow self-test: all cases passed (cursor, skipped gap, verdicts, timeout, 403, 401, "
          "transient/unreadable, 404 grace, command neutralisation, OIDC grant, argument guards, "
          "heartbeat cadence + its control, step-summary board and page link, redirect refusal)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
