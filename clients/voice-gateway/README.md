# memex-voice-gateway

Wires an **ESPHome voice satellite** (built for the
[FutureProofHomes Satellite1](https://github.com/FutureProofHomes/Satellite1-ESPHome)) directly to
**MeshWeaver agent threads** — no Home Assistant required. One container on any always-on LAN box.

```
Satellite1 (wake word on-device, XMOS AEC)
   │ mic audio (ESPHome native API, this gateway dials the device)
   ▼
memex-voice-gateway
   ├─ endpointing (energy VAD)                          [vad.py]
   ├─ STT  → POST {MEMEX_URL}/api/speech/transcribe     [stt.py]   Swiss German Whisper container
   ├─ chat → /mcp start_thread / submit_message          [threads.py]  one conversation ≈ one thread
   └─ TTS  → Piper (de_DE) served over HTTP              [tts.py]   device plays the URL
```

**Latency reality (measured 2026-08-18):** a memex thread round takes ~60–70 s wall clock —
dispatch/startup, not generation. The gateway therefore answers inline only when the reply lands
within `REPLY_BUDGET_S`; otherwise it speaks a short holding phrase, ends the voice run, and
delivers the real answer as an **announcement** when it arrives. If platform dispatch gets faster,
the same code starts answering inline — nothing to change.

## Quick start

1. **Mint a token**: memex portal → Settings ▸ Security ▸ API Tokens → label `voice-gateway`.
   The gateway acts as *you* on the mesh — anyone who talks to the speaker does too.
2. **Get the device's API encryption key** from your Satellite1's ESPHome config (`api: encryption:`).
3. Create `.env` next to `docker-compose.yml`:

   ```env
   SATELLITE_HOST=192.168.1.42
   SATELLITE_PSK=<the device's api encryption key>
   MEMEX_TOKEN=mw_…
   MEMEX_NAMESPACE=<your user id>
   GATEWAY_HOST=192.168.1.10        # THIS machine's LAN IP — the satellite fetches TTS audio here
   ```

4. `docker compose up --build -d`, say the wake word, ask something.

## The Voice agent

Point `MEMEX_AGENT` at a **lean** agent (short instructions, `chat` model tier, at most the Mesh
Get/Search plugin). The platform's default assistant carries ~12k tokens of context into every
round — fine in the portal, wasteful when the reply is one spoken sentence.

## Coexisting with Home Assistant

The device accepts multiple ESPHome API clients, so HA can own the satellite's **sensors, music
and multi-room audio** while this gateway owns **voice** — but only ONE client may subscribe as
the voice assistant. If you run HA alongside: do **not** assign the satellite to an Assist
pipeline there. (Apple Music on the satellite = HA + Music Assistant; Siri cannot run on the
device — but an Apple Shortcut on your phone can call the same thread API.)

## Configuration reference

| Variable | Default | Meaning |
|---|---|---|
| `SATELLITE_HOST` / `SATELLITE_PORT` | — / `6053` | the device's LAN address |
| `SATELLITE_PSK` | — | ESPHome API encryption key (`SATELLITE_PASSWORD` for legacy auth) |
| `MEMEX_URL` | `https://memex.meshweaver.cloud` | the mesh |
| `MEMEX_TOKEN` | — | `mw_…` bearer token |
| `MEMEX_NAMESPACE` | — | where threads are created (`{ns}/_Thread/…`) |
| `MEMEX_AGENT` | `Voice` | agent name; empty = platform default |
| `GATEWAY_HOST` / `GATEWAY_PORT` | — / `8200` | LAN address the satellite fetches TTS from |
| `STT_LANGUAGE` | `de` | `de` transcribes Swiss German OUT as Standard German |
| `REPLY_BUDGET_S` | `10` | wait this long before acking with the holding phrase |
| `ANNOUNCE_BUDGET_S` | `240` | keep polling this long, then announce the answer |
| `THREAD_IDLE_MINUTES` | `5` | follow-up questions reuse the same thread within this window |
| `HOLD_PHRASE` / `ERROR_PHRASE` | German defaults | what the speaker says meanwhile / on failure |
| `PIPER_VOICE` / `PIPER_VOICE_URL` | thorsten-medium | High German TTS voice |

## Development

```bash
pip install -e .[dev]
pytest
```

The device protocol follows the same `aioesphomeapi` surface Home Assistant's ESPHome
integration uses (`subscribe_voice_assistant` in API-audio mode, `send_voice_assistant_event`,
announcements with a media-player fallback). The thread and STT clients are unit-tested against
the live-verified wire shapes; the device leg needs a real satellite to verify end-to-end.

## Speech model license

The default STT model behind `/api/speech/transcribe` is a derivative of
[Flurin17/whisper-large-v3-turbo-swiss-german](https://huggingface.co/Flurin17/whisper-large-v3-turbo-swiss-german)
(**CC BY-NC 4.0 — non-commercial use only**). A personal home assistant is non-commercial use;
for anything commercial, point the whisper container at a permissively licensed model
(see `deploy/whisper/README.md`).
