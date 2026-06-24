---
NodeType: Markdown
Name: "On-device voice — Whisper + Swiss German"
Abstract: "How the native Memex (MAUI) client does on-device speech-to-text with Whisper.net / whisper.cpp: where Swiss German is configured, how the fine-tuned model was converted + quantized, where every model file lives, GPU acceleration (Vulkan on Windows/Android, Metal on the iOS device, CoreML on macOS), the non-blocking IIoPool pipeline, and the exact commands used."
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

| `WhisperModelSize` | File | Source | Size |
|---|---|---|---|
| **`Base`** (default) | `ggml-base.bin` | downloaded from `ggerganov/whisper.cpp` | ~140 MB |
| `LargeV3Turbo` | `ggml-large-v3-turbo.bin` | downloaded from `ggerganov/whisper.cpp` | ~1.5 GB |
| `SwissGerman` (opt-in, `swiss-german` flag) | `ggml-swiss-german-turbo-q5_0.bin` | the **Flurin17/whisper-large-v3-turbo-swiss-german** fine-tune, converted + quantized by us (no public GGML exists) | 547 MB (+ 1.2 GB CoreML apple image on macOS) |

- **Default is the small multilingual `Base` model** (~140 MB, English + other languages from the same
  download). `MauiProgram.cs` selects `SwissGerman` instead **only when the `swiss-german` feature flag is
  on** — the Swiss-German bundle is a large opt-in download (`FeatureFlags.SwissGerman`, default off).
- The model resolves from `FileSystem.AppDataDirectory/models/`; if absent it downloads from the catalog
  URL (see [Where the model lives](#where-the-model-image-lives)).

**Language selection** lives in `WhisperTranscriber` (`memex/Memex.Client/Voice/WhisperTranscriber.cs`).
The Voice page defaults to **English** (`"en"`); other languages (de/fr/it/es/pt/nl + the auto modes) are
optional from a dropdown. When the `swiss-german` flag is on, the default switches to **`"auto-de-en-first"`**:
detect among German/English on the **first ~5 s** (Swiss German has no Whisper code, so constraining the
candidates resolves it to `de` → Standard German output); below the confidence threshold it falls back to
full auto-detect so French/Italian/etc. still work.

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
6. **macOS GPU (CoreML)** — the macOS client (MacCatalyst) has **no Metal** in Whisper.net's build, so
   added `Whisper.net.Runtime.CoreML` (maccatalyst-only), the runtime order **CoreML → CPU**, the
   `apple-gpu` **feature flag** (default **on for MacCatalyst** — the Base encoder is publicly hosted), and
   apple-image provisioning in `VoiceModelCatalog`. See
   [GPU on macOS](#gpu-on-macos-the-coreml-apple-image).

---

## Performance — which backend runs where

`whisper.cpp` has multiple backends; the right one is platform-specific:

| Platform | Backend | How |
|---|---|---|
| **Windows / Android** | **Vulkan (GPU)** | `Whisper.net.Runtime.Vulkan` package (csproj, conditional on the TFM) + `RuntimeOptions.RuntimeLibraryOrder = [Vulkan, Cpu]` set in `MauiProgram` under `#if WINDOWS`. CPU is the fallback if Vulkan init fails. |
| **iOS device** | **Metal (GPU)** | The base `Whisper.net.Runtime` ships a Metal-built native lib for the iOS *device* — used automatically, no config. |
| **macOS (MacCatalyst)** | **CoreML (Apple GPU / Neural Engine)** | ⚠️ Whisper.net builds whisper.cpp with `GGML_METAL=OFF` for MacCatalyst — there is **no Metal** here, so the macOS client is **CPU-only on the base runtime**. GPU comes from `Whisper.net.Runtime.CoreML` + the CoreML "apple image" (`…-encoder.mlmodelc`) placed next to the model, behind the `apple-gpu` feature flag. See [GPU on macOS](#gpu-on-macos-the-coreml-apple-image). |
| any | **CPU** | The base runtime; whisper.cpp caps at 4 threads by default, so `WhisperTranscriber` sets `WithThreads(nCPU-1)`. |

🔑 **Don't extrapolate Windows-CPU slowness to iPhone** — iPhone is Metal-GPU by default. The Vulkan work
is for the Windows dev box (e.g. Intel Arc 140V). q5_0 quantization helps **every** backend (memory
bandwidth) and is mandatory for iPhone RAM.

**Non-blocking:** the Voice page subscribes to `IObservable<TranscriptSegment>`; both the detect pass and
the transcription run on the local mesh's CPU `IoPool` (`IoPoolNames.Compile`), so the UI never freezes.
See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

---

## GPU on macOS: the CoreML apple image

The **macOS client is MacCatalyst**, and this is the one platform where "the base runtime ships Metal" is
**false**. Whisper.net builds the MacCatalyst (and iOS-CoreML) native libs with `-DGGML_METAL=OFF` (see its
`Makefile` → `maccatalyst_arm64`), so on macOS the base runtime runs Whisper **entirely on the CPU**. The
only GPU path on MacCatalyst is **CoreML** (the Apple GPU + Neural Engine).

> **Why CoreML is a big win for *this* model:** `large-v3-turbo` keeps the full 32-layer encoder but has
> only 4 decoder layers — the **encoder dominates** compute, and CoreML accelerates exactly the encoder.

**Wiring (`memex/Memex.Client`):**

| Piece | Where |
|---|---|
| `Whisper.net.Runtime.CoreML` package | `Memex.Client.csproj`, conditional on `maccatalyst` **only** (iOS device keeps its Metal runtime — don't regress it) |
| Prefer CoreML over CPU | `MauiProgram.cs` `#if MACCATALYST` → `RuntimeOptions.RuntimeLibraryOrder = [CoreML, Cpu]`, set **before** the first `WhisperFactory` |
| Opt the GGML backend into GPU | `WhisperTranscriber` → `WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = true })` |
| **Feature flag** | `Services/FeatureFlags.cs` → `apple-gpu` (device-persisted via `Preferences`), **default ON for MacCatalyst** (off elsewhere). Both the runtime-order switch and the apple-image download are gated on it. |
| **Apple image** provisioning | `VoiceModelCatalog` downloads + unzips `…-encoder.mlmodelc` next to the ggml model; **best-effort** — the CoreML runtime is built `WHISPER_COREML_ALLOW_FALLBACK=ON`, so a missing image is non-fatal (just CPU). |

**Out of the box (default Base model):** with the default `Base` model, the encoder is the publicly-hosted
`ggml-base-encoder.mlmodelc.zip` (ggerganov's whisper.cpp HF repo) — so on a fresh macOS launch Whisper runs
the encoder on the GPU **with no hosting on our side**. The Voice page shows the loaded backend (a green
`engine: CoreML` badge) once you transcribe.

**Swiss German on the GPU** (when the `swiss-german` flag is on) needs *our* encoder — `ggml-swiss-german-
turbo-encoder.mlmodelc.zip` — which has no public host (serving caveat). On the **macOS desktop app we
PACKAGE it**: the q5_0 model (547 MB) + the CoreML encoder zip (929 MB) are staged in
`memex/Memex.Client/Models/` (gitignored — large runtime artifacts) and bundled as **MacCatalyst-only
`MauiAsset`s** (csproj `ItemGroup` gated on `Exists(...)`, so the build never breaks when absent; scoped to
maccatalyst so the ~1.5 GB bundle never bloats the iPhone build). At runtime `VoiceModelCatalog.
TryCopyFromPackageAsync` copies them out of the app package by logical name — **fully offline, no download**.
A non-bundled build (or other platform) falls back to download/host; absent everything, it's CPU. The model
files are produced by the [reproduce commands below](#reproducing-the-apple-image--the-exact-commands-no-xcode-needed)
(+ `whisper-quantize … q5_0` for the bin).

### The apple image — naming, hosting, fallback

whisper.cpp auto-loads a CoreML encoder named by **its own rule** (`whisper_get_coreml_path_encoder`): drop
the `.bin`, drop a trailing `-qX_Y` quantization tag, append `-encoder.mlmodelc`. So our model maps as:

```
ggml-swiss-german-turbo-q5_0.bin   →   ggml-swiss-german-turbo-encoder.mlmodelc
```

`VoiceModelCatalog` derives the same name and downloads `ggml-swiss-german-turbo-encoder.mlmodelc.zip` from
the **same content URL directory** as the ggml model, unpacking the `.mlmodelc` directory next to it. The
encoder is **fp16** (929 MB zip / ~1.2 GB unpacked) — independent of the ggml `q5_0` quantization, and only
ever downloaded on the macOS desktop client, so the size is a non-issue (iOS uses Metal, not this image).

### Reproducing the apple image — the exact commands (no Xcode needed)

CoreML conversion needs `coremltools` + PyTorch + `ane_transformers`. whisper.cpp's `generate-coreml-model.sh`
shells out to Xcode's `coremlc` to compile the `.mlpackage` → `.mlmodelc`, but **Xcode is not required**:
coremltools ≥ 7 compiles via the OS CoreML framework (`coremltools.models.utils.compile_model`), which runs
with only the Command-Line-Tools. Convert the **same** Flurin17 fine-tune used for the GGML model:

```bash
python3 -m venv venv && ./venv/bin/pip install "numpy<2" coremltools torch transformers openai-whisper huggingface_hub
./venv/bin/pip install --no-deps ane_transformers     # pure-python reference layers; --no-deps so it can't downgrade

git clone --depth 1 https://github.com/ggml-org/whisper.cpp && cd whisper.cpp
# HF fine-tune ENCODER → CoreML .mlpackage (fp16). Run from the whisper.cpp root.
../venv/bin/python models/convert-h5-to-coreml.py --model-name large-v3-turbo \
    --model-path Flurin17/whisper-large-v3-turbo-swiss-german --encoder-only True --quantize True
#   → models/coreml-encoder-large-v3-turbo.mlpackage   (dims n_mels=128, n_audio_ctx=1500, n_audio_state=1280)
```

```python
# Compile .mlpackage → .mlmodelc WITHOUT Xcode, then validate it runs (input/output match whisper.cpp):
import numpy as np
from coremltools.models.utils import compile_model
from coremltools.models import CompiledMLModel
out = compile_model("models/coreml-encoder-large-v3-turbo.mlpackage",
                    "ggml-swiss-german-turbo-encoder.mlmodelc")   # q5_0 tag stripped → whisper.cpp's name
m = CompiledMLModel(out)
r = m.predict({"logmel_data": np.zeros((1, 128, 3000), np.float32)})
assert next(iter(r.values())).shape == (1, 1500, 1280)            # encoder output contract
```

```bash
zip -r ggml-swiss-german-turbo-encoder.mlmodelc.zip ggml-swiss-german-turbo-encoder.mlmodelc

# Upload alongside the ggml model on the AKS static-assets share (same Speech/ folder):
key=$(az storage account keys list -n memexaksfiles57ymot2ons3 -g memex-aks-rg --query "[0].value" -o tsv)
az storage file upload --account-name memexaksfiles57ymot2ons3 --account-key "$key" \
    --share-name static-assets --source ggml-swiss-german-turbo-encoder.mlmodelc.zip \
    --path "Speech/ggml-swiss-german-turbo-encoder.mlmodelc.zip"
```

Then flip the flag on (`FeatureFlags.AppleGpu`) — the next launch downloads the apple image and the encoder
runs on the Apple GPU / Neural Engine. The same **static-assets serving caveat** below applies: until the
share is mounted on the portal pod, place the unzipped `.mlmodelc` directly in the device `models/` folder.

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

## On-device AI and logging (the app)

The app is **lean by design — the only model we ship is Swiss-German Whisper.** Everything else on-device
comes from the OS:

- **Text AI = Apple Intelligence.** On iPhone and Apple-silicon Macs the LLM is the OS **FoundationModels**
  framework (iOS 26 / macOS 26 on capable hardware) — **no model is bundled**. `IOnDeviceChat`
  (`Services/OnDeviceChat.cs`) abstracts it; `AppleIntelligenceChat` bridges the async FoundationModels
  call through a tiny C-ABI Swift shim (`Platforms/{iOS,MacCatalyst}/AppleIntelligence.swift`) and runs it
  on the `IIoPool`. The Voice page prefers it when `Availability == Available` (fully offline); otherwise it
  falls back to the connected mesh. It **degrades safely**: if the shim isn't linked or the hardware/OS
  lacks Apple Intelligence, availability is `Unavailable` and the app uses the mesh — no crash.
  - 🛠️ The Swift shim needs the **iOS 26 / macOS 26 SDK** and a native-build step (compile + link into the
    app via `<NativeReference>`); the C# compiles and runs without it (reports `Unavailable`).
  - This is distinct from the **portal/cluster** Qwen-via-Ollama path (server-side, web users) — see
    [Mac local stack](/Doc/Architecture/MacLocalStack). The app never bundles or runs Qwen.
- **Minimalistic logging** (`Services/FileLogger.cs`): a **size-capped rolling file** in app data
  (`logs/memex.log`, ≤ 2× cap on disk) plus an in-memory ring buffer — bounded disk + memory, zero
  dependencies, phone-safe. Registered via `builder.Logging.AddDeviceFileLogger(...)`; the provider is a
  singleton so an in-app diagnostics view can read `Recent`.

## Code map

| Concern | File |
|---|---|
| On-device text AI (Apple Intelligence) + native shim | `memex/Memex.Client/Services/OnDeviceChat.cs`, `AppleIntelligenceChat.cs`, `Platforms/*/AppleIntelligence.swift` |
| Minimalistic on-device logging | `memex/Memex.Client/Services/FileLogger.cs` |
| Swiss-German model + catalog URLs | `memex/Memex.Client/Voice/VoiceModelCatalog.cs` |
| Detection (5 s slice, de/en-first), threads, progress, `IIoPool` | `memex/Memex.Client/Voice/WhisperTranscriber.cs` |
| Reactive entry point + model-ensure on the pool | `memex/Memex.Client/Voice/VoiceService.cs` |
| Apple image (CoreML encoder) naming + provisioning | `memex/Memex.Client/Voice/VoiceModelCatalog.cs` |
| Vulkan / CoreML runtime order + voice DI + default model | `memex/Memex.Client/MauiProgram.cs` |
| Feature flags (`apple-gpu`) | `memex/Memex.Client/Services/FeatureFlags.cs` |
| UI (progress bar, language menu, record/stop) | `memex/Memex.Client/Components/Pages/Voice.razor` |
| `Whisper.net` + `…Runtime` + `…Runtime.Vulkan` + `…Runtime.CoreML` refs | `memex/Memex.Client/Memex.Client.csproj` |
| Mic permission (iOS) + (OAuth) callback scheme | `memex/Memex.Client/Platforms/iOS/Info.plist` |
| Mic permission + audio-input entitlement (macOS) | `memex/Memex.Client/Platforms/MacCatalyst/Info.plist`, `…/Entitlements.plist` |
| Static-assets content collection (serving) | `memex/Memex.Portal.Shared/MemexConfiguration.cs` |
