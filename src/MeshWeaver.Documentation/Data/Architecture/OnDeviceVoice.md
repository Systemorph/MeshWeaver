---
NodeType: Markdown
Name: "On-device voice — Whisper + Swiss German"
Abstract: "How the native Memex (MAUI) client does on-device speech-to-text with Whisper.net / whisper.cpp: where Swiss German is configured, how the fine-tuned model was converted + quantized, where every model file lives, GPU acceleration (Vulkan / Metal), the non-blocking IIoPool pipeline, and the exact commands used."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#5b21b6'/><rect x='9.5' y='4' width='5' height='10' rx='2.5' fill='white'/><path d='M7 11a5 5 0 0 0 10 0' stroke='white' stroke-width='1.6' fill='none'/><rect x='11.2' y='16' width='1.6' height='3.5' fill='white'/><rect x='8.5' y='19.3' width='7' height='1.5' rx='.75' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Client"
  - "Voice"
  - "Whisper"
---

The native **Memex MAUI client** (`memex/Memex.Client`) transcribes speech **fully on the device** with
[Whisper.net](https://github.com/sandrohanea/whisper.net) (a .NET binding over `whisper.cpp`). Audio never
leaves the device; if a mesh is connected the transcript opens/feeds a thread and the agent's reply is
spoken back. The marquee feature is **proper Swiss German** (Bernese included), which the stock Whisper
models can't do.

> **Read alongside:** [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — the inference is
> offloaded through `IIoPool` exactly as described there.

---

## Where Swiss German is configured

Everything keys off **`VoiceModelCatalog`** (`memex/Memex.Client/Voice/VoiceModelCatalog.cs`):

| `WhisperModelSize` | File | Source |
|---|---|---|
| `Base` | `ggml-base.bin` | downloaded from `ggerganov/whisper.cpp` |
| `LargeV3Turbo` | `ggml-large-v3-turbo.bin` | downloaded from `ggerganov/whisper.cpp` |
| **`SwissGerman`** (default) | **`ggml-swiss-german-turbo-q5_0.bin`** | the **Flurin17/whisper-large-v3-turbo-swiss-german** fine-tune, converted + quantized by us (no public GGML exists) |

- `MauiProgram.cs` registers the catalog with `WhisperModelSize.SwissGerman` as the default.
- The model resolves from `FileSystem.AppDataDirectory/models/`; if absent it downloads from the catalog
  URL (see [Where the model lives](#where-the-model-image-lives)).

**Language selection** lives in `WhisperTranscriber` (`memex/Memex.Client/Voice/WhisperTranscriber.cs`).
The default mode is **`"auto-de-en-first"`**: detect among German/English on the **first ~5 s** (Swiss
German has no Whisper code, so constraining the candidates resolves it to `de` → Standard German output);
if the audio isn't confidently de/en it falls back to full auto-detect so French/Italian/etc. still work.
The Voice page (`Components/Pages/Voice.razor`) also exposes individual languages (de/en/fr/it/es/pt/nl)
and the auto modes from a dropdown.

---

## What was done (chronological, with commits)

1. **Converted the fine-tune to GGML** — `bb060d827`. Flurin17's model ships as HF safetensors; whisper.cpp
   needs GGML. (Plus `WhisperTranscriber` detection modes; default model = SwissGerman.)
2. **Hosting** — `61d846373`: a FileSystem **content collection** (`static`) on the MeshWeaver space serves
   large assets from an AKS file-share mount; the catalog downloads the model from that content URL.
3. **Non-blocking + faster + nicer** — `aa735180b`: inference moved off the UI thread onto `IIoPool`
   (`InvokeBlocking` for the detect pass, `InvokeStream` for `ProcessAsync`); 5-second detect slice;
   `WithThreads(nCPU-1)`; a real 0–100 % progress bar (`WithProgressHandler`); individual languages; cleaner UI.
4. **Quantized** — `e628ab1b6`: f16 (1.5 GB) → **q5_0 (547 MB)** — faster + iPhone-RAM-friendly, negligible
   quality loss. Catalog points at the q5_0 file.
5. **GPU** — `09fe1ac36`: added `Whisper.net.Runtime.Vulkan` (Windows + Android) and forced the runtime
   order **Vulkan → CPU** on Windows so inference runs on the GPU (Intel Arc etc.).

---

## Performance — which backend runs where

`whisper.cpp` has multiple backends; the right one is platform-specific:

| Platform | Backend | How |
|---|---|---|
| **Windows / Android** | **Vulkan (GPU)** | `Whisper.net.Runtime.Vulkan` package (csproj, conditional on the TFM) + `RuntimeOptions.RuntimeLibraryOrder = [Vulkan, Cpu]` set in `MauiProgram` under `#if WINDOWS`. CPU is the fallback if Vulkan init fails. |
| **iOS / macOS** | **Metal (GPU)** | The base `Whisper.net.Runtime` ships the Metal-built native lib — used automatically, no config. |
| any | **CPU** | The base runtime; whisper.cpp caps at 4 threads by default, so `WhisperTranscriber` sets `WithThreads(nCPU-1)`. |

🔑 **Don't extrapolate Windows-CPU slowness to iPhone** — iPhone is Metal-GPU by default. The Vulkan work
is for the Windows dev box (e.g. Intel Arc 140V). q5_0 quantization helps **every** backend (memory
bandwidth) and is mandatory for iPhone RAM.

**Non-blocking:** the Voice page subscribes to `IObservable<TranscriptSegment>`; both the detect pass and
the transcription run on the local mesh's CPU `IoPool` (`IoPoolNames.Compile`), so the UI never freezes.
See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

---

## Where the model (image) lives

The GGML model is a **runtime artifact, NOT committed to git** (too large). It lives in three places:

| Location | Path | Purpose |
|---|---|---|
| **Device (this dev box)** | `%LOCALAPPDATA%\User Name\com.companyname.memex.client\Data\models\ggml-swiss-german-turbo-q5_0.bin` | what the running Windows app loads |
| **AKS file share** | storage account `memexaksfiles57ymot2ons3`, share `static-assets`, path `Speech/ggml-swiss-german-turbo-q5_0.bin` (the 1.5 GB `…-f16.bin` is there too) | the source other machines/iOS download from |
| **Content URL (catalog target)** | `https://memex.meshweaver.cloud/MeshWeaver/static/Speech/ggml-swiss-german-turbo-q5_0.bin` | the `VoiceModelCatalog.SwissGerman` download URL |

🩹 **Serving caveat:** the content URL only returns the model once the `static-assets` share is **mounted
read-only** on the **memex-cloud** portal pod with `StaticAssets:Path` set — that mount/deploy is still
pending. Until then the catalog falls back to the **local** placed file (which is in place, so voice works
now). The serving side is wired in `MemexConfiguration.ConfigureMemexMesh` (a FileSystem
`AddContentCollection` named `static` on the `MeshWeaver` space, gated on `StaticAssets:Path`).

**Conversion working tree (ephemeral, not committed):**
- `C:\tmp\swiss-whisper\` — the cloned model + `whisper.cpp` + `openai/whisper` repos + the converted `out/`.
- `C:\tmp\whisper-bin\` — the prebuilt whisper.cpp Windows binaries (incl. `whisper-quantize.exe`).

---

## Reproducing it — the exact commands

### 1. Convert the fine-tune → GGML (f16)

```bash
# Repos (model is git-lfs, ~1.6 GB):
git clone https://huggingface.co/Flurin17/whisper-large-v3-turbo-swiss-german
git clone https://github.com/ggml-org/whisper.cpp
git clone https://github.com/openai/whisper        # ONLY for whisper/assets/mel_filters.npz (v3 needs mel_128)

pip install torch --index-url https://download.pytorch.org/whl/cpu
pip install transformers numpy

# 🔑 The fine-tune's weights are bf16; convert-h5-to-ggml.py does .squeeze().numpy() →
#    "TypeError: unsupported ScalarType BFloat16". Patch that ONE line:
sed -i 's/\.squeeze()\.numpy()/.squeeze().float().numpy()/' whisper.cpp/models/convert-h5-to-ggml.py

python whisper.cpp/models/convert-h5-to-ggml.py \
    whisper-large-v3-turbo-swiss-german whisper out/      # → out/ggml-model.bin (~1.5 GB, f16)
```

### 2. Quantize → q5_0 (no compiler needed — prebuilt binary)

```powershell
# whisper.cpp v1.9.1 prebuilt Windows binaries include whisper-quantize.exe:
#   asset: whisper-bin-x64.zip  (github.com/ggml-org/whisper.cpp/releases)
whisper-quantize.exe out\ggml-model.bin out\ggml-swiss-german-turbo-q5_0.bin q5_0
# → 547 MB (ftype = 8 / q5_0); ~16 s
```

### 3. Place + upload

```powershell
# Local app models dir (what the dev app loads):
Copy-Item out\ggml-swiss-german-turbo-q5_0.bin `
    "$env:LOCALAPPDATA\User Name\com.companyname.memex.client\Data\models\"

# AKS static-assets share (the host other devices download from):
$key = az storage account keys list -n memexaksfiles57ymot2ons3 -g memex-aks-rg --query "[0].value" -o tsv
az storage file upload --account-name memexaksfiles57ymot2ons3 --account-key $key `
    --share-name static-assets --source out\ggml-swiss-german-turbo-q5_0.bin `
    --path "Speech/ggml-swiss-german-turbo-q5_0.bin"
```

### Quantization trade-off

| ftype | Size | Notes |
|---|---|---|
| f16 | ~1.5 GB | reference; slowest, biggest |
| **q5_0** | **547 MB** | shipped default — negligible quality loss |
| q8_0 | ~850 MB | a hair more accuracy if needed |

---

## Code map

| Concern | File |
|---|---|
| Swiss-German model + catalog URLs | `memex/Memex.Client/Voice/VoiceModelCatalog.cs` |
| Detection (5 s slice, de/en-first), threads, progress, `IIoPool` | `memex/Memex.Client/Voice/WhisperTranscriber.cs` |
| Reactive entry point + model-ensure on the pool | `memex/Memex.Client/Voice/VoiceService.cs` |
| Vulkan runtime order + voice DI + default model | `memex/Memex.Client/MauiProgram.cs` |
| UI (progress bar, language menu, record/stop) | `memex/Memex.Client/Components/Pages/Voice.razor` |
| `Whisper.net` + `Whisper.net.Runtime` + `…Runtime.Vulkan` refs | `memex/Memex.Client/Memex.Client.csproj` |
| Mic permission + (OAuth) callback scheme | `memex/Memex.Client/Platforms/iOS/Info.plist` |
| Static-assets content collection (serving) | `memex/Memex.Portal.Shared/MemexConfiguration.cs` |
