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

# Courteous closure is the END of an exchange, not a prompt: answering "Vielen Dank" with
# "Gerne geschehen!" made the device hear its own reply (or the TV's politeness) and wake
# again — a self-thanking loop, five rounds in 90 seconds, observed live.
# A wake-word chant ("hey memex hey memex …") is a TRAINING RECORDING, not a question —
# record it (the satellite already has), answer nothing.
_CHANT = re.compile(r"^\s*(?:hey,?\s*(?:memex|jarvis|nabu)[.!,;\s]*){2,}$", re.IGNORECASE)

_CLOSURE = re.compile(r"^\s*(?:vielen dank|danke(?:\s*(?:dir|sch(?:ö|oe)n|vielmals))?|merci(?:\s*vielmal)?|"
                      r"thank(?:s| you)|ok(?:ay)?|gut|super|perfekt|alles klar|tsch(?:ü|ue)ss|"
                      r"bis sp(?:ä|ae)ter|gute nacht)[.!,\s]*$", re.IGNORECASE)

_SWITCH = re.compile(
    r"^\s*(?:switch to|connect to|go to|wechsle zu|wechsel zu|verbinde mit|verbinde dich mit|"
    r"gang uf|verbind mit|geh zu)\s+(?P<target>[\wäöüéè .-]+?)[.!?]?\s*$",
    re.IGNORECASE,
)


def parse_switch_command(transcript: str) -> str | None:
    """The raw target phrase of a spoken switch command, or None."""
    match = _SWITCH.match(transcript.strip())
    return match.group("target").strip() if match else None


# The spoken CONTEXT commands — the side-panel model, by voice: one current thread is the
# context; switch to another by topic, post into one by topic, or start fresh.
_NEW_TOPIC = re.compile(
    r"^\s*(?:neues thema|themawechsel|thema wechseln|new topic|"
    r"(?:schliesse?|beende)\s+(?:das|den|dieses)?\s*(?:thema|thread|kontext)|"
    r"close (?:the )?(?:topic|thread|context))[.!]?\s*$", re.IGNORECASE)
_LIST_THREADS = re.compile(
    r"^\s*(?:welche threads?\s+(?:sind|haben wir)\s+offen|liste\s+(?:der|die)\s+threads?|"
    r"what threads?\s+(?:are|do we have)\s+open|list\s+(?:the\s+)?(?:open\s+)?threads?)"
    r"\s*\??[.!]?\s*$", re.IGNORECASE)
_SWITCH_THREAD = re.compile(
    r"^\s*(?:wechsle|wechsel|geh|gehe|switch|go)\s+(?:zum|zu dem|to the|to)\s+thread\s*"
    r"(?:über|ueber|about|on|zu)?\s+(?P<topic>[\wäöüéè .-]{2,50}?)[.!?]?\s*$", re.IGNORECASE)
_POST_TO = re.compile(
    r"^\s*(?:im|in dem|in den|an den|in the|to the|post to the|poste an den)\s+thread\s*"
    r"(?:über|ueber|about|on|zu)?\s+(?P<topic>[^:,]{2,50}?)\s*[:,]\s+(?P<message>.{4,})$",
    re.IGNORECASE)

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
    "new_topic": "Okay, new topic.",
    "switched": "Now in the thread about {topic}.",
    "posted": "Posted to the thread about {topic}. I will announce the answer.",
    "no_thread": "I have no open thread about {topic}.",
    "threads_open": "Open threads: {list}.",
    "no_threads": "No open threads.",
}


class BrainRouter:
    def __init__(self, brains: dict[str, Brain], active: str,
                 phrases: dict[str, str] | None = None,
                 mesh_brains: set[str] | None = None,
                 home: object = None,
                 agent_homes: dict[str, str] | None = None) -> None:
        if active not in brains:
            raise ValueError(f"active brain {active!r} not among {list(brains)}")
        self._brains = brains
        self.active = active
        self._phrases = {**_DEFAULT_PHRASES, **(phrases or {})}
        self._mesh = mesh_brains if mesh_brains is not None else {
            n for n, b in brains.items() if hasattr(b, "delegate")}
        self._home = home     # HomeAssistant client, when configured
        # Agent name → the portal that HOSTS it (a portal entry's "agents" list): the
        # ExecutiveAssistant may live on the local mesh while Researcher lives in the cloud.
        self._agent_homes = agent_homes or {}
        # The SPOKEN CONTEXT — side-panel semantics: `_context` is THE current thread
        # (delegations post into it until switched or closed), `_threads` lists everything
        # opened this session, `_pending` carries spoken posts awaiting their announcement,
        # `on_change` persists the session cookie after every mutation.
        self._context: dict | None = None
        self._threads: list[dict] = []
        self._pending: list = []
        self.on_change: Callable[[], None] | None = None

    # ----- the spoken context (session state) -----

    def session_state(self) -> dict:
        return {"portal": self.active, "context": self._context, "threads": self._threads}

    def restore(self, state: dict) -> None:
        """Resume a persisted session: active portal, context, open threads."""
        if state.get("portal") in self._brains:
            self.active = state["portal"]
        context = state.get("context")
        if context and context.get("portal") in self._mesh:
            self._context = context
        self._threads = [t for t in state.get("threads", [])
                         if t.get("portal") in self._mesh]

    def _changed(self) -> None:
        if self.on_change is not None:
            self.on_change()

    def _remember(self, portal: str, path: str, task: str, agent: str | None) -> dict:
        import time
        entry = next((t for t in self._threads if t["path"] == path), None)
        if entry is None:
            entry = {"portal": portal, "path": path,
                     "task": task if len(task) <= 80 else task[:77] + "…",
                     "agent": agent or ""}
            self._threads.append(entry)
        entry["last"] = time.time()
        self._context = entry
        self._changed()
        return entry

    def _find_thread(self, topic: str) -> dict | None:
        """Fuzzy topic → open thread: substring or word overlap on task/agent, most
        recently used first."""
        wanted = topic.lower().strip()
        words = {w for w in re.split(r"\W+", wanted) if len(w) > 2}
        best = None
        for entry in sorted(self._threads, key=lambda t: t.get("last", 0), reverse=True):
            haystack = f"{entry['task']} {entry['agent']}".lower()
            if wanted in haystack or (words and words <= set(re.split(r"\W+", haystack))):
                return entry
            if best is None and words and words & set(re.split(r"\W+", haystack)):
                best = entry
        return best

    def _mesh_target(self) -> str | None:
        """The portal delegations go to: the active brain when it is a mesh, else the first mesh."""
        if self.active in self._mesh:
            return self.active
        return next(iter(self._mesh), None)

    async def delegate_task(self, task: str, agent: str | None = None) -> str:
        """The local brain's triage seam, with CONTEXT: while a context thread is open,
        follow-ups post INTO it (the mesh agent keeps the whole conversation); a different
        explicit agent reuses/opens that agent's own thread and the context switches to it —
        the side-panel model. Returns the STAMPED handle so await_reply later polls the
        right brain. Raises when no mesh is configured."""
        context = self._context
        if context is not None and agent and agent != (context.get("agent") or None):
            context = next((t for t in self._threads if t["agent"] == agent), None)
        if context is not None:
            portal, path = context["portal"], context["path"]
            await self._brains[portal].submit(path, task, agent or context["agent"] or None)  # type: ignore[attr-defined]
            self._remember(portal, path, context["task"], context["agent"] or None)
            return f"{portal}::{path}"
        target = None
        if agent and (housed := self._agent_homes.get(agent)) in self._mesh:
            target = housed
        target = target or self._mesh_target()
        if target is None:
            raise RuntimeError("no mesh portal configured")
        path = await self._brains[target].delegate(task, agent)  # type: ignore[attr-defined]
        self._remember(target, path, task, agent)
        return f"{target}::{path}"

    # Irreversible mesh tools stay behind an explicit opt-in — one mis-heard word must
    # never delete a node. Everything else on the portal's MCP surface passes through.
    _DESTRUCTIVE = {"delete", "restore_version", "restore_from_point_in_time"}
    allow_destructive: bool = False

    async def run_tool(self, name: str, args: dict) -> str:
        """The local brain's quick tools, run in-round: mesh calls go to the same portal
        delegations would (the active brain when it is a mesh, else the first mesh);
        home_assistant goes to the configured HA instance. mesh_tool is the FULL MCP
        passthrough — any tool on the portal, by name."""
        if name == "home_assistant":
            if self._home is None:
                return "Home Assistant is not configured."
            return await self._home.run(args)
        target = self._mesh_target()
        if target is None:
            return "No mesh portal is configured."
        client = self._brains[target]
        if name == "search_mesh":
            return await client.call("search", {"query": str(args.get("query", "")).strip(),
                                                "limit": 8})  # type: ignore[attr-defined]
        if name == "get_node":
            return await client.call("get", {"path": str(args.get("path", "")).strip()})  # type: ignore[attr-defined]
        if name == "mesh_tool":
            import json as _json
            tool = str(args.get("tool", "")).strip()
            if tool in self._DESTRUCTIVE and not self.allow_destructive:
                return f"The tool {tool} is destructive and disabled for voice."
            raw = args.get("arguments") or {}
            if isinstance(raw, str):
                try: raw = _json.loads(raw)
                except Exception: return "arguments must be a JSON object."
            return await client.call(tool, raw)  # type: ignore[attr-defined]
        return f"Unknown tool: {name}"

    def drain_delegations(self) -> list:
        """Pending (handle, task) pairs from ANY brain that collects them (the local one),
        plus spoken posts routed by handle_command."""
        pending, self._pending = self._pending, []
        for brain in self._brains.values():
            drain = getattr(brain, "drain_delegations", None)
            if drain:
                pending.extend(drain())
        return pending

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
        if (_STOP.match(transcript.strip()) or _CLOSURE.match(transcript.strip())
                or _CHANT.match(transcript.strip())):
            return ""     # stop or courteous closure: end quietly, never reply to a reply

        # The spoken CONTEXT commands come BEFORE the portal switch — "wechsle zum Thread
        # über X" must never be eaten by "wechsle zu {portal}".
        stripped = transcript.strip()
        if _NEW_TOPIC.match(stripped):
            self._context = None
            self._changed()
            return self._phrases["new_topic"]
        if _LIST_THREADS.match(stripped):
            if not self._threads:
                return self._phrases["no_threads"]
            names = ", ".join(t["task"] if len(t["task"]) <= 40 else t["task"][:37] + "…"
                              for t in self._threads[-6:])
            return self._phrases["threads_open"].format(list=names)
        posted = _POST_TO.match(stripped)
        if posted is not None:
            entry = self._find_thread(posted.group("topic"))
            if entry is None:
                return self._phrases["no_thread"].format(topic=posted.group("topic").strip())
            message = posted.group("message").strip()
            await self._brains[entry["portal"]].submit(entry["path"], message,
                                                       entry["agent"] or None)  # type: ignore[attr-defined]
            self._remember(entry["portal"], entry["path"], entry["task"], entry["agent"] or None)
            self._pending.append((f"{entry['portal']}::{entry['path']}", message))
            return self._phrases["posted"].format(topic=posted.group("topic").strip())
        switched = _SWITCH_THREAD.match(stripped)
        if switched is not None:
            entry = self._find_thread(switched.group("topic"))
            if entry is None:
                return self._phrases["no_thread"].format(topic=switched.group("topic").strip())
            self._context = entry
            self._changed()
            return self._phrases["switched"].format(topic=switched.group("topic").strip())

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
            if self._mesh_target() is None:
                return self._phrases["no_mesh"]
            if _DELEGATE_THREAD.match(transcript.strip()):
                self._context = None   # "start a thread" is EXPLICITLY a fresh one
            handle = await self.delegate_task(delegated.group("task").strip(), agent_name)
            return self._phrases["delegated"].format(agent=agent_name or "Assistant",
                                                     portal=handle.partition("::")[0])

        spoken = parse_switch_command(transcript)
        if spoken is None:
            return None
        name = self.resolve(spoken)
        if name is None:
            return self._phrases["unknown"].format(target=spoken,
                                                   names=", ".join(self._brains))
        self.active = name
        self._changed()   # the active portal is part of the persisted session
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
        if self._home is not None:
            await self._home.close()  # type: ignore[attr-defined]
