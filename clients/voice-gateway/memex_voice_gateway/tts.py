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


class SayTts:
    """macOS's built-in `say` — zero-setup local TTS (voice e.g. "Anna" for German).

    Writes a 16-bit 22.05 kHz WAV to a temp file (say has no stdout mode) and returns
    its bytes; the temp file is removed immediately.
    """

    def __init__(self, voice: str = "Anna") -> None:
        self.voice = voice

    @staticmethod
    def build_args(voice: str, out_path: str) -> list[str]:
        return ["say", "-v", voice, "-o", out_path, "--data-format=LEI16@22050", "-f", "-"]

    async def synthesize(self, text: str) -> bytes:
        import os
        import tempfile
        fd, path = tempfile.mkstemp(suffix=".wav")
        os.close(fd)
        try:
            process = await asyncio.create_subprocess_exec(
                *self.build_args(self.voice, path),
                stdin=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            _, err = await process.communicate(text.encode())
            if process.returncode != 0:
                raise RuntimeError(f"say failed ({process.returncode}): {err.decode()[:200]}")
            with open(path, "rb") as f:
                return f.read()
        finally:
            try:
                os.unlink(path)
            except OSError:
                pass


def split_wav(wav: bytes) -> tuple[bytes, int]:
    """(pcm, sample_rate) out of a RIFF/WAVE blob — walks the chunks, no fixed 44-byte guess."""
    if wav[:4] != b"RIFF" or wav[8:12] != b"WAVE":
        raise ValueError("not a RIFF/WAVE blob")
    rate, offset = 16000, 12
    while offset + 8 <= len(wav):
        cid = wav[offset:offset + 4]
        size = int.from_bytes(wav[offset + 4:offset + 8], "little")
        if cid == b"fmt ":
            rate = int.from_bytes(wav[offset + 12:offset + 16], "little")
        elif cid == b"data":
            return wav[offset + 8:offset + 8 + size], rate
        offset += 8 + size + (size & 1)
    raise ValueError("no data chunk")


def streaming_wav_header(sample_rate: int, channels: int = 1) -> bytes:
    """A WAV header that promises 'read until EOF' — sizes maxed, for chunked streaming."""
    import struct
    byte_rate = sample_rate * channels * 2
    header = b"RIFF" + struct.pack("<I", 0xFFFFFFFF) + b"WAVE"
    header += b"fmt " + struct.pack("<IHHIIHH", 16, 1, channels, sample_rate, byte_rate, channels * 2, 16)
    header += b"data" + struct.pack("<I", 0xFFFFFFFF - 44)
    return header


class StreamingWav:
    """One in-flight streamed utterance: PCM sentences arrive on a queue while the device
    is already playing the URL. `close()` ends the stream (and the playback)."""

    def __init__(self, sample_rate: int) -> None:
        self.sample_rate = sample_rate
        self.queue: asyncio.Queue[bytes | None] = asyncio.Queue()

    async def push(self, pcm: bytes) -> None:
        await self.queue.put(pcm)

    async def close(self) -> None:
        await self.queue.put(None)


class TtsFileServer:
    """Serves synthesized WAVs at /tts/{id}.wav from memory, expiring after `ttl_s` —
    and chunked live streams at /tts-stream/{id}.wav (see StreamingWav)."""

    def __init__(self, host: str, port: int, ttl_s: float = 300.0) -> None:
        self.host = host
        self.port = port
        self.ttl_s = ttl_s
        self._files: dict[str, tuple[float, bytes]] = {}
        self._streams: dict[str, StreamingWav] = {}
        self._runner: web.AppRunner | None = None

    def open_stream(self, sample_rate: int) -> tuple[StreamingWav, str]:
        stream = StreamingWav(sample_rate)
        stream_id = secrets.token_urlsafe(8)
        self._streams[stream_id] = stream
        return stream, f"http://{self.host}:{self.port}/tts-stream/{stream_id}.wav"

    async def _handle_stream(self, request: web.Request) -> web.StreamResponse:
        stream = self._streams.pop(request.match_info["id"], None)
        if stream is None:
            return web.Response(status=404)
        response = web.StreamResponse(headers={"Content-Type": "audio/wav"})
        response.enable_chunked_encoding()
        await response.prepare(request)
        await response.write(streaming_wav_header(stream.sample_rate))
        while (pcm := await stream.queue.get()) is not None:
            await response.write(pcm)
        await response.write_eof()
        return response

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
        app.router.add_get("/tts-stream/{id}.wav", self._handle_stream)
        self._runner = web.AppRunner(app)
        await self._runner.setup()
        site = web.TCPSite(self._runner, "0.0.0.0", self.port)
        await site.start()

    async def stop(self) -> None:
        if self._runner is not None:
            await self._runner.cleanup()
            self._runner = None
