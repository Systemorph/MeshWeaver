#!/usr/bin/env python3
"""Is the pinned-digest protection CURRENT enough to switch a purge on? (MeshWeaver#3438)

    acr-retention-protection.py --check "<tasks being enabled>"
    acr-retention-protection.py --self-test

Reads the newest COMPLETED run of the lock lane on core's `main` and exits 0 only when it concluded
`success` and is younger than the limit. Every other answer — no run, a red or cancelled one, an old
one, an answer that is not JSON or carries no date, a request that failed — exits non-zero with a
sentence naming why.

🚨 FAILS CLOSED, by construction. "Could not tell" must never read as "nothing against it": that is
the swallow that turns a failed protection collection into a deletion (#3859). Called by
`acr-retention-tasks.sh apply` / `preflight` only when the record asks to ENABLE a task.

🚨 THE EVIDENCE IS POLICY, NOT INPUT (review of #6379). The repository, the workflow and the maximum
age are the constants below, and this script fetches the answer itself. No environment variable,
argument or file can substitute any of them: an override would let whoever runs the apply choose the
evidence that authorizes a purge. The self-test reaches `verdict()` in-process with its own values —
that is the only seam — and proves that setting the old override variables changes nothing.
"""
from __future__ import annotations

import datetime
import json
import os
import sys
import urllib.request
from pathlib import Path

# ── the policy (core policy `registry-retention`) ──────────────────────────────────────────────────
LOCK_LANE_REPO = "Systemorph/MeshWeaver"
LOCK_LANE_WORKFLOW = "lock-pinned-digests.yml"
MAX_AGE_HOURS = 36.0
EVIDENCE_URL = (f"https://api.github.com/repos/{LOCK_LANE_REPO}/actions/workflows/{LOCK_LANE_WORKFLOW}"
                "/runs?branch=main&status=completed&per_page=1")

# The variable names an earlier draft honoured. Listed so the self-test can set them and prove they are
# inert — never read anywhere else.
RETIRED_OVERRIDES = ("MW_LOCK_LANE_REPO", "MW_LOCK_LANE_WORKFLOW", "MW_LOCK_LANE_MAX_AGE_HOURS",
                     "MW_LOCK_LANE_RUNS_FILE")


def verdict(answer: str, enabling: str, max_age_hours: float,
            now: datetime.datetime) -> tuple[bool, str]:
    """(may enable, the sentence saying why). Pure — the self-test's seam."""
    refuse = f"REFUSING to enable {enabling}: "
    try:
        doc = json.loads(answer)
    except ValueError:
        return False, refuse + "the lock lane's answer is not JSON."
    runs = doc.get("workflow_runs") if isinstance(doc, dict) else None
    if not isinstance(runs, list):
        return False, refuse + "the lock lane's answer carries no `workflow_runs` list."
    if not runs:
        return False, refuse + "the lock lane has NO completed run on main. Zero runs is not a green one."
    run = runs[0]
    if not isinstance(run, dict):
        return False, refuse + "the newest lock-lane run is not an object."
    try:
        created = datetime.datetime.fromisoformat(str(run["created_at"]).replace("Z", "+00:00"))
    except (KeyError, ValueError):
        return False, refuse + "the newest lock-lane run carries no readable date."
    if created.tzinfo is None:
        return False, refuse + "the newest lock-lane run's date has no time zone."
    url = run.get("html_url", "?")
    conclusion = run.get("conclusion")
    if conclusion != "success":
        return False, (refuse + f"the newest lock-lane run on main concluded {conclusion!r} ({url}). "
                       "A purge switched on behind a red protection run deletes what the lock was "
                       "meant to keep. Make the lock lane green first.")
    age = (now - created).total_seconds() / 3600
    if age > max_age_hours:
        return False, (refuse + f"the newest lock-lane run on main is {age:.0f} h old (limit "
                       f"{max_age_hours:.0f} h, {url}). A stale green says nothing about tonight.")
    if age < -1:
        return False, refuse + f"the newest lock-lane run is dated {-age:.0f} h in the FUTURE ({url})."
    return True, f"protection is current: the lock lane was green {age:.1f} h ago ({url})."


def fetch_evidence() -> str:
    """The lock lane's newest completed run on main, from the FIXED url. Raises on any failure."""
    request = urllib.request.Request(EVIDENCE_URL, headers={"Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:      # noqa: S310 — fixed https URL
        return response.read().decode("utf-8", "replace")


def check(enabling: str, fetch=fetch_evidence,
          now: datetime.datetime | None = None) -> tuple[bool, str]:
    """The production decision: fixed evidence, fixed limit. `fetch`/`now` exist for the self-test."""
    try:
        answer = fetch()
    except Exception as exc:                                            # noqa: BLE001 — fail closed
        return False, (f"REFUSING to enable {enabling}: the lock lane ({LOCK_LANE_REPO} "
                       f"{LOCK_LANE_WORKFLOW}) could not be read ({exc}), so whether the pinned set "
                       "is protected tonight is unknown.")
    return verdict(answer, enabling, MAX_AGE_HOURS, now or datetime.datetime.now(datetime.timezone.utc))


def self_test() -> int:
    now = datetime.datetime(2026, 10, 9, 12, 0, tzinfo=datetime.timezone.utc)

    def runs(conclusion: str, created: str) -> str:
        return json.dumps({"workflow_runs": [{"conclusion": conclusion, "created_at": created,
                                              "html_url": "https://example/run/1"}]})

    cases = [
        # (answer, expected may-enable, what the case is)
        (runs("success", "2026-10-09T01:26:46Z"), True, "a green run 10 h old POSITIVE CONTROL"),
        (runs("failure", "2026-10-09T01:26:46Z"), False, "the measured 2026-10-09 state: red"),
        (runs("cancelled", "2026-10-09T01:26:46Z"), False, "a cancelled run is not a green one"),
        (runs("success", "2026-10-07T01:26:46Z"), False, "a green run 58 h old is stale"),
        (runs("success", "2026-10-11T01:26:46Z"), False, "a run dated in the future"),
        (runs("success", "not-a-date"), False, "an unreadable date"),
        (json.dumps({"workflow_runs": []}), False, "zero runs"),
        (json.dumps({"message": "API rate limit exceeded"}), False, "an error body"),
        ("<html>", False, "not JSON"),
        (json.dumps({"workflow_runs": ["x"]}), False, "a run that is not an object"),
    ]
    failures = []
    for answer, expected, what in cases:
        ok, sentence = verdict(answer, "purge-old-images", MAX_AGE_HOURS, now)
        if ok != expected:
            failures.append(f"{what}: expected {'ALLOW' if expected else 'REFUSE'}, got "
                            f"{'ALLOW' if ok else 'REFUSE'} — {sentence}")

    # The production path fails closed on a failed read.
    def broken() -> str:
        raise OSError("connection refused")
    if check("purge-old-images", fetch=broken, now=now)[0]:
        failures.append("a failed read of the lock lane ALLOWED enabling")

    # 🚨 NEGATIVE CONTROL for the review of #6379: the retired override variables are set to values that
    # WOULD flip the verdict (a 1000 h limit, a different workflow, a fixture of a green run) — and the
    # decision, the url and the limit stay exactly what policy says.
    stale_green = runs("success", "2026-10-07T01:26:46Z")
    fixture = Path(os.environ.get("TMPDIR", "/tmp")) / f"acr-retention-fixture-{os.getpid()}.json"
    fixture.write_text(runs("success", "2026-10-09T11:00:00Z"))
    saved = {name: os.environ.get(name) for name in RETIRED_OVERRIDES}
    try:
        os.environ.update({"MW_LOCK_LANE_REPO": "someone/else", "MW_LOCK_LANE_WORKFLOW": "always-green.yml",
                           "MW_LOCK_LANE_MAX_AGE_HOURS": "1000", "MW_LOCK_LANE_RUNS_FILE": str(fixture)})
        ok, sentence = check("purge-old-images", fetch=lambda: stale_green, now=now)
        if ok:
            failures.append(f"an environment override changed the verdict: {sentence}")
        if EVIDENCE_URL != ("https://api.github.com/repos/Systemorph/MeshWeaver/actions/workflows/"
                            "lock-pinned-digests.yml/runs?branch=main&status=completed&per_page=1"):
            failures.append(f"the evidence url is not the policy one: {EVIDENCE_URL}")
    finally:
        for name, value in saved.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
        fixture.unlink(missing_ok=True)
    # …and structurally: neither this script's code nor its caller reads any of those names.
    here = Path(__file__).resolve()
    code = here.read_text().split("RETIRED_OVERRIDES = (", 1)[0]
    caller = (here.parent / "acr-retention-tasks.sh").read_text()
    for name in RETIRED_OVERRIDES:
        if name in code:
            failures.append(f"{here.name} reads {name} — the evidence must not be overridable")
        if name in caller:
            failures.append(f"acr-retention-tasks.sh names {name} — the evidence must not be overridable")

    for failure in failures:
        print(f"::error::self-test: {failure}")
    if failures:
        return 1
    print(f"self-test: {len(cases)} verdict cases — only a green lock-lane run on main younger than "
          f"{MAX_AGE_HOURS:.0f} h permits enabling; red, cancelled, stale, future-dated, undated, empty, "
          "error and non-JSON answers all refuse; a failed read refuses; and the retired override "
          "variables, set to values that would flip it, change nothing.")
    return 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) != 3 or argv[1] != "--check":
        print(__doc__, file=sys.stderr)
        return 2
    ok, sentence = check(argv[2])
    print(sentence if ok else f"::error::{sentence}", file=sys.stdout if ok else sys.stderr)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
