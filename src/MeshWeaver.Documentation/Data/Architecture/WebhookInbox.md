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

The registry lists node-repo roots of type `Space`, `Store/Plugin`, and `Store/Catalog` — see
[Plugin Registry](/Doc/Architecture/PluginRegistry) for how packages are served and installed.
