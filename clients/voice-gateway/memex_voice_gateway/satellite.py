"""The LAN link to the ESPHome voice satellite (e.g. FutureProofHomes Satellite1).

Speaks the same aioesphomeapi surface Home Assistant uses: `subscribe_voice_assistant`
(API-audio mode — the handler returns port 0), `send_voice_assistant_event` for the
RUN/STT/TTS event sequence that drives the device's LEDs and player, and
`send_voice_assistant_announcement_await_response` (with a media-player fallback) for
answers that outlive the voice run.

The device is the SERVER — this gateway dials it on the LAN, so it can run beside a Home
Assistant that owns the satellite's sensors and media, as long as only ONE of them
subscribes as the voice assistant (leave the device unassigned in HA's Assist).
"""

from __future__ import annotations

import asyncio
import logging

from aioesphomeapi import APIClient
from aioesphomeapi.model import VoiceAssistantAudioSettings, VoiceAssistantEventType as Event

from .config import Config
from .pipeline import VoicePipeline
from .vad import Endpointer

logger = logging.getLogger(__name__)


class SatelliteLink:
    def __init__(self, cfg: Config, pipeline: VoicePipeline) -> None:
        self.cfg = cfg
        self.pipeline = pipeline
        self.client = APIClient(
            cfg.satellite_host, cfg.satellite_port,
            cfg.satellite_password, noise_psk=cfg.satellite_psk,
        )
        self._endpointer: Endpointer | None = None
        self._utterance_done = asyncio.Event()
        self._media_player_key: int | None = None
        self._round_task: asyncio.Task | None = None

    # --- lifecycle -------------------------------------------------------------------

    async def run_forever(self) -> None:
        backoff = 2.0
        while True:
            try:
                await self.client.connect(login=True)
                info = await self.client.device_info()
                logger.info("connected to %s (%s)", info.name, self.cfg.satellite_host)
                await self._find_media_player()
                unsubscribe = self.client.subscribe_voice_assistant(
                    handle_start=self._handle_start,
                    handle_stop=self._handle_stop,
                    handle_audio=self._handle_audio,
                )
                await self._ensure_wake_word()
                backoff = 2.0
                try:
                    while self.client._connection is not None:  # noqa: SLF001 — liveness probe
                        await asyncio.sleep(5)
                finally:
                    unsubscribe()
            except Exception as error:
                logger.warning("satellite link lost (%s); retrying in %.0fs", error, backoff)
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60)

    async def _find_media_player(self) -> None:
        entities, _ = await self.client.list_entities_services()
        for entity in entities:
            if type(entity).__name__ == "MediaPlayerInfo":
                self._media_player_key = entity.key
                return

    async def _ensure_wake_word(self) -> None:
        """Activate a wake word if none is active.

        Modern ESPHome voice firmware hands wake-word selection to the API client (HA
        normally does this), so a factory-fresh device can sit with NO active wake word —
        it hears nothing and no voice run ever starts. The configuration request is only
        answered on the connection that holds the voice-assistant subscription, so this
        must run here, not from a side channel.
        """
        try:
            conf = await self.client.get_voice_assistant_configuration(timeout=10)
        except Exception:
            logger.info("device does not answer wake-word configuration (older firmware) — skipping")
            return
        available = [w.id for w in conf.available_wake_words]
        active = list(conf.active_wake_words)
        logger.info("wake words available=%s active=%s", available, active)
        if active or not available:
            return
        wanted = self.cfg.wake_word
        chosen = wanted if wanted in available else available[0]
        if wanted and wanted not in available:
            logger.warning("WAKE_WORD %r not on the device; using %r", wanted, chosen)
        await self.client.set_voice_assistant_configuration(active_wake_words=[chosen])
        logger.info("activated wake word %r", chosen)

    # --- voice assistant callbacks ----------------------------------------------------

    async def _handle_start(
        self, conversation_id: str, flags: int,
        audio_settings: VoiceAssistantAudioSettings, wake_word_phrase: str | None,
    ) -> int:
        logger.info("wake (%s)", wake_word_phrase or "button")
        self._endpointer = Endpointer(
            sample_rate=self.cfg.sample_rate,
            silence_ms=self.cfg.silence_ms,
            max_utterance_s=self.cfg.max_utterance_s,
            min_utterance_s=self.cfg.min_utterance_s,
        )
        self._utterance_done.clear()
        self._round_task = asyncio.create_task(self._run_round())
        return 0  # 0 = stream microphone audio over the API connection (no UDP)

    async def _handle_audio(self, data: bytes, data2: bytes | None = None) -> None:
        # Two channels on devices with MULTI_CHANNEL_AUDIO (the XMOS sends processed + raw);
        # channel 0 (`data`) is the echo-cancelled one — feed only that.
        endpointer = self._endpointer
        if endpointer is not None and endpointer.feed(data):
            self._utterance_done.set()

    async def _handle_stop(self, *_args) -> None:
        self._utterance_done.set()

    # --- the round ---------------------------------------------------------------------

    async def _run_round(self) -> None:
        send = self.client.send_voice_assistant_event
        send(Event.VOICE_ASSISTANT_RUN_START, {})
        send(Event.VOICE_ASSISTANT_STT_START, {})
        try:
            await asyncio.wait_for(self._utterance_done.wait(),
                                   timeout=self.cfg.max_utterance_s + 5)
        except asyncio.TimeoutError:
            pass
        pcm = self._endpointer.audio if self._endpointer else b""
        self._endpointer = None

        try:
            result = await self.pipeline.run(pcm)
            if result.transcript:
                send(Event.VOICE_ASSISTANT_STT_END, {"text": result.transcript})
            if result.tts_url:
                send(Event.VOICE_ASSISTANT_TTS_START, {"text": result.reply or ""})
                send(Event.VOICE_ASSISTANT_TTS_END, {"url": result.tts_url})
        except Exception as error:
            logger.exception("voice round failed")
            send(Event.VOICE_ASSISTANT_ERROR, {"code": "gateway", "message": str(error)[:200]})
        finally:
            send(Event.VOICE_ASSISTANT_RUN_END, {})

    # --- late answers --------------------------------------------------------------------

    async def announce(self, url: str, text: str) -> None:
        """Deliver a late answer. Prefers the voice-assistant announcement (waits for
        playback), falls back to a media-player announcement command."""
        announce_api = getattr(self.client, "send_voice_assistant_announcement_await_response", None)
        if announce_api is not None:
            try:
                await announce_api(url, 60.0, text)
                return
            except Exception:
                logger.debug("announcement API failed; falling back to media player", exc_info=True)
        if self._media_player_key is not None:
            self.client.media_player_command(
                self._media_player_key, media_url=url, announcement=True)
        else:
            logger.error("no announcement path available — reply dropped: %s", text[:100])
