"""Entry point: wire config → STT → threads → TTS → satellite, then run forever."""

from __future__ import annotations

import asyncio
import functools
import logging

import aiohttp

from . import stt
from .config import Config
from .pipeline import VoicePipeline
from .satellite import SatelliteLink
from .threads import MemexThreads
from .tts import PiperTts, TtsFileServer

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


async def run() -> None:
    cfg = Config.from_env()
    http = aiohttp.ClientSession()
    threads = MemexThreads(cfg.memex_url, cfg.memex_token, cfg.namespace,
                           agent=cfg.agent, thread_idle_minutes=cfg.thread_idle_minutes)
    piper = PiperTts(cfg.piper_bin, cfg.piper_voice)
    server = TtsFileServer(cfg.gateway_host, cfg.gateway_port)
    await server.start()

    async def speak(text: str) -> str:
        return server.add(await piper.synthesize(text))

    link: SatelliteLink | None = None

    async def announce(url: str, text: str) -> None:
        if link is not None:
            await link.announce(url, text)

    pipeline = VoicePipeline(
        transcribe=functools.partial(stt.transcribe, http, cfg.memex_url, cfg.memex_token,
                                     path=cfg.stt_path, language=cfg.stt_language,
                                     sample_rate=cfg.sample_rate),
        ask=threads.ask,
        await_reply=threads.await_reply,
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
        await threads.close()
        await http.close()


def main() -> None:
    asyncio.run(run())


if __name__ == "__main__":
    main()
