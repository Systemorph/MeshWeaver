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


_STOP = re.compile(r"^\s*(?:stop|stopp|halt|sei still|ruhe|schweig|genug)[.!]?\s*$", re.IGNORECASE)

_SWITCH = re.compile(
    r"^\s*(?:switch to|connect to|go to|wechsle zu|wechsel zu|verbinde mit|verbinde dich mit|"
    r"gang uf|verbind mit|geh zu)\s+(?P<target>[\wäöüéè .-]+?)[.!?]?\s*$",
    re.IGNORECASE,
)


def parse_switch_command(transcript: str) -> str | None:
    """The raw target phrase of a spoken switch command, or None."""
    match = _SWITCH.match(transcript.strip())
    return match.group("target").strip() if match else None


_DELEGATE = re.compile(
    r"^\s*(?:frag|frage|ask)\s+(?:den|die|das|the)?\s*(?P<agent>[a-zA-Zäöü]+)[:,]?\s+"
    r"(?:nach\s+|about\s+|to\s+)?(?P<task>.{4,})$",
    re.IGNORECASE,
)
_DELEGATE_THREAD = re.compile(
    r"^\s*(?:starte\s+(?:einen\s+)?thread|start\s+a\s+thread)[:,]?\s+(?P<task>.{4,})$",
    re.IGNORECASE,
)

# Spoken agent names → the standard agents on the mesh. The gate keeps ordinary sentences
# that happen to start with "ask …" from being swallowed as delegations.
KNOWN_AGENTS = {"assistant": "Assistant", "researcher": "Researcher", "worker": "Worker",
                "tutor": "Tutor", "voice": "Voice"}

_DEFAULT_PHRASES = {
    "hold": "Let me check. One moment please.",
    "connected": "Connected to {name}.",
    "unknown": "I don't know {target}. Available: {names}.",
    "delegated": "Started a {agent} thread on {portal}. You can review it there.",
    "no_mesh": "No mesh portal is configured for threads.",
    "submitted": "Submitted to {portal}. I will tell you when the answer arrives.",
}


class BrainRouter:
    def __init__(self, brains: dict[str, Brain], active: str,
                 phrases: dict[str, str] | None = None,
                 mesh_brains: set[str] | None = None) -> None:
        if active not in brains:
            raise ValueError(f"active brain {active!r} not among {list(brains)}")
        self._brains = brains
        self.active = active
        self._phrases = {**_DEFAULT_PHRASES, **(phrases or {})}
        self._mesh = mesh_brains if mesh_brains is not None else {
            n for n, b in brains.items() if hasattr(b, "delegate")}

    def _mesh_target(self) -> str | None:
        """The portal delegations go to: the active brain when it is a mesh, else the first mesh."""
        if self.active in self._mesh:
            return self.active
        return next(iter(self._mesh), None)

    def describe_hold(self, handle: str) -> str:
        """What to say when the answer will come later — mesh submissions are ACKNOWLEDGED
        by portal name, because their answers can arrive highly asynchronously."""
        name, _, _ = handle.partition("::")
        if name in self._mesh:
            return self._phrases["submitted"].format(portal=name)
        return self._phrases["hold"]

    def resolve(self, spoken: str) -> str | None:
        """Match a spoken target against the configured names, forgivingly."""
        wanted = spoken.lower().strip()
        for name in self._brains:
            lowered = name.lower()
            if wanted == lowered or wanted.startswith(lowered) or lowered.startswith(wanted):
                return name
        return None

    async def handle_command(self, transcript: str) -> str | None:
        """Returns the spoken confirmation when the transcript was a command;
        empty string = handled silently (say nothing)."""
        if _STOP.match(transcript.strip()):
            return ""     # barge-in already stopped playback at wake; just end quietly

        # Delegation: launch a real thread for a STANDARD agent on the mesh and leave the
        # work to be evaluated THERE — the speaker only confirms the submission.
        delegated = _DELEGATE_THREAD.match(transcript.strip())
        agent_name: str | None = None
        if delegated is None:
            match = _DELEGATE.match(transcript.strip())
            if match and match.group("agent").lower() in KNOWN_AGENTS:
                delegated = match
                agent_name = KNOWN_AGENTS[match.group("agent").lower()]
        if delegated is not None:
            target = self._mesh_target()
            if target is None:
                return self._phrases["no_mesh"]
            await self._brains[target].delegate(delegated.group("task").strip(), agent_name)  # type: ignore[attr-defined]
            return self._phrases["delegated"].format(agent=agent_name or "Assistant",
                                                     portal=target)

        spoken = parse_switch_command(transcript)
        if spoken is None:
            return None
        name = self.resolve(spoken)
        if name is None:
            return self._phrases["unknown"].format(target=spoken,
                                                   names=", ".join(self._brains))
        self.active = name
        return self._phrases["connected"].format(name=name)

    async def ask(self, text: str) -> str:
        return f"{self.active}::{await self._brains[self.active].ask(text)}"

    async def await_reply(self, handle: str, budget_s: float) -> str | None:
        # The handle is stamped with its brain so a late announce still polls the brain
        # that produced it, even if the active brain changed meanwhile.
        name, _, inner = handle.partition("::")
        return await self._brains[name].await_reply(inner, budget_s)

    def stream_text(self, handle: str):
        """The producing brain's live text stream for this handle, or None (brain doesn't stream)."""
        name, _, inner = handle.partition("::")
        stream = getattr(self._brains[name], "stream_text", None)
        return stream(inner) if stream else None

    async def close(self) -> None:
        for brain in self._brains.values():
            await brain.close()
