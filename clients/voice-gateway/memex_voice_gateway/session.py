"""The speaker's SESSION COOKIE — spoken context, persisted with an expiry.

The voice conversation has the same context model as the portal's side panel: one CURRENT
thread is the context, everything posts into it until the user switches ("wechsle zum
Thread über …") or starts fresh ("neues Thema"). That state — active portal, current
context, the open threads — is persisted to disk per speaker and expires after a TTL,
exactly like an MCP session id: a gateway restart inside the window resumes where the
conversation was; after the window, the speaker starts clean.
"""

from __future__ import annotations

import json
import time
from pathlib import Path


class SpokenSession:
    def __init__(self, path: str, ttl_hours: float = 8.0) -> None:
        self._path = Path(path).expanduser()
        self._ttl_s = ttl_hours * 3600

    def load(self) -> dict:
        """The persisted session, or {} when absent/expired/corrupt."""
        try:
            state = json.loads(self._path.read_text())
        except (OSError, ValueError):
            return {}
        if float(state.get("expires_at", 0)) < time.time():
            return {}
        return state

    def save(self, *, portal: str, context: dict | None, threads: list[dict]) -> None:
        """Persist and re-arm the expiry — every interaction extends the session."""
        state = {"portal": portal, "context": context, "threads": threads,
                 "expires_at": time.time() + self._ttl_s}
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            self._path.write_text(json.dumps(state, indent=2))
        except OSError:
            pass   # a failed persist degrades to an in-memory session, never breaks a round

    def clear(self) -> None:
        try:
            self._path.unlink(missing_ok=True)
        except OSError:
            pass
