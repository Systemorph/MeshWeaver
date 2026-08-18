"""Text-to-speech (Piper) plus the tiny HTTP server the satellite fetches audio from.

The ESPHome voice pipeline delivers TTS as a URL in the TTS_END event; the device's media
player then streams it. So the gateway synthesizes with the Piper CLI and serves the WAV from
memory for a short TTL. High German out (`de_DE` voices) — the assistant *understands*
Schwiizerdütsch (the Whisper fine-tune transcribes it to Standard German) and answers in
Standard German.
"""

from __future__ import annotations

import asyncio
import secrets
import time

from aiohttp import web


class PiperTts:
    def __init__(self, piper_bin: str, voice_path: str) -> None:
        self.piper_bin = piper_bin
        self.voice_path = voice_path

    async def synthesize(self, text: str) -> bytes:
        process = await asyncio.create_subprocess_exec(
            self.piper_bin, "--model", self.voice_path, "--output_file", "-",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        wav, err = await process.communicate(text.encode())
        if process.returncode != 0 or not wav:
            raise RuntimeError(f"piper failed ({process.returncode}): {err.decode()[:300]}")
        return wav


class TtsFileServer:
    """Serves synthesized WAVs at /tts/{id}.wav from memory, expiring after `ttl_s`."""

    def __init__(self, host: str, port: int, ttl_s: float = 300.0) -> None:
        self.host = host
        self.port = port
        self.ttl_s = ttl_s
        self._files: dict[str, tuple[float, bytes]] = {}
        self._runner: web.AppRunner | None = None

    def add(self, wav: bytes) -> str:
        now = time.monotonic()
        self._files = {k: v for k, v in self._files.items() if now - v[0] < self.ttl_s}
        file_id = secrets.token_urlsafe(8)
        self._files[file_id] = (now, wav)
        return f"http://{self.host}:{self.port}/tts/{file_id}.wav"

    async def _handle(self, request: web.Request) -> web.Response:
        entry = self._files.get(request.match_info["id"])
        if entry is None:
            return web.Response(status=404)
        return web.Response(body=entry[1], content_type="audio/wav")

    async def start(self) -> None:
        app = web.Application()
        app.router.add_get("/tts/{id}.wav", self._handle)
        self._runner = web.AppRunner(app)
        await self._runner.setup()
        site = web.TCPSite(self._runner, "0.0.0.0", self.port)
        await site.start()

    async def stop(self) -> None:
        if self._runner is not None:
            await self._runner.cleanup()
            self._runner = None
