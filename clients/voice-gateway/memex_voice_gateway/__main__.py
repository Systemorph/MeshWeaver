"""Entry point: wire config → STT → brain → TTS → satellite, then run forever."""

from __future__ import annotations

import asyncio
import functools
import logging

import aiohttp

from . import stt
from .config import Config
from .ollama import OllamaBrain
from .pipeline import VoicePipeline
from .satellite import SatelliteLink
from .threads import MemexThreads
from .tts import PiperTts, SayTts, TtsFileServer

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


def make_brain(cfg: Config):
    """Returns (ask, await_reply, close) for the configured brain."""
    if cfg.brain == "ollama":
        brain = OllamaBrain(cfg.ollama_url, cfg.ollama_model,
                            idle_minutes=cfg.thread_idle_minutes)
        return brain.ask, brain.await_reply, brain.close
    threads = MemexThreads(cfg.memex_url, cfg.memex_token, cfg.namespace,
                           agent=cfg.agent, thread_idle_minutes=cfg.thread_idle_minutes)
    return threads.ask, threads.await_reply, threads.close


def make_tts(cfg: Config):
    if cfg.tts_engine == "say":
        return SayTts(cfg.say_voice)
    return PiperTts(cfg.piper_bin, cfg.piper_voice)


async def run() -> None:
    cfg = Config.from_env()
    http = aiohttp.ClientSession()
    ask, await_reply, close_brain = make_brain(cfg)
    tts = make_tts(cfg)
    server = TtsFileServer(cfg.gateway_host, cfg.gateway_port)
    await server.start()

    async def speak(text: str) -> str:
        return server.add(await tts.synthesize(text))

    link: SatelliteLink | None = None

    async def announce(url: str, text: str) -> None:
        if link is not None:
            await link.announce(url, text)

    pipeline = VoicePipeline(
        transcribe=functools.partial(stt.transcribe, http, cfg.stt_url, cfg.stt_token,
                                     language=cfg.stt_language, sample_rate=cfg.sample_rate),
        ask=ask,
        await_reply=await_reply,
        speak=speak,
        announce=announce,
        reply_budget_s=cfg.reply_budget_s,
        announce_budget_s=cfg.announce_budget_s,
        hold_phrase=cfg.hold_phrase,
        error_phrase=cfg.error_phrase,
    )
    link = SatelliteLink(cfg, pipeline)
    try:
        await link.run_forever()
    finally:
        await server.stop()
        await close_brain()
        await http.close()


def main() -> None:
    asyncio.run(run())


if __name__ == "__main__":
    main()
