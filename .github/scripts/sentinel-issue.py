#!/usr/bin/env python3
"""sentinel-issue — find a workflow's ONE standing tracking issue by a dedicated LABEL.

A scheduled workflow that keeps one issue updated in place (chart-drift.yml) has to find that
issue again on every run. It used to scan the 200 most recently created issues for a marker in the
body — a WINDOW, not a key. The sentinel is long-lived by design (it is reopened, never recreated),
so every new issue in the repository pushed it one place further back; once more than 200 newer
issues existed it aged out, the scan found nothing, and the workflow opened a NEW sentinel. That is
how #5049, #5491 and #5844 came to exist (consolidated into #5844). This repository opens far more
than 200 issues between drift episodes, so the window was guaranteed to miss.

The key is now a dedicated label that the REST issues endpoint filters on SERVER-side
(`GET /repos/{repo}/issues?labels=<label>&state=all`), read to the last page. The answer does not
depend on how many other issues exist or how old the sentinel is. (Not the search API: its index
lags a freshly created issue by minutes, so a search lookup misses its own issue on the next run.
The label filter is not instantaneous either — measured on Systemorph/MeshWeaver 2026-10-07, a
label applied to #5844 was absent from the filtered listing seconds later and present on the next
read. Serialising the runs does not close that by itself: a queued run can reach its lookup before
the previous run's NEW issue is visible, find nothing, and create a second one. So creation has a
postcondition — `await-visible` — and the creating run does not END until the new sentinel is
found by the very lookup the next run will use; it goes RED if that never happens. With the runs
serialised, the next lookup therefore starts after the issue is visible by construction.)

🚨 An UNKNOWN label folds to an empty listing — `labels=does-not-exist` answers `[]`, byte-identical
to "no sentinel yet" — and "no sentinel" leads straight to creating one. So the label's existence
is checked first on the SINGLE-label endpoint, which 404s when it is absent; with --ensure-label a
missing label is created, otherwise the lookup FAILS rather than answering "none".

Selection, when the label matches several issues:
  * any OPEN → the OLDEST open one (lowest number) is the sentinel; every other open one is
    reported as a duplicate. A duplicate is NEVER a reason to open another.
  * none open → the NEWEST closed one, so a returning divergence reopens the latest record.
  * pull requests carrying the label are ignored.

Usage:
  sentinel-issue.py find --repo OWNER/NAME --label LABEL [--ensure-label --color C --description D]
      prints {"number": N|null, "state": "open"|"closed"|null, "duplicates": [N, ...]}
  sentinel-issue.py await-visible --repo OWNER/NAME --label LABEL --number N [--within SECONDS]
      exits 0 once issue N is in the label listing, 1 (loud) if it is not within the bound
  sentinel-issue.py --self-test
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from typing import Callable, Optional
from urllib.parse import quote

PER_PAGE = 100

# api(method, path, fields) -> (http_status, parsed_json_or_None). Injected so the self-test runs
# with no network.
Api = Callable[[str, str, Optional[dict]], "tuple[int, object]"]


class LookupError_(Exception):
    """The lookup could not establish an answer. Never folded into "no sentinel"."""


def gh_api(method: str, path: str, fields: Optional[dict] = None) -> "tuple[int, object]":
    cmd = ["gh", "api", "-X", method, "-i", path]
    for k, v in (fields or {}).items():
        cmd += ["-f", f"{k}={v}"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    # `-i` puts the status line first; the body follows the blank line.
    head, _, body = r.stdout.replace("\r\n", "\n").partition("\n\n")
    status_line = head.split("\n", 1)[0] if head else ""
    try:
        status = int(status_line.split()[1])
    except (IndexError, ValueError):
        raise LookupError_(f"gh api {method} {path}: no HTTP status (rc={r.returncode}): {r.stderr.strip()}")
    try:
        parsed = json.loads(body) if body.strip() else None
    except json.JSONDecodeError:
        raise LookupError_(f"gh api {method} {path}: HTTP {status}, body is not JSON")
    return status, parsed


def ensure_label(api: Api, repo: str, label: str, create: bool, color: str, description: str) -> None:
    status, _ = api("GET", f"repos/{repo}/labels/{quote(label, safe='')}", None)
    if status == 200:
        return
    if status != 404:
        raise LookupError_(f"label '{label}': HTTP {status} — cannot tell whether it exists")
    if not create:
        raise LookupError_(
            f"label '{label}' does not exist on {repo} — a label filter on it would answer an empty "
            "list indistinguishable from 'no sentinel' (pass --ensure-label to create it)")
    status, body = api("POST", f"repos/{repo}/labels",
                       {"name": label, "color": color, "description": description})
    if status == 201:
        return
    # 422 already_exists = a concurrent run created it between our GET and POST — the label now
    # exists, which is the state we asked for. Anything else is a failure.
    errors = (body or {}).get("errors") if isinstance(body, dict) else None
    if status == 422 and any(isinstance(e, dict) and e.get("code") == "already_exists" for e in errors or []):
        return
    raise LookupError_(f"creating label '{label}': HTTP {status}: {body}")


def list_labelled(api: Api, repo: str, label: str) -> list:
    """Every issue (both states) carrying `label`, read to the LAST page."""
    out: list = []
    page = 1
    while True:
        status, body = api("GET", f"repos/{repo}/issues?labels={quote(label, safe='')}"
                                  f"&state=all&per_page={PER_PAGE}&page={page}", None)
        if status != 200 or not isinstance(body, list):
            raise LookupError_(f"listing issues labelled '{label}' (page {page}): HTTP {status}")
        out.extend(body)
        if len(body) < PER_PAGE:
            return out
        page += 1


def select(issues: list) -> dict:
    real = [i for i in issues if not i.get("pull_request")]
    opened = sorted((i for i in real if i.get("state") == "open"), key=lambda i: i["number"])
    if opened:
        return {"number": opened[0]["number"], "state": "open",
                "duplicates": [i["number"] for i in opened[1:]]}
    closed = sorted((i for i in real if i.get("state") == "closed"), key=lambda i: i["number"])
    if closed:
        return {"number": closed[-1]["number"], "state": "closed", "duplicates": []}
    return {"number": None, "state": None, "duplicates": []}


def find(api: Api, repo: str, label: str, create: bool = False, color: str = "ededed",
         description: str = "") -> dict:
    ensure_label(api, repo, label, create, color, description)
    return select(list_labelled(api, repo, label))


def await_visible(api: Api, repo: str, label: str, number: int, within: float = 300.0,
                  interval: float = 5.0, sleep: Callable[[float], None] = time.sleep,
                  clock: Callable[[], float] = time.monotonic) -> int:
    """Block until `number` is returned by the SAME listing `find` uses; return the attempt count.

    This is a read-your-write postcondition, not a retry: nothing is re-done, the run only refuses
    to finish before its own write is observable by the next run's key. Not visible within the
    bound ⇒ LookupError_, so the run reds instead of leaving a sentinel the next run cannot see.
    """
    deadline = clock() + within
    attempts = 0
    while True:
        attempts += 1
        if any(i.get("number") == number for i in list_labelled(api, repo, label)):
            return attempts
        if clock() >= deadline:
            raise LookupError_(
                f"issue #{number} is not in the '{label}' listing {within:.0f}s after it was created — "
                "the next run would not find it and would open another")
        sleep(interval)


# ── self-test ───────────────────────────────────────────────────────────────────────────────────

class FakeRepo:
    """A repository with N issues, some carrying the label. Answers the REST shapes `find` uses."""

    def __init__(self, issues: list, labels: set, hidden_reads: int = 0):
        self.issues = issues          # each: {"number", "state", "labels": [..], "pull_request"?}
        self.labels = set(labels)
        self.posts: list = []
        # Delayed visibility (measured on the real endpoint): an issue created through `create`
        # stays out of the label listing for this many listing reads.
        self.hidden_reads = hidden_reads
        self.hidden: dict = {}

    def create(self, number: int) -> None:
        self.issues.append({"number": number, "state": "open", "labels": [self.labels and next(iter(self.labels))]})
        self.hidden[number] = self.hidden_reads

    def __call__(self, method, path, fields):
        if method == "GET" and "/labels/" in path:
            return (200, {}) if path.rsplit("/", 1)[1] in self.labels else (404, {"message": "Not Found"})
        if method == "POST" and path.endswith("/labels"):
            self.posts.append(fields)
            self.labels.add(fields["name"])
            return 201, {}
        if method == "GET" and "/issues?" in path:
            q = dict(p.split("=", 1) for p in path.split("?", 1)[1].split("&"))
            if q["page"] == "1":
                for n in list(self.hidden):
                    self.hidden[n] -= 1
            hit = [i for i in sorted(self.issues, key=lambda i: -i["number"])  # newest first, as REST
                   if q["labels"] in i["labels"] and (q["state"] == "all" or i["state"] == q["state"])
                   and self.hidden.get(i["number"], -1) < 0]
            pg, n = int(q["page"]), int(q["per_page"])
            return 200, hit[(pg - 1) * n: pg * n]
        return 500, None


def _window_scan(repo: FakeRepo, marker_label: str, window: int = 200):
    """THE OLD LOOKUP, kept as the negative control: the `window` newest issues, filtered locally."""
    recent = sorted(repo.issues, key=lambda i: -i["number"])[:window]
    hit = [i for i in recent if marker_label in i["labels"]]
    return hit[0]["number"] if hit else None


def _first_page_only(api: Api, repo: str, label: str) -> dict:
    """A pager that forgets to paginate — the control for the pagination case."""
    _, body = api("GET", f"repos/{repo}/issues?labels={label}&state=all&per_page={PER_PAGE}&page=1", None)
    return select(body)


def self_test() -> int:
    L, R = "chart-drift-sentinel", "o/r"
    failures: list = []

    def check(name, got, want):
        if got != want:
            failures.append(f"{name}: got {got!r}, want {want!r}")

    def issue(n, state="open", labelled=True, pr=False):
        d = {"number": n, "state": state, "labels": [L] if labelled else []}
        if pr:
            d["pull_request"] = {"url": "x"}
        return d

    # 1. THE INCIDENT: the sentinel (#100, open) is older than the 500 issues opened since.
    aged = FakeRepo([issue(100)] + [issue(n, labelled=False) for n in range(101, 601)], {L})
    check("aged-out sentinel is found", find(aged, R, L)["number"], 100)
    # NEGATIVE CONTROL: the old 200-window scan, on the same fixture, misses it — so case 1 really
    # exercises the aging-out that produced #5049/#5491/#5844, and would fail against the old code.
    check("CONTROL: the window scan misses the aged-out sentinel", _window_scan(aged, L), None)

    # 2. Pagination: more labelled issues than one page, the open sentinel the OLDEST (last page).
    many = FakeRepo([issue(1)] + [issue(n, "closed") for n in range(2, 232)], {L})
    check("sentinel on the last page is found", find(many, R, L), {"number": 1, "state": "open", "duplicates": []})
    check("CONTROL: a first-page-only pager misses it", _first_page_only(many, R, L)["state"], "closed")

    # 3. Several open: keep the oldest, report the rest — never "none".
    dup = FakeRepo([issue(5049), issue(5491), issue(5844), issue(10, "closed")], {L})
    check("several open → oldest + duplicates", find(dup, R, L),
          {"number": 5049, "state": "open", "duplicates": [5491, 5844]})

    # 4. Only closed: reopen the newest record.
    check("only closed → newest closed",
          find(FakeRepo([issue(7, "closed"), issue(9, "closed")], {L}), R, L),
          {"number": 9, "state": "closed", "duplicates": []})

    # 5. A labelled PULL REQUEST is not a sentinel.
    check("labelled PR ignored", find(FakeRepo([issue(3, pr=True)], {L}), R, L)["number"], None)

    # 6. Genuinely none → null (the only case that may create).
    check("none → null", find(FakeRepo([issue(1, labelled=False)], {L}), R, L),
          {"number": None, "state": None, "duplicates": []})

    # 7. 🚨 Unknown label must NOT fold to "none" (which would create a duplicate).
    try:
        find(FakeRepo([], set()), R, L)
        failures.append("unknown label without --ensure-label: answered instead of failing")
    except LookupError_:
        pass
    created = FakeRepo([], set())
    check("--ensure-label creates the label", find(created, R, L, create=True)["number"], None)
    check("--ensure-label POSTed once", [p["name"] for p in created.posts], [L])

    # 9. DELAYED VISIBILITY (Copilot on #6272). Run A creates a sentinel that the listing hides for
    #    3 reads; run B (serialised behind A) then looks it up.
    #    CONTROL: without the postcondition, B finds nothing — the duplicate the reviewer described.
    lag = FakeRepo([], {L}, hidden_reads=3)
    lag.create(42)
    check("CONTROL: an immediate lookup after a lagging create finds nothing", find(lag, R, L)["number"], None)
    #    With it, A does not finish until the listing shows #42, so B finds it.
    lag = FakeRepo([], {L}, hidden_reads=3)
    lag.create(42)
    slept: list = []
    check("await-visible waits out the lag", await_visible(lag, R, L, 42, sleep=slept.append, clock=lambda: 0.0), 4)
    check("await-visible slept between reads, not before the first", len(slept), 3)
    check("after await-visible the next run finds the sentinel", find(lag, R, L)["number"], 42)
    #    And a write that never becomes visible reds rather than finishing quietly.
    never = FakeRepo([], {L}, hidden_reads=10**6)
    never.create(7)
    t = [0.0]
    def tick(dt):
        t[0] += dt
    try:
        await_visible(never, R, L, 7, within=30, interval=5, sleep=tick, clock=lambda: t[0])
        failures.append("await-visible on a never-visible issue: returned instead of failing")
    except LookupError_:
        pass

    # 8. An API failure on the listing is a failure, never "none".
    def broken(method, path, fields):
        return (200, {}) if "/labels/" in path else (502, None)
    try:
        find(broken, R, L)
        failures.append("listing HTTP 502: answered instead of failing")
    except LookupError_:
        pass

    if failures:
        print("::error::sentinel-issue self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print("✓ sentinel-issue self-test: aged-out, paginated, duplicate, closed, PR, none, unknown-label "
          "API-failure and delayed-visibility cases all decided correctly; all three negative controls "
          "miss as the unguarded code did")
    return 0


def main(argv: list) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true", help="prove the lookup is not vacuous")
    sub = ap.add_subparsers(dest="cmd")
    f = sub.add_parser("find")
    f.add_argument("--repo", required=True)
    f.add_argument("--label", required=True)
    f.add_argument("--ensure-label", action="store_true")
    f.add_argument("--color", default="ededed")
    f.add_argument("--description", default="")
    w = sub.add_parser("await-visible")
    w.add_argument("--repo", required=True)
    w.add_argument("--label", required=True)
    w.add_argument("--number", required=True, type=int)
    w.add_argument("--within", type=float, default=300.0)
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    if a.cmd not in ("find", "await-visible"):
        ap.error("a command is required: find, await-visible, or --self-test")
    try:
        if a.cmd == "await-visible":
            n = await_visible(gh_api, a.repo, a.label, a.number, within=a.within)
            print(f"#{a.number} is in the '{a.label}' listing (read {n})")
            return 0
        print(json.dumps(find(gh_api, a.repo, a.label, a.ensure_label, a.color, a.description)))
    except LookupError_ as exc:
        print(f"::error::sentinel lookup failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
