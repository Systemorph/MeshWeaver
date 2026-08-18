"""Multiple brains, one wake word — and a spoken command to switch between them.

`PORTALS` (JSON) declares a named list of targets — mesh portals (memex, systemorph, a
customer instance) and/or a local model:

    PORTALS='[
      {"name":"memex","url":"https://memex.meshweaver.cloud","token":"mw_…","namespace":"rbuergi","agent":"Voice"},
      {"name":"systemorph","url":"https://memex.systemorph.com","token":"mw_…","namespace":"rbuergi","agent":"Voice"},
      {"name":"lokal","kind":"ollama","model":"qwen3.6"}
    ]'

Switching is a DETERMINISTIC spoken command matched before the brain ever sees the text
("switch to systemorph", "wechsle zu memex", "verbinde mit lokal", "gang uf systemorph") —
regex, not a tool call, so it works identically on every brain and cannot be mis-refused
by a model. The confirmation is spoken back; everything else flows to the ACTIVE brain.
"""

from __future__ import annotations

import re
from typing import Awaitable, Callable, Protocol


class Brain(Protocol):
    async def ask(self, text: str) -> str: ...
    async def await_reply(self, handle: str, budget_s: float) -> str | None: ...
    async def close(self) -> None: ...


_SWITCH = re.compile(
    r"^\s*(?:switch to|connect to|go to|wechsle zu|wechsel zu|verbinde mit|verbinde dich mit|"
    r"gang uf|verbind mit|geh zu)\s+(?P<target>[\wäöüéè .-]+?)[.!?]?\s*$",
    re.IGNORECASE,
)


def parse_switch_command(transcript: str) -> str | None:
    """The raw target phrase of a spoken switch command, or None."""
    match = _SWITCH.match(transcript.strip())
    return match.group("target").strip() if match else None


class BrainRouter:
    def __init__(self, brains: dict[str, Brain], active: str) -> None:
        if active not in brains:
            raise ValueError(f"active brain {active!r} not among {list(brains)}")
        self._brains = brains
        self.active = active

    def resolve(self, spoken: str) -> str | None:
        """Match a spoken target against the configured names, forgivingly."""
        wanted = spoken.lower().strip()
        for name in self._brains:
            lowered = name.lower()
            if wanted == lowered or wanted.startswith(lowered) or lowered.startswith(wanted):
                return name
        return None

    async def handle_command(self, transcript: str) -> str | None:
        """Returns the spoken confirmation when the transcript was a switch command."""
        spoken = parse_switch_command(transcript)
        if spoken is None:
            return None
        name = self.resolve(spoken)
        if name is None:
            return f"Ich kenne {spoken} nicht. Verfügbar: {', '.join(self._brains)}."
        self.active = name
        return f"Verbunden mit {name}."

    async def ask(self, text: str) -> str:
        return f"{self.active}::{await self._brains[self.active].ask(text)}"

    async def await_reply(self, handle: str, budget_s: float) -> str | None:
        # The handle is stamped with its brain so a late announce still polls the brain
        # that produced it, even if the active brain changed meanwhile.
        name, _, inner = handle.partition("::")
        return await self._brains[name].await_reply(inner, budget_s)

    async def close(self) -> None:
        for brain in self._brains.values():
            await brain.close()
