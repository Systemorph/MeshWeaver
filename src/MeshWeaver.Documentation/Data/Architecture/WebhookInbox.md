---
Name: Webhook Inbox
Category: Architecture
Description: The generic webhook inbox — POST /api/hooks/{target} stores any external service's delivery verbatim as a WebhookEvent node under {target}/_Inbox. Fail-closed on a config allowlist and target-node existence; the CONSUMING plugin verifies the signature itself, so no integration-specific code lives in the portal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/></svg>
---

# Webhook Inbox

External services (Stripe, GitHub, …) notify a MeshWeaver portal by HTTP POST — they cannot
authenticate as a mesh user, and their payloads are integration-specific. The webhook inbox is the
ONE generic bridge: it stores each delivery **verbatim** as a mesh node and leaves every
integration-specific concern — above all signature verification — to the consuming plugin. No
payment (or other integration) code ever lands in the portal.

## The endpoint

`POST /api/hooks/{target}` (anonymous — see `WebhookInboxEndpoints` in `Memex.Portal.Shared`)
stores the delivery as a `WebhookEvent` node at `{target}/_Inbox/{id}`:

- **`Body`** — the raw request body, verbatim: the exact bytes an HMAC signature was computed
  over.
- **`Headers`** — the request headers, minus credentials (`Authorization`, `Cookie`, … are never
  persisted). Signature headers (`Stripe-Signature`, `X-Hub-Signature-256`, …) survive verbatim.
- **`ContentType`**, **`ReceivedAt`**.

## Fail-closed, twice

1. **The allowlist.** Only targets listed in configuration accept deliveries; everything else is
   404 (no detail leaks about which paths exist):

   ```json
   { "WebhookInbox": { "Targets": [ "Store/Payments" ] } }
   ```

2. **The owner must exist.** A satellite must anchor under a real node — an ownerless satellite
   NotFound-storms the router — so a delivery to an allowlisted path whose node does not exist is
   refused too.

Bodies over 1 MB are refused with 413. The event is written under the System identity (the
anonymous caller has no write access anywhere; the allowlist is the authorization to *store* —
never to *act*).

## The consumer's contract

A plugin that receives webhooks:

1. Ships the **target node** (e.g. `Store/Payments`) and documents its endpoint URL
   (`{portal}/api/hooks/Store/Payments`).
2. **Watches its inbox** with a live children query over `{target}/_Inbox` from its hub
   initialization, processing strictly one event at a time.
3. **Verifies authenticity itself** over the stored raw `Body` + `Headers` — e.g. Stripe's
   `t=…,v1=…` HMAC-SHA256 with the endpoint's signing secret. Only a verified event authorizes an
   action.
4. **Deletes processed (and unverifiable) events** — a poison event must never loop; unprocessed
   events replay naturally on the next start, so every action taken from an event must be
   idempotent.

## 🚨 What a 2xx does NOT prove — the mismatched-secret hole (open, #3312)

**A 2xx from this endpoint means "I stored bytes", never "a consumer accepted them".** That is an
honest answer — the inbox verifies nothing by design — but two callers currently read it as
acceptance, and one of them is the platform's own release path:

- `main-cd.yml` → `notify-platform-update` signs the platform-build fact with
  `secrets.PLATFORM_WEBHOOK_SECRET`;
- `node-repo-publish-bake.yml` → `register-publication` signs the publication record with the
  caller's `webhook-secret`.

Both verify against the control instance's `Hosting:PlatformWebhookSecret`, in the Hosting plugin's
watcher — **after** the POST has already answered. So from CI three states exist and only two are
distinguishable:

| state | what CI sees | what actually happens |
|---|---|---|
| secret correct | 2xx, job green | record stored, dependents notified |
| secret **mismatched** | **2xx, job green** | the watcher drops every delivery as unverifiable; nobody is notified |
| secret empty | RED, named | caught at the caller — fixed in #3311 |

The empty case was found precisely because it fails **closed**. The mismatch fails **open** and is
byte-identical to success, so it can persist indefinitely while every dependent quietly falls back
to its schedule poll — the same consequence #3311 fixed, with nothing anywhere going red. The
property that would close it: **a run that publishes must be able to fail because the record was not
ACCEPTED, not merely because it was not sent.**

### Why it is still open, and what it costs

The verdict has to come from something holding the shared secret, and the only thing that holds it
is the consuming plugin — after the response. Three shapes were considered; each is blocked, and the
blocker is worth writing down so the next attempt does not re-derive it:

1. **Teach the inbox an optional per-target signature requirement.** The generic shape — a target
   may declare that deliveries must carry a verifying `X-Hub-Signature-256`, with the secret named
   by CONFIGURATION KEY (`Hosting:PlatformWebhookSecret`, the key already provisioned) so no secret
   value is duplicated and the two ends cannot drift. A mismatch then answers 401, an accepted
   delivery answers a body stating `signature: verified`, and the lanes assert **the verdict**
   rather than the status code — so an instance that declares no requirement is refused by name too,
   never read as "verified". This is the right fix, and it is blocked on **delivery, not design**:
   the declaration is instance configuration, the portal host ships no `appsettings.json` in this
   repo (it lives in MeshWeaver.Plugins), and a chart value reaches a running portal only through
   `helm upgrade` from the private `Systemorph/Memex` env folders — never through the self-update,
   which is a `set image`. Landing the lane half before the instance half would red core CD on every
   publish, indefinitely.
2. **Read the record back.** The lane cannot: the inbox is anonymous to WRITE only, and reading a
   registration needs a mesh credential in CI plus a surface that does not exist.
3. **Wait for the consequence** — poll the satellites for the `repository_dispatch` the broadcast
   produces. Rejected on two counts: it is a timeout-bounded poll (the bound, not the fact, would
   decide the verdict), and it makes core's CD verdict depend on other repositories' state, which is
   the CI-to-CI coupling `main-cd.yml` has already deleted twice.

Until (1) lands, the residual is tracked by #3312 and #2235, and the honest statement in both lanes'
step summaries stands: *the mesh STORED the delivery; whether the wave ran is read on the instance
and on the dependents, never here.*

## Where the code lives

- `memex/Memex.Portal.Shared/Api/WebhookInboxEndpoints.cs` — the anonymous `POST /api/hooks/{target}`
  endpoint: allowlist check → 404, `ContentLength > MaxBodyBytes` → 413, target-node existence,
  then the `WebhookEvent` write.
- `src/MeshWeaver.Graph/Configuration/WebhookEventNodeType.cs` — the `WebhookEvent` node type and the
  constants both ends share: `TargetsConfigSection = "WebhookInbox:Targets"` and
  `MaxBodyBytes = 1024 * 1024`.
