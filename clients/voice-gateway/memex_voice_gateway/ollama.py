"""A local model as the brain, via Ollama's /api/chat.

Implements the same two-phase surface as MemexThreads (`ask` → handle, `await_reply` with a
budget), so the pipeline — including the holding-phrase + announce path — is identical for a
local model and a mesh agent. Generation runs in a background task; a budget miss leaves it
running and the announce poll picks the answer up when it lands.

Conversation history lives in-process (this brain has no thread node) and resets after the
same idle window a mesh thread would.
"""

from __future__ import annotations

import asyncio
import itertools
import time
from dataclasses import dataclass, field

import aiohttp

SYSTEM_PROMPT = (
    "You are Memex, the on-device voice of the user's personal mesh — when asked who you "
    "are, your name is Memex. Everything you write is read aloud by text-to-speech: at most "
    "two short sentences, no markdown, lists, code, or links. Reply in the language you were "
    "addressed in; Swiss German arrives transcribed as Standard German — reply in Standard "
    "German. Say numbers and dates in speakable words. Each user message starts with its "
    "wall-clock time in brackets — use it to judge gaps in the conversation.\n\n"
    "TRIAGE — decide per request: answer DIRECTLY yourself for time, dates, general "
    "knowledge, conversions, and small talk (you run on-device; speed is your virtue). "
    "For a QUICK lookup use your tools: search_mesh finds nodes in the user's mesh, "
    "get_node reads one, mesh_tool calls any other portal tool by name, home_assistant "
    "controls the smart home. DELEGATE by calling "
    "delegate_to_memex — it opens a thread in the mesh where a full agent with its own "
    "tools works the task — for anything that needs current information from the web, "
    "documents, writing or changing data, or multi-step work. Anything about EMAIL — "
    "reading, summarizing, or sending mail — always delegates to the ExecutiveAssistant "
    "agent: it acts on the user's own mailbox and calendar. NEVER claim you cannot access "
    "email and NEVER tell the user to open a mail program — delegate instead. After "
    "delegating, tell the user in ONE sentence that Memex is on it and will announce the "
    "answer.\n\n"
    "You never lack capability: whatever you cannot do on-device, the mesh can. Whenever "
    "you think 'I cannot do that', that is EXACTLY when to call delegate_to_memex — never "
    "refuse, never apologize for missing abilities, never send the user to another app.\n\n"
    "Small talk, greetings, and questions about yourself or the speaker get a DIRECT "
    "answer — never a tool call. Children talk to you too: stay warm, simple, and honest — "
    "never invent an agent, a capability, or a promise you cannot keep this instant."
)

_WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
_MONTHS = ["January", "February", "March", "April", "May", "June", "July",
           "August", "September", "October", "November", "December"]


def now_line(now=None) -> str:
    """The current wall-clock — a local model has NO clock of its own, and a voice
    assistant unmoored from real time answers 'today' questions wrongly. Model-facing, so
    English regardless of the spoken language."""
    import datetime
    now = now or datetime.datetime.now().astimezone()
    return (f"Current time: {_WEEKDAYS[now.weekday()]}, {now.day} "
            f"{_MONTHS[now.month - 1]} {now.year}, {now:%H:%M}.")


def build_messages(history: list[dict], text: str, system_prompt: str = SYSTEM_PROMPT,
                   max_turns: int = 8, now: str | None = None) -> list[dict]:
    """System prompt (+ the real-time anchor) + the last `max_turns` exchanges + the new
    user message. History user entries carry their [HH:MM] stamps from ask()."""
    system = system_prompt if now is None else f"{system_prompt}\n\n{now}"
    trimmed = history[-2 * max_turns:]
    return [{"role": "system", "content": system}, *trimmed,
            {"role": "user", "content": text}]


# The standard agents offered for delegation — extended/overridden per deployment
# (a portal entry's "agents" list adds names AND pins where each one runs).
DEFAULT_AGENTS = {
    "Voice": "quick data lookups",
    "Assistant": "general work",
    "Researcher": "deep research",
    "ExecutiveAssistant": "EMAIL — reading, summarizing, and sending mail",
}


def delegate_tool(agents: dict[str, str] | None = None) -> dict:
    """The delegate_to_memex spec, its agent enum built from the CONFIGURED roster."""
    roster = agents or DEFAULT_AGENTS
    return {
        "type": "function",
        "function": {
            "name": "delegate_to_memex",
            "description": "Open a THREAD in the user's mesh, where a full agent with its "
                           "own system prompt and tools (their data, web search, documents, "
                           "email) works the task. The answer is announced when ready.",
            "parameters": {
                "type": "object",
                "properties": {
                    "task": {"type": "string", "description": "The task, self-contained, in the user's language."},
                    "agent": {"type": "string", "enum": list(roster),
                              "description": " ".join(f"{n}: {d}." for n, d in roster.items())},
                },
                "required": ["task"],
            },
        },
    }


DELEGATE_TOOL = delegate_tool()

# The normal read-only tools every mesh client has — run in-round by the router against the
# active/first mesh portal, so a quick lookup never costs a whole thread.
MESH_TOOLS = [
    {"type": "function", "function": {
        "name": "search_mesh",
        "description": "Search the user's mesh (notes, documents, courses, data). Returns "
                       "matching nodes with their paths. Free text works; so do filters "
                       "like nodeType:Agent or name:*sales*.",
        "parameters": {"type": "object", "properties": {
            "query": {"type": "string", "description": "Free text or GitHub-style query."}},
            "required": ["query"]},
    }},
    {"type": "function", "function": {
        "name": "get_node",
        "description": "Read one mesh node by path (as returned by search_mesh), e.g. "
                       "@Doc/Architecture/Plugins.",
        "parameters": {"type": "object", "properties": {
            "path": {"type": "string", "description": "The node path, e.g. @Edu/Guide."}},
            "required": ["path"]},
    }},
]

# FULL MCP: any tool on the connected portal, by name — the same surface an MCP client
# has. Destructive tools are filtered router-side unless explicitly enabled.
MESH_TOOL = {
    "type": "function",
    "function": {
        "name": "mesh_tool",
        "description": "Call ANY tool on the user's mesh portal by its MCP name — e.g. "
                       "autocomplete, render_area, navigate_to, create, update, "
                       "execute_script. Use search_mesh/get_node for plain lookups; use "
                       "this for everything else the portal offers.",
        "parameters": {"type": "object", "properties": {
            "tool": {"type": "string", "description": "The MCP tool name, e.g. render_area."},
            "arguments": {"type": "object", "description": "The tool's arguments object."},
        }, "required": ["tool"]},
    },
}

HOME_TOOL = {
    "type": "function",
    "function": {
        "name": "home_assistant",
        "description": "Control or read the smart home via Home Assistant: list entities, "
                       "read a state, or call a service (turn_on, turn_off, toggle, ...).",
        "parameters": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["list_entities", "get_state", "call_service"]},
            "entity_id": {"type": "string", "description": "e.g. light.living_room"},
            "domain": {"type": "string", "description": "service domain, e.g. light, switch, media_player"},
            "service": {"type": "string", "description": "e.g. turn_on, turn_off, toggle"},
        }, "required": ["action"]},
    },
}

_TOOL_RESULT_LIMIT = 1500   # a small on-device model drowns in a full search dump


@dataclass
class OllamaBrain:
    base_url: str
    model: str
    idle_minutes: float = 5.0
    num_predict: int = 200
    system_prompt: str = SYSTEM_PROMPT
    location: str | None = None    # anchors 'here'/weather questions to a real place
    # The triage seams, set by the router: delegator(task, agent) -> stamped handle OPENS A
    # THREAD in the mesh (the pipeline drains pending delegations and announces their
    # answers); tool_runner(name, args) -> str runs the quick read-only tools in-round.
    delegator: object = None
    tool_runner: object = None
    tools: list = field(default_factory=list)   # specs offered alongside delegate_to_memex
    agents: dict | None = None                  # delegation roster: name → description
    _pending: list = field(default_factory=list, init=False)

    _history: list[dict] = field(default_factory=list, init=False)
    _last_used: float = field(default=0.0, init=False)
    _tasks: dict[str, asyncio.Task] = field(default_factory=dict, init=False)
    _chunks: dict[str, asyncio.Queue] = field(default_factory=dict, init=False)
    _ids: itertools.count = field(default_factory=itertools.count, init=False)
    _http: aiohttp.ClientSession | None = field(default=None, init=False)

    async def _chat(self, messages: list[dict], handle: str | None = None) -> str:
        """One completion; when `handle` names a chunk queue, STREAM pieces into it as they
        arrive — the voice pipeline speaks them sentence-by-sentence while generation runs."""
        if handle is not None:
            return await self._chat_streaming(messages, handle)
        if self._http is None:
            self._http = aiohttp.ClientSession()
        # keep_alive holds the model in RAM between questions — without it, Ollama unloads
        # after ~5 min idle and the next wake pays the full model load (measured: 15s cold
        # vs 0.4s warm for qwen3.6 on an M5 Max).
        payload = {"model": self.model, "messages": messages, "stream": False,
                   "think": False, "keep_alive": "60m",
                   "options": {"num_predict": self.num_predict}}
        async with self._http.post(f"{self.base_url}/api/chat", json=payload,
                                   timeout=aiohttp.ClientTimeout(total=300)) as response:
            if response.status == 400:
                # Older Ollama / non-thinking model rejecting "think" — retry without it.
                del payload["think"]
                async with self._http.post(f"{self.base_url}/api/chat", json=payload,
                                           timeout=aiohttp.ClientTimeout(total=300)) as retry:
                    retry.raise_for_status()
                    data = await retry.json()
            else:
                response.raise_for_status()
                data = await response.json()
        return (data.get("message") or {}).get("content", "").strip()

    async def _chat_streaming(self, messages: list[dict], handle: str) -> str:
        if self._http is None:
            self._http = aiohttp.ClientSession()
        queue = self._chunks[handle]
        pieces: list[str] = []
        import json as _json
        working = list(messages)
        offered = list(self.tools) + (
            [delegate_tool(self.agents)] if self.delegator is not None else [])
        try:
            for _round in range(3):   # tools on rounds 0-1; the LAST round must SPEAK —
                # a child's "Wie heisse ich?" once sent the model tool-calling in circles
                # until the cap killed the turn, and the spoken reply was the one word
                # that dribbled out before the first call ("Du").
                payload = {"model": self.model, "messages": working, "stream": True,
                           "think": False, "keep_alive": "60m",
                           "options": {"num_predict": self.num_predict}}
                if offered and _round < 2:
                    payload["tools"] = offered
                tool_calls: list = []
                async with self._http.post(f"{self.base_url}/api/chat", json=payload,
                                           timeout=aiohttp.ClientTimeout(total=300)) as response:
                    response.raise_for_status()
                    async for line in response.content:
                        if not line.strip():
                            continue
                        data = _json.loads(line)
                        message = data.get("message") or {}
                        piece = message.get("content", "")
                        if piece:
                            pieces.append(piece)
                            await queue.put(piece)
                        if message.get("tool_calls"):
                            tool_calls.extend(message["tool_calls"])
                        if data.get("done"):
                            break
                if not tool_calls:
                    break
                working.append({"role": "assistant", "content": "", "tool_calls": tool_calls})
                for call in tool_calls:
                    working.append({"role": "tool",
                                    "content": await self._dispatch_tool(call)})
        finally:
            await queue.put(None)
        return "".join(pieces).strip()

    async def _dispatch_tool(self, call: dict) -> str:
        """One tool call → its result string. delegate_to_memex OPENS A THREAD (via the
        router's delegator) and queues the stamped handle for the announce path; everything
        else runs through tool_runner, truncated to what a small model can digest."""
        import json as _json
        fn = (call.get("function") or {})
        name = fn.get("name", "")
        args = fn.get("arguments") or {}
        if isinstance(args, str):
            try: args = _json.loads(args)
            except Exception: args = {"task": args} if name == "delegate_to_memex" else {}
        if name == "delegate_to_memex":
            if self.delegator is None:
                return "Delegation is not configured."
            task = str(args.get("task", "")).strip()
            try:
                stamped = await self.delegator(task, args.get("agent") or None)
                self._pending.append((stamped, task))
                return "Delegated: a memex thread is working on it. The answer will be announced."
            except Exception as e:
                return f"Delegation failed: {e}"
        if self.tool_runner is None:
            return f"Unknown tool: {name}"
        try:
            result = str(await self.tool_runner(name, args))
        except Exception as e:
            return f"Tool {name} failed: {e}"
        if len(result) > _TOOL_RESULT_LIMIT:
            result = result[:_TOOL_RESULT_LIMIT] + " …(truncated)"
        return result

    def drain_delegations(self) -> list:
        """(stamped_handle, task) pairs the pipeline should announce answers for."""
        pending, self._pending = self._pending, []
        return pending

    async def ask(self, text: str) -> str:
        """Start generating; returns a handle usable with await_reply (pipeline contract)."""
        if self._history and (time.monotonic() - self._last_used) > self.idle_minutes * 60:
            self._history = []
        self._last_used = time.monotonic()
        import datetime
        stamped = f"[{datetime.datetime.now().astimezone():%H:%M}] {text}"
        anchor = now_line() + (f" Location: {self.location}." if self.location else "")
        messages = build_messages(self._history, stamped, system_prompt=self.system_prompt,
                                  now=anchor)
        self._history.append({"role": "user", "content": stamped})

        handle = f"ollama-{next(self._ids)}"
        self._chunks[handle] = asyncio.Queue()

        async def generate() -> str:
            reply = await self._chat(messages, handle=handle)
            self._history.append({"role": "assistant", "content": reply})
            return reply

        self._tasks[handle] = asyncio.create_task(generate())
        return handle

    def stream_text(self, handle: str):
        """Async iterator over the reply's text pieces as they are generated, or None."""
        queue = self._chunks.get(handle)
        if queue is None:
            return None

        async def drain():
            while (piece := await queue.get()) is not None:
                yield piece
            self._chunks.pop(handle, None)

        return drain()

    async def await_reply(self, handle: str, budget_s: float) -> str | None:
        task = self._tasks.get(handle)
        if task is None:
            return None
        try:
            reply = await asyncio.wait_for(asyncio.shield(task), timeout=budget_s)
        except asyncio.TimeoutError:
            return None      # generation keeps running; the announce path retries this handle
        self._tasks.pop(handle, None)
        self._chunks.pop(handle, None)   # nobody streamed this round — drop the piece queue
        return reply

    async def warmup(self) -> None:
        """Load the model into RAM before the first question — a cold 23GB MoE load reads
        as the assistant ignoring you (measured: ~15-30s cold vs sub-second warm)."""
        if self._http is None:
            self._http = aiohttp.ClientSession()
        try:
            payload = {"model": self.model, "messages": [{"role": "user", "content": "hi"}],
                       "stream": False, "think": False, "keep_alive": "60m",
                       "options": {"num_predict": 1}}
            async with self._http.post(f"{self.base_url}/api/chat", json=payload,
                                       timeout=aiohttp.ClientTimeout(total=180)) as response:
                await response.read()
        except Exception:
            pass   # warmup is best-effort; the first real question pays the load instead

    async def close(self) -> None:
        for task in self._tasks.values():
            task.cancel()
        if self._http is not None:
            await self._http.close()
            self._http = None
