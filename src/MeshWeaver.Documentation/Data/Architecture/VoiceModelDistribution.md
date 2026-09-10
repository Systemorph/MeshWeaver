---
NodeType: Markdown
Name: "Voice model distribution — where a 547 MB CC BY-NC model may live"
Abstract: "The Swiss-German Whisper model is a 547 MB CC BY-NC-4.0 derivative on a PRIVATE GitHub release. That licence and that privacy decide where it may be distributed from — and they rule out the two fixes that look easiest. This is the standing rule for the asset, why the whisper container bakes it into the image, and the checklist for the next time it moves."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#b45309'/><path d='M6 7.5h12M6 12h12M6 16.5h7' stroke='white' stroke-width='1.6' stroke-linecap='round'/><circle cx='16.5' cy='16.5' r='3' fill='none' stroke='white' stroke-width='1.5'/><path d='M15.2 16.5h2.6' stroke='white' stroke-width='1.5' stroke-linecap='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Voice"
  - "Whisper"
  - "Speech"
  - "Deployment"
---

The fine-tuned Swiss-German Whisper model — `ggml-swiss-german-turbo-q5_0.bin`, **574,041,195 bytes**,
`sha256 2d56e773724a247360067b527417842b81d25ff891fed014341a6844f15ea612` — is the one artifact in this
platform that is simultaneously **too large for git**, **not ours to publish**, and **needed by a process
that has no credential**. Those three facts together decide where it may come from, and they have already
cost two broken consumers: the asset moved once and left the chart behind, then the chart's replacement had
to be chosen against constraints nobody had written down. This page writes them down.

## The asset

| | |
|---|---|
| **File** | `ggml-swiss-german-turbo-q5_0.bin` (GGML, q5_0 quantized) |
| **Size / digest** | 574,041,195 bytes · `sha256 2d56e773724a247360067b527417842b81d25ff891fed014341a6844f15ea612` |
| **Home of record** | GitHub release `voice-model-swiss-german` on **`Systemorph/MeshWeaver.Plugins`** — a **private** repo |
| **Upstream** | the [`Flurin17/whisper-large-v3-turbo-swiss-german`](https://huggingface.co/Flurin17/whisper-large-v3-turbo-swiss-german) fine-tune, converted + quantized by us (no public GGML exists) |
| **Upstream licence** | **CC BY-NC-4.0** — Attribution, **NonCommercial** |
| **Base model** | `openai/whisper-large-v3-turbo`, MIT — the *permissive* half, and not the binding one |

🚨 **The binding licence is the fine-tune's, not the base model's.** Our GGML file is a derivative of a
CC BY-NC-4.0 work, so it inherits the **NonCommercial** restriction and the attribution requirement. The
MIT base is irrelevant to redistribution: a derivative cannot be more permissive than the work it derives
from. Measured from the model card and the HuggingFace API on 2026-09-10 — `cardData.license` is literally
`cc-by-nc-4.0`, and the card says *"This model is distributed under the Creative Commons
Attribution-NonCommercial 4.0 license."*

**This was already known in one corner of the repo, and the pointer was dangling.**
`clients/voice-gateway/README.md` has carried a "Speech model license" section all along — *"**CC BY-NC 4.0
— non-commercial use only**. A personal home assistant is non-commercial use; for anything commercial, point
the whisper container at a permissively licensed model (see `deploy/whisper/README.md`)"* — but
`deploy/whisper/README.md`, the page it sends you to, said nothing about a licence at all. So the constraint
existed, in prose, in the one file least likely to be read by someone editing a Helm chart. That is precisely
why the chart's fix was chosen against it without anybody noticing, and why it is written here instead.

🚨 **The commercial question is open and is NOT answered by this page.** NonCommercial and a commercial
product are in tension, and that tension predates this issue — it applies equally to the shipped on-device
MAUI path. Nothing here changes the posture; it only stops the constraint being implicit. The escape hatch
the voice-gateway README names — *point the container at a permissively licensed model* — is a real one and
costs nothing structural: the chart takes any GGML file, so swapping to a permissive model
(`ggml-large-v3-turbo`, MIT) is a rebuild, at the price of the Swiss-German dialect accuracy that is the
whole reason for the fine-tune.

## The standing rule

> **The model is distributed only through channels that already authenticate their reader.** It is never
> placed at a URL that answers an anonymous `GET`, in this repo's releases, in a public bucket, on a public
> CDN, or in a public container image. When a consumer needs it, the consumer moves to a channel that has a
> credential — the credential does not move to the consumer.

That rule has two independent roots, and either one alone is sufficient:

1. **Licence.** NonCommercial forbids publishing this derivative for the world to download as part of a
   commercial product's infrastructure. We may use it; we may not become its distributor.
2. **A deliberate prior decision.** [#2593](https://github.com/Systemorph/MeshWeaver/issues/2593) moved the
   asset off this (public) repo on 2026-08-28 and said why, in as many words: *"this is a Systemorph-tuned
   model, and anonymous world-download was an accident of hosting."* Private hosting was recorded as
   **"a feature, not a cost"**, with the guardrail *"any future release of a large binary goes to artifact
   storage, never a release asset on this repo."*

## What broke, twice

**Move one (#2593, 2026-08-28).** The asset moved to the private repo. The commit updated three READMEs and
a shell script — and missed `deploy/whisper/helm/values.yaml` and `deploy/whisper/docker-compose.yml`, which
both pinned the old anonymous URL. `deploy/whisper` was untouched for the next fortnight.

**The consequence ([#3906](https://github.com/Systemorph/MeshWeaver/issues/3906)).** The chart's
initContainer ran `curl -fSL --retry 3 --retry-delay 5 "$MODEL_URL"` under `set -e` against a URL that
returns **HTTP 404** (measured again 2026-09-10). `-f` fails, `set -e` aborts, the pod never leaves
`Init:Error`, and speech-to-text could not be enabled on any deployment — `/api/speech/transcribe` answered
503 with the module loaded and `Speech:Enabled=false`.

🚨 **The tempting "fixes" for that failure are all forbidden**, and each converts a loud failure into a
silent one: dropping `-f`, appending `|| true`, making the initContainer non-blocking, or raising the retry
count all produce a pod that *starts* without a model and then serves nothing. A hard failure is
information; suppressing it trades a loud 404 for a silent 503.

## The three options, and why two lost

The initContainer needed a source it could read with **no credential**. There were exactly three shapes.

### ❌ 1. Give the fetch a credential

Point the initContainer at the GitHub **API asset URL** with an `Authorization` header, backed by a
Kubernetes Secret.

**Rejected on blast radius.** The only credential that can read a private release asset is one that can read
the **repository** — and `Systemorph/MeshWeaver.Plugins` is the private home of the entire AI engine, the
plugin sources, and the fleet's module trees. That trades read access to the whole private platform for one
547 MB blob, and it puts that token in a namespace secret readable by anything that can read secrets there,
to be rotated forever. It also inverts the plugin-catalog credential model, whose whole point is that the
registry holds the git credential **so that consumers never need one** (see
[Plugin registry](/Doc/Architecture/PluginRegistry) for that encapsulation). No such credential is
provisioned for this cluster today, so adopting this option means *creating* standing access that does not
currently exist.

### ❌ 2. Republish it somewhere anonymous

Put the file behind a public URL — this repo's releases, a public blob container, a CDN.

**Rejected twice over.** It is refused by the **licence** (NonCommercial: we may use the model, not
distribute it to the world), and independently by the **decision already taken in #2593**, which moved the
asset *off* exactly such a URL on purpose and recorded a guardrail against putting large binaries back onto
this repo's releases. A fix whose first step is "undo the previous fix's stated intent" needs a stronger
argument than convenience.

For completeness, the two anonymous URLs that already exist were both measured on 2026-09-10 and both answer
**404**: the old core release asset, and the portal content route
`/api/content/MeshWeaver/static/Speech/ggml-swiss-german-turbo-q5_0.bin` documented in
[On-device voice](/Doc/Architecture/OnDeviceVoice). The second is *expected* to 404 — that page's own
serving caveat says the `static-assets` share is not yet mounted on the memex-cloud portal — and it is an
**access-controlled** route in any case, so it was never an anonymous channel to begin with.

### ✅ 3. Stage the model into the image

Bake the model into the whisper container at `/models/model.bin` and let the image registry distribute it.

**Chosen.** The pod already authenticates to the registry in order to pull its own image, so this adds
**zero new credentials, zero new secrets and zero new standing access**. The model never leaves the
authenticated boundary it is in today, which is what the licence requires. It is also the option #2593 named
first — *"ACR (an OCI artifact) or the blob account the deployments already use"* — with a container image
being the form of OCI artifact that `kubelet` can already fetch, unlike a bare artifact that would need
`oras` in an image that ships only `curl`.

Two things it fixes beyond the 404:

- **The `emptyDir` was per-pod.** Every restart, reschedule and scale-up re-downloaded 547 MB. A baked model
  is a cached image layer instead.
- **The failure moved earlier and got specific.** A missing model now fails the **build**, on the machine of
  the person who can fix it, naming the file and the exact `gh release download` command — instead of failing
  as a pod in somebody else's cluster.

**The cost, stated plainly:** the image grows by 574,041,195 bytes (~547 MiB), and building it requires the
builder to place the model in the build context first (`gh auth` against the private repo). `az acr build`
uploads the whole context, so that build ships ~547 MB to the registry each time; `docker buildx build --push`
from a machine that already has the file avoids the double hop.

### What the build checks, and what it deliberately does not

The Dockerfile asserts the baked file is **at least 10 MB** and **prints its size and sha256**. The floor is
chosen to reject the things that actually happen — an interrupted download, an empty file, an HTML error page
saved under the model's name, all of them kilobytes — while sitting far below the smallest real GGML whisper
model (`ggml-tiny`, ~75 MB), so the documented model swap keeps working. The digest is **printed rather than
asserted** for the same reason: asserting it would make the swap a code change, while printing puts the one
value a reader needs to compare against this page into the build log.

## Consumers — every one of them, because the last move missed two

Anything that names this asset is a consumer, and a move must visit all of them **in the same change**:

| Consumer | What it needs |
|---|---|
| `deploy/whisper/Dockerfile` | the file in the build context at `models/model.bin` |
| `deploy/whisper/docker-compose.yml` | the same file — it is a **build** input, not a runtime mount |
| `deploy/whisper/helm/templates/deployment.yaml` | nothing at run time; the model is in the image |
| `deploy/whisper/helm/values.yaml` | an `image.tag` whose image was built **with** a model |
| `Systemorph/Memex` → `deployments/aks/memex-cloud/whisper/helm/` | a **verbatim vendored copy** of the chart above — it drifts silently and must be mirrored |
| `clients/voice-gateway/run-local.sh` + README | `gh release download` into `$WHISPER_MODEL` |
| `.gitignore` | keeps `deploy/whisper/models/` untracked (GitHub rejects any blob over 100 MB) |
| MAUI `VoiceModelCatalog` | its own catalog URL — see [On-device voice](/Doc/Architecture/OnDeviceVoice) |

🚨 **The Memex overlay is a COPY, not a dependency.** `deployments/aks/memex-cloud/whisper/helm` in the
private Memex repo is a byte-for-byte fork of this chart, including its values. Nothing makes the two agree,
and a fix applied only here leaves the deployed one broken — which is the same half-fix shape #2593
committed, one repository over.

## Checklist for the next move

1. **Name every consumer first** (the table above), not just the ones the last commit touched.
2. **Check the licence before choosing a host.** Anonymous hosting of this asset is refused; if a future model
   carries a different licence, say which licence and where you read it, in the same change.
3. **Prefer a channel whose reader is already authenticated** over one that needs a new credential. If a
   credential is genuinely unavoidable, scope it to the asset — never to the repository that holds it.
4. **Never soften the failure.** No `|| true`, no dropped `-f`, no non-blocking init, no raised retry count.
5. **Move the pins in the same pull request as the asset**, in both repositories.
6. **Verify by measuring**, and quote the status code: `curl -sSI <url>`. "The URL looks right" is not a check.

## Related

- [Centralized speech — Whisper Swiss German as a container](/Doc/Architecture/CentralizedSpeech) — what the
  container is for and how `MeshWeaver.Speech` calls it.
- [On-device voice — Whisper + Swiss German](/Doc/Architecture/OnDeviceVoice) — the MAUI path, the model
  conversion commands, and the file-share/content-URL catalog target.
- [Deployment on AKS](/Doc/Architecture/DeploymentAKS) — how a chart reaches the cluster.
