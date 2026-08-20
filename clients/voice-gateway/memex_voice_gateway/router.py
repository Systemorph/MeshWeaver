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
    "error": "Sorry, that did not work.",
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
    "ready": "The answer about {topic} is ready. Say: read it.",
    "nothing_new": "No new answers.",
    "answer_to": "Answering your question: {question} —",
    "radio_on": "Here comes {station}. Say stop to end it.",
    "song_hint": "I cannot play single songs yet — but here comes {station}.",
}

_READ = re.compile(
    r"^\s*(?:vorlesen|lies\s+(?:es|sie|mir|die antwort)?\s*vor|antwort(?:en)? vorlesen|"
    r"read (?:it|the answer)|play (?:it|the answer))[.!]?\s*$", re.IGNORECASE)

# EMAIL routes DETERMINISTICALLY to the ExecutiveAssistant — a small local model told
# "delegate email" still answers "open your mail program" often enough that the rule
# cannot live in its prompt alone (observed 2026-08-20).
_EMAIL = re.compile(r"\b(?:e-?mails?|mails?|inbox|posteingang|mailbox)\b", re.IGNORECASE)

# MUSIC is deterministic too — the model answered a child's eight requests for a song
# with invented promises about music agents that do not exist (observed 2026-08-20).
# Radio streams play NOW; a named song gets radio plus an honest sentence until a music
# library (Apple Music via Music Assistant) is connected.
_MUSIC = re.compile(
    r"^(?=.*\b(?:ab)?spiel\w*\b|.*\bplay\b)(?=.*\b(?:lied\w*|musik|songs?|radio)\b)|"
    r"^\s*(?:musik|radio)\s*(?:an|bitte)?[.!]?\s*$", re.IGNORECASE | re.DOTALL)
_MUSIC_OFF = re.compile(r"\b(?:musik|radio)\s+(?:aus|stopp?|off)\b|"
                        r"\b(?:stopp?|stop)\s+(?:die\s+)?(?:musik|radio)\b", re.IGNORECASE)
STATIONS = {
    "energy": ("Energy Zürich", "https://energyzuerich.ice.infomaniak.ch/energyzuerich-high.mp3"),
    "srf 3": ("Radio SRF 3", "http://stream.srg-ssr.ch/m/drs3/mp3_128"),
    "srf drei": ("Radio SRF 3", "http://stream.srg-ssr.ch/m/drs3/mp3_128"),
    "srf 1": ("Radio SRF 1", "http://stream.srg-ssr.ch/m/drs1/mp3_128"),
    "virus": ("Radio SRF Virus", "http://stream.srg-ssr.ch/m/drsvirus/mp3_128"),
}
_DEFAULT_STATION = "energy"


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
        self.player: object = None   # async url -> None; the satellite's media player
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
        # The MAILBOX: answers that arrived after their conversation ended. In signal mode
        # the speaker plays a short ready-chime and the full text waits here for "vorlesen".
        self._inbox: list[dict] = []
        self.on_change: Callable[[], None] | None = None

    # ----- the spoken context (session state) -----

    def session_state(self) -> dict:
        return {"portal": self.active, "context": self._context, "threads": self._threads,
                "inbox": self._inbox}

    def restore(self, state: dict) -> None:
        """Resume a persisted session: active portal, context, open threads, unread answers."""
        if state.get("portal") in self._brains:
            self.active = state["portal"]
        context = state.get("context")
        if context and context.get("portal") in self._mesh:
            self._context = context
        self._threads = [t for t in state.get("threads", [])
                         if t.get("portal") in self._mesh]
        self._inbox = list(state.get("inbox", []))

    def deliver_answer(self, handle: str, task: str, reply: str) -> str:
        """An async answer arrived: store the full text in the mailbox and return the short
        READY signal to speak — 'vorlesen' plays it. The answer's thread stays the pinned
        context, so follow-ups continue exactly where the work happened."""
        topic = task if len(task) <= 50 else task[:47] + "…"
        self._inbox.append({"handle": handle, "task": task, "reply": reply})
        for entry in self._threads:
            if f"{entry['portal']}::{entry['path']}" == handle:
                self._context = entry
                break
        self._changed()
        return self._phrases["ready"].format(topic=topic)

    def _changed(self) -> None:
        if self.on_change is not None:
            self.on_change()

    async def sync_system_prompt(self, default_text: str) -> str:
        """THE STANDARD VOICE PROMPT LIVES IN THE MESH, PER USER: read
        @{namespace}/Voice/Prompt from the delegation portal; when absent, DEPOSIT the
        built-in standard there so the user can edit it in the portal. The mesh node wins
        over the built-in; a local SYSTEM_PROMPT_FILE (checked by the caller) wins over
        both. Failures degrade to the built-in — the voice must come up without a mesh."""
        import json as _json
        import logging
        target = self._mesh_target()
        if target is None:
            return default_text
        client = self._brains[target]
        ns = getattr(client, "namespace", None)
        if not ns:
            return default_text
        path = f"{ns}/Voice/Prompt"
        try:
            node = _json.loads(await client.call("get", {"path": f"@{path}"}))  # type: ignore[attr-defined]
            body = ((node.get("content") or {}).get("body") or "").strip()
            if body:
                logging.getLogger(__name__).info("system prompt loaded from @%s", path)
                return body
        except Exception as e:
            logging.getLogger(__name__).info("no prompt node at @%s (%r) — depositing", path, e)
        try:
            await client.call("create", {"node": _json.dumps({  # type: ignore[attr-defined]
                "id": "Prompt", "namespace": f"{ns}/Voice",
                "name": "Voice System Prompt", "nodeType": "Markdown",
                "content": {"$type": "MarkdownContent", "body": default_text}})})
            logging.getLogger(__name__).info("system prompt DEPOSITED at @%s — edit it there", path)
        except Exception as e:
            logging.getLogger(__name__).warning("could not deposit the voice prompt at @%s: %r", path, e)
        return default_text

    def apply_system_prompt(self, text: str) -> None:
        for brain in self._brains.values():
            if hasattr(brain, "system_prompt"):
                brain.system_prompt = text  # type: ignore[attr-defined]

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
        """The portal delegations go to: the active brain when it is a mesh, else the FIRST
        mesh in configured order — `_mesh` is a set, and iterating it directly once sent
        delegations to an arbitrary portal (observed: the prompt sync landing on systemorph
        instead of the local mesh)."""
        if self.active in self._mesh:
            return self.active
        return next((name for name in self._brains if name in self._mesh), None)

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
        import logging
        logging.getLogger(__name__).info("delegated to %s (%s): %r", target, agent or "-", task[:80])
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
        if _READ.match(stripped):
            if not self._inbox:
                return self._phrases["nothing_new"]
            item = self._inbox.pop(0)
            self._changed()
            question = item["task"] if len(item["task"]) <= 60 else item["task"][:57] + "…"
            return f"{self._phrases['answer_to'].format(question=question)} {item['reply']}"
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
        if _MUSIC_OFF.search(stripped) and self.player is not None:
            return ""    # the interrupt path stops the player quietly
        if _MUSIC.search(stripped) and self.player is not None:
            lowered = stripped.lower()
            key = next((k for k in STATIONS if k in lowered), None)
            named_song = key is None and re.search(
                r"\b(?:lied|song)\b", lowered) and len(stripped) > 25
            name, url = STATIONS[key or _DEFAULT_STATION]
            try:
                await self.player(url)  # type: ignore[operator]
            except Exception:
                return self._phrases["error"]
            if named_song:
                return self._phrases["song_hint"].format(station=name)
            return self._phrases["radio_on"].format(station=name)
        if _EMAIL.search(stripped) and self._mesh_target() is not None:
            handle = await self.delegate_task(stripped, "ExecutiveAssistant")
            self._pending.append((handle, stripped))
            return self._phrases["submitted"].format(portal=handle.partition("::")[0])
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
