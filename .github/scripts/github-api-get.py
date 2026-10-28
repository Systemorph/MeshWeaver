#!/usr/bin/env python3
"""github-api-get.py <path> — one GitHub REST GET for a shell script, with a verdict-safe exit code.

Shell gates used to write `gh api … || continue`, which turns EVERY failure — a rate limit, a 5xx, a
dropped connection — into "this candidate does not exist" and lets the script conclude from the
survivors (2026-10-27: an installation rate limit made `fetch-deployed-plugin-set.sh` report "the
deployed plugin set is not available"). This helper keeps the one distinction a caller needs:

  exit 0  the JSON body on stdout
  exit 4  HTTP 404 — the ONLY answer a caller may treat as "absent" and skip
  exit 3  NO VERDICT — rate-limited / 5xx / unreachable past the retry budget; the `::error`
          naming the reset time is already printed (on stderr, so a `$(…)` capture cannot swallow
          it), the caller must stop RED
  exit 1  any other refusal (401, a non-rate-limit 403, 422, …) — stop RED

Retry and classification are `scripts/read-current-pr-body.py`'s `api_get` (one implementation):
bounded backoff honouring `retry-after` / `x-ratelimit-reset`, and a wait the budget cannot cover
is not slept. The budget is shared by every call of one script through GITHUB_API_DEADLINE (epoch
seconds), so N lookups cannot add up to N budgets. Token: GH_TOKEN, else GITHUB_TOKEN.
"""

import importlib.util
import json
import os
from pathlib import Path
import sys
import time

_READER = Path(__file__).resolve().parents[2] / "scripts" / "read-current-pr-body.py"


def _load_reader():
    spec = importlib.util.spec_from_file_location("read_current_pr_body", _READER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main(argv):
    if len(argv) != 2 or not argv[1] or argv[1].startswith("/"):
        print("usage: github-api-get.py <repos/...path>", file=sys.stderr)
        return 1
    path = argv[1]
    reader = _load_reader()
    deadline = os.environ.get("GITHUB_API_DEADLINE", "")
    budget = (max(0, int(deadline) - int(time.time())) if deadline.isdigit()
              else reader.DEFAULT_BUDGET_SECONDS)
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    try:
        body = reader.api_get(path, token=token, budget_seconds=budget)
    except reader.ApiUnavailable as error:
        print(f"::error title=NO VERDICT — GitHub API::{error.sentence()}", file=sys.stderr)
        return reader.NO_VERDICT_EXIT
    except reader.ApiRefused as error:
        if str(error).startswith("HTTP 404 "):
            return 4
        print(f"::error::GitHub refused {error} — not a transient failure, so no retry.", file=sys.stderr)
        return 1
    json.dump(body, sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
