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


import re

# Whisper annotates non-speech as *Musik*, [Lachen], (Applaus) … — an utterance that is ONLY
# such annotation is ambient noise, never a question. Answering it is how a TV becomes a
# conversation partner through the WAKE path (observed: false wakes every ~10s, each round
# transcribing its own or the TV's audio).
_NOISE_ONLY = re.compile(r"^[\s\*\[\(]*[^\w]*[\wäöüéè' -]{0,40}?"
                         r"(musik|lachen|applaus|geräusch|räusper|husten|"
                         r"music|laughter|applause|noise|coughs?)"
                         r"[^\w]*[\s\*\]\)]*$", re.IGNORECASE)


def is_noise_transcript(transcript: str) -> bool:
    stripped = transcript.strip()
    if not stripped:
        return True
    if stripped.startswith(("*", "[", "(")) and stripped.endswith(("*", "]", ")")):
        return True
    return _NOISE_ONLY.match(stripped) is not None


@dataclass
class RoundResult:
    transcript: str
    reply: str | None      # None = budget missed, announcement pending
    tts_url: str | None
    interrupt: bool = False   # a spoken "stop": kill current playback, say nothing
    noise: bool = False       # ambient/non-speech round: discarded without an answer


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
        command_handler: Callable[[str], Awaitable[str | None]] | None = None,
        stream_text: Callable[[str], object | None] | None = None,
        stream_speak: Callable[[object], Awaitable[str]] | None = None,
        hold_phrase_for: Callable[[str], str] | None = None,
        answer_to_template: str | None = None,
        drain_delegations: Callable[[], list] | None = None,
    ) -> None:
        self._drain_delegations = drain_delegations
        self._command_handler = command_handler
        self._stream_text = stream_text
        self._stream_speak = stream_speak
        self._hold_phrase_for = hold_phrase_for
        self._answer_to_template = answer_to_template
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
        if is_noise_transcript(transcript):
            logger.info("noise round discarded: %r", transcript[:60])
            return RoundResult(transcript, None, None, noise=True)

        # Control commands (e.g. "wechsle zu systemorph") are handled deterministically,
        # before any brain sees the text — the handler's confirmation is spoken directly.
        if self._command_handler is not None:
            confirmation = await self._command_handler(transcript)
            if confirmation == "":
                return RoundResult(transcript, None, None, interrupt=True)  # spoken "stop"
            if confirmation is not None:
                return RoundResult(transcript, confirmation,
                                   await self._try_speak(confirmation))

        try:
            thread_path = await self._ask(transcript)

            # Streaming path: a brain that streams text + a streaming TTS session mean the
            # device starts PLAYING while generation is still running — the URL blocks until
            # the first spoken sentence lands, then sentences flow as the model writes them.
            if self._stream_text is not None and self._stream_speak is not None:
                chunks = self._stream_text(thread_path)
                if chunks is not None:
                    url = await self._stream_speak(chunks)
                    logger.info("round: %r → streaming reply", transcript)
                    return RoundResult(transcript, None, url)

            reply = await self._await_reply(thread_path, self._reply_budget_s)
        except Exception:
            logger.exception("thread submission failed")
            return RoundResult(transcript, self._error_phrase,
                               await self._try_speak(self._error_phrase))
        logger.info("round: %r → %r", transcript, (reply or "")[:120])

        # The local brain may have TRIAGED work to the mesh mid-round: schedule the
        # announcement of each delegated thread's answer, attributed to its task.
        if self._drain_delegations is not None:
            for handle, task in self._drain_delegations():
                delegated = asyncio.create_task(self._announce_when_ready(handle, task))
                self._background.add(delegated)
                delegated.add_done_callback(self._background.discard)

        if reply is not None:
            return RoundResult(transcript, reply, await self._try_speak(reply))

        # Budget missed: acknowledge the SUBMISSION (naming the portal — answers can be
        # highly async), and deliver the real answer later, prefixed with which question it
        # belongs to, so out-of-order arrivals stay attributable.
        task = asyncio.create_task(self._announce_when_ready(thread_path, transcript))
        self._background.add(task)
        task.add_done_callback(self._background.discard)
        hold = (self._hold_phrase_for(thread_path) if self._hold_phrase_for
                else self._hold_phrase)
        return RoundResult(transcript, None, await self._try_speak(hold))

    async def _announce_when_ready(self, thread_path: str, transcript: str) -> None:
        try:
            reply = await self._await_reply(thread_path, self._announce_budget_s)
            if reply is None:
                reply = self._error_phrase
            if self._answer_to_template:
                question = transcript if len(transcript) <= 60 else transcript[:57] + "…"
                reply = f"{self._answer_to_template.format(question=question)} {reply}"
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
