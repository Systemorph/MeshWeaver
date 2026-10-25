---
Name: OCR Service — local Tesseract in the cluster
Category: Architecture
Description: The fleet's default OCR engine is a stateless Tesseract 5 pod (core deploy/ocr) with the eng, deu, fra, ita and ind packs and osd baked into its image, language detection when no hints are given, no egress, and a chart that renders nothing until an instance's deployment record turns it on. The portal reaches it through MeshWeaver.Ocr's HttpService engine.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M7 8h10M7 12h10M7 16h6"/></svg>
---

# OCR Service — local Tesseract in the cluster

Scanned pages and image attachments are read by a **local** OCR engine, never a cloud service.
By default that engine is **Tesseract 5**, run as one stateless pod per instance from core's
`deploy/ocr`. It is built and shipped the same way as the two local model services that came
before it, [`deploy/whisper`](../CentralizedSpeech) and `deploy/s1-extract`:

- the engine's data, here the language packs, is **baked into the image**;
- CI builds the image and pushes it to the fleet registry;
- the image line is a declared release channel;
- a standalone chart runs it, and only an approved chart apply installs it.

```
  portal (MeshWeaver.Ocr, Ocr:Engine=HttpService)
      │  POST http://ocr:8080/ocr?languages=de,id   (image bytes)
      ▼
  ocr pod — Tesseract 5.5 + eng deu fra ita ind + osd      NetworkPolicy: ingress from the portal's
      │  {"text": "…", "languages": ["de","id"],             namespace only, egress: none
      │   "engine": "tesseract 5.5.0"}
```

## The contract

The client is the `HttpService` engine of MeshWeaver.Plugins' `MeshWeaver.Ocr`. The service
implements that engine's contract exactly:

| | |
|---|---|
| Request | `POST {Endpoint}`, with the image bytes as the body and `Content-Type: image/*`. Optional `?languages=de,id` hints in ISO 639-1, plus `X-File-Name` and an optional `Authorization: Bearer {ApiKey}` |
| Answer | `200 {"text": "…", "languages": ["de", "id"], "engine": "tesseract 5.5.0"}`. Languages are ISO 639-1, the language with the most text first |
| Anything else | a JSON `{"error": "…"}` with 401 / 411 / 413 / 415 / 422 / 503 / 504, which the client reports as `Failed` |

There is no default engine. A portal whose configuration declares none answers `NotConfigured` and
sends an image nowhere (policy `declared-not-derived`). This service is what the declaration
points at.

## Languages: baked, detected, hinted

**Baked.** The image carries `eng`, `deu`, `fra`, `ita` and `ind`, plus `osd`. These are Debian's
`tesseract-ocr-*` packages, Tesseract's `tessdata_fast` models under Apache-2.0. Indonesian is
in the set on purpose. Adding a language is a Dockerfile line, a new image, and an entry in the
language-identification table (`deploy/ocr/app/ocr.py`). Without that entry the language is read
but never reported.

**Detected.** With no hints, the page is read with every pack in the default set, and the text is
then identified paragraph by paragraph. Each language has a list of distinctive function words.
A word two languages share counts for neither (`di` is Italian and Indonesian, `la` is French and
Italian). A paragraph is assigned only with at least two hits and a clear lead. A language is
reported when its paragraphs hold at least 15 % of the assigned words. Tesseract itself has no
language identification. What `osd` contributes is the **script** and the **orientation**. A page
it reads as rotated is turned upright before recognition, and a phone photo's EXIF orientation is
applied first.

**Hinted.** A hint is the caller saying what the page is. It **restricts** the set (`languages=de`
reads with `deu` alone). It may also **extend** past a narrowed default: an instance can set
`defaultLanguages: de,en`, and a caller can still ask for `id`. A hint with no baked pack is
refused with 422, naming the installed packs, because reading with a pack the caller did not ask
for answers a different question. A page read with one pack whose text the identifier cannot
assign reports that pack's language. A blank page reports none.

## Bounds

| Bound | Default | Enforced |
|---|---|---|
| body size | 20 MiB (the client's `Ocr:MaxImageBytes`) | from `Content-Length`, before the body is read |
| pixels | 40 MP (an A4 page at 600 dpi is about 35 MP) | from the image header, before decoding, so a decompression bomb never reaches Tesseract |
| time | 150 s per image, slot wait included | the Tesseract process is killed at the deadline, and the answer is 504. That is under the client's 180 s `Ocr:TimeoutSeconds`, so the service, not the client, names the cause |
| concurrency | 2 Tesseract processes per pod, one thread each | the rest wait inside their own deadline |
| network | ingress from the named namespaces only, **no egress** | the chart's NetworkPolicy. The pod mounts no ServiceAccount token |

## Proof it works, and that the proof can fail

The image build **is** the test, as for s1-extract. The stage that ships runs two things.
`app/test_server.py` is offline, with a fake engine. `app/smoke.py` runs the real engine and packs
through the real HTTP contract on `deploy/ocr/fixtures/de-id.png`, a German paragraph above an
Indonesian one. The smoke asserts six words, including the umlaut in `Gebäude`, which `eng` alone
reads as `Gebaude`. It also asserts the languages `{de, id}`, three times: with no hints, with
`languages=de,id`, and with the page rotated 90°.

Its negative controls are part of the same run:

- the identical German+Indonesian assertion must **fail** on `fixtures/en.png`;
- a blank page must come back with no text and no languages;
- `languages=en` must report neither German nor Indonesian.

`.github/workflows/ocr-image.yml` adds checks a build cannot show:

- it re-runs the smoke with German removed from the default set and is red unless the smoke
  **fails**;
- it runs the image's own `CMD` (uid 10001, read-only root) and calls it from a sibling container;
- it renders the chart three ways: nothing with `enabled: false`, exactly Deployment + Service +
  NetworkPolicy with `egress: []` when enabled, and a refusal when enabled without an immutable tag.

On `main` the workflow pushes `meshweaver.azurecr.io/ocr:tesseract-5.5-<short sha>`. That tag is
immutable, and it is the one an estate overlay names. It also pushes `:tesseract-5.5`, the moving
line for a laptop, which is declared in `.github/release-channels.json` so retention never prunes
it. Both are read back from the registry. The base image comes from Docker Hub in the build, as
for the other images core builds ([Docker Hub in CI](../DockerHubInCi), *What this does not cover*).

## Enabling it on an instance

The chart is **disabled by default**, and nothing in core turns it on. Enabling OCR on an instance
changes what that instance's cluster runs and where its documents go, so it is a
**deployment-record change the owner approves**. It is never a hand `helm install`, and never a
change to the image pin of the portal. In the estate's deployment repository (for the fleet,
`Systemorph/Memex`), following the whisper precedent (`Deployments/memex-cloud-whisper`):

1. **Vendor the chart.** Copy `deploy/ocr/helm` to `deployments/aks/<env>/ocr/helm`.
   `scripts/check-vendored-charts.py` holds it byte-identical.
2. **Write the values overlay.** In `values.<env>.yaml`, set `enabled: true`,
   `image.repository: meshweaver.azurecr.io/ocr`, the **immutable** `image.tag` printed in the
   publishing run's summary, and `networkPolicy.allowedNamespaces: [<env namespace>]`. Optionally
   add `defaultLanguages` and `auth.existingSecret`.
3. **Record the chart release.** Add a `Hosting/ChartRelease` record `Deployments/<env>-ocr` that
   names the chart, the values file and the release `ocr`. The owner then approves the
   `ApplyChart` InstanceAction that installs it.
4. **Point the portal at it.** On the instance's deployment record, set `extraPortalConfig`:
   `Ocr__Engine = HttpService` and `Ocr__HttpService__Endpoint = http://ocr:8080/ocr`. The portal
   chart renders both keys only when they are set (`deploy/helm/templates/memex-portal/config.yaml`).
   Then roll the portal through the usual governed action.
5. **Verify on the running instance.** `/readyz` on the pod names the five packs. A scanned
   document ingested there carries its text and `languages`.

The client half, the `HttpService` engine, ships with MeshWeaver.Plugins' `MeshWeaver.Ocr`. Step 4
does nothing on a portal whose build does not include it.

## Related

- [End Vision — Document Ingestion](../DocumentIngestionEndVision): G10 (images to a local OCR
  extractor) and G11 (local model services), which this page partly delivers.
- [Centralized speech](../CentralizedSpeech): the whisper precedent, a model baked into an image
  behind one HTTP contract.
- [Release Editions](../ReleaseEditions): the declared `tesseract-5.5` service line.
