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
    "You are a voice assistant on a smart speaker. Everything you write is read aloud by "
    "text-to-speech: answer in at most two short sentences — no markdown, lists, code, or "
    "links. Reply in the language you were addressed in; Swiss German arrives transcribed "
    "as Standard German — reply in Standard German. Give the answer directly, not the "
    "process; ask a follow-up question only when the request is genuinely ambiguous. Each "
    "user message starts with its wall-clock time in brackets — use it to judge the time "
    "gaps in the conversation."
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


@dataclass
class OllamaBrain:
    base_url: str
    model: str
    idle_minutes: float = 5.0
    num_predict: int = 200
    system_prompt: str = SYSTEM_PROMPT

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
        payload = {"model": self.model, "messages": messages, "stream": True,
                   "think": False, "keep_alive": "60m",
                   "options": {"num_predict": self.num_predict}}
        queue = self._chunks[handle]
        pieces: list[str] = []
        import json as _json
        try:
            async with self._http.post(f"{self.base_url}/api/chat", json=payload,
                                       timeout=aiohttp.ClientTimeout(total=300)) as response:
                response.raise_for_status()
                async for line in response.content:
                    if not line.strip():
                        continue
                    data = _json.loads(line)
                    piece = (data.get("message") or {}).get("content", "")
                    if piece:
                        pieces.append(piece)
                        await queue.put(piece)
                    if data.get("done"):
                        break
        finally:
            await queue.put(None)
        return "".join(pieces).strip()

    async def ask(self, text: str) -> str:
        """Start generating; returns a handle usable with await_reply (pipeline contract)."""
        if self._history and (time.monotonic() - self._last_used) > self.idle_minutes * 60:
            self._history = []
        self._last_used = time.monotonic()
        import datetime
        stamped = f"[{datetime.datetime.now().astimezone():%H:%M}] {text}"
        messages = build_messages(self._history, stamped, system_prompt=self.system_prompt,
                                  now=now_line())
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

    async def close(self) -> None:
        for task in self._tasks.values():
            task.cancel()
        if self._http is not None:
            await self._http.close()
            self._http = None
