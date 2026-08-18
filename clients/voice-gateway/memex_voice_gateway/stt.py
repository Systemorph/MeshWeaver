"""Speech-to-text against the mesh's centralized Whisper container.

The portal exposes `POST /api/speech/transcribe` (Bearer auth, multipart, ≤25 MB) in front of
the cluster-internal whisper.cpp server — see Doc/Architecture/CentralizedSpeech. We wrap the
raw satellite PCM in a WAV header and post it; the reply is `{"text": …, "language": …}`.
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
    base_url: str,
    token: str,
    pcm: bytes,
    *,
    path: str = "/api/speech/transcribe",
    language: str = "de",
    sample_rate: int = 16000,
) -> str:
    """Returns the transcript text; raises on transport errors, empty text on silence."""
    form = aiohttp.FormData()
    form.add_field("file", wav_from_pcm(pcm, sample_rate), filename="utterance.wav",
                   content_type="audio/wav")
    form.add_field("language", language)
    async with session.post(
        f"{base_url}{path}", data=form,
        headers={"Authorization": f"Bearer {token}"},
        timeout=aiohttp.ClientTimeout(total=60),
    ) as response:
        response.raise_for_status()
        payload = await response.json(content_type=None)
        return (payload.get("text") or "").strip()
