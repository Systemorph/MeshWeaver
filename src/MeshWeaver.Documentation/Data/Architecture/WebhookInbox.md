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

🚨 **The declaration is paired with a SLOT, and slot `__0` is not the same target on every
deployment.** The instance that receives platform builds renders
`webhookInboxTargets: ["Store/Payments", "Hosting/PlatformBuilds"]` from its `Hosting/Deployment`
record — so *its* `__0` is **Stripe**, and the platform target is `__1`. A blanket default would
demand `X-Hub-Signature-256` from Stripe, which signs `Stripe-Signature`, and 401 every payment
delivery. The chart therefore renders both slots' declarations **empty**, and each instance sets
its own on the index that actually carries the target — in the same record that sets the slot
(`extraPortalConfig` on the `Hosting/Deployment` record, which `HelmValues` projects onto
`config.memex_portal`). Set the target and its declaration in one change, never separately.

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
used to verify" the day a chart value goes missing — the same shape as the bug, one level up.

### How a publishing lane judges that answer

Both lanes — `main-cd.yml`'s *Did the inbox VERIFY the build fact?* and
`node-repo-publish-bake.yml`'s *Did the inbox VERIFY the publication record?* — read the **body**,
not the status, in a step of their own. There are **three** answers, and only one is a
misconfiguration:

| the answer carries | what it means | the lane |
|---|---|---|
| `"signature":"verified"` | this instance checked the HMAC we just sent | passes, silently |
| `"signature":"not-required"` | it ran the verdict code and declares no `SecretConfigKey` for this target — our signature was never looked at | **`::error::` + `exit 1`** |
| no `signature` field at all | it cannot answer: the instance predates the verdict body and returns a bare `200` | warns (core lane) / echoes (satellite lane) |

🚨 **The third row is the one that is easy to get wrong, and getting it wrong is worse than having
no check.** The first form of these steps tested for the single string `verified` and reported
*everything else* as `not-required`, so the **absence** of a verdict was printed as a verdict — and
named the wrong fix. Measured on 2026-09-06 (core CD run `34039122957`, the notify job at 14:44:39Z
and the satellite bake leg at 15:16:47Z): the control instance answered `200` with an **empty body**
on both lanes, because it still runs the pre-verdict endpoint, and both lanes told the reader to
provision a chart key on a pod that could not have read one.

### What makes `not-required` a misconfiguration and an absent verdict not

**The expectation is the act of signing** — there is no flag, no input and no opt-out, because a
"verification expected" knob is a skip-trapdoor by another name. Both lanes ALWAYS sign:
`main-cd.yml`'s `preflight` asserts `PLATFORM_WEBHOOK_SECRET`, the satellite lane's POST step fails
RED without `webhook-secret`, and both always send `X-Hub-Signature-256`. So inside these lanes a
`not-required` answer is a **broken pair**: the sender's secret is doing nothing, and a drifted one
would be as invisible as it was before the verdict existed. The fix is one key on the receiving
instance's record, and the error message names it.

The legitimate `not-required` case is a target with an **unsigned sender** — Stripe on
`Store/Payments`, which signs `Stripe-Signature`; GitHub on its own target — and neither of these
steps ever runs against one. A target cannot be moved into that category silently: doing so means
removing the lane's secret, at which point the POST step fails first, naming what to provision.

An absent verdict is not a receiver declining to verify; it is a receiver that **cannot answer**
because it has not rolled the endpoint yet. That half arrives by `helm upgrade` from the private
`Systemorph/Memex` env folders, never through self-update (which is a `set image`), so failing on it
would red every promoted build until a roll no repository here controls happens.

**The escalation therefore arms itself.** The moment a receiver answers a verdict at all, the
`not-required` branch becomes reachable and fatal — nothing has to be remembered, switched on, or
re-decided. An unrecognised verdict word is fatal too: this step judges the receiver's own word, so
a word it does not know is not a pass.

`InboxSignatureVerdictGuard` lifts each step's script verbatim out of its workflow and RUNS it
against synthetic answers, because a substring assertion cannot tell "classifies three states" from
"classifies two and guesses" — which is precisely the defect that shipped. That is also why both
steps read their response file through `RESP="${RESP:-/tmp/resp}"`.

### The declaration has no typed home, and it has already been lost once

`SecretConfigKey` is not a field on the `Hosting/Deployment` record — it rides in the free-form
`extraPortalConfig` bag, which every writer of that record replaces **wholesale**. Measured on the
control instance's own record:

| version | when | by | `WebhookInbox__Targets__1__SecretConfigKey` |
|---|---|---|---|
| 25 | 2026-09-05 07:42Z | a person | `Hosting:PlatformWebhookSecret` |
| 26 | 2026-09-05 09:24Z | `system-security` | **gone** — every other key in the bag survived |
| 32 | 2026-09-06 17:03Z | `system-security` | still gone |

So the declaration was provisioned by hand and removed by the next automated write, 1 h 42 min
later, with nothing red anywhere — the shape this whole page exists to end, arriving one level
further up than the last time. Until it is set again the receiver has nothing to verify with, and
the escalation above is what will say so: it fires the first time that portal answers a verdict at
all. When re-provisioning it, set it on slot **`__1`** (its `__0` is `Store/Payments`), and expect
the next unrelated record rewrite to drop it again until the key has a typed home.

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
