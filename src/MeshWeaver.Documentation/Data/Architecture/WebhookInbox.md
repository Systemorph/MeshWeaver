---
Name: Webhook Inbox
Category: Architecture
Description: The generic webhook inbox — POST /api/hooks/{target} stores any external service's delivery verbatim as a WebhookEvent node under {target}/_Inbox. Fail-closed on a config allowlist, target-node existence and — for a target that declares one — a GitHub-style HMAC the endpoint checks itself, so a mismatched secret answers 401 instead of a green 2xx nobody could see through.
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

## Fail-closed, three times

1. **The allowlist.** Only targets listed in configuration accept deliveries; everything else is
   404 (no detail leaks about which paths exist):

   ```json
   { "WebhookInbox": { "Targets": [ "Store/Payments" ] } }
   ```

2. **The owner must exist.** A satellite must anchor under a real node — an ownerless satellite
   NotFound-storms the router — so a delivery to an allowlisted path whose node does not exist is
   refused too.

3. **The signature, when the target declares one** — see below. A target that declares no
   `SecretConfigKey` keeps the original contract and is stored unverified.

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
   action. A consumer whose target declares a `SecretConfigKey` has already been verified at the
   endpoint, and still re-verifies: the endpoint's check is what makes a drifted secret VISIBLE to
   the sender, not a replacement for the consumer's own gate.
4. **Deletes processed (and unverifiable) events** — a poison event must never loop; unprocessed
   events replay naturally on the next start, so every action taken from an event must be
   idempotent.

## 🚨 What a 2xx proves — and what it used to hide (#3312)

**A 2xx from this endpoint used to mean "I stored bytes", never "a consumer accepted them".** That
was an honest answer for a deliberately dumb inbox, but two callers read it as acceptance, and one
of them is the platform's own release path:

- `main-cd.yml` → `notify-platform-update` signs the platform-build fact with
  `secrets.PLATFORM_WEBHOOK_SECRET`;
- `node-repo-publish-bake.yml` → `register-publication` signs the publication record with the
  caller's `webhook-secret`.

Both are verified against the control instance's `Hosting:PlatformWebhookSecret` — but that
happened in the Hosting plugin's watcher, **after** the POST had already answered. So from CI three
states existed and only two were distinguishable:

| state | what CI saw | what actually happened |
|---|---|---|
| secret correct | 2xx, job green | record stored, dependents notified |
| secret **mismatched** | **2xx, job green** | the watcher dropped every delivery as unverifiable; nobody notified |
| secret empty | RED, named | caught at the caller — fixed in #3311 |

The empty case was found precisely because it failed **closed**. The mismatch failed **open** and was
byte-identical to success, so it could persist indefinitely while every dependent quietly fell back
to its schedule poll — the same consequence #3311 fixed, with nothing anywhere going red.

### The fix: a target may declare how it is authenticated

A target entry MAY name the configuration key holding its shared secret. The declaration rides on
the allowlist entry itself — a configuration section carries both a value and children — so the
record that makes a target reachable is the record that says how it is authenticated, and the two
cannot drift:

```yaml
WebhookInbox__Targets__0: "Hosting/PlatformBuilds"
WebhookInbox__Targets__0__SecretConfigKey: "Hosting:PlatformWebhookSecret"
```

It names a **key**, never a secret value: nothing here belongs in a ConfigMap, and a secret pasted
in by mistake resolves to no key and refuses everything rather than leaking.

When a target declares one, the endpoint verifies `X-Hub-Signature-256` over the raw body **before
anything is stored**:

| outcome | status | meaning |
|---|---|---|
| verifies | 200, `{"status":"accepted","signature":"verified"}` | stored, and the HMAC was checked |
| absent / malformed / does not verify | **401** | nothing stored — the state that used to be a green 2xx |
| the declared key is empty on this instance | **500** | nothing stored — OUR misconfiguration, deliberately not 401 |
| no declaration | 200, `{"status":"accepted","signature":"not-required"}` | stored unverified — the dumb contract, below |

That order is contract: target first (an unlisted path answers 404 without revealing whether it is
signed), size next (an oversized body is refused before it is hashed), signature last and always
before the node is created. A delivery that fails to verify must leave **nothing** behind — otherwise
the fix has only moved the silent drop from the consumer into the store.

Everything else keeps the dumb contract. Schemes this endpoint does not speak — Stripe's
`t=…,v1=…` — declare no key and are still verified by the consuming plugin over the verbatim stored
body, so no integration-specific code lands in the portal.

### Why the body carries the verdict too

`"verified"` and `"not-required"` are both 200, and they mean very different things to a sender that
signed. The second says this instance declares no `SecretConfigKey` for the target, so the signature
was never looked at. Without that distinction, "we verify now" would degrade silently back to "we
used to verify" the day a chart value goes missing — the same shape as the bug, one level up. Both
lanes therefore read the **body**, print the verdict into the step summary, and `::warning::` on
`not-required`.

That is a warning and not a failure **only while the declaration rolls out**: the instance half
arrives by `helm upgrade` from the private `Systemorph/Memex` env folders, never through
self-update (which is a `set image`), so failing on it would red every publish until that lands.
Escalating it to an error once the control instance answers `verified` is the remaining step, and
it is a one-line change in both lanes.

### The two shapes that were rejected, so they are not re-derived

1. **Read the record back.** The lane cannot: the inbox is anonymous to WRITE only, and reading a
   registration needs a mesh credential in CI plus a surface that does not exist.
2. **Wait for the consequence** — poll the satellites for the `repository_dispatch` the broadcast
   produces. Rejected on two counts: it is a timeout-bounded poll (the bound, not the fact, would
   decide the verdict), and it makes core's CD verdict depend on other repositories' state, which is
   the CI-to-CI coupling `main-cd.yml` has already deleted twice.

## Where the code lives

- `memex/Memex.Portal.Shared/Api/WebhookInboxEndpoints.cs` — the anonymous `POST /api/hooks/{target}`
  endpoint: allowlist check → 404, `ContentLength > MaxBodyBytes` → 413, target-node existence,
  then the `WebhookEvent` write.
- `src/MeshWeaver.Graph/Configuration/WebhookEventNodeType.cs` — the `WebhookEvent` node type, the
  allowlist reader (`WebhookInbox.ReadTargets` → `WebhookTarget(Path, SecretConfigKey)`), the HMAC
  check (`VerifyHmacSha256`) and the constants both ends share: `TargetsConfigSection`,
  `SecretConfigKeyName`, `SignatureHeader` and `MaxBodyBytes = 1024 * 1024`.
- `test/MeshWeaver.Graph.Test/WebhookInboxTest.cs` — the pair that pins the fix:
  `SignedTarget_WithTheRightSecret_IsAccepted` against
  `SignedTarget_WithADriftedSecret_IsRefused_AndStoresNothing`. Both returned `Accepted` before
  #3312; if a change ever makes them agree again, the hole is back.
