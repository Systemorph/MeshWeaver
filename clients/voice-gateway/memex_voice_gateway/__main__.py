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
from .router import BrainRouter
from .satellite import SatelliteLink
from .threads import MemexThreads
from .tts import PiperTts, SayTts, TtsFileServer

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


def make_router(cfg: Config) -> BrainRouter:
    """One router over all configured brains — PORTALS entries, else the legacy single brain."""
    def ollama(entry: dict) -> OllamaBrain:
        return OllamaBrain(entry.get("url", cfg.ollama_url), entry.get("model", cfg.ollama_model),
                           idle_minutes=cfg.thread_idle_minutes)

    def memex(entry: dict) -> MemexThreads:
        return MemexThreads(entry["url"].rstrip("/"), entry["token"], entry["namespace"],
                            agent=entry.get("agent", cfg.agent),
                            thread_idle_minutes=cfg.thread_idle_minutes)

    if cfg.portals:
        brains = {e["name"]: (ollama(e) if e.get("kind") == "ollama" else memex(e))
                  for e in cfg.portals}
        active = next((e["name"] for e in cfg.portals if e.get("default")), cfg.portals[0]["name"])
        return BrainRouter(brains, active)
    if cfg.brain == "ollama":
        return BrainRouter({"lokal": ollama({})}, "lokal")
    return BrainRouter({"memex": memex({"url": cfg.memex_url, "token": cfg.memex_token,
                                        "namespace": cfg.namespace})}, "memex")


def make_tts(cfg: Config):
    if cfg.tts_engine == "say":
        return SayTts(cfg.say_voice)
    return PiperTts(cfg.piper_bin, cfg.piper_voice)


async def run() -> None:
    cfg = Config.from_env()
    http = aiohttp.ClientSession()
    router = make_router(cfg)
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
        ask=router.ask,
        await_reply=router.await_reply,
        speak=speak,
        announce=announce,
        reply_budget_s=cfg.reply_budget_s,
        announce_budget_s=cfg.announce_budget_s,
        hold_phrase=cfg.hold_phrase,
        error_phrase=cfg.error_phrase,
        command_handler=router.handle_command,
    )
    link = SatelliteLink(cfg, pipeline)
    try:
        await link.run_forever()
    finally:
        await server.stop()
        await router.close()
        await http.close()


def main() -> None:
    asyncio.run(run())


if __name__ == "__main__":
    main()
