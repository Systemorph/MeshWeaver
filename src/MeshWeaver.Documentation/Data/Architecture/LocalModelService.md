---
Name: Local Model Service — Ollama in the cluster
Category: Architecture
Description: The fleet's self-hosted model service is one Ollama pod per instance (core deploy/ollama) with qwen3.6 (image recognition and OCR, translation, summaries) and bge-m3 (embeddings) baked into its image, OpenAI-compatible at /v1, no egress, and a chart that renders nothing until an instance's deployment record turns it on. The page covers the models and why, the capacity it needs, the configuration each consumer sets, worked examples for every job, and how to enable it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3"/></svg>
---

# Local Model Service — Ollama in the cluster

*"We should have all services like image recognition, translation, summary, … running on local
ollama"* and *"also vector embeddings"* (maintainer, 2026-10-25). This page is the design that
answers both. Each instance runs **one** self-hosted model service. It is an Ollama pod built from
core's `deploy/ollama`, and it serves every AI job that does not need a frontier model:

| Job | Model | Called as |
|---|---|---|
| Image recognition, OCR of photos and screenshots | `qwen3.6` (qwen3.6:35b-a3b, Q4_K_M + vision projector) | `POST /v1/chat/completions` with an `image_url` part |
| Translation (German, English, French, Italian, Indonesian, …) | `qwen3.6` | `POST /v1/chat/completions` |
| Summaries, descriptions, classification | `qwen3.6` | `POST /v1/chat/completions` |
| Vector embeddings (search, chunk retrieval) | `bge-m3` (567M, **1024** dimensions, multilingual) | `POST /v1/embeddings` |

It is built and shipped the same way as the services that came before it,
[`deploy/whisper`](../CentralizedSpeech) and [`deploy/ocr`](../OcrService):

- the weights are **baked into the image** and pinned by manifest digest;
- CI builds the image and pushes it to the fleet registry;
- the image line is a declared release channel;
- a standalone chart runs it, and only an approved chart apply installs it.

```
  portal ──────────────────────────────────────────────────────────────┐
   MeshWeaver.Ocr (Ocr:Engine=OllamaVision)        POST /v1/chat/completions (image)
   Embedding provider (Embedding:Provider=Ollama)  POST /v1/embeddings
   Provider/OpenAICompatible model nodes           POST /v1/chat/completions
   scripts, Code cells, kubectl exec curl          any of the above
                                                                       ▼
  ollama pod — Ollama 0.33.2 (CPU) + qwen3.6 + bge-m3, baked     NetworkPolicy: ingress from the
      http://ollama:11434/v1   (OpenAI-compatible)                portal's namespace only, egress: none
```

## Why one Ollama, and why these two models

**One server, OpenAI-compatible.** Every consumer the platform has already speaks the OpenAI wire
format. The embedding provider (`OllamaEmbeddingProvider`) posts to `{base}/embeddings`. The
`Provider/OpenAICompatible` model provider posts chat completions. The `OllamaVision` OCR engine
(MeshWeaver.Plugins #4204) posts `{Endpoint}/chat/completions` with the page as an image. So one
service with one base URL, `http://ollama:11434/v1`, serves all of them. A laptop running Ollama
answers the same URLs at `http://localhost:11434/v1`. The image names the models `qwen3.6` and
`bge-m3`, the names a Mac's Ollama uses, so one configuration works in both places.

**`qwen3.6` is the model the maintainer already runs.** The baked build is `qwen3.6:35b-a3b`, the
same source build (`qwen3.6-source:35b-a3b-mtp-q4_K_M-20260824`, config digest `99afa5bb…`) that
a Mac's `qwen3.6:latest` resolves to. It is a mixture-of-experts model: 35B parameters, about 3B
active per token. On a CPU that matters more than the size, because generation speed follows the
active parameters, so a 3B-active MoE generates faster than a dense 7–9B model, while it reads and
translates at 35B quality. It is natively multimodal: the manifest carries a separate vision
projector, and `/api/show` reports `vision`, `completion`, `tools` and `thinking`. Licence:
Apache-2.0.

Measured on a Mac (maintainer, 2026-10-25): qwen3.6 read a 1080×1312 game-inventory screenshot in
**24 s with every tile count correct**, but **its arithmetic on the totals was wrong**. That
result sets the rule for every consumer:

> 🚨 **The model reads; the code computes.** Ask the model to transcribe or extract (counts,
> labels, values) as structured output, then sum, convert and compare in code. Never let a model's
> own total, conversion or percentage reach a user or a record.

**`bge-m3` for embeddings.** It is multilingual, so a German query finds an Indonesian document.
The build checks exactly that. It has 1024 dimensions, it is small (1.2 GB), it is fast on CPU,
and it is MIT-licensed. `EmbeddingOptions` already knows it (`bge-m3` → 1024).

**Considered and rejected.** These are all real models in the Ollama library:

| Candidate | Why not the default |
|---|---|
| `qwen3.5:9b` / `qwen3.5:4b` (dense, multimodal) | Fits more easily, but a dense 9B generates *slower* on CPU than the 3B-active MoE, and reads worse. It is the fallback line if a pool cannot hold ~28 GiB (build-arg `VISION_MODEL`) |
| `qwen3-vl:8b` / `:4b` | A vision-only line. It would need a second model for translation and summaries, which means two models resident instead of one |
| `qwen3.6:35b-a3b-q8_0` | Twice the memory (≈38 GB) for a small gain in reading quality. Not worth it on CPU nodes |
| GPU node pool | Not needed for the jobs above, and adding one is a cluster change for the owner (see *Capacity*) |

## The contract

Ollama's OpenAI-compatible surface, unchanged. With the release named `ollama`, the base URL inside
the portal's namespace is `http://ollama:11434/v1`:

| Endpoint | Use |
|---|---|
| `POST /v1/chat/completions` | chat, translation, summary, and images as `image_url` data URIs. `"reasoning_effort": "none"` turns qwen3.6's thinking off, which is what a transcription or translation wants on CPU |
| `POST /v1/embeddings` | `{"model":"bge-m3","input":[…]}` returns one 1024-dimension vector per input |
| `GET /v1/models` | the catalogue: `qwen3.6:latest`, `bge-m3:latest` (plus the pinned tags they alias) |
| `GET /api/version`, `POST /api/show` | Ollama's own; the probes and the smoke use them |

There is **no API key**: Ollama ignores the `Authorization` header. The boundary is the
NetworkPolicy. A client that insists on a key can send any placeholder. Ollama's management API
(`/api/pull`, `/api/delete`, `/api/create`) is reachable from the admitted namespaces, but it
cannot change anything. The root filesystem is read-only, so the baked models cannot be deleted or
overwritten, and egress is denied, so nothing can be pulled.

## Worked examples

The examples call `$OLLAMA`, a base that answers the service:

- **On a laptop** (Ollama, or this image under `docker run -p 11434:11434`), it is
  `OLLAMA=http://localhost:11434`, and every command below runs as written.
- **In the instance's namespace**, the portal, a job or a script calls the Service directly at
  `OLLAMA=http://ollama:11434`. That is the normal path, and the one the configuration keys below
  use.
- **On a fleet cluster, from outside**, there is no supported path, and that is deliberate. The
  service has no Ingress, and operating a fleet instance goes through the control instance's
  Hosting surfaces ([Operating from the Portal](../OperatingFromThePortal)). The in-cluster proof
  is the chart's `helm test` (see *Proof*). A `kubectl exec` or
  `port-forward` into a fleet namespace is **break-glass**, for when the control plane itself
  cannot act, and is recorded as such. It is never the way to use or check this service.

### 1. Read an image (OCR, a screenshot, a photo)

```bash
img=$(base64 < screenshot.png | tr -d '\n')
curl -s $OLLAMA/v1/chat/completions -H 'Content-Type: application/json' -d @- <<EOF | jq -r '.choices[0].message.content'
{"model": "qwen3.6", "temperature": 0, "reasoning_effort": "none", "max_tokens": 1500,
 "messages": [{"role": "user", "content": [
   {"type": "text", "text": "Transcribe all text in this image exactly, line by line. Output only the text."},
   {"type": "image_url", "image_url": {"url": "data:image/png;base64,$img"}}]}]}
EOF
```

To extract values for computing, ask for JSON with `response_format`, then do the arithmetic in
code:

```bash
curl -s $OLLAMA/v1/chat/completions -H 'Content-Type: application/json' -d @- <<EOF \
  | jq -r '.choices[0].message.content' | jq '[.tiles[].count] | add'      # ← the TOTAL is computed here
{"model": "qwen3.6", "temperature": 0, "reasoning_effort": "none", "response_format": {"type": "json_object"},
 "messages": [{"role": "user", "content": [
   {"type": "text", "text": "List every inventory tile as JSON: {\"tiles\":[{\"name\":string,\"count\":integer}]}. Read counts exactly; do not add anything up."},
   {"type": "image_url", "image_url": {"url": "data:image/png;base64,$img"}}]}]}
EOF
```

**In the portal:** OCR at ingestion (MeshWeaver.Plugins #4204) sends scanned pages and image
attachments to this endpoint once an instance declares:

```yaml
Ocr__Engine: "OllamaVision"
Ocr__OllamaVision__Endpoint: "http://ollama:11434/v1"
Ocr__OllamaVision__Model: "qwen3.6"
# Ocr__OllamaVision__MaxOutputTokens / Ocr__TimeoutSeconds: raise the timeout for CPU (see Latency)
```

Tesseract (`Ocr__Engine=HttpService`, [OCR Service](../OcrService)) stays the fast engine for
clean printed pages: about 0.6 s a page against tens of seconds here. Choose the vision engine
where Tesseract fails: photos, screenshots, tables, handwriting, mixed layouts.

### 2. Translate

```bash
curl -s $OLLAMA/v1/chat/completions -H 'Content-Type: application/json' -d '{
  "model": "qwen3.6", "temperature": 0, "reasoning_effort": "none",
  "messages": [
    {"role": "system", "content": "Translate the user text to English. Answer with the translation only."},
    {"role": "user", "content": "Pihak asuransi sudah memeriksa kerusakan pada bangunan dan akan membayar tagihan itu sebelum akhir bulan."}]}' \
  | jq -r '.choices[0].message.content'
# → The insurance company has already inspected the damage to the building and will pay the bill before the end of the month.
```

### 3. Summarise

```bash
jq -n --rawfile text report.txt '{model: "qwen3.6", temperature: 0, reasoning_effort: "none", max_tokens: 300,
  messages: [{role: "system", content: "Summarise the user text in three sentences, in the language of the text."},
             {role: "user", content: $text}]}' \
  | curl -s $OLLAMA/v1/chat/completions -H 'Content-Type: application/json' -d @- \
  | jq -r '.choices[0].message.content'
```

With the default `contextLength: 8192`, a summary request holds about 6,000 words of input. Split a
longer text, summarise the parts, then summarise the summaries; or raise `ollama.contextLength`
in the instance's values, which costs memory (see *Capacity*).

### 4. Embed

```bash
curl -s $OLLAMA/v1/embeddings -H 'Content-Type: application/json' \
  -d '{"model": "bge-m3", "input": ["Das Haus ist groß.", "The house is big.", "Rumah itu besar."]}' \
  | jq '[.data[].embedding | length]'
# → [1024, 1024, 1024]
```

**In the portal:** the embedding provider (MeshWeaver.Plugins `MeshWeaver.Hosting.Embeddings`) and
the migration Job that sizes the vector column read the same three keys. Set them on **both**
`config.memex_portal` and `config.memex_migration`:

```yaml
Embedding__Provider: "Ollama"
Embedding__Endpoint: "http://ollama:11434/v1"
Embedding__Model: "bge-m3"          # → 1024 dimensions, derived from the model name
Embedding__ApiKey: "ollama"         # a placeholder: see the note below
```

> 🚨 **Changing the embedding model is a RE-INDEX, never a config flip.** Vectors from two models
> are not comparable, and the dimension differs. memex embeds with OpenRouter's
> `openai/text-embedding-3-small` today, at **1536** dimensions; bge-m3 has **1024**. On the next
> migration run, `MeshNodeEmbeddingBackfill` reconciles every partition's `embedding` column to the
> provider's dimension. That **NULLs every existing vector** and rebuilds the HNSW index, then
> re-embeds every row from `name + node_type`. Content chunks are re-embedded only when their
> files are re-indexed. Until that finishes, search degrades to the lexical half of the hybrid
> query. Plan the switch as a migration with its own window, with the owner's go. Never ship it in
> passing.

> ⚠️ **Placeholder key.** The embedding provider itself is keyless when `Provider=Ollama`
> (`EmbeddingCapability`). memex's content-indexing gate (`MemexConfiguration`) still activates
> only when `Embedding:ApiKey` is non-empty, so an Ollama-backed instance sets a placeholder.
> Remove the record's vault mapping for `Embedding__ApiKey` at the same time, so no cloud key is
> sent to the local service.

### 5. Use it from an agent (a model provider and a tier)

The portal's `Provider/OpenAICompatible` provider registers the service's chat model as an
ordinary `LanguageModel` node. The configuration is **declared** in the deployment record, not
created by hand (policy `declared-not-derived`):

```yaml
Features__Ai__Providers__OpenAICompatible: "true"
OpenAICompatible__Endpoint: "http://ollama:11434/v1"
OpenAICompatible__ApiKey: "ollama"       # Ollama ignores it; the provider requires a non-empty key
OpenAICompatible__Models__0: "qwen3.6"
OpenAICompatible__DataResidency: "Eu"     # it runs in the instance's own cluster
```

The placeholder key is the same one the Mac local stack sets ([Mac Local Stack](../MacLocalStack)):
the chat-client factory refuses an empty key even though Ollama reads none. `Embedding:Model` is
kept out of the chat catalogue automatically (`OpenAICompatibleModelSync`).
To route background jobs to it, label the model with the `utility` tier
([Model Tiers](/Doc/AI/ModelTiers)). Utility covers thread names, descriptions, classification
and triage. Agents that declare `modelTier: utility` then run locally, and everything else keeps
its cloud model. Measure the latency on the instance before you label it: a utility job nobody
waits for tolerates seconds, and an interactive round does not.

### 6. Use it from a script

Any OpenAI client works. Point it at the base URL and use any key:

```python
from openai import OpenAI
client = OpenAI(base_url="http://ollama:11434/v1", api_key="ollama")
text = client.chat.completions.create(model="qwen3.6", temperature=0,
    extra_body={"reasoning_effort": "none"},
    messages=[{"role": "user", "content": "Translate to German: The report is not complete."}]
).choices[0].message.content
vectors = [d.embedding for d in client.embeddings.create(model="bge-m3", input=["a", "b"]).data]
```

```csharp
// Microsoft.Extensions.AI over the OpenAI SDK — the same IChatClient the platform's providers build.
var openAi = new OpenAIClient(new ApiKeyCredential("ollama"),
    new OpenAIClientOptions { Endpoint = new Uri("http://ollama:11434/v1") });
IChatClient chat = openAi.GetChatClient("qwen3.6").AsIChatClient();
```

A script running in the portal's namespace reaches the service directly. Nothing outside the
cluster does: there is no Ingress, and there will not be one. An operator's `port-forward` into a
fleet namespace is break-glass (see the start of this section).

## Capacity — what the cluster has, and what this needs

Measured on 2026-10-25 (`az aks show`, `kubectl describe nodes`, read-only), the fleet cluster has
**no GPU pool**:

| Pool | VM | Nodes | Per node |
|---|---|---|---|
| `system` | Standard_D4s_v3 | 2 | 4 vCPU, 16 GiB |
| `silos` (user; the portals) | Standard_D16s_v5 | 7 (min 2, **max 7**) | 16 vCPU, 64 GiB; 15.7 vCPU / 57.8 GiB allocatable |
| `cibuildv6`, `cidind` | Standard_D16s_v6 | CI only | — |

On `silos`, the scheduled **requests** used 48–85 % of each node's CPU and 30–49 % of its memory.
That leaves 2.3–8 vCPU and 30–40 GiB requestable per node. Actual use was far lower: 2–7 % CPU and
22–31 % memory. The default requests are **4 vCPU / 28 GiB** with limits of 12 vCPU / 32 GiB, so
the pod fits on any one `silos` node without a pool change. On a node with few free requests it
competes with portal pods for CPU up to its limit, but only while a request runs. The pool is at
its maximum (7). A second large tenant, such as a second instance's model service, would need
the owner to raise `silos` max or add a pool:

> **Proposal (not applied — a cluster change is the owner's):** if two or more instances enable
> the service, add a dedicated user pool `models` of `Standard_E8s_v5` (8 vCPU, **64 GiB**,
> memory-optimised), min 0 / max 2, tainted `workload=models:NoSchedule`. Point the chart at it
> with `nodeSelector` + `tolerations`. A GPU pool (`Standard_NC4as_T4_v3`, one T4 16 GB) would cut
> an image read to a few seconds. It is worth proposing only once CPU latency has been measured
> and found wrong for a real job.

The image is ~25 GB, of which 23.8 GB is weights. A node pulls it once: a few minutes from the
in-region registry, then it is cached. The Deployment uses `strategy: Recreate`, so a roll never
needs a second 28 GiB node for a surge. The cost is a short gap, which callers absorb as a failed
call.

## Latency

**Not yet measured on the fleet's nodes.** The helm test (below) prints the time of every call,
and its first run on an instance is the measurement. Read that, not the estimate. What is known:

- On a Mac (Apple GPU, maintainer, 2026-10-25), a 1080×1312 screenshot reads in **24 s**.
- On CPU, generation follows the ~3B active parameters. Expect on the order of 5–15 tokens/s on a
  D16s_v5 under the 12-vCPU limit. Reading the image itself (the vision encoder plus ~1–2k prompt
  tokens) is the expensive part, and expect **a minute or more per image** until measured.
  Translation and summaries of a paragraph take seconds to tens of seconds. Embeddings
  (bge-m3) take milliseconds to well under a second per sentence.
- An attempt to measure CPU-only on the Mac's own Ollama (`num_gpu: 0`) did not finish its first
  image read within 10 minutes. That runner keeps the vision projector off the GPU and runs MTP
  speculative decoding, so it is **not** evidence for an x86 node in either direction. It is
  recorded here so nobody repeats it as a proxy.

Bound the callers accordingly. The OCR client's `Ocr:TimeoutSeconds` must exceed one CPU image
read. With `OLLAMA_NUM_PARALLEL=1`, requests to one model queue (up to `OLLAMA_MAX_QUEUE=64`)
behind each other inside their own deadlines. A batch of 50 screenshots at ingestion is therefore
a background job of tens of minutes, not something a user waits for.

## Proof it works, and that the proof can fail

**In the image build** (`deploy/ollama/app/bake.sh`, every build):

1. Each model is pulled by tag, and the build **fails** unless the stored manifest's sha256 is the
   pinned one (`VISION_MANIFEST_SHA256`, `EMBED_MANIFEST_SHA256`). A re-pointed upstream tag is a
   red build that names both digests, never a silent model swap.
2. `app/smoke.sh` runs against the build's own server. The catalogue must list both names.
   `/api/show` must report `vision`+`completion` for qwen3.6 and `embedding` for bge-m3, with the
   control that bge-m3 must not claim vision. Embeddings must have 1024 dimensions. A German
   sentence and its Indonesian twin must each lie closer to their English translation than to an
   unrelated German sentence, by a clear margin (measured: 0.93 / 0.92 against 0.43 / 0.39). The
   swapped pair must fail the same check.

**In CI** (`.github/workflows/ollama-image.yml`, pull requests that touch it):

- the image's own `CMD` runs as uid 10001 on a read-only root with no capabilities. It is called
  from a sibling container on an internal network, exactly as the portal calls the Service;
- a **negative control**: the same smoke told to expect 768 dimensions must go red **on the
  dimension assertion**. A smoke that cannot fail proves nothing;
- the chart renders nothing by default. Enabled, it renders Deployment + Service + NetworkPolicy
  (`egress: []`) + the test Pod, and the test Pod does **not** carry the Service's selector. A
  missing or moving tag is refused.

**In the cluster** (`helm test ollama`, the chart's `templates/tests/smoke.yaml`): a hosted CI
runner has 16 GB of memory and cannot hold the vision model, so its proof runs where it runs. The
test pod uses the same image, runs `smoke.sh --vision` against the Service and asserts:

- the German+Indonesian fixture page reads with all six words, `Gebäude`'s umlaut included;
- the English page does **not** yield the German words (the negative control);
- Indonesian translates to English with *insur…*, *damage* and *invoice*/*bill*, and the
  untranslated source fails that same check;
- a one-sentence summary is shorter than its input and keeps the subject;
- everything the build checked.

The test pod has its own labels and an ingress rule in the NetworkPolicy, so the policy admits it
without selecting it. Each line it prints carries the call's duration, and that output is the
latency reading above.

The mechanics (pinning, aliases, read-only non-root runtime, preload gate, every smoke assertion
including `--vision`) were also run end to end on a laptop with a small variant
(`--build-arg VISION_MODEL=qwen3.5:0.8b --build-arg VISION_MANIFEST_SHA256=`): 0 failures.

## Runtime behaviour

- **Preload, then ready.** `entrypoint.sh` starts `ollama serve` and loads every model named in
  `OLLAMA_PRELOAD`, which the chart sets from `models.vision` and `models.embedding`, with
  `keep_alive: -1`. Only then does it write `/tmp/ready`, which the readiness probe requires. A
  model that fails to load (memory, a renamed alias) leaves the pod **not ready**, and the reason
  is in its log. The first caller after a restart therefore never pays the 23 GB load.
- **Bounded context.** Ollama's default context for qwen3.6 is its maximum, 262,144 tokens, and
  that KV cache alone does not fit a CPU node. The chart sets `OLLAMA_CONTEXT_LENGTH` to 8192. A
  request may still ask for a different `num_ctx` through Ollama's native API.
- **Quiet.** Ollama logs request lines (method, path, status, duration), never prompt or image
  content. The pod stores nothing.
- **No recycle needed.** The service holds no mesh state. A new image is a chart apply with a new
  immutable tag; nothing in the portal needs a recycle. A portal picks up **configuration** changes
  (the keys above) only through its own roll.

## Enabling it on an instance

The chart is **disabled by default**, and nothing in core turns it on. Enabling it changes what the
instance's cluster runs and which model reads its documents, so it is a **deployment-record change
the owner approves**. It is never a hand `helm install`. In the estate's deployment repository,
follow the whisper and OCR precedents:

1. **Vendor the chart.** Copy `deploy/ollama/helm` to `deployments/aks/<env>/ollama/helm`. The
   chart check holds it byte-identical.
2. **Write the values overlay.** Set `enabled: true`, `image.repository:
   meshweaver.azurecr.io/ollama`, the **immutable** `image.tag` printed in the publishing run's
   summary (`0.33.2-qwen3.6-bge-m3-<sha>`), and `networkPolicy.allowedNamespaces: [<env
   namespace>]`. Optionally add a `nodeSelector` and resources.
3. **Record the chart release.** Add a `Hosting/ChartRelease` record `Deployments/<env>-ollama` that
   names the chart, the values file and the release `ollama`. The owner approves the `ApplyChart`
   InstanceAction that installs it.
4. **Prove it in the cluster.** Run the chart's test (`helm test ollama`) and record the timings it
   prints. 🚨 The estate's chart lane (`aks-ops-chart.py` / `ApplyChart`) does **not** run chart tests
   today (searched 2026-10-25: no `helm test` in Systemorph/Memex's workflows). Until a lane step
   does, running it is a break-glass `helm test` by an operator, recorded as such. The lane step is
   listed under *What is not built yet*.
5. **Point the consumers at it**, one at a time, on the instance's deployment record
   (`extraPortalConfig`). Each key reaches the portal through the chart's pass-through:
   - OCR: `Ocr__Engine`, `Ocr__OllamaVision__Endpoint`, `Ocr__OllamaVision__Model` (example 1;
     needs a portal build that includes MeshWeaver.Plugins #4204);
   - a chat model / utility tier: the `OpenAICompatible__*` keys (example 5);
   - embeddings: the `Embedding__*` keys on portal **and** migration, **only** as a planned re-index
     (example 4).

   Then roll the portal through the usual governed action.
6. **Verify on the running instance.** An image ingested there carries its transcript, and a
   search finds it.

## What is not built yet

- **Image description at ingestion still uses the default chat model.** `ChatClientImageDescriber`
  (MeshWeaver.Plugins, `ContentCollections.Indexing.Graph`) captions images with the mesh's
  default chat model. On memex today that model has no image input. Pointing it at `qwen3.6`
  needs a configuration seam in that describer, which is a Plugins change. Until then, image text
  arrives through OCR (#4204).
- **A translation or summary service contract of its own.** Today those are chat completions with a
  system prompt (examples 2 and 3). A typed `ITranslator` / `ISummarizer` that defaults to this
  service would make the choice declarative per partition. That is a Plugins design, not started.
- **The chart lane does not run `helm test`.** The vision half of the proof, and with it the
  latency reading, exists only once the estate's `ApplyChart` lane runs the release's tests and
  returns their log (Systemorph/Memex, `aks-ops-chart.py`).
- **No instance runs it yet.** Enabling it on memex is the owner's record change (*Enabling it*).

## Related

- [OCR Service](../OcrService): the Tesseract engine, the fast path for clean pages.
- [Centralized speech](../CentralizedSpeech): the whisper precedent, a model baked into an image
  behind one HTTP contract.
- [Vector search](../VectorSearch): where the embeddings go, and the dimension rules.
- [Model Tiers](/Doc/AI/ModelTiers) and [Model provider setup](/Doc/AI/ModelProviderSetup): how a
  model node, its provider and its tier are declared.
- [End Vision — Document Ingestion](../DocumentIngestionEndVision): G11, local model services.
- [Release Editions](../ReleaseEditions): the declared `0.33.2-qwen3.6-bge-m3` service line.
