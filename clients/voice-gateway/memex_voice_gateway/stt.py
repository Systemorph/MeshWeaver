"""Speech-to-text over HTTP — one client for both hosting modes.

The mesh's `POST /api/speech/transcribe` (Bearer auth, in front of the cluster-internal
whisper.cpp — Doc/Architecture/CentralizedSpeech) and a LOCAL whisper.cpp server's
`POST /inference` speak the same multipart contract: `file` + `language`
(+ `response_format=json`, which the mesh accepts and ignores) → `{"text": …}`.
So the gateway just points STT_URL at whichever one it uses.
"""

from __future__ import annotations

import struct

import aiohttp


def wav_from_pcm(pcm: bytes, sample_rate: int = 16000, channels: int = 1) -> bytes:
    """Minimal RIFF/WAVE wrapper for 16-bit PCM."""
    byte_rate = sample_rate * channels * 2
    block_align = channels * 2
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE"
    header += b"fmt " + struct.pack("<IHHIIHH", 16, 1, channels, sample_rate, byte_rate, block_align, 16)
    header += b"data" + struct.pack("<I", len(pcm))
    return header + pcm


async def transcribe(
    session: aiohttp.ClientSession,
    url: str,
    token: str | None,
    pcm: bytes,
    *,
    language: str = "de",
    sample_rate: int = 16000,
) -> str:
    """Returns the transcript text; raises on transport errors, empty text on silence."""
    form = aiohttp.FormData()
    form.add_field("file", wav_from_pcm(pcm, sample_rate), filename="utterance.wav",
                   content_type="audio/wav")
    form.add_field("language", language)
    form.add_field("response_format", "json")
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    async with session.post(url, data=form, headers=headers,
                            timeout=aiohttp.ClientTimeout(total=60)) as response:
        response.raise_for_status()
        payload = await response.json(content_type=None)
        return (payload.get("text") or "").strip()
