"""Entry point: wire config → STT → brain → TTS → satellite, then run forever."""

from __future__ import annotations

import asyncio
import functools
import logging
import re

import aiohttp

from . import stt
from .config import Config, phrases_for
from .ollama import OllamaBrain
from .pipeline import VoicePipeline
from .router import BrainRouter
from .satellite import SatelliteLink
from .threads import MemexThreads
from .tts import PiperTts, SayTts, TtsFileServer, split_wav

SENTENCE_END = re.compile(r"[.!?…][\"')\]]*\s")

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


DIALECT_SUFFIX = (
    " Antworte auf Schweizerdeutsch (Züritüütsch), schreibe den Dialekt phonetisch aus."
)


def make_router(cfg: Config) -> BrainRouter:
    """One router over all configured brains — PORTALS entries, else the legacy single brain."""
    from .config import phrases_for
    from .ollama import SYSTEM_PROMPT
    phrases = phrases_for(cfg.stt_language)
    base_prompt = SYSTEM_PROMPT
    if cfg.system_prompt_file:
        try:
            with open(cfg.system_prompt_file) as f:
                base_prompt = f.read().strip() or SYSTEM_PROMPT
        except OSError:
            logging.getLogger(__name__).warning(
                "SYSTEM_PROMPT_FILE %s not readable — using the built-in prompt",
                cfg.system_prompt_file)
    system_prompt = base_prompt + (DIALECT_SUFFIX if cfg.speak_dialect else "")

    def ollama(entry: dict) -> OllamaBrain:
        return OllamaBrain(entry.get("url", cfg.ollama_url), entry.get("model", cfg.ollama_model),
                           idle_minutes=cfg.thread_idle_minutes, system_prompt=system_prompt,
                           location=cfg.location)

    def memex(entry: dict) -> MemexThreads:
        return MemexThreads(entry["url"].rstrip("/"), entry["token"], entry["namespace"],
                            agent=entry.get("agent", cfg.agent),
                            thread_idle_minutes=cfg.thread_idle_minutes,
                            verify_tls=not entry.get("insecure", False))

    home = None
    if cfg.ha_url and cfg.ha_token:
        from .homeassistant import HomeAssistant
        home = HomeAssistant(cfg.ha_url, cfg.ha_token)

    if cfg.portals:
        brains = {e["name"]: (ollama(e) if e.get("kind") == "ollama" else memex(e))
                  for e in cfg.portals}
        active = next((e["name"] for e in cfg.portals if e.get("default")), cfg.portals[0]["name"])
        # A portal entry's "agents" pins agents to the portal that HOSTS them (list of
        # names, or name → spoken-roster description) — e.g. the ExecutiveAssistant on the
        # local mesh: {"name":"mac", …, "agents": {"ExecutiveAssistant": "email"}}.
        from .ollama import DEFAULT_AGENTS, HOME_TOOL, MESH_TOOL, MESH_TOOLS
        roster = dict(DEFAULT_AGENTS)
        agent_homes: dict[str, str] = {}
        for entry in cfg.portals:
            declared = entry.get("agents") or {}
            if isinstance(declared, list):
                declared = {name: roster.get(name, "specialist work") for name in declared}
            for name, description in declared.items():
                roster[name] = description
                agent_homes[name] = entry["name"]
        router = BrainRouter(brains, active, phrases=phrases, home=home,
                             agent_homes=agent_homes)
        router.allow_destructive = cfg.allow_destructive
        # The local brain TRIAGES: quick lookups through the normal tools (search_mesh,
        # get_node, mesh_tool = full MCP, home_assistant), real work through
        # delegate_to_memex — a mesh thread the context sticks to.
        for brain in brains.values():
            if isinstance(brain, OllamaBrain):
                brain.delegator = router.delegate_task
                brain.tool_runner = router.run_tool
                brain.tools = (list(MESH_TOOLS) + [MESH_TOOL]
                               + ([HOME_TOOL] if home is not None else []))
                brain.agents = roster
        # The SESSION COOKIE: resume the speaker's context within the TTL, persist on
        # every context mutation — an MCP-session-style continuity for the voice.
        from .session import SpokenSession
        session = SpokenSession(cfg.session_file, cfg.session_ttl_h)
        router.restore(session.load())
        router.on_change = lambda: session.save(**router.session_state())
        return router
    if cfg.brain == "ollama":
        return BrainRouter({"lokal": ollama({})}, "lokal", phrases=phrases, home=home)
    return BrainRouter({"memex": memex({"url": cfg.memex_url, "token": cfg.memex_token,
                                        "namespace": cfg.namespace})}, "memex",
                       phrases=phrases, home=home)


def make_tts(cfg: Config):
    if cfg.tts_engine == "say":
        return SayTts(cfg.say_voice)
    return PiperTts(cfg.piper_bin, cfg.piper_voice)


async def run() -> None:
    cfg = Config.from_env()
    http = aiohttp.ClientSession()
    router = make_router(cfg)
    # The prompt comes FROM THE PLATFORM: @Voice/Prompt (the package's standard) seeds
    # @{user}/Voice/Prompt (the user's editable copy), which wins; SYSTEM_PROMPT_FILE
    # stays the strongest override for local experiments. CAPABILITIES are then injected
    # by what is actually installed — radio stations, smart home — never hard-coded.
    from .ollama import SYSTEM_PROMPT
    prompt_text = SYSTEM_PROMPT
    if not cfg.system_prompt_file:
        try:
            prompt_text = (await router.sync_system_prompt(SYSTEM_PROMPT)).strip() or SYSTEM_PROMPT
        except Exception:
            pass
    try:
        await router.refresh_stations()
    except Exception:
        logging.getLogger(__name__).warning("station refresh failed", exc_info=True)
    capabilities = []
    if router.stations:
        names = ", ".join(name for name, _ in router.stations.values())
        capabilities.append(f"Radio is available ({names}) and handled for you "
                            "automatically — never claim you cannot play radio.")
    if cfg.ha_url and cfg.ha_token:
        capabilities.append("The smart home is reachable through the home_assistant tool.")
    router.apply_system_prompt(prompt_text
                               + (DIALECT_SUFFIX if cfg.speak_dialect else "")
                               + ("\n\n" + " ".join(capabilities) if capabilities else ""))
    tts = make_tts(cfg)
    server = TtsFileServer(cfg.gateway_host, cfg.gateway_port)
    await server.start()

    async def speak(text: str) -> str:
        return server.add(await tts.synthesize(text))

    async def stream_speak(chunks) -> str:
        """Speak a live text stream: open a chunked-WAV session (the device starts playing
        the URL immediately), synthesize sentence-by-sentence as pieces arrive, close on end.
        Both `say` (forced LEI16@22050) and the piper medium voices emit 22.05 kHz."""
        stream, url = server.open_stream(sample_rate=16000)

        async def feed() -> None:
            buffer = ""

            async def speak_piece(text: str) -> None:
                pcm, _ = split_wav(await tts.synthesize(text))
                await stream.push(pcm)

            try:
                async for piece in chunks:
                    buffer += piece
                    while (match := SENTENCE_END.search(buffer)) is not None:
                        sentence, buffer = buffer[:match.end()].strip(), buffer[match.end():]
                        if sentence:
                            await speak_piece(sentence)
                if buffer.strip():
                    await speak_piece(buffer.strip())
            except Exception:
                logging.getLogger(__name__).exception("streaming synthesis failed")
            finally:
                await stream.close()

        asyncio.create_task(feed())
        return url

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
        stream_text=router.stream_text if cfg.stream_tts else None,
        stream_speak=stream_speak if cfg.stream_tts else None,
        hold_phrase_for=router.describe_hold,
        answer_to_template=phrases_for(cfg.stt_language)["answer_to"],
        drain_delegations=router.drain_delegations,
        deliver=router.deliver_answer if cfg.announce_mode == "signal" else None,
    )
    link = SatelliteLink(cfg, pipeline)
    router.player = link.play_media   # deterministic music: "spiel Musik/Radio …"
    # Pre-warm the local model so the FIRST question after a quiet hour answers instantly.
    for brain in getattr(router, "_brains", {}).values():
        if isinstance(brain, OllamaBrain):
            asyncio.ensure_future(brain.warmup())

    async def barge_in() -> None:
        await server.interrupt_streams()
        await link.stop_playback()

    link.on_wake = barge_in
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
