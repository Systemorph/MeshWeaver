#!/usr/bin/env python3
"""probe-fleet-read.py — PRESENT is not VALID: prove a minted fleet token can read every registered repo.

Replaces three copies of a `curl … -w '%{http_code}'` loop (dotnet-test.yml's shared-rules and
cross-repo-pair jobs, shared-rules.yml's sweep) that printed, for every 403, "the token is valid but
lacks <capability> here, OR IS RATE-LIMITED" and failed the gate. That sentence leaves the reader to
guess between a provisioning defect and an outage, and the run reads as a finding either way
(MeshWeaver#7448: on 2026-10-27 an installation rate limit reddened the PR gates of every open
pull request with text that read like a defect in each of them).

Every call goes through `scripts/read-current-pr-body.py`'s `api_get` — ONE classification: a
rate-limit 403/429, a 5xx or a transport failure is retried inside a SHARED budget (honouring
`retry-after` / `x-ratelimit-reset`), and what is still unanswerable is reported as

    NO VERDICT — GitHub API rate-limited until <reset>; re-run after it

with exit 3. A deliberate refusal (401, a 403 that is not a rate limit, 404) stays a finding, with
the sentence that names its fix, exit 1. A finding beats an outage: if any repo REFUSED, the exit is
1 even when another was merely unreachable, because that refusal needs a person either way.

    python3 .github/scripts/probe-fleet-read.py --capability pull-requests:read \
        --path 'repos/{repo}/pulls?per_page=1' --pair-only --exclude Systemorph/MeshWeaver
    python3 .github/scripts/probe-fleet-read.py --self-test

Token: FLEET_TOKEN. Budget: GITHUB_API_DEADLINE (epoch seconds) when set, else the reader's default.
"""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

_READER = Path(__file__).resolve().parents[2] / "scripts" / "read-current-pr-body.py"
OK, REFUSED, OUTAGE = 0, 1, 3


def _load_reader():
    spec = importlib.util.spec_from_file_location("read_current_pr_body", _READER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def register_repos(register: Path, pair_only: bool, exclude: set) -> list:
    data = json.loads(register.read_text(encoding="utf-8"))
    repos = list(data["repos"]) + (list(data.get("pair-only-repos", [])) if pair_only else [])
    return [r for r in repos if r not in exclude]


def refusal_sentence(status: int, capability: str, what: str) -> str:
    return {
        401: "the App token is invalid or expired.",
        403: f"the token is valid but lacks {capability} here (GitHub answered a plain refusal, not a rate limit).",
        404: f"{what}the App is not installed on this repo (GitHub reports an invisible private repo as 404, not 403).",
    }.get(status, f"HTTP {status}.")


def probe(repos, path_template, capability, what, token, reader, budget_seconds, urlopen=None,
          sleep=time.sleep, clock=time.time, out=print):
    """Returns the exit code: 0 every repo readable, 1 any refusal, 3 only outages."""
    deadline = clock() + budget_seconds
    refused, outages = [], []
    for repo in repos:
        path = path_template.format(repo=repo)
        try:
            reader.api_get(path, token=token, budget_seconds=max(0, deadline - clock()),
                           urlopen=urlopen, sleep=sleep, clock=clock)
            out(f"  ok   {repo}")
        except reader.ApiUnavailable as e:
            outages.append(repo)
            out(f"::error title=NO VERDICT — GitHub API::{repo} -> {e.sentence()}")
        except reader.ApiRefused as e:
            refused.append(repo)
            try:
                status = int(str(e).split()[1])
            except (IndexError, ValueError):
                status = 0
            out(f"::error::{repo} -> {status}: {refusal_sentence(status, capability, what)}")
    if len(repos) < 2:
        out(f"::error::The probe examined only {len(repos)} repo(s) — the register did not expand. Refusing to report success.")
        return REFUSED
    if refused:
        out(f"::error::The token cannot read {len(refused)} of {len(repos)} repo(s) ({', '.join(refused)}), so "
            f"the gate cannot report on them. Refusing to pass on partial evidence.")
        return REFUSED
    if outages:
        out(f"::error title=NO VERDICT — GitHub API::{len(outages)} of {len(repos)} repo(s) could not be asked "
            f"({', '.join(outages)}). Nothing was decided: this is an API outage, not a finding about this "
            f"change and not a missing grant — re-run after the reset named above.")
        return OUTAGE
    out(f"The token reads {capability} on all {len(repos)} repo(s).")
    return OK


def self_test() -> int:
    import email.message
    import io
    import urllib.error

    reader = _load_reader()
    fails = []

    def check(name, cond, detail=""):
        print(f"  {'ok  ' if cond else 'FAIL'} {name}")
        if not cond:
            fails.append(f"{name}: {detail}")

    class _Ok:
        def __init__(self, text="{}"):
            self.text = text

        def read(self):
            return self.text.encode()

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

    def err(code, body, **hdrs):
        h = email.message.Message()
        for k, v in hdrs.items():
            h[k.replace("_", "-")] = str(v)
        return urllib.error.HTTPError("https://api.github.com/x", code, "x", h, io.BytesIO(body.encode()))

    rate = '{"message":"API rate limit exceeded for installation ID 12345."}'

    def run(answers_by_repo, budget=60):
        lines, slept, now = [], [], [1_000_000.0]

        def urlopen(req, timeout=None):
            repo = "/".join(req.full_url.split("/repos/")[1].split("/")[:2])
            a = answers_by_repo[repo].pop(0)
            if isinstance(a, BaseException):
                raise a
            return a

        def sleep(s):
            slept.append(s)
            now[0] += s

        code = probe(list(answers_by_repo), "repos/{repo}/pulls?per_page=1", "pull-requests:read", "", "t",
                     reader, budget, urlopen=urlopen, sleep=sleep, clock=lambda: now[0], out=lines.append)
        return code, "\n".join(lines), slept

    code, said, _ = run({"o/a": [_Ok()], "o/b": [_Ok()]})
    check("every repo readable -> 0", code == OK, said)

    code, said, slept = run({"o/a": [_Ok()], "o/b": [err(403, rate, x_ratelimit_remaining=0, x_ratelimit_reset=1_009_999)]})
    check("an installation rate limit past the budget -> 3, NO VERDICT naming the reset, nothing slept",
          code == OUTAGE and "NO VERDICT" in said and "rate-limited until" in said and slept == [], said)
    check("…and it never says the token lacks a capability", "lacks" not in said, said)

    code, said, slept = run({"o/a": [err(403, rate, retry_after=5), _Ok()], "o/b": [_Ok()]})
    check("a rate limit inside the budget is waited out -> 0", code == OK and slept == [5], said)

    # NEGATIVE CONTROL: a real permission 403 is still a finding, exit 1, with its fix.
    code, said, slept = run({"o/a": [err(403, '{"message":"Resource not accessible by integration"}')], "o/b": [_Ok()]})
    check("a permission 403 -> 1, naming the missing capability", code == REFUSED and "lacks pull-requests:read" in said
          and slept == [] and "NO VERDICT" not in said, said)

    code, said, _ = run({"o/a": [err(404, '{"message":"Not Found"}')], "o/b": [err(502, "x")] * 20})
    check("a refusal beats an outage -> 1", code == REFUSED and "not installed" in said, said)

    code, said, _ = run({"o/a": [_Ok()]})
    check("one repo is a register that did not expand -> 1", code == REFUSED, said)

    if fails:
        print("::error title=probe-fleet-read self-test::" + "; ".join(fails), file=sys.stderr)
        return 1
    print("✓ probe-fleet-read self-test: ok / outage-is-no-verdict / waited-out / permission-is-a-finding / refusal-beats-outage / unexpanded-register")
    return 0


def main(argv) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--register", default=".github/shared-rules.json")
    ap.add_argument("--pair-only", action="store_true", help="also probe the register's pair-only-repos")
    ap.add_argument("--exclude", action="append", default=[])
    ap.add_argument("--path", help="REST path with {repo}, e.g. 'repos/{repo}/pulls?per_page=1'")
    ap.add_argument("--capability", help="the grant a 403 would be missing, e.g. contents:read")
    ap.add_argument("--what", default="", help="what else a 404 can mean, e.g. 'no AGENTS.md, or '")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    if not a.path or "{repo}" not in a.path or not a.capability:
        ap.error("--path (with {repo}) and --capability are required")
    token = os.environ.get("FLEET_TOKEN", "")
    if not token:
        print("::error::FLEET_TOKEN is empty — the mint step above did not hand a token over.")
        return REFUSED
    try:
        repos = register_repos(Path(a.register), a.pair_only, set(a.exclude))
    except (OSError, ValueError, KeyError) as e:
        print(f"::error::Could not read the repo register from {a.register}: {e}")
        return REFUSED
    reader = _load_reader()
    deadline = os.environ.get("GITHUB_API_DEADLINE", "")
    budget = (max(0, int(deadline) - int(time.time())) if deadline.isdigit() else reader.DEFAULT_BUDGET_SECONDS)
    return probe(repos, a.path, a.capability, a.what, token, reader, budget)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
