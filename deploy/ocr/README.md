# ocr — a stateless OCR service (Tesseract 5, packs baked in)

`ocr` reads the text in an image it is sent and says which languages that text is in. It is the
fleet's default OCR engine: the `HttpService` engine of MeshWeaver.Plugins' `MeshWeaver.Ocr`
(`Ocr:Engine=HttpService`) calls it for scanned pages and image attachments. The full design is
`Doc/Architecture/OcrService`.

- **Tesseract 5.5** (Debian trixie), with the `eng`, `deu`, `fra`, `ita` and `ind` packs plus `osd`
  for script and orientation, all **baked into the image**. Nothing is downloaded at run time.
- **Language detection** when the caller gives no hints. Hints restrict the set or extend it to any
  baked pack.
- **Stateless and quiet.** It stores nothing and logs no text, only sizes, timings and languages.
- **Cluster-internal with no egress.** It is a ClusterIP Service with no Ingress. Its
  NetworkPolicy admits only the namespaces you name and denies **all** egress, because the
  service calls nothing.

## API

```
POST /ocr?languages=de,id      body: the image bytes; Content-Type: image/* (or application/octet-stream)
                             → 200 {"text": "…", "languages": ["de", "id"], "engine": "tesseract 5.5.0"}
GET  /healthz                  process alive (startup + liveness)
GET  /readyz                   {"engine", "languages": installed, "default": the no-hint set} (readiness)
```

`languages` is optional. Codes are ISO 639-1 (`de`), or the pack name (`deu`). They are
comma-separated and may be repeated. The answer always uses ISO 639-1 codes, most text first.

| Status | When |
|---|---|
| 401 | a key is configured (`auth.existingSecret`) and `Authorization: Bearer …` is not it |
| 411 | no `Content-Length` (chunked bodies are not read) |
| 413 | body over `limits.maxBytes`, refused without reading it; or pixel count over `limits.maxPixels`, refused before decoding |
| 415 | not an image, or a non-image `Content-Type` |
| 422 | a hint with no baked pack; the error names the installed ones |
| 503 | every admission place (`limits.maxAdmitted`) is taken, refused before the body is read; or every slot (`limits.concurrency`) stayed busy for the whole deadline |
| 504 | the read did not finish within `limits.timeoutSeconds`; the Tesseract process is killed |

Every refusal is JSON `{"error": "…"}`. The client treats any non-200 as `Failed`.

### How one image is read

1. **Bound it.** The pixel count comes from the header, and the image is decoded only if it fits.
2. **Turn it upright.** EXIF orientation is applied first. Then `tesseract --psm 0 -l osd` reads
   the script and the rotation, and a rotated page is turned before recognition. A page with too
   little text for OSD is read as it is.
3. **Recognise** with the hinted packs, or the default set (`defaultLanguages`; empty means every
   baked pack).
4. **Identify the languages** paragraph by paragraph from Tesseract's TSV. Each language has its own
   function words, and a word two languages share counts for neither (`di` is Italian *and*
   Indonesian). A paragraph needs at least two hits and a clear lead. A language is reported when
   its paragraphs hold at least 15 % of the assigned words. A page read with ONE pack whose text
   could not be assigned reports that pack's language. A blank page reports none.

## Build and test

```bash
docker build -t ocr:dev deploy/ocr
```

The build is the test. In the stage that ships it runs:

- `app/test_server.py`: offline, with a fake engine. It covers hint parsing, language
  identification, and the 401/413/422/504 bounds.
- `app/smoke.py`: the real engine and packs, through the real HTTP contract, on
  `fixtures/de-id.png` (German above Indonesian). It asserts six words, umlaut included, and the
  languages `{de, id}`, with no hints, with `languages=de,id`, with the page rotated 90°, and with
  the page as black ink on a transparent background (composited onto white before reading).

It carries its own **negative controls**. The same German+Indonesian assertion must fail on
`fixtures/en.png`. A blank page must return no text and no languages. `languages=en` must report
neither German nor Indonesian.

CI (`.github/workflows/ocr-image.yml`) adds four checks:

- the smoke **goes red** when German is taken out of the default set;
- the image's own `CMD` serves the contract as uid 10001 on a read-only root, called from a sibling
  container;
- the chart renders nothing by default;
- on `main`, the image is pushed as `meshweaver.azurecr.io/ocr:tesseract-5.5-<short sha>`
  (immutable, which is what an overlay names) and `:tesseract-5.5` (the moving line), and both are
  read back.

Run it on a laptop:

```bash
docker run --rm --network none --read-only --tmpfs /tmp ocr:dev python3 smoke.py   # offline, in-process
docker run --rm -p 8080:8080 --read-only --tmpfs /tmp ocr:dev
curl -s --data-binary @deploy/ocr/fixtures/de-id.png -H 'Content-Type: image/png' 'localhost:8080/ocr'
```

`fixtures/make_fixtures.py` renders the fixtures (DejaVu Sans). Change them only on purpose.

## Install

The chart is `deploy/ocr/helm`. It is **disabled by default** (`enabled: false` renders nothing).
Turning it on for an instance is a deployment-record change the owner approves. The steps are in
`Doc/Architecture/OcrService` → *Enabling it on an instance*. It is never a hand `helm install`.

| Value | What |
|---|---|
| `enabled` | `true` to render anything at all |
| `image.repository`, `image.tag` | `meshweaver.azurecr.io/ocr` and the **immutable** `tesseract-5.5-<sha>`. The chart fails without either, and refuses a tag that is not `tesseract-<line>-<hex sha>` (`tesseract-5.5`, `latest`) |
| `networkPolicy.allowedNamespaces` | the callers' namespaces (the portal's). Empty means nothing is admitted |
| `auth.existingSecret` / `auth.secretKey` | optional bearer key from an existing Secret, the client's `Ocr:HttpService:ApiKey` |
| `defaultLanguages` | the no-hint set, e.g. `de,en`. Empty means all five |
| `limits.*` | `maxBytes` 20 MiB, `maxPixels` 40 MP, `timeoutSeconds` 150, `concurrency` 2, `maxAdmitted` 8 (bodies held at once; buffered memory ≤ `maxAdmitted` × `maxBytes`) |

With release name `ocr`, the portal's endpoint is `http://ocr:8080/ocr`.
