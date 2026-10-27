# ollama — the self-hosted model service (Ollama on CPU, models baked in)

One cluster-internal pod that serves every AI job that does not need a frontier model. It speaks
the OpenAI wire format at `http://<release>:11434/v1`:

| Job | Model | Endpoint |
|---|---|---|
| image recognition / OCR, translation, summaries | `qwen3.6` (qwen3.6:35b-a3b, Q4_K_M + vision) | `POST /v1/chat/completions` |
| vector embeddings | `bge-m3` (1024 dims, multilingual) | `POST /v1/embeddings` |

The design is in `Doc/Architecture/LocalModelService`: the models and why, capacity, latency,
worked examples for every job, the portal keys, and how to enable it on an instance. In short:

- **Baked and pinned.** `app/bake.sh` pulls each model at build time and **fails** unless its
  manifest sha256 is the one pinned in the `Dockerfile`. Nothing is fetched at run time. The pod
  has no egress, a read-only root, and runs as uid 10001.
- **Ready means loaded.** `app/entrypoint.sh` loads every model in `OLLAMA_PRELOAD` before it writes
  `/tmp/ready`, which the readiness probe requires.
- **The model reads; the code computes.** Extract counts and values with the model, then add them up
  in code. qwen3.6 reads a game screenshot's tiles correctly and still gets their totals wrong.

## Build and test

```bash
docker build -t ollama:dev deploy/ollama                       # the real image, ~25 GB
docker build -t ollama:small --build-arg VISION_MODEL=qwen3.5:0.8b \
    --build-arg VISION_MANIFEST_SHA256= deploy/ollama          # a laptop variant (unpinned vision model)
docker run -d --name oll -p 11434:11434 --read-only --tmpfs /tmp ollama:small
docker exec oll /app/smoke.sh http://127.0.0.1:11434 --vision  # every assertion, negative controls included
```

`app/smoke.sh <base> [--vision]` is the one proof, and three places run it:

- the build runs the catalogue, capability and multilingual embedding checks;
- `ollama-image.yml` runs the same checks against the image's own CMD from a sibling container,
  plus a negative control that must go red;
- the chart's `helm test` runs the vision, translation and summary checks on the real node pool,
  because a hosted runner cannot hold the 26 GiB model.

## Install

The chart is `deploy/ollama/helm`. It is **disabled by default** (`enabled: false` renders
nothing), and enabling it is a deployment-record change the owner approves. It refuses any tag
but the immutable `<ollama>-<models>-<short sha>` that CI publishes
(`meshweaver.azurecr.io/ollama:0.33.2-qwen3.6-bge-m3-<sha>`).

| Value | What |
|---|---|
| `enabled` | `true` to render anything at all |
| `image.repository`, `image.tag` | `meshweaver.azurecr.io/ollama` and the immutable tag |
| `networkPolicy.allowedNamespaces` | the callers' namespaces (the portal's). Empty means nothing is admitted |
| `models.vision`, `models.embedding`, `models.embeddingDimensions` | the names the image was built with (`qwen3.6`, `bge-m3`, 1024), preloaded and smoke-tested |
| `ollama.contextLength` | default context per request (8192). qwen3.6's 262,144 maximum does not fit a CPU node |
| `ollama.numParallel`, `ollama.maxQueue` | generations per model at a time (1) and the queue behind them (64) |
| `resources` | requests 4 vCPU / 28 GiB, limits 12 vCPU / 32 GiB: the models are ~26 GiB resident |

With release name `ollama`, the portal's base URL is `http://ollama:11434/v1`.
