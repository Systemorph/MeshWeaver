# s1-extract — a stateless System-1 extractor (GLiNER on ONNX)

`s1-extract` finds **organisation** and **person** spans in text it is sent, using
[`urchade/gliner_multi-v2.1`](https://huggingface.co/urchade/gliner_multi-v2.1) (Apache-2.0,
multilingual, CPU). The model is exported to ONNX at build time and runs on ONNX Runtime.

It is the candidate finder of the **name check**. Deciding whether a name is a client is the
**CRM's** call, and the CRM lives on the instance that owns it. That instance runs the deterministic
matcher (the blocking half) and calls this service to find names the CRM does *not* know yet, which
then go to review. The service:

- holds **no names**, no list, no state; texts are never logged or stored;
- decides nothing: it returns spans with a label and a score;
- is **cluster-internal**: a ClusterIP service with no Ingress, and a NetworkPolicy that admits
  only the namespaces you name;
- admits a caller only with a **projected ServiceAccount token** minted for the audience
  `s1-extract`. It validates the token with a TokenReview (its own ServiceAccount is bound to
  `system:auth-delegator`) and checks the ServiceAccount against an allow-list. There is no shared
  secret.

## API

```
POST /v1/extract   {"texts": ["…"], "labels": ["organization", "person"], "threshold": 0.5}
                 → {"model": "urchade/gliner_multi-v2.1", "results": [[{"start","end","label","score","text"}]]}
GET  /healthz      process alive
GET  /readyz       model loaded (the readiness probe only — startup and liveness use /healthz)
```

Limits: 256 texts per call and 2,000 characters per text (`limits.*`). A larger batch is split by
the caller.

## Build

```bash
docker build -t s1-extract:dev deploy/s1-extract            # local
```

The registry image is built by CI, never by hand: `.github/workflows/s1-extract-image.yml` builds
on every PR that touches this folder and, on `main`, pushes `meshweaver.azurecr.io/s1-extract` with
two tags — `gliner-multi-2.1-<short sha>` (immutable) and `gliner-multi-2.1` (the moving model
line). An estate overlay names the **immutable** tag, so the values an approved chart apply binds
name the bytes that run. The run's summary prints the tag and its digest.

The `export` stage downloads the model from Hugging Face, exports it to ONNX and **smoke-tests the
export**: it must find an organisation in a fixed sentence, or the build fails. The runtime stage
copies the exported model, runs the offline service tests (`app/test_server.py`, fake model, no
network) and sets `HF_HUB_OFFLINE`.

## Install

The chart is `deploy/s1-extract/helm`. Required values:

| Value | What |
|---|---|
| `image.repository` | `<registry>/s1-extract`. The chart fails without it. |
| `auth.allowedServiceAccounts` | `system:serviceaccount:<ns>:<sa>` of each caller. The chart fails when this is empty. |
| `networkPolicy.allowedNamespaces` | the callers' namespaces. Empty means nothing is admitted. |

A caller mounts a projected token and sends it as `Authorization: Bearer …`:

```yaml
volumes:
  - name: s1-token
    projected:
      sources:
        - serviceAccountToken: { audience: s1-extract, expirationSeconds: 3600, path: token }
```

Installing it on a shared cluster goes through that estate's approved operations lane, never a
hand `helm install`.

## Local run (no cluster)

```bash
docker run --rm -p 8080:8080 -e S1_AUTH=none -e S1_ALLOW_NO_AUTH=1 s1-extract:dev
curl -s localhost:8080/v1/extract -H 'content-type: application/json' \
  -d '{"texts":["Contoso Ltd hired Jane Roe."]}'
```

`S1_AUTH=none` is refused unless `S1_ALLOW_NO_AUTH=1` is also set, so a values typo cannot open the
service.
