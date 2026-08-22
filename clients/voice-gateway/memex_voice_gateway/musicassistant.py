"""Music Assistant — the household's OWN music tooling, spoken to over its WebSocket API.

MA is the executor for everything beyond plain radio-station URLs: its providers (Spotify,
Apple Music, Radio Browser, …) resolve songs, artists and stations, and its players play
them. This client only RESOLVES and DELEGATES — it never re-implements a music service.
Configure with MA_URL + an API token (created in MA's settings after login, stored in the
keychain as `music-assistant-token`); absent, music stays radio-URLs-only.
"""

from __future__ import annotations

import itertools
import json
import logging

import aiohttp

logger = logging.getLogger(__name__)


class MusicAssistant:
    def __init__(self, base_url: str, token: str) -> None:
        self.base_url = base_url.rstrip("/")
        self._token = token
        self._http: aiohttp.ClientSession | None = None
        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._ids = itertools.count(1)

    async def _ensure(self) -> None:
        if self._ws is not None and not self._ws.closed:
            return
        if self._http is None:
            self._http = aiohttp.ClientSession()
        self._ws = await self._http.ws_connect(f"{self.base_url}/ws", timeout=10)
        await self._ws.receive(timeout=5)          # server-info greeting
        reply = await self._cmd_raw("auth", token=self._token)
        if reply.get("error_code"):
            raise RuntimeError(f"Music Assistant auth failed: {reply.get('details')}")

    async def _cmd_raw(self, command: str, **args) -> dict:
        message_id = str(next(self._ids))
        assert self._ws is not None
        await self._ws.send_json({"message_id": message_id, "command": command, "args": args})
        while True:
            msg = json.loads((await self._ws.receive(timeout=30)).data)
            if msg.get("message_id") == message_id:
                return msg

    async def cmd(self, command: str, **args):
        await self._ensure()
        reply = await self._cmd_raw(command, **args)
        if reply.get("error_code"):
            raise RuntimeError(f"{command}: {reply.get('details')}")
        return reply.get("result")

    async def search(self, query: str, media_types: list[str] | None = None,
                     limit: int = 8) -> dict:
        """MA-wide search across all configured providers. Returns MA's results object
        (keys per media type: tracks, artists, radio, …)."""
        return await self.cmd("music/search", search_query=query,
                              media_types=media_types or ["track", "radio", "playlist"],
                              limit=limit)

    async def players(self) -> list[dict]:
        return list(await self.cmd("players/all") or [])

    async def play(self, uri: str, queue_id: str) -> None:
        """Hand the resolved item to MA's own playback (its providers stream it)."""
        await self.cmd("player_queues/play_media", queue_id=queue_id, media=uri)

    async def close(self) -> None:
        if self._ws is not None:
            await self._ws.close()
            self._ws = None
        if self._http is not None:
            await self._http.close()
            self._http = None
