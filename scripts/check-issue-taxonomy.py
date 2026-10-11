#!/usr/bin/env python3
"""check-issue-taxonomy — the issue taxonomy's invariant, measured over REST (Doc/Architecture/IssueTaxonomy).

Every OPEN issue carries exactly one TYPE (bug, enhancement, documentation, chore); every `bug` carries
exactly one SEVERITY (sev:B, sev:H, sev:M, sev:L); nothing else carries a severity, and no `sev:` label
outside those four exists on an issue. The release gate counts open `sev:B`/`sev:H` bugs, so an issue
that breaks the invariant is INVISIBLE to it: an untyped issue and a bug without a severity both read as
"nothing blocking".

Two subcommands, one rule:

  check   --repo OWNER/NAME [--repo …] [--older-than-hours H]
          Reads every open issue of every repository (the issues endpoint, every page — never search,
          whose zero cannot be told from a truncated answer) and verifies, on the SINGLE-label endpoint,
          that each repository HAS the four severity labels (a label that does not exist folds every
          query for it to an empty, green answer). Fails RED naming each violation older than H hours
          (default 24 — the issue sweep classifies hourly, so a younger one is triage's to finish) and
          each missing severity label. Exits 2 — never 0 — when anything could not be read.

  ensure  --repo OWNER/NAME --label L [--label …]
          Makes sure each taxonomy label exists in the repository, creating a missing one with the
          canonical colour and description below, then reads it back. A label that is not a taxonomy
          label is refused: a sentinel workflow stamps a type and a severity, never invents a label.
          Prints the labels, comma-separated, for `gh issue create --label`.

  stamp   --repo OWNER/NAME --number N --label L [--label …]
          `ensure`, then gives issue N those labels — but ONLY when it carries no type and no severity
          yet. A standing sentinel a workflow REOPENS keeps whatever a person decided since; an issue a
          person has typed differently is never overwritten (a contradiction is the `check`'s to name).

  --self-test   the rule and the paging on fixtures, no network.

Every call goes through `gh api`, so the token is whatever GH_TOKEN the caller provides.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from typing import Callable, Optional
from urllib.parse import quote

TYPES = ("bug", "enhancement", "documentation", "chore")
SEVERITIES = ("sev:B", "sev:H", "sev:M", "sev:L")

# The canonical label set — the colours and descriptions core's own labels carry (read 2026-10-11 off
# Systemorph/MeshWeaver). A missing label is created with exactly these; an existing one is never edited.
CANONICAL = {
    "bug": ("d73a4a", "Something isn't working"),
    "enhancement": ("a2eeef", "New feature or request"),
    "documentation": ("0075ca", "Improvements or additions to documentation"),
    "chore": ("C5DEF5", "CI, build, dependencies, cleanup - no user-visible behaviour change"),
    "sev:B": ("B60205", "BLOCKING - the release cannot ship while this is open"),
    "sev:H": ("D93F0B", "BLOCKING: primary path broken, intermittent user-visible failure, or silently wrong results."),
    "sev:M": ("FBCA04", "Medium - secondary path broken, or easy workaround"),
    "sev:L": ("0E8A16", "Low - cosmetic, rare edge case, or developer-only"),
}

PER_PAGE = 100
MAX_PAGES = 50

# api(path) -> (http_status, parsed_json_or_None); injected so the self-test runs with no network.
Api = Callable[[str, str, Optional[dict]], "tuple[int, object]"]


class Unreadable(Exception):
    """Something could not be read. Never folded into "no violation"."""


def gh_api(method: str, path: str, fields: Optional[dict] = None) -> "tuple[int, object]":
    cmd = ["gh", "api", "-X", method, "-i", path]
    for k, v in (fields or {}).items():
        for item in (v if isinstance(v, list) else [v]):
            cmd += ["-f", f"{k}={item}"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    head, _, body = r.stdout.replace("\r\n", "\n").partition("\n\n")
    status_line = head.split("\n", 1)[0] if head else ""
    try:
        status = int(status_line.split()[1])
    except (IndexError, ValueError):
        raise Unreadable(f"gh api {method} {path}: no HTTP status (rc={r.returncode}): {r.stderr.strip()[:200]}")
    try:
        return status, (json.loads(body) if body.strip() else None)
    except json.JSONDecodeError:
        raise Unreadable(f"gh api {method} {path}: HTTP {status}, the body is not JSON")


def violation(labels: list) -> Optional[str]:
    """What breaks the taxonomy in one issue's labels, in words — or None when it is classified."""
    names = [str(l).strip() for l in labels if str(l).strip()]
    types = sorted({n.lower() for n in names if n.lower() in TYPES})
    sevs = sorted({n for n in names if n in SEVERITIES})
    unknown = sorted({n for n in names if n.lower().startswith("sev:") and n not in SEVERITIES})
    if unknown:
        return "a severity outside sev:B/H/M/L (" + ", ".join(unknown) + ")"
    if not types:
        return "no type label"
    if len(types) > 1:
        return "more than one type (" + ", ".join(types) + ")"
    if types[0] == "bug" and not sevs:
        return "a bug without a severity"
    if len(sevs) > 1:
        return "more than one severity (" + ", ".join(sevs) + ")"
    if types[0] != "bug" and sevs:
        return f"a severity on a {types[0]}"
    return None


def open_issues(api: Api, repo: str) -> list:
    """Every OPEN issue (pull requests excluded), every page; refused rather than truncated."""
    out = []
    for page in range(1, MAX_PAGES + 1):
        status, items = api("GET", f"repos/{repo}/issues?state=open&per_page={PER_PAGE}&page={page}", None)
        if status != 200 or not isinstance(items, list):
            raise Unreadable(f"{repo}: the open issues (page {page}) answered HTTP {status}")
        out += [i for i in items if "pull_request" not in i]
        if len(items) < PER_PAGE:
            return out
    raise Unreadable(f"{repo}: more than {MAX_PAGES * PER_PAGE} open issues — refused, not truncated")


def label_exists(api: Api, repo: str, label: str) -> bool:
    status, _ = api("GET", f"repos/{repo}/labels/{quote(label, safe='')}", None)
    if status == 200:
        return True
    if status == 404:
        return False
    raise Unreadable(f"{repo}: the label {label!r} answered HTTP {status}")


def check(api: Api, repos: list, older_than: timedelta, now: datetime) -> "tuple[list, list, list]":
    """(violations older than the bound, younger ones, missing severity labels) over every repository."""
    old, young, missing = [], [], []
    for repo in repos:
        for sev in SEVERITIES:
            if not label_exists(api, repo, sev):
                missing.append(f"{repo}: the label {sev} does not exist — a query for it answers an empty, green list")
        for issue in open_issues(api, repo):
            why = violation([l.get("name", "") for l in issue.get("labels", [])])
            if why is None:
                continue
            created = datetime.fromisoformat(str(issue["created_at"]).replace("Z", "+00:00"))
            row = (repo, int(issue["number"]), why, created)
            (old if now - created >= older_than else young).append(row)
    return old, young, missing


def ensure(api: Api, repo: str, labels: list) -> list:
    """Each taxonomy label exists in the repository afterwards (created with the canonical look when absent)."""
    out = []
    for label in labels:
        if label not in CANONICAL:
            raise Unreadable(f"{label!r} is not a taxonomy label ({', '.join(CANONICAL)}) — a sentinel never invents one")
        if not label_exists(api, repo, label):
            color, description = CANONICAL[label]
            status, _ = api("POST", f"repos/{repo}/labels", {"name": label, "color": color, "description": description})
            if status not in (201, 422):  # 422: a concurrent run created it a moment ago
                raise Unreadable(f"{repo}: creating the label {label!r} answered HTTP {status}")
            if not label_exists(api, repo, label):
                raise Unreadable(f"{repo}: the label {label!r} still does not exist after it was created")
        out.append(label)
    if sum(1 for l in out if l in TYPES) != 1 or violation(out) is not None:
        raise Unreadable(f"the labels {out} are not a complete classification ({violation(out) or 'exactly one type'})")
    return out


def stamp(api: Api, repo: str, number: int, labels: list) -> str:
    """Give issue `number` the classification `labels` when it carries neither a type nor a severity yet."""
    wanted = ensure(api, repo, labels)
    status, issue = api("GET", f"repos/{repo}/issues/{number}", None)
    if status != 200 or not isinstance(issue, dict):
        raise Unreadable(f"{repo}#{number} answered HTTP {status}")
    current = [l.get("name", "") for l in issue.get("labels", [])]
    if any(c.lower() in TYPES or c.lower().startswith("sev:") for c in current):
        return f"{repo}#{number} already carries {', '.join(c for c in current if c.lower() in TYPES or c.lower().startswith('sev:'))} — kept"
    status, _ = api("POST", f"repos/{repo}/issues/{number}/labels", {"labels[]": wanted})
    if status != 200:
        raise Unreadable(f"{repo}#{number}: adding {wanted} answered HTTP {status}")
    return f"{repo}#{number} labelled {', '.join(wanted)}"


# ── self-test ────────────────────────────────────────────────────────────────────────────────────

def self_test() -> int:
    failures = []

    def expect(name, got, want):
        if got != want:
            failures.append(f"{name}: got {got!r}, want {want!r}")

    expect("classified bug", violation(["bug", "sev:H", "area:ci"]), None)
    expect("classified chore", violation(["chore"]), None)
    expect("untyped", violation(["feature:x"]), "no type label")
    expect("bug without severity", violation(["bug"]), "a bug without a severity")
    expect("two types", violation(["bug", "chore", "sev:M"]), "more than one type (bug, chore)")
    expect("two severities", violation(["bug", "sev:H", "sev:L"]), "more than one severity (sev:H, sev:L)")
    expect("severity on a non-bug", violation(["enhancement", "sev:M"]), "a severity on a enhancement")
    expect("unknown severity", violation(["bug", "sev:critical"]), "a severity outside sev:B/H/M/L (sev:critical)")
    expect("type case-insensitive", violation(["Bug", "sev:M"]), None)

    now = datetime(2026, 10, 11, 12, tzinfo=timezone.utc)

    class Fake:
        def __init__(self, issues, labels, broken=False):
            self.issues, self.labels, self.broken, self.created = issues, set(labels), broken, []

        def __call__(self, method, path, fields):
            if self.broken:
                return 502, None
            if method == "POST" and "/issues/" in path and path.endswith("/labels"):
                n = int(path.split("/issues/")[1].split("/")[0])
                hit = next(i for i in self.issues if i["number"] == n)
                hit["labels"] += [{"name": l} for l in fields["labels[]"]]
                return 200, []
            if method == "POST" and path.endswith("/labels"):
                self.labels.add(fields["name"])
                self.created.append(fields)
                return 201, {}
            if "/issues/" in path:
                n = int(path.rsplit("/", 1)[1])
                return 200, next(i for i in self.issues if i["number"] == n)
            if "/labels/" in path:
                from urllib.parse import unquote
                return (200, {}) if unquote(path.rsplit("/", 1)[1]) in self.labels else (404, None)
            page = int(path.split("&page=")[1])
            chunk = self.issues[(page - 1) * PER_PAGE: page * PER_PAGE]
            return 200, chunk

    def issue(n, labels, hours_ago, pr=False):
        i = {"number": n, "labels": [{"name": l} for l in labels],
             "created_at": (now - timedelta(hours=hours_ago)).isoformat().replace("+00:00", "Z")}
        if pr:
            i["pull_request"] = {}
        return i

    # 150 classified issues push the violations onto the SECOND page — a one-page read would miss them.
    many = [issue(n, ["chore"], 100) for n in range(1, 151)]
    repo = Fake(many + [issue(200, ["bug"], 48), issue(201, [], 2), issue(202, ["bug"], 48, pr=True)], SEVERITIES)
    old, young, missing = check(repo, ["o/r"], timedelta(hours=24), now)
    expect("old violation found past page one", [(r[1], r[2]) for r in old], [(200, "a bug without a severity")])
    expect("young violation reported apart", [r[1] for r in young], [201])
    expect("a pull request is never an issue", any(r[1] == 202 for r in old + young), False)
    expect("labels present", missing, [])

    no_sev = Fake([issue(1, ["bug", "sev:H"], 48)], ["sev:H", "sev:M", "sev:L"])
    _, _, missing = check(no_sev, ["o/r"], timedelta(hours=24), now)
    expect("a missing severity label is named", len(missing) == 1 and "sev:B" in missing[0], True)

    try:
        check(Fake([], SEVERITIES, broken=True), ["o/r"], timedelta(hours=24), now)
        failures.append("an unreadable repository must raise, never read as clean")
    except Unreadable:
        pass

    target = Fake([], ["bug"])
    expect("ensure creates what is missing", ensure(target, "o/r", ["bug", "sev:M"]), ["bug", "sev:M"])
    expect("…with the canonical look", [(c["name"], c["color"]) for c in target.created], [("sev:M", "FBCA04")])
    for bad, why in ((["bug"], "a bug without its severity"), (["bug", "flaky"], "an invented label"),
                     (["chore", "sev:M"], "a severity on a chore"), (["sev:H"], "no type")):
        try:
            ensure(Fake([], CANONICAL), "o/r", bad)
            failures.append(f"ensure must refuse {why}")
        except Unreadable:
            pass

    sentinel = Fake([issue(7, ["ci-failure"], 1), issue(8, ["ci-failure", "bug", "sev:H"], 1), issue(9, ["chore"], 1)], CANONICAL)
    stamp(sentinel, "o/r", 7, ["bug", "sev:M"])
    expect("an unclassified sentinel is stamped", [l["name"] for l in sentinel.issues[0]["labels"]], ["ci-failure", "bug", "sev:M"])
    stamp(sentinel, "o/r", 8, ["bug", "sev:M"])
    expect("a person's severity is kept, never a second one", [l["name"] for l in sentinel.issues[1]["labels"]], ["ci-failure", "bug", "sev:H"])
    stamp(sentinel, "o/r", 9, ["bug", "sev:M"])
    expect("a person's type is kept", [l["name"] for l in sentinel.issues[2]["labels"]], ["chore"])

    for f in failures:
        print("FAIL", f)
    print("self-test:", "FAILED" if failures else "ok")
    return 1 if failures else 0


def main(argv: list) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check")
    c.add_argument("--repo", action="append", required=True)
    c.add_argument("--older-than-hours", type=float, default=24.0)
    e = sub.add_parser("ensure")
    e.add_argument("--repo", required=True)
    e.add_argument("--label", action="append", required=True)
    s = sub.add_parser("stamp")
    s.add_argument("--repo", required=True)
    s.add_argument("--number", type=int, required=True)
    s.add_argument("--label", action="append", required=True)
    a = p.parse_args(argv[1:])
    try:
        if a.cmd == "ensure":
            print(",".join(ensure(gh_api, a.repo, a.label)))
            return 0
        if a.cmd == "stamp":
            print(stamp(gh_api, a.repo, a.number, a.label))
            return 0
        old, young, missing = check(gh_api, a.repo, timedelta(hours=a.older_than_hours), datetime.now(timezone.utc))
    except Unreadable as ex:
        print(f"::error::NOT CHECKED — {ex}")
        return 2
    for repo, n, why, created in young:
        print(f"::notice::{repo}#{n}: {why} (opened {created:%Y-%m-%d %H:%MZ} — younger than {a.older_than_hours:g} h, triage's to finish)")
    for line in missing:
        print(f"::error::{line}")
    for repo, n, why, created in old:
        print(f"::error::{repo}#{n}: {why} — open since {created:%Y-%m-%d %H:%MZ}")
    print(f"{len(a.repo)} repositories read: {len(old)} violation(s) older than {a.older_than_hours:g} h, "
          f"{len(young)} younger, {len(missing)} missing severity label(s)")
    return 1 if old or missing else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
