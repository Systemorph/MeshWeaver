---
NodeType: Markdown
Name: "Mac local stack — on-device AI + local observability (M-series)"
Abstract: "End-to-end guide to running Memex fully locally on an Apple-silicon Mac (e.g. M5 Max / 64 GB): on-device Swiss-German voice (Whisper + CoreML), a local Qwen3-Coder AI layer via native Ollama on the Metal GPU, and a local LGTM observability stack (Grafana/Loki/Tempo/Prometheus + OpenTelemetry Collector) in the Aspire cluster. Covers the Apple-silicon GPU gotchas, the feature flags, and the exact run commands."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#111827'/><rect x='4' y='6' width='16' height='10' rx='1.5' fill='none' stroke='white' stroke-width='1.5'/><rect x='2.5' y='17.5' width='19' height='1.6' rx='.8' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Client"
  - "Mac"
  - "AI"
  - "Observability"
  - "Local"
---

This is the **one-stop guide to running Memex fully locally on an Apple-silicon Mac** — written against an
**M5 Max / 64 GB** dev box but valid for any recent M-series machine. Three independent pieces, each
**opt-in behind a flag** so you only pay for what you turn on:

1. **On-device voice** — Swiss-German speech-to-text in the native MAUI client (Whisper + CoreML on the GPU).
2. **Local AI layer** — Qwen3-Coder running in **native Ollama** on the Metal GPU, wired into the portal.
3. **Local observability** — a Grafana/Loki/Tempo/Prometheus (LGTM) stack in the Aspire cluster.

> **The one rule that explains every Mac-specific choice below:** on Apple silicon, **the GPU is reachable
> from native (host) processes, NOT from Docker containers** — Docker Desktop on macOS has *no GPU
> passthrough*. So anything that must use the GPU (Whisper, Ollama) runs **natively**; only the things that
> don't need a GPU (Loki, Grafana, …) run in containers.

---

## 0. The hardware ceiling

| | M5 Max / 64 GB (this box) |
|---|---|
| Unified memory | 64 GB — shared CPU+GPU, so "VRAM" = system RAM |
| What fits for a local LLM | Anything whose weights fit in ~that, with headroom for the OS + portal + Postgres |
| GPU access | **Native processes only** (Metal). Docker = CPU-only. |

This ceiling is why model choice matters — see [§2](#2-local-ai-layer-qwen-via-native-ollama).

---

## 1. On-device voice (Whisper and Swiss German)

Full detail in [On-device voice](/Doc/Architecture/OnDeviceVoice). The Mac-relevant essentials:

- The **macOS client is MacCatalyst**, and Whisper.net builds whisper.cpp with **`GGML_METAL=OFF`** for that
  target — so unlike the iOS device, the macOS client has **no Metal**. The GPU path on macOS is **CoreML**.
- GPU therefore needs the **CoreML "apple image"** (`ggml-swiss-german-turbo-encoder.mlmodelc`, fp16, ~1.2 GB)
  next to the ggml model. It's generated with `coremltools` (no Xcode needed — `compile_model` uses the OS
  CoreML framework) and runs the dominant **encoder** of the large-v3-turbo model on the Apple GPU / ANE.

### Feature flags (device-persisted, `Preferences`-backed — `Services/FeatureFlags.cs`)

| Flag | Default | Effect |
|---|---|---|
| `swiss-german` | **off** | off → small multilingual **Base** model (~140 MB, English default). on → the 547 MB Swiss-German fine-tune (+ the 1.2 GB CoreML apple image on macOS). |
| `apple-gpu` | **off** | macOS only. on → prefer the CoreML runtime + download the apple image so the encoder runs on the GPU/ANE; off → CPU. Flip on **after** the apple image is hosted. |

Language: the Voice page defaults to **English**; other languages are optional from the dropdown. With
`swiss-german` on, the default switches to the de/Swiss-de/en-first auto mode.

> **Building/running the MacCatalyst client needs full Xcode** (not just Command-Line-Tools) + the
> `maui` workload (`dotnet workload install maui`).

---

## 2. Local AI layer: Qwen via native Ollama

The portal's AI runs through `IChatClientFactory` providers (see [the model provider design](/Doc/Architecture/CqrsAndContentAccess)).
The framework already ships an **OpenAI-wire-compatible** provider (`AddOpenAICompatible`), and **Ollama
exposes the OpenAI API** at `http://localhost:11434/v1` — so the local AI layer is **config, not new code**.

### Why native Ollama, not a container

A containerized Ollama on macOS runs Qwen **on the CPU** (no Metal passthrough). Run Ollama **natively** so
it uses the M5 Max GPU. The portal runs as a **host process** in `mode=local`, so it reaches native Ollama
on `localhost` directly — no container, no `host.docker.internal` gymnastics.

```bash
brew install ollama            # or the Ollama.app
ollama serve                   # native — uses Metal
ollama pull qwen3-coder:30b    # ~18 GB (Q4); first pull only
```

### Which Qwen — the 35B trap

**Use `qwen3-coder:30b`** = **Qwen3-Coder-30B-A3B** (30 B total / **3 B active** MoE). At Q4 it's ~18 GB and,
because only 3 B params are active per token, it's *fast* on the M5 Max GPU — ideal for 64 GB.

🚫 **Not** `Qwen3-Coder-480B-A35B`. Its "**A35B**" is the *active* MoE parameters of a **480 B-total** model;
an MoE must hold *all* experts resident, so its GGUFs are **150 GB (IQ1) – 276 GB (Q4)** and **do not fit in
64 GB**. "35B" is not the model size.

### Turn it on

`--localai true` sets, on the portal (`MemexLocalStack.AddLocalAi`):

```
Features__Ai__Providers__OpenAICompatible = true
OpenAICompatible__Endpoint  = http://localhost:11434/v1
OpenAICompatible__ApiKey    = ollama        # Ollama ignores it; the factory requires non-empty
OpenAICompatible__Models__0 = qwen3-coder:30b
```

`BuiltInLanguageModelProvider` reads those and emits the `qwen3-coder:30b` model node, so it appears in the
chat composer for **portal / web** users.

> **This is the cluster's AI, not the native app's.** The MAUI app does **not** use Qwen — it ships only the
> Swiss-German Whisper model and uses **Apple Intelligence** (the OS FoundationModels) for text AI on iPhone
> and Apple-silicon Macs. See [On-device AI and logging](/Doc/Architecture/OnDeviceVoice#on-device-ai-and-logging-the-app).
> A native-app voice transcript that *is* routed to the mesh (when connected) can still reach this Qwen, but
> nothing is bundled into the app.

---

## 3. Local observability (LGTM)

OpenTelemetry is **already emitted** by the portal + db-migration (`ServiceDefaults.ConfigureOpenTelemetry()`
→ logs, metrics, traces over OTLP). By default Aspire routes that to its own dashboard. `--observability true`
adds a full **LGTM** stack and routes the same OTLP into it (`MemexLocalStack.AddObservability`):

```
portal / db-migration ──OTLP/HTTP──▶ otel-collector ─┬─ logs   ─▶ Loki
   (already instrumented)                             ├─ traces ─▶ Tempo
                                                       └─ metrics─▶ Prometheus
                                                                      └──────▶ Grafana (all 3 datasources)
```

| Container | Image | Port | Role |
|---|---|---|---|
| `otel-collector` | `otel/opentelemetry-collector-contrib` | 4318 (OTLP/HTTP) | receive + fan out |
| `loki` | `grafana/loki` | 3100 | logs (native OTLP ingest) |
| `tempo` | `grafana/tempo` | 3200 | traces |
| `prometheus` | `prom/prometheus` | 9090 | metrics (remote-write receiver) |
| `grafana` | `grafana/grafana` | 3000 | dashboards (anonymous admin, datasources pre-provisioned) |

These are **CPU-only**, so containers are fine. Configs live in `memex/aspire/Memex.AppHost/observability/`;
data persists in volumes across restarts. OTLP uses **HTTP/protobuf** (4318) rather than gRPC to avoid
http/2-through-proxy fragility. Open **Grafana** from the Aspire dashboard's resource list → Explore → Loki.

> Opt-in by design: with the flag off, a plain `aspire run` is unchanged and telemetry stays on the Aspire
> dashboard. With it on, the services' OTLP is redirected to the collector (so it lands in Grafana).

---

## 4. Running it all

Everything is `mode=local` (Docker pgvector + emulated storage). The two extras are opt-in flags:

```bash
# Native, GPU-backed (run once / leave running):
ollama serve &                       # only if you enabled --localai
ollama pull qwen3-coder:30b

# The local cluster, with both extras:
aspire run --project memex/aspire/Memex.AppHost -- --observability true --localai true
#   • plain:                 aspire run --project memex/aspire/Memex.AppHost
#   • observability only:    ... -- --observability true
#   • local AI only:         ... -- --localai true
```

Then, from the **Aspire dashboard** (`https://localhost:17200/`): open **grafana** (3000) for logs/traces/
metrics, and the **portal** for the app. The MAUI client is separate — see [§1](#1-on-device-voice-whisper-and-swiss-german).

### Mac gotchas, collected

| Symptom | Cause | Fix |
|---|---|---|
| Qwen is slow / pegs CPU | Ollama running **in Docker** | Run Ollama **natively** (`brew`/.app); Docker on macOS has no Metal |
| macOS client voice on CPU | MacCatalyst has **no Metal** (Whisper.net `GGML_METAL=OFF`) | Host the CoreML apple image + flip `apple-gpu` on ([OnDeviceVoice](/Doc/Architecture/OnDeviceVoice)) |
| MacCatalyst build fails | only Command-Line-Tools installed | install **full Xcode** + `dotnet workload install maui` |
| Grafana empty | flag off, or services started before the collector | `--observability true`; the collector `WaitFor`s its backends and the portal `WaitFor`s the collector |
| `qwen3-coder:30b` won't load | confused with the 480B-A35B | use the **30B-A3B** tag — the 480B does not fit 64 GB ([§2](#2-local-ai-layer-qwen-via-native-ollama)) |

---

## Code map

| Concern | File |
|---|---|
| Local-cluster extras (LGTM + Ollama/Qwen wiring, opt-in flags) | `memex/aspire/Memex.AppHost/MemexLocalStack.cs` |
| Observability configs (collector, Loki, Tempo, Prometheus, Grafana datasources) | `memex/aspire/Memex.AppHost/observability/` |
| OpenTelemetry emission (already wired) | `memex/aspire/Memex.Portal.ServiceDefaults/ServiceDefaults.cs` |
| OpenAI-compatible provider (serves Ollama) | `src/MeshWeaver.AI.OpenAI/OpenAIExtensions.cs` |
| On-device voice + CoreML apple image | [On-device voice](/Doc/Architecture/OnDeviceVoice) · `memex/Memex.Client/Voice/` |
| Client feature flags | `memex/Memex.Client/Services/FeatureFlags.cs` |
