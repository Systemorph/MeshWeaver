"""Gateway configuration — one flat dataclass, filled from environment variables.

Three pluggable legs, each independently local or mesh-hosted:

  BRAIN=memex   agent threads over /mcp (needs MEMEX_TOKEN + MEMEX_NAMESPACE)   [default]
  BRAIN=ollama  a local model via Ollama (OLLAMA_URL / OLLAMA_MODEL)

  STT_URL       full transcription endpoint. Default: {MEMEX_URL}/api/speech/transcribe.
                Point it at a local whisper.cpp server instead (http://127.0.0.1:8090/inference)
                — both speak multipart file+language → {"text": …}.

  TTS_ENGINE=piper  the Piper CLI (container default)
  TTS_ENGINE=say    macOS's built-in `say` (zero setup on a Mac; SAY_VOICE, e.g. "Anna")
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _env(name: str, default: str | None = None) -> str | None:
    value = os.environ.get(name, default)
    return value if value not in ("", None) else default


# What the SPEAKER says in its own voice (holding, errors, switch confirmations) — chosen by
# the configured language; every instruction and prompt in this repo stays English.
PHRASES: dict[str, dict[str, str]] = {
    "en": {"hold": "Let me check. One moment please.",
           "error": "Sorry, that did not work.",
           "connected": "Connected to {name}.",
           "unknown": "I don't know {target}. Available: {names}.",
           "delegated": "Started a {agent} thread on {portal}. You can review it there.",
           "no_mesh": "No mesh portal is configured for threads.",
           "new_topic": "Okay, new topic.",
           "switched": "Now in the thread about {topic}.",
           "posted": "Posted to the thread about {topic}. I will announce the answer.",
           "no_thread": "I have no open thread about {topic}.",
           "threads_open": "Open threads: {list}.",
           "no_threads": "No open threads.",
           "ready": "The answer about {topic} is ready. Say: read it.",
           "nothing_new": "No new answers.",
           "radio_on": "Here comes {station}. Say stop to end it.",
           "song_hint": "I cannot play single songs yet — but here comes {station}.",
           "no_stations": "No radio stations are set up yet — add them under Voice, Station in the portal."},
    "de": {"hold": "Ich schaue nach. Einen Moment bitte.",
           "error": "Entschuldigung, das hat gerade nicht geklappt.",
           "connected": "Verbunden mit {name}.",
           "unknown": "Ich kenne {target} nicht. Verfügbar: {names}.",
           "delegated": "Thread mit {agent} auf {portal} gestartet. Du kannst ihn dort anschauen.",
           "no_mesh": "Es ist kein Portal für Threads konfiguriert.",
           "new_topic": "Okay, neues Thema.",
           "switched": "Wir sind jetzt im Thread über {topic}.",
           "posted": "An den Thread über {topic} geschickt. Ich melde mich mit der Antwort.",
           "no_thread": "Ich habe keinen offenen Thread über {topic}.",
           "threads_open": "Offene Threads: {list}.",
           "no_threads": "Keine offenen Threads.",
           "ready": "Die Antwort zu {topic} ist bereit. Sag: vorlesen.",
           "nothing_new": "Keine neuen Antworten.",
           "radio_on": "Hier kommt {station}. Sag Stopp, wenn es reicht.",
           "song_hint": "Einzelne Lieder kann ich noch nicht spielen — dafür kommt {station}.",
           "no_stations": "Es sind noch keine Radiosender eingerichtet — füge sie im Portal unter Voice, Station hinzu."},
}
PHRASES["en"]["answer_to"] = "Answering your question: {question} —"
PHRASES["de"]["answer_to"] = "Antwort auf deine Frage: {question} —"


def phrases_for(language: str) -> dict[str, str]:
    return PHRASES.get((language or "en").split("-")[0].lower(), PHRASES["en"])


@dataclass
class Config:
    # --- the satellite (ESPHome native API, LAN) ---
    satellite_host: str = ""
    satellite_port: int = 6053
    satellite_psk: str | None = None        # noise encryption key (preferred)
    satellite_password: str | None = None   # legacy plaintext API password

    # --- the brain ---
    brain: str = "memex"                    # "memex" (agent threads) | "ollama" (local model)
    memex_url: str = "https://memex.meshweaver.cloud"
    memex_token: str = ""                   # mw_… API token (Settings ▸ Security ▸ API Tokens)
    namespace: str = ""                     # thread namespace, e.g. your user id
    agent: str | None = "Voice"             # lean spoken-answer agent; None = platform default
    ollama_url: str = "http://127.0.0.1:11434"
    ollama_model: str = "qwen3.6"

    # --- speech-to-text ---
    stt_url: str = ""                       # default derived: {memex_url}/api/speech/transcribe
    stt_token: str | None = None            # bearer for the STT endpoint; None = unauthenticated
    stt_language: str = "de"                # "de" transcribes Swiss German OUT as Standard German
    sample_rate: int = 16000                # the satellite streams 16 kHz mono s16le

    # --- conversation pacing ---
    reply_budget_s: float = 10.0            # wait this long for the answer before acking
    announce_budget_s: float = 1800.0       # keep polling this long — memex is ASYNC, real
                                            # work takes minutes; the poll backs off to 10s
    announce_mode: str = "signal"           # "signal": chime + mailbox, answer on "vorlesen";
                                            # "full": speak the whole answer unprompted
    thread_idle_minutes: float = 5.0        # reuse the conversation within this window
    hold_phrase: str = ""                   # default: PHRASES[stt_language]["hold"]
    error_phrase: str = ""                  # default: PHRASES[stt_language]["error"]

    # --- multiple brains (optional) — see router.py; overrides the single-brain fields ---
    portals: list = field(default_factory=list)   # PORTALS: JSON list of named targets

    # --- wake word (activated on the device if none is) ---
    wake_word: str = "hey_jarvis"           # micro-wake-word model id on the device
    continue_conversation: bool = True      # after a reply, listen again without a wake word
    speak_dialect: bool = False             # experiment: model writes Swiss German for the TTS
    location: str | None = None             # where the speaker is (weather/'here' questions)
    system_prompt_file: str | None = None   # override the built-in voice prompt with a file

    # --- Home Assistant (optional) — a tool for the local brain, not a voice owner ---
    ha_url: str | None = None               # e.g. http://homeassistant.local:8123
    ha_token: str | None = None             # long-lived access token

    # --- the spoken session (context cookie) + tool reach ---
    session_file: str = "~/.memex-voice/session.json"   # persisted context, per speaker
    session_ttl_h: float = 8.0              # like an MCP session id: resumes within, expires after
    allow_destructive: bool = False         # let mesh_tool reach delete/restore (default: never)

    # --- endpointing (energy VAD) ---
    silence_ms: int = 800
    max_utterance_s: float = 15.0
    min_utterance_s: float = 0.4

    # --- text-to-speech + the URL the satellite fetches audio from ---
    tts_engine: str = "piper"               # "piper" | "say" (macOS)
    stream_tts: bool = False
    record_dir: str | None = None           # save each round's raw utterance WAV here — the
                                            # corpus for retraining the wake word on REAL voices                # chunked live TTS; OFF: ESPHome 2026's player
                                            # aborts chunked bodies after ~200 bytes — replies
                                            # ship as complete WAVs until device-verified
    piper_bin: str = "piper"
    piper_voice: str = "/voices/de_DE-thorsten-medium.onnx"
    say_voice: str = "Anna"                 # macOS German voice
    gateway_host: str = ""                  # LAN IP of THIS gateway, reachable by the satellite
    gateway_port: int = 8200

    extra: dict = field(default_factory=dict)

    @staticmethod
    def from_env() -> "Config":
        import json
        portals_raw = _env("PORTALS")
        portals = json.loads(portals_raw) if portals_raw else []
        memex_url = (_env("MEMEX_URL", "https://memex.meshweaver.cloud") or "").rstrip("/")
        memex_token = _env("MEMEX_TOKEN") or ""
        stt_url = _env("STT_URL") or f"{memex_url}/api/speech/transcribe"
        # The mesh endpoint needs the bearer; a local whisper.cpp server does not.
        stt_token = _env("STT_TOKEN") or (memex_token if stt_url.startswith(memex_url) else None)
        cfg = Config(
            satellite_host=_env("SATELLITE_HOST") or "",
            satellite_port=int(_env("SATELLITE_PORT", "6053")),
            satellite_psk=_env("SATELLITE_PSK"),
            satellite_password=_env("SATELLITE_PASSWORD"),
            brain=(_env("BRAIN", "memex") or "memex").lower(),
            memex_url=memex_url,
            memex_token=memex_token,
            namespace=_env("MEMEX_NAMESPACE") or "",
            agent=_env("MEMEX_AGENT", "Voice"),
            ollama_url=(_env("OLLAMA_URL", "http://127.0.0.1:11434") or "").rstrip("/"),
            ollama_model=_env("OLLAMA_MODEL", "qwen3.6") or "qwen3.6",
            stt_url=stt_url,
            stt_token=stt_token,
            stt_language=_env("STT_LANGUAGE", "de") or "de",
            reply_budget_s=float(_env("REPLY_BUDGET_S", "10")),
            announce_budget_s=float(_env("ANNOUNCE_BUDGET_S", "1800")),
            announce_mode=(_env("ANNOUNCE_MODE", "signal") or "signal").lower(),
            thread_idle_minutes=float(_env("THREAD_IDLE_MINUTES", "5")),
            hold_phrase=_env("HOLD_PHRASE") or phrases_for(_env("STT_LANGUAGE", "de") or "de")["hold"],
            error_phrase=_env("ERROR_PHRASE") or phrases_for(_env("STT_LANGUAGE", "de") or "de")["error"],
            wake_word=_env("WAKE_WORD", "hey_jarvis") or "hey_jarvis",
            continue_conversation=(_env("CONTINUE_CONVERSATION", "true") or "true").lower() != "false",
            speak_dialect=(_env("SPEAK_DIALECT", "false") or "false").lower() == "true",
            location=_env("LOCATION"),
            system_prompt_file=_env("SYSTEM_PROMPT_FILE"),
            ha_url=(_env("HA_URL") or "").rstrip("/") or None,
            ha_token=_env("HA_TOKEN"),
            session_file=_env("SESSION_FILE", Config.session_file) or Config.session_file,
            session_ttl_h=float(_env("SESSION_TTL_H", "8")),
            allow_destructive=(_env("ALLOW_DESTRUCTIVE", "false") or "false").lower() == "true",
            silence_ms=int(_env("SILENCE_MS", "800")),
            tts_engine=(_env("TTS_ENGINE", "piper") or "piper").lower(),
            stream_tts=(_env("STREAM_TTS", "false") or "false").lower() == "true",
            record_dir=_env("RECORD_DIR"),
            piper_bin=_env("PIPER_BIN", "piper") or "piper",
            piper_voice=_env("PIPER_VOICE", Config.piper_voice) or Config.piper_voice,
            say_voice=_env("SAY_VOICE", "Anna") or "Anna",
            gateway_host=_env("GATEWAY_HOST") or "",
            gateway_port=int(_env("GATEWAY_PORT", "8200")),
            sample_rate=int(_env("SAMPLE_RATE", "16000")),
            portals=portals,
        )
        required = [("SATELLITE_HOST", cfg.satellite_host), ("GATEWAY_HOST", cfg.gateway_host)]
        if not cfg.portals and cfg.brain == "memex":
            required += [("MEMEX_TOKEN", cfg.memex_token), ("MEMEX_NAMESPACE", cfg.namespace)]
        missing = [name for name, value in required if not value]
        if missing:
            raise SystemExit(f"Missing required environment variables: {', '.join(missing)}")
        if cfg.brain not in ("memex", "ollama"):
            raise SystemExit(f"BRAIN must be 'memex' or 'ollama', not '{cfg.brain}'")
        return cfg
