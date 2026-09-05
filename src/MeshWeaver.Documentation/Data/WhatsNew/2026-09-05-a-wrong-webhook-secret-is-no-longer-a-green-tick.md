---
Name: A wrong webhook secret is no longer a green tick
Category: Fix
Description: A webhook target can now declare which configuration key holds its shared secret. When it does, the portal checks the signature itself and answers 401 — so a secret that has drifted between sender and instance fails visibly, instead of being accepted, stored, and silently discarded by the consumer with every job still green.
Icon: ShieldKeyhole
Order: -20260905
---

# A wrong webhook secret is no longer a green tick

The webhook inbox — `POST /api/hooks/{target}` — was built to be dumb on purpose: it stores a
delivery verbatim and leaves signature checking to whichever plugin consumes it. That is still true
for integrations whose schemes the portal does not speak.

But it meant a `200 OK` said *"I received bytes"*, and callers heard *"you were accepted"*. Those are
the same answer when the shared secret is right, and **also** the same answer when it is wrong: the
delivery is stored, the sender sees a green request, and the consumer then discards every event as
unverifiable. Nothing is logged on the sending side, nothing turns red, and the only visible symptom
is downstream work that quietly never happens. A missing secret was caught — it fails closed — but a
*mismatched* one is byte-identical to success and could persist indefinitely.

## What changes

A target may now name the configuration key holding its secret:

```yaml
WebhookInbox__Targets__0: "Hosting/PlatformBuilds"
WebhookInbox__Targets__0__SecretConfigKey: "Hosting:PlatformWebhookSecret"
```

The declaration rides on the allowlist entry itself, so the record that makes a target reachable is
the record that says how it is authenticated — there is no second list to fall out of step. And it
names a **key**, never a secret value.

When a target declares one, the portal verifies `X-Hub-Signature-256` over the raw body *before it
stores anything*:

- it verifies → `200` with `{"status":"accepted","signature":"verified"}`
- it is absent, malformed, or does not match → **`401`**, and nothing is stored
- the declared key is empty on this instance → **`500`**, and nothing is stored — that is the
  instance's own misconfiguration, and saying `401` would blame the caller for it

A target that declares nothing behaves exactly as before, and says so: `"signature":"not-required"`.
That distinction is deliberate. "Verified" and "not required" are both `200`, and a sender that
signed its request needs to know which one it got — otherwise the day a configuration value goes
missing, checking would stop happening again with nothing to see.

## What you may notice

If you operate an instance that receives signed webhooks, declare the key for those targets and a
drifted secret will announce itself at the source instead of going quiet. Nothing changes for
targets you leave undeclared.
