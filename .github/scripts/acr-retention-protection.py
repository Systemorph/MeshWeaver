#!/usr/bin/env python3
"""Is the pinned-digest protection CURRENT enough to switch a purge on? (MeshWeaver#3438)

    <runs JSON on stdin> | acr-retention-protection.py "<tasks being enabled>" <max age hours>
    acr-retention-protection.py --self-test

Reads GitHub's `actions/workflows/<lock lane>/runs?branch=main&status=completed&per_page=1` answer
and exits 0 only when the newest completed run of the lock lane on `main` concluded `success` and is
younger than the limit. Every other answer — no run, a red or cancelled one, an old one, an answer
that is not JSON or carries no date — exits non-zero with a sentence naming why.

🚨 FAILS CLOSED, by construction. "Could not tell" must never read as "nothing against it": that is
the swallow that turns a failed protection collection into a deletion (#3859). Called by
`acr-retention-tasks.sh apply` / `preflight` only when the record asks to ENABLE a task.
"""
from __future__ import annotations

import datetime
import json
import sys


def verdict(answer: str, enabling: str, max_age_hours: float,
            now: datetime.datetime) -> tuple[bool, str]:
    """(may enable, the sentence saying why). Pure."""
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
        ok, sentence = verdict(answer, "purge-old-images", 36, now)
        if ok != expected:
            failures.append(f"{what}: expected {'ALLOW' if expected else 'REFUSE'}, got "
                            f"{'ALLOW' if ok else 'REFUSE'} — {sentence}")
    for failure in failures:
        print(f"::error::self-test: {failure}")
    if failures:
        return 1
    print(f"self-test: {len(cases)} cases — only a green lock-lane run on main younger than the "
          "limit permits enabling; red, cancelled, stale, future-dated, undated, empty, error and "
          "non-JSON answers all refuse.")
    return 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        max_age = float(argv[2])
    except ValueError:
        print(f"::error::max age '{argv[2]}' is not a number of hours.", file=sys.stderr)
        return 2
    ok, sentence = verdict(sys.stdin.read(), argv[1], max_age,
                           datetime.datetime.now(datetime.timezone.utc))
    print(sentence if ok else f"::error::{sentence}", file=sys.stdout if ok else sys.stderr)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
