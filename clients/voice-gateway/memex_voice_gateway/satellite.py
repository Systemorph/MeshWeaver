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
from typing import Awaitable, Callable

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
        self._alt_audio = bytearray()   # channel 1 of the round, for capture diagnostics
        self._utterance_done = asyncio.Event()
        self._media_player_key: int | None = None
        self._round_task: asyncio.Task | None = None
        self._follow_up = False
        self._round_serial = 0
        self._noise_strikes: list[float] = []
        self._suppress_until = 0.0
        self.on_wake: Callable[[], Awaitable[None]] | None = None  # barge-in hook

    async def stop_playback(self) -> None:
        """Stop whatever the media player is doing — the device half of a barge-in."""
        if self._media_player_key is None:
            return
        try:
            from aioesphomeapi.model import MediaPlayerCommand
            self.client.media_player_command(self._media_player_key,
                                             command=MediaPlayerCommand.STOP)
        except Exception:
            logger.debug("media stop failed", exc_info=True)

    async def play_media(self, url: str) -> None:
        """Start a media stream (radio, music) on the device's player."""
        if self._media_player_key is None:
            raise RuntimeError("no media player entity on the device")
        self.client.media_player_command(self._media_player_key, media_url=url)

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
        if not available:
            return
        wanted = self.cfg.wake_word
        chosen = wanted if wanted in available else (active[0] if active else available[0])
        if wanted and wanted not in available:
            logger.warning("WAKE_WORD %r not on the device; using %r", wanted, chosen)
        # The CONFIGURED wake word must actually be active — a reflash or NVS can leave an
        # old selection in place, and "available but inactive" hears nothing.
        limit = getattr(conf, "max_active_wake_words", 1) or 1
        # Prefer the configured word; keep a proven fallback model active beside it when the
        # device allows more than one (a young custom model should not be the only ear).
        fallback = [w for w in ("hey_jarvis", *active) if w in available and w != chosen]
        desired = ([chosen] + fallback)[:limit]
        if set(desired) != set(active):
            await self.client.set_voice_assistant_configuration(active_wake_words=desired)
            logger.info("activated wake words %s (max %s)", desired, limit)

    # --- voice assistant callbacks ----------------------------------------------------

    async def _handle_start(
        self, conversation_id: str, flags: int,
        audio_settings: VoiceAssistantAudioSettings, wake_word_phrase: str | None,
    ) -> int:
        # WAKE-STORM SUPPRESSION: repeated junk rounds mean the wake word is being triggered
        # by ambient audio (a TV) or our own playback — go deaf for a cooldown instead of
        # answering the room. Silence from the user must mean silence from the device.
        import time as _time
        if _time.monotonic() < self._suppress_until:
            logger.info("wake ignored (suppressed for %.0fs after a wake storm)",
                        self._suppress_until - _time.monotonic())
            return 0
        # NOTE: playback is deliberately NOT interrupted here — a false wake must not kill a
        # real answer. The interrupt happens once the round yields actual speech (or "stop").
        # No wake-word phrase = a CONTINUED conversation (start_conversation re-opened the
        # mic). Those rounds calibrate against ambient audio (a TV must not become the
        # conversation partner) and give up early when nobody starts speaking.
        follow_up = not wake_word_phrase
        logger.info("wake (%s)", wake_word_phrase or "follow-up")
        self._follow_up = follow_up
        self._round_serial += 1
        self._endpointer = Endpointer(
            sample_rate=self.cfg.sample_rate,
            silence_ms=self.cfg.silence_ms,
            max_utterance_s=self.cfg.max_utterance_s,
            min_utterance_s=self.cfg.min_utterance_s,
            # EVERY round ignores its first moments: a wake round starts with the wake
            # word's own tail (which must not count as the utterance — it made rounds close
            # ~1s after wake, before the question was asked), a follow-up starts with room
            # ambience. The lead-in calibrates the noise floor instead of listening.
            calibrate_ms=400 if follow_up else 600,
            onset_timeout_s=5.0 if follow_up else 8.0,
        )
        self._utterance_done.clear()
        self._alt_audio = bytearray()
        self._round_task = asyncio.create_task(self._run_round())
        return 0  # 0 = stream microphone audio over the API connection (no UDP)

    async def _handle_audio(self, data: bytes, data2: bytes | None = None) -> None:
        # Two channels on devices with MULTI_CHANNEL_AUDIO (the XMOS sends two feeds);
        # we feed channel 0 to STT and RECORD channel 1 beside it — which one is actually
        # the echo-cancelled/beamformed feed is an ASSUMPTION under test (2026-08-20:
        # every STT model garbles room audio while the on-device wake engine hears fine).
        endpointer = self._endpointer
        if data2 and self._endpointer is not None:
            self._alt_audio.extend(data2)
        if endpointer is not None and endpointer.feed(data):
            self._utterance_done.set()

    async def _handle_stop(self, *_args) -> None:
        self._utterance_done.set()

    # --- the round ---------------------------------------------------------------------

    async def _run_round(self) -> None:
        serial = self._round_serial
        send = self.client.send_voice_assistant_event
        send(Event.VOICE_ASSISTANT_RUN_START, {})
        send(Event.VOICE_ASSISTANT_STT_START, {})
        try:
            await asyncio.wait_for(self._utterance_done.wait(),
                                   timeout=self.cfg.max_utterance_s + 5)
        except asyncio.TimeoutError:
            pass
        endpointer, self._endpointer = self._endpointer, None
        pcm = endpointer.audio if endpointer else b""
        # Retain the raw utterance locally when configured: real-voice recordings are the
        # retraining corpus that fixes what synthetic TTS samples cannot (the owner's accent).
        if self.cfg.record_dir and pcm:
            try:
                import os as _os
                import time as _t
                from .stt import wav_from_pcm as _wav
                _os.makedirs(self.cfg.record_dir, exist_ok=True)
                name = f"round_{_t.strftime('%Y%m%d_%H%M%S')}_{'fu' if self._follow_up else 'wake'}.wav"
                with open(_os.path.join(self.cfg.record_dir, name), "wb") as f:
                    f.write(_wav(pcm, self.cfg.sample_rate))
                if self._alt_audio:   # channel 1, for the which-channel-is-clean experiment
                    with open(_os.path.join(self.cfg.record_dir,
                                            name.replace(".wav", "_ch2.wav")), "wb") as f:
                        f.write(_wav(bytes(self._alt_audio), self.cfg.sample_rate))
            except Exception:
                logger.debug("recording save failed", exc_info=True)
        # A follow-up that never paused (TV, music) or never started (silence) is not
        # addressed to the assistant: end the conversation quietly instead of answering it.
        if self._follow_up and endpointer is not None and endpointer.ended_by_cap:
            logger.info("follow-up discarded (%s) — conversation ends",
                        "continuous audio" if endpointer.speech_seen else "silence")
            send(Event.VOICE_ASSISTANT_RUN_END, {})
            return

        try:
            result = await self.pipeline.run(pcm)
            if result.transcript:
                send(Event.VOICE_ASSISTANT_STT_END, {"text": result.transcript})
        except Exception as error:
            logger.exception("voice round failed")
            send(Event.VOICE_ASSISTANT_ERROR, {"code": "gateway", "message": str(error)[:200]})
            send(Event.VOICE_ASSISTANT_RUN_END, {})
            return
        # Junk-round accounting: three noise rounds inside a minute = a wake storm; go deaf
        # for 60s rather than machine-gunning answers at a TV.
        import time as _time
        if result.noise:
            now = _time.monotonic()
            self._noise_strikes = [t for t in self._noise_strikes if now - t < 60] + [now]
            if len(self._noise_strikes) >= 3:
                self._suppress_until = now + 60
                self._noise_strikes.clear()
                logger.warning("wake storm detected — suppressing wakes for 60s")
        elif result.transcript:
            self._noise_strikes.clear()
            # REAL speech arrived: NOW interrupt whatever was still playing (barge-in), and
            # on an explicit "stop" also silence the media player.
            if self.on_wake is not None:
                try:
                    await self.on_wake()
                except Exception:
                    logger.debug("barge-in interrupt failed", exc_info=True)
            if result.interrupt:
                await self.stop_playback()
        # End the run FIRST, then deliver the reply as an ANNOUNCEMENT with
        # start_conversation: the device plays it and — when playback finishes — opens the
        # mic again by itself, no wake word ("conversation mode"). A silent follow-up ends
        # the chain via the pipeline's quiet empty-transcript path.
        send(Event.VOICE_ASSISTANT_RUN_END, {})
        # SUPERSEDED replies are dropped, not queued: if another wake started meanwhile,
        # playing this answer late would drain a stale backlog at the listener (observed
        # with a TV feeding rounds faster than they played).
        if result.tts_url and serial == self._round_serial:
            await self.announce(result.tts_url, result.reply or "",
                                start_conversation=self.cfg.continue_conversation
                                and bool(result.transcript))
        elif result.tts_url:
            logger.info("reply superseded by a newer round — not played")

    # --- late answers --------------------------------------------------------------------

    async def announce(self, url: str, text: str, start_conversation: bool = False) -> None:
        """Deliver a reply. Prefers the voice-assistant announcement (waits for playback;
        `start_conversation` re-opens the mic when it ends), falls back to a media-player
        announcement command."""
        announce_api = getattr(self.client, "send_voice_assistant_announcement_await_response", None)
        if announce_api is not None:
            try:
                try:
                    await announce_api(url, 120.0, text, start_conversation=start_conversation)
                except TypeError:
                    await announce_api(url, 120.0, text)   # older aioesphomeapi: no kwarg
                return
            except Exception:
                logger.debug("announcement API failed; falling back to media player", exc_info=True)
        if self._media_player_key is not None:
            self.client.media_player_command(
                self._media_player_key, media_url=url, announcement=True)
        else:
            logger.error("no announcement path available — reply dropped: %s", text[:100])
