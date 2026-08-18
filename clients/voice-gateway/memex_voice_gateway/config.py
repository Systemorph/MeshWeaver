"""Gateway configuration — one flat dataclass, filled from environment variables.

Every knob has a default except the four that identify YOUR satellite and YOUR mesh:
SATELLITE_HOST, SATELLITE_PSK (or SATELLITE_PASSWORD), MEMEX_URL, MEMEX_TOKEN.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _env(name: str, default: str | None = None) -> str | None:
    value = os.environ.get(name, default)
    return value if value not in ("", None) else default


@dataclass
class Config:
    # --- the satellite (ESPHome native API, LAN) ---
    satellite_host: str = ""
    satellite_port: int = 6053
    satellite_psk: str | None = None        # noise encryption key (preferred)
    satellite_password: str | None = None   # legacy plaintext API password

    # --- the mesh ---
    memex_url: str = "https://memex.meshweaver.cloud"
    memex_token: str = ""                   # mw_… API token (Settings ▸ Security ▸ API Tokens)
    namespace: str = ""                     # thread namespace, e.g. your user id
    agent: str | None = "Voice"             # lean spoken-answer agent; None = platform default

    # --- speech ---
    stt_path: str = "/api/speech/transcribe"
    stt_language: str = "de"                # "de" transcribes Swiss German OUT as Standard German
    sample_rate: int = 16000                # the satellite streams 16 kHz mono s16le

    # --- conversation pacing ---
    reply_budget_s: float = 10.0            # wait this long for the agent before acking
    announce_budget_s: float = 240.0        # keep polling this long, then announce the answer
    thread_idle_minutes: float = 5.0        # reuse the same thread within this window
    hold_phrase: str = "Ich schaue nach. Einen Moment bitte."
    error_phrase: str = "Entschuldigung, das hat gerade nicht geklappt."

    # --- endpointing (energy VAD) ---
    silence_ms: int = 800
    max_utterance_s: float = 15.0
    min_utterance_s: float = 0.4

    # --- TTS (Piper) + the URL the satellite fetches audio from ---
    piper_bin: str = "piper"
    piper_voice: str = "/voices/de_DE-thorsten-medium.onnx"
    gateway_host: str = ""                  # LAN IP of THIS gateway, reachable by the satellite
    gateway_port: int = 8200

    extra: dict = field(default_factory=dict)

    @staticmethod
    def from_env() -> "Config":
        cfg = Config(
            satellite_host=_env("SATELLITE_HOST") or "",
            satellite_port=int(_env("SATELLITE_PORT", "6053")),
            satellite_psk=_env("SATELLITE_PSK"),
            satellite_password=_env("SATELLITE_PASSWORD"),
            memex_url=(_env("MEMEX_URL", "https://memex.meshweaver.cloud") or "").rstrip("/"),
            memex_token=_env("MEMEX_TOKEN") or "",
            namespace=_env("MEMEX_NAMESPACE") or "",
            agent=_env("MEMEX_AGENT", "Voice"),
            stt_language=_env("STT_LANGUAGE", "de") or "de",
            reply_budget_s=float(_env("REPLY_BUDGET_S", "10")),
            announce_budget_s=float(_env("ANNOUNCE_BUDGET_S", "240")),
            thread_idle_minutes=float(_env("THREAD_IDLE_MINUTES", "5")),
            hold_phrase=_env("HOLD_PHRASE", Config.hold_phrase) or Config.hold_phrase,
            error_phrase=_env("ERROR_PHRASE", Config.error_phrase) or Config.error_phrase,
            silence_ms=int(_env("SILENCE_MS", "800")),
            piper_bin=_env("PIPER_BIN", "piper") or "piper",
            piper_voice=_env("PIPER_VOICE", Config.piper_voice) or Config.piper_voice,
            gateway_host=_env("GATEWAY_HOST") or "",
            gateway_port=int(_env("GATEWAY_PORT", "8200")),
        )
        missing = [n for n, v in (
            ("SATELLITE_HOST", cfg.satellite_host),
            ("MEMEX_TOKEN", cfg.memex_token),
            ("MEMEX_NAMESPACE", cfg.namespace),
            ("GATEWAY_HOST", cfg.gateway_host),
        ) if not v]
        if missing:
            raise SystemExit(f"Missing required environment variables: {', '.join(missing)}")
        return cfg
