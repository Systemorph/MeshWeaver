#!/usr/bin/env python3
"""Refuse a control handoff addressed to a different portal than the declaration."""

import json
import sys
from pathlib import Path


HOOK_PATH = "/api/hooks/Hosting/PlatformBuilds"


def expected_url(declaration: Path) -> str:
    base = json.loads(declaration.read_text())["url"].rstrip("/")
    if not base.startswith("https://"):
        raise ValueError(f"control declaration is not an HTTPS URL: {base}")
    return base + HOOK_PATH


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: check-control-webhook-url.py DECLARATION WEBHOOK_URL", file=sys.stderr)
        return 2
    try:
        expected = expected_url(Path(sys.argv[1]))
    except (OSError, KeyError, ValueError, json.JSONDecodeError) as error:
        print(f"::error::cannot read the control instance declaration: {error}")
        return 1
    actual = sys.argv[2]
    if actual != expected:
        print(
            "::error::vars.CONTROL_WEBHOOK_URL targets a different inbox than "
            f".github/control-instance.json: expected {expected}, got {actual}"
        )
        return 1
    print(f"Control webhook matches the declared control instance: {expected}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
