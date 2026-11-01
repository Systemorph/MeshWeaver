#!/usr/bin/env python3
"""Read the current PR declaration text, rather than a frozen Actions event body.

Rerunning a failed declaration gate must see the maintainer's corrected body.
The original event supplies the PR identity and head only. A changed head, failed
REST read or malformed response fails closed; none falls back to the stale body.
The body is written as data and is never evaluated by a shell.

🚨 AN UNAVAILABLE API IS "NO VERDICT", NEVER A FINDING (Doc/Architecture/CiRestBudget).
On 2026-10-27 (20:37-21:14Z) the repository's GITHUB_TOKEN bucket ran dry ("API rate limit
exceeded for installation", HTTP 403) and every declaration gate printed "Could not read the
current PR body" — which reads as a defect in the pull request. A rate-limit answer (403/429 with
`x-ratelimit-remaining: 0`, a `retry-after`, or "rate limit" in the message), a 5xx or a transport
failure is therefore retried with bounded backoff honouring `retry-after` / `x-ratelimit-reset`,
and when the window does not reopen within the budget the run ends RED with
"NO VERDICT — GitHub API rate-limited until <reset>; re-run after it" (exit 3). A wait the budget
cannot cover is not slept at all. Only the deliberate refusals (wrong PR, changed head, 401, a
non-rate-limit 403, 404) fail at once.

ONE READ PER RUN ATTEMPT. The pull-request gates of core's dotnet-test.yml share one read: the
`pr-body` job runs this with `--emit-github-output` (body base64, head, attempt), and each gate runs
it with `--reuse-from-env`, which reuses that body ONLY when it was read in THIS run attempt for
THIS head. A "re-run failed jobs" keeps the earlier attempt's outputs, so a gate re-run after the
author corrected the body reads the CURRENT body itself, exactly as before.

This file is fetched ALONE by other repositories (MeshWeaver.Plugins' database-version gate reads
it at core `main`), so it imports nothing outside the standard library and keeps `--output` as its
one required argument.
"""

import argparse
import base64
import binascii
import datetime
import http.client
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.request

NO_VERDICT_EXIT = 3
DEFAULT_BUDGET_SECONDS = 240
MAX_SINGLE_WAIT_SECONDS = 120


class ApiUnavailable(Exception):
    """GitHub could not be asked: rate-limited, 5xx or unreachable past the retry budget."""

    def __init__(self, path, reason, reset_epoch=None):
        super().__init__(reason)
        self.path = path
        self.reason = reason
        self.reset_epoch = reset_epoch

    def sentence(self):
        if self.reset_epoch:
            until = datetime.datetime.fromtimestamp(
                self.reset_epoch, tz=datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            return (f"NO VERDICT — GitHub API rate-limited until {until}; re-run after it. "
                    f"({self.reason}; GET {self.path}). This is not a finding about the pull request.")
        return (f"NO VERDICT — GitHub API unavailable ({self.reason}; GET {self.path}); re-run "
                "once it answers. This is not a finding about the pull request.")


class ApiRefused(Exception):
    """GitHub answered with a deliberate refusal (401, 404, a non-rate-limit 403, ...)."""


class DestinationError(Exception):
    """The LOCAL file a download streams into could not be opened, written or closed (a full disk,
    a read-only path). Deliberately NOT an OSError: `api_get` reads every OSError as a transport
    outage, and a local storage failure retried as one would end as "GitHub unavailable"."""

    def __init__(self, dest, error):
        super().__init__(f"cannot write the download to {dest}: {error}")
        self.dest = dest
        self.error = error


def _stream_to(response, dest, chunk_size=1 << 20):
    """Copy `response` into `dest`, keeping the two failure sides apart: a failed READ propagates
    as the transport error it is (retried), a failed open/write/close raises DestinationError
    (surfaced at once). A body shorter than its Content-Length is IncompleteRead — a sized
    HTTPResponse.read() returns b"" at an early close instead of raising, so the count is checked."""
    expected = _header(getattr(response, "headers", None), "content-length")
    try:
        sink = open(dest, "wb")
    except OSError as error:
        raise DestinationError(dest, error) from error
    received = 0
    try:
        while True:
            chunk = response.read(chunk_size)
            if not chunk:
                break
            received += len(chunk)
            try:
                sink.write(chunk)
            except OSError as error:
                raise DestinationError(dest, error) from error
    finally:
        try:
            sink.close()
        except OSError as error:
            raise DestinationError(dest, error) from error
    if expected.isdigit() and received < int(expected):
        raise http.client.IncompleteRead(b"", int(expected) - received)


def _header(headers, name):
    if headers is None:
        return ""
    value = headers.get(name)
    return value.strip() if isinstance(value, str) else ""


def classify(status, headers, body_text):
    """'ok', 'rate-limited', 'transient' or 'refused' for one HTTP answer."""
    if 200 <= status < 300:
        return "ok"
    if status in (403, 429) and (
            status == 429
            or _header(headers, "x-ratelimit-remaining") == "0"
            or _header(headers, "retry-after")
            or "rate limit" in body_text.lower()):
        return "rate-limited"
    if status >= 500:
        return "transient"
    return "refused"


def wait_for(attempt, headers, now):
    """Seconds to wait before the next try, and the reset epoch the answer named (if any)."""
    retry_after = _header(headers, "retry-after")
    if retry_after.isdigit():
        return int(retry_after), None
    reset = _header(headers, "x-ratelimit-reset")
    if _header(headers, "x-ratelimit-remaining") == "0" and reset.isdigit():
        return max(1, int(reset) - int(now) + 1), int(reset)
    return min(MAX_SINGLE_WAIT_SECONDS, 2 ** (attempt + 1)), None


def _opener():
    # Proxies from the ENVIRONMENT only: the default lookup consults the macOS SystemConfiguration
    # framework, which can crash a Python process; runners configure proxies through env vars.
    return urllib.request.build_opener(
        urllib.request.ProxyHandler(urllib.request.getproxies_environment())).open


def api_get(path, *, token, base_url=None, budget_seconds=DEFAULT_BUDGET_SECONDS,
            urlopen=None, sleep=time.sleep, clock=time.time, accept="application/vnd.github+json",
            raw=False, user_agent="MeshWeaver-read-current-pr-body", dest=None):
    """GET one REST path; JSON on success (the decoded text when `raw`), ApiUnavailable /
    ApiRefused otherwise. Every CI caller that asks GitHub something shares THIS classification
    (probe-fleet-read.py, check-cross-repo-pair.py, transitional_allow.py, check-shared-rules.py,
    github-api-get.py) — a second copy is how "lacks a grant, or is rate-limited" got written.

    `dest` (a path) streams the BYTES of a successful answer to that file and returns None — an
    artifact `…/zip`, whose 302 to signed storage is followed WITHOUT the token. A connection that
    drops mid-body — raised, or a short body against its Content-Length — is transient like any
    other and the file is rewritten from the start on retry (MeshWeaver#7448: `gh run download`
    gave a rate limit no classification at all). A LOCAL failure to open, write or close the file
    raises DestinationError at once, never a retry and never NO VERDICT."""
    base_url = (base_url or os.environ.get("GITHUB_API_URL") or "https://api.github.com").rstrip("/")
    urlopen = urlopen or _opener()
    if dest is not None:
        # Opened once up front: an unwritable destination is the caller's defect, raised here as
        # DestinationError — the same class `_stream_to` raises for a failed write or close.
        try:
            open(dest, "wb").close()
        except OSError as error:
            raise DestinationError(dest, error) from error
    deadline = clock() + budget_seconds
    attempt = 0
    while True:
        request = urllib.request.Request(f"{base_url}/{path}", headers={
            "Accept": accept,
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": user_agent,
        })
        if token:
            # Unredirected: urllib copies ordinary headers onto a redirect to ANY host
            # (.github/scripts/ghrest.py measures it); the token must never leave api.github.com.
            request.add_unredirected_header("Authorization", f"Bearer {token}")
        headers, reset_epoch = None, None
        try:
            with urlopen(request, timeout=30) as response:
                if dest is not None:
                    _stream_to(response, dest)
                    return None
                text = response.read().decode("utf-8")
                return text if raw else json.loads(text)
        except urllib.error.HTTPError as error:
            headers = error.headers
            # The error body is read over the same connection, so it can fail exactly like the
            # request did (a reset or a timeout on a 503). Raised here, it would escape this handler
            # past the sibling `except` below and exit as a defect instead of NO VERDICT (#7457).
            try:
                text = error.read(2048).decode("utf-8", errors="replace")
                body_unread = None
            except (TimeoutError, OSError, http.client.HTTPException) as read_error:
                text, body_unread = "", type(read_error).__name__
            kind = classify(error.code, headers, text)
            if kind == "refused" and body_unread and error.code == 403:
                # A 403 is told apart from a rate limit partly by its BODY; with the body unread
                # the answer is undecided, so it is retried like an outage, never refused on a guess.
                kind = "transient"
            if kind == "refused":
                raise ApiRefused(f"HTTP {error.code} for GET {path}") from error
            reason = f"HTTP {error.code}" + (" rate limit" if kind == "rate-limited" else "")
            if body_unread:
                reason += f" (error body unreadable: {body_unread})"
        # http.client.HTTPException covers IncompleteRead: a connection closed before the full
        # Content-Length body arrived is transient, retried under the same budget, never a traceback.
        except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException) as error:
            reason = f"transport failure: {type(error).__name__}"
        delay, reset_epoch = wait_for(attempt, headers, clock())
        if clock() + delay > deadline:
            # A wait the budget cannot cover is not slept at all: say when to re-run instead.
            raise ApiUnavailable(path, reason, reset_epoch)
        print(f"GitHub API {reason} on GET {path}; retrying in {delay}s "
              f"(budget {int(deadline - clock())}s left).", file=sys.stderr)
        sleep(delay)
        attempt += 1


def read_body(event, repository, read_pr):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("GITHUB_REPOSITORY must identify one owner/repository")
    original = event["pull_request"]
    number = original["number"]
    if type(number) is not int or number < 1:
        raise ValueError("the event does not identify a pull request")
    if original["base"]["repo"]["full_name"] != repository:
        raise ValueError("the event's base repository does not match this workflow")
    expected_head = original["head"]["sha"]
    if not re.fullmatch(r"[0-9a-f]{40}", expected_head):
        raise ValueError("the event does not identify an exact PR head")
    current = read_pr(f"repos/{repository}/pulls/{number}")
    if current["number"] != number or current["base"]["repo"]["full_name"] != repository:
        raise ValueError("the REST response identifies a different pull request")
    if current["head"]["sha"] != expected_head:
        raise ValueError("the PR head changed; validate the new head, not this obsolete run")
    body = current["body"]
    if body is not None and not isinstance(body, str):
        raise ValueError("the REST response has no readable PR body")
    return body or ""


def read_pr(path):
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        raise ValueError("no GH_TOKEN/GITHUB_TOKEN to read the pull request with")
    budget = int(os.environ.get("GITHUB_API_BUDGET_SECONDS") or DEFAULT_BUDGET_SECONDS)
    return api_get(path, token=token, budget_seconds=budget)


def shared_body(event, environ):
    """The body the `pr-body` job read in THIS attempt for THIS head, or None (with why)."""
    encoded = environ.get("SHARED_PR_BODY_B64")
    attempt = environ.get("SHARED_PR_BODY_ATTEMPT", "")
    head = environ.get("SHARED_PR_BODY_HEAD", "")
    if encoded is None or not attempt or not head:
        return None, "no shared read was handed to this job"
    if attempt != environ.get("GITHUB_RUN_ATTEMPT", ""):
        return None, (f"the shared read is from run attempt {attempt}, this is attempt "
                      f"{environ.get('GITHUB_RUN_ATTEMPT', '?')} (a re-run of failed jobs) — "
                      "the author may have corrected the body since")
    if head != event["pull_request"]["head"]["sha"]:
        return None, "the shared read names a different head"
    try:
        return base64.b64decode(encoded.encode("ascii"), validate=True).decode("utf-8"), None
    except (binascii.Error, UnicodeError):
        return None, "the shared read is not valid base64 UTF-8"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--reuse-from-env", action="store_true",
                        help="reuse SHARED_PR_BODY_B64 when it was read in this run attempt for this head")
    parser.add_argument("--emit-github-output", action="store_true",
                        help="also write body-b64, head and attempt to $GITHUB_OUTPUT for later jobs")
    args = parser.parse_args()
    # Never leave an earlier successful read available after a failed refresh.
    args.output.unlink(missing_ok=True)
    try:
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        body, why_not = (shared_body(event, os.environ) if args.reuse_from_env
                         else (None, None))
        if body is not None:
            print("Reusing the pull-request body read once in this run attempt (job `pr-body`).")
        else:
            if why_not:
                print(f"Reading the pull-request body through REST: {why_not}.")
            body = read_body(event, os.environ["GITHUB_REPOSITORY"], read_pr)
        args.output.write_text(body, encoding="utf-8")
        if args.emit_github_output:
            encoded = base64.b64encode(body.encode("utf-8")).decode("ascii")
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
                out.write(f"body-b64={encoded}\n")
                out.write(f"head={event['pull_request']['head']['sha']}\n")
                out.write(f"attempt={os.environ['GITHUB_RUN_ATTEMPT']}\n")
    except ApiUnavailable as error:
        print(f"::error title=NO VERDICT — GitHub API::{error.sentence()}")
        return NO_VERDICT_EXIT
    except (KeyError, TypeError, ValueError, OSError, ApiRefused) as error:
        # Do not echo arbitrary PR text or authentication output into workflow commands.
        detail = f": {error}" if isinstance(error, ApiRefused) else ""
        print(f"::error::Could not read the current PR body ({type(error).__name__}{detail}); "
              "check REST access and whether the PR head changed. No cached body was used.")
        return 1
    print(f"Current pull-request body: {len(body.encode('utf-8'))} bytes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
