"""The memex thread surface, spoken over MCP streamable HTTP.

The mesh's `/mcp` endpoint takes plain JSON-RPC POSTs with a Bearer token: `initialize`
(capture `Mcp-Session-Id`), `notifications/initialized`, then `tools/call`. Responses arrive
as JSON or as a single-event SSE body — both are handled. This mirrors the framing already
proven by education/scripts/upload-videos.py.

Thread lifecycle (verified against live nodes, 2026-08-18):
  start_thread → {"status":"Started","threadPath":…}; the agent runs asynchronously.
  The thread node's content lists message ids in `messages`; children only materialize at
  round end as ThreadMessage nodes ({"role":"assistant","text":…,"status":"Completed"}).
  A failed dispatch leaves no assistant child but writes the failure into `summary`.
"""

from __future__ import annotations

import asyncio
import json
import time
from dataclasses import dataclass, field

import aiohttp


class McpError(RuntimeError):
    pass


def parse_tool_result(body: str) -> str:
    """Extract the tool's text payload from a JSON or single-event SSE response body."""
    for line in body.splitlines():
        if line.startswith("data:"):
            body = line[5:].strip()
            break
    result = json.loads(body)
    if "error" in result:
        raise McpError(json.dumps(result["error"]))
    parts = result.get("result", {}).get("content", [])
    if result.get("result", {}).get("isError"):
        raise McpError(parts[0].get("text", "tool error") if parts else "tool error")
    return parts[0].get("text", "") if parts else json.dumps(result)


def extract_reply(thread_json: str, known_ids: set[str]) -> tuple[list[str], str | None]:
    """From a thread node's JSON: (new message ids, dispatch-failure summary or None).

    The summary is only a failure signal while no assistant reply exists AND nothing is
    pending — a completed round always carries its reply as a message child.
    """
    node = json.loads(thread_json)
    content = node.get("content") or {}
    ids = [m for m in content.get("messages", []) if m not in known_ids]
    pending = content.get("pendingUserMessages") or {}
    summary = content.get("summary")
    failure = summary if (not ids and not pending and summary) else None
    return ids, failure


@dataclass
class MemexThreads:
    base_url: str
    token: str
    namespace: str
    agent: str | None = None
    thread_idle_minutes: float = 5.0

    _session_id: str | None = field(default=None, init=False)
    _http: aiohttp.ClientSession | None = field(default=None, init=False)
    _thread_path: str | None = field(default=None, init=False)
    _known_ids: set[str] = field(default_factory=set, init=False)
    _last_used: float = field(default=0.0, init=False)

    async def _post(self, payload: dict) -> tuple[dict, str]:
        if self._http is None:
            self._http = aiohttp.ClientSession()
        headers = {
            "Authorization": f"Bearer {self.token}",
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if self._session_id:
            headers["Mcp-Session-Id"] = self._session_id
        async with self._http.post(
            f"{self.base_url}/mcp", json=payload, headers=headers,
            timeout=aiohttp.ClientTimeout(total=120),
        ) as response:
            body = await response.text()
            if response.status >= 400:
                raise McpError(f"HTTP {response.status}: {body[:300]}")
            return dict(response.headers), body

    async def _ensure_session(self) -> None:
        if self._session_id:
            return
        head, _ = await self._post({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                       "clientInfo": {"name": "memex-voice-gateway", "version": "0.1"}},
        })
        self._session_id = head.get("Mcp-Session-Id") or head.get("mcp-session-id")
        await self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})

    async def call(self, tool: str, arguments: dict) -> str:
        await self._ensure_session()
        try:
            _, body = await self._post({"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                                        "params": {"name": tool, "arguments": arguments}})
        except McpError:
            # One re-init retry: the server may have expired the session between wake words.
            self._session_id = None
            await self._ensure_session()
            _, body = await self._post({"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                                        "params": {"name": tool, "arguments": arguments}})
        return parse_tool_result(body)

    async def _snapshot_ids(self, thread_path: str) -> set[str]:
        node = json.loads(await self.call("get", {"path": f"@{thread_path}"}))
        return set((node.get("content") or {}).get("messages", []))

    async def ask(self, text: str) -> str:
        """Submit an utterance; returns the thread path. Reuses a recent thread for context."""
        fresh = self._thread_path and (time.monotonic() - self._last_used) < self.thread_idle_minutes * 60
        if fresh and self._thread_path:
            self._known_ids = await self._snapshot_ids(self._thread_path)
            args: dict = {"threadPath": self._thread_path, "message": text}
            if self.agent:
                args["agentName"] = self.agent
            await self.call("submit_message", args)
        else:
            args = {"namespacePath": self.namespace, "message": text}
            if self.agent:
                args["agentName"] = self.agent
            started = json.loads(await self.call("start_thread", args))
            self._thread_path = started["threadPath"]
            self._known_ids = set()
        self._last_used = time.monotonic()
        return self._thread_path  # type: ignore[return-value]

    async def await_reply(self, thread_path: str, budget_s: float,
                          poll_interval_s: float = 1.5) -> str | None:
        """Poll until an assistant reply (or a dispatch failure) lands; None on budget miss."""
        deadline = time.monotonic() + budget_s
        while time.monotonic() < deadline:
            thread_json = await self.call("get", {"path": f"@{thread_path}"})
            new_ids, failure = extract_reply(thread_json, self._known_ids)
            for message_id in new_ids:
                child = json.loads(await self.call("get", {"path": f"@{thread_path}/{message_id}"}))
                content = child.get("content") or {}
                self._known_ids.add(message_id)
                if content.get("role") == "assistant" and content.get("text"):
                    return content["text"]
            if failure:
                return failure
            await asyncio.sleep(poll_interval_s)
        return None

    async def close(self) -> None:
        if self._http is not None:
            await self._http.close()
            self._http = None
