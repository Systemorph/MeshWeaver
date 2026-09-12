#!/usr/bin/env python3
"""Read the current PR declaration text, rather than a frozen Actions event body.

Rerunning a failed declaration gate must see the maintainer's corrected body.
The original event supplies the PR identity and head only. A changed head, failed
REST read or malformed response fails closed; none falls back to the stale body.
The body is written as data and is never evaluated by a shell.
"""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess


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
    result = subprocess.run(
        ["gh", "api", "--method", "GET", path],
        check=True, capture_output=True, text=True, timeout=45,
    )
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    # Never leave an earlier successful read available after a failed refresh.
    args.output.unlink(missing_ok=True)
    try:
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        body = read_body(event, os.environ["GITHUB_REPOSITORY"], read_pr)
        args.output.write_text(body, encoding="utf-8")
    except (KeyError, TypeError, ValueError, OSError, subprocess.SubprocessError) as error:
        # Do not echo arbitrary PR text or authentication output into workflow commands.
        print(f"::error::Could not read the current PR body ({type(error).__name__}); "
              "check REST access and whether the PR head changed. No cached body was used.")
        return 1
    print(f"Current pull-request body: {len(body.encode('utf-8'))} bytes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
