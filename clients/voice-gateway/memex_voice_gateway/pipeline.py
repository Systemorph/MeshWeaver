"""The voice round, orchestrated: audio → STT → thread → reply (now or announced later).

Measured reality on memex (2026-08-18): a thread round takes ~60–70 s wall clock even for a
trivial question — the time is dispatch/startup, not generation. So the pipeline's PRIMARY
shape is: try to answer within `reply_budget_s`; on a miss, speak a short holding phrase, end
the voice run (freeing the device), keep polling in the background, and deliver the real
answer as an ANNOUNCEMENT when it lands. If the platform's dispatch latency improves, the
same code simply starts answering inline.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from typing import Awaitable, Callable

logger = logging.getLogger(__name__)

# Narrow protocol the satellite link implements — kept as callables so the pipeline is
# unit-testable with plain fakes and never imports aioesphomeapi.
SpeakFn = Callable[[str], Awaitable[str]]          # text -> served WAV url
TranscribeFn = Callable[[bytes], Awaitable[str]]   # pcm  -> transcript


@dataclass
class RoundResult:
    transcript: str
    reply: str | None      # None = budget missed, announcement pending
    tts_url: str | None


class VoicePipeline:
    def __init__(
        self,
        *,
        transcribe: TranscribeFn,
        ask: Callable[[str], Awaitable[str]],                 # text -> thread path
        await_reply: Callable[[str, float], Awaitable[str | None]],
        speak: SpeakFn,
        announce: Callable[[str, str], Awaitable[None]],      # (url, text) -> None
        reply_budget_s: float,
        announce_budget_s: float,
        hold_phrase: str,
        error_phrase: str,
    ) -> None:
        self._transcribe = transcribe
        self._ask = ask
        self._await_reply = await_reply
        self._speak = speak
        self._announce = announce
        self._reply_budget_s = reply_budget_s
        self._announce_budget_s = announce_budget_s
        self._hold_phrase = hold_phrase
        self._error_phrase = error_phrase
        self._background: set[asyncio.Task] = set()

    async def run(self, pcm: bytes) -> RoundResult:
        """One voice round. Returns what to speak NOW; may schedule a later announcement."""
        try:
            transcript = await self._transcribe(pcm)
        except Exception:
            logger.exception("STT failed")
            return RoundResult("", self._error_phrase, await self._try_speak(self._error_phrase))
        if not transcript:
            return RoundResult("", None, None)  # silence / non-speech: end quietly

        try:
            thread_path = await self._ask(transcript)
            reply = await self._await_reply(thread_path, self._reply_budget_s)
        except Exception:
            logger.exception("thread submission failed")
            return RoundResult(transcript, self._error_phrase,
                               await self._try_speak(self._error_phrase))

        if reply is not None:
            return RoundResult(transcript, reply, await self._try_speak(reply))

        # Budget missed: hold phrase now, real answer as an announcement when it lands.
        task = asyncio.create_task(self._announce_when_ready(thread_path))
        self._background.add(task)
        task.add_done_callback(self._background.discard)
        return RoundResult(transcript, None, await self._try_speak(self._hold_phrase))

    async def _announce_when_ready(self, thread_path: str) -> None:
        try:
            reply = await self._await_reply(thread_path, self._announce_budget_s)
            if reply is None:
                reply = self._error_phrase
            url = await self._speak(reply)
            await self._announce(url, reply)
        except Exception:
            logger.exception("late announcement failed")

    async def _try_speak(self, text: str) -> str | None:
        try:
            return await self._speak(text)
        except Exception:
            logger.exception("TTS failed")
            return None
