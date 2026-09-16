---
Title: "First-run setup on a PROVISIONED instance"
Abstract: >
  A fleet-provisioned instance boots fully configured and with nobody able to sign in to it. The
  first-run wizard does not appear (it keys on "no storage"), the onboarding gate's first-user
  promotion does not fire (it keys on "no admin grant"), and the only remaining door is an
  undocumented endpoint whose secret nobody set. This page is the measurement of that gap on
  pearl.meshweaver.cloud, and the design that closes it.
Thumbnail: "sitemap"
---

# First-run setup on a PROVISIONED instance

**The symptom, 2026-09-16.** `pearl.meshweaver.cloud` came up healthy, over its own certificate,
with `Features:Onboarding:InvitationOnly = true`. The maintainer signed in and was told the portal
is invitation-only. Correct, and useless: *"first user should become global admin, otherwise who is
going to attend"*. An instance nobody can administer is not provisioned, whatever the rollout says.

## Why no existing path caught it

Three mechanisms exist. Each is sound, and each keys on a condition a provisioned instance does not
meet.

| Mechanism | Fires when | Why pearl missed it |
|---|---|---|
| The first-run **wizard** (`ISetupCatalogProvider`, `PortalSetupCatalogProvider`) — storage, sign-in, models | `MeshBuilder.IsAwaitingSetup`: no `Graph:Storage` configuration **and** no complete `instance.json` | A provisioned instance is configured by its ConfigMap, rendered from its `Hosting/Deployment` record. It has storage, so it is never awaiting setup. The wizard is for an empty image an operator installs on purpose |
| **First-user promotion** (`OnboardingGate.Decide`) — the first visitor is admitted and made platform admin | No `AccessAssignment` under `Admin/_Access` | pearl's refusal *is* the evidence that a grant already exists there: with none, `isFirstUser` is true and the visitor is admitted even on an invitation-only portal. Whose grant it is, is not established here — but the design must not depend on that probe being empty |
| **`/bootstrap/first-admin`** (`BootstrapController`) — secret-gated, materialises the first admin | `Bootstrap:Secret` is configured | Provisioning never sets it. The endpoint answers `404` when unset — correct, and indistinguishable from "no such endpoint" |

So the gap is not a missing mechanism. It is that **"configured" and "administered" are different
states, and only the first one is modelled.** A provisioned instance is fully configured and has no
human in it.

## The rule

> An instance that has **no human administrator** serves a SETUP surface at its entry point, not a
> welcome page — and completing that surface requires a secret the operator holds, never merely
> arriving first.

Two halves, and the second is why the first is safe.

### Who may complete it

**A one-time setup token, minted during provisioning into the instance's own Key Vault prefix**, and
mapped into the instance like every other secret it holds.

The alternative — an allow-list of emails on the deployment record — was weighed and rejected as the
*primary* gate. It authenticates a claim the instance cannot yet verify: sign-in may itself be one
of the things setup is there to configure, and on an instance whose only provider is misconfigured
the allow-list locks the door it is meant to open. The token is a bearer secret held by whoever
provisioned the instance, which is exactly the person entitled to finish it. The record's allow-list
remains useful as a *second* condition where sign-in already works, and the design keeps room for it.

**Refusal is legible and closed.** No token, a wrong token, a token already spent, an unreadable
expected value — each refuses, and each says which, because "it says invitation only" is precisely
the message that cost a morning.

**Single use.** The token is consumed when setup completes. An endpoint that stays open until a
human remembers to remove its secret is the shape `/bootstrap/first-admin` has today, and it is the
one thing about that endpoint this design does not copy.

### What it collects, and where the answers go

Driven by what **this instance's own configuration** still lacks, not by a hard-coded form: the
sign-in routes the image can serve and whose client id or secret is unset, the model providers with
no key, mail, and anything else whose absence the health checks already report.

🚨 **The portal does not write to Key Vault, and this design does not ask for that right.** Measured
in the estate's own infrastructure code: the portal identity holds *Key Vault Secrets User* (read),
the hosting operator holds *Key Vault Secrets Officer* (write). That split is deliberate — a portal
that can write secrets is a portal whose compromise writes secrets — and it matches the fleet rule
that everything is steered from the control instance.

So the answers travel:

1. the setup surface holds a secret **in memory only** — never a node, never a log line, never a URL;
2. it hands the answers to the **control instance** over the existing signed channel;
3. the control instance writes them as vault objects under the instance's own
   `keyVaultSecretPrefix`, through the operator path that already writes `{prefix}…` objects, and
   records the non-secret half on the instance's `Hosting/Deployment` record;
4. the instance reads them at its next start, because sign-in schemes are registered while the host
   is being built and nothing re-registers them afterwards. The surface says so rather than implying
   a live effect it cannot deliver.

The naming rule is the fleet's existing one, `{keyVaultSecretPrefix}{Section}-{Key}`, so a value
collected here lands where a value provisioned by the operator would have landed. Nothing learns a
second convention.

### What does not change

Invitation-only stays on. Setup ends with exactly one platform administrator, who invites everybody
else through the path that already exists. Setup is not a second way to onboard users; it is the way
the first administrator comes to exist.

## Sequence

```
provision  ──mints──▶  {prefix}Setup-BootstrapToken           (operator → vault, never printed)
instance   ──boots──▶  no human admin ⇒ entry point is SETUP, not WELCOME
operator   ──opens──▶  /setup, presents the token             (header or form field, never a query string)
setup      ──asks──▶   what this instance still lacks         (derived from its own configuration)
setup      ──hands──▶  control instance                       (signed channel; secrets in memory only)
control    ──writes──▶ vault objects + record keys            (operator identity, the only writer)
setup      ──grants──▶ ONE platform administrator             (the existing onboarding write path)
token      ──spent──▶  refused thereafter
```

## What this replaces, and what it keeps

`/bootstrap/first-admin` stays as the headless escape hatch for scripted scaffolds and the e2e
stack. It is hardened here rather than removed: the secret may be presented as a **header** instead
of a query parameter (a query string is logged by every proxy between the caller and the pod, and
this fleet ships those logs to Loki), and the comparison is constant-time. The query form still
works, and warns.

## Open, and deliberately not decided here

- **Whose grant pearl already holds.** The refusal proves one exists; its origin is unestablished.
  The design is deliberately independent of that probe, so the answer changes nothing here.
- **The transport for the hand-off.** The control inbox is a signed channel that already exists, but
  it persists events; a secret must not land in one. Either a non-persisting endpoint on the control
  instance or an operator-run action is required, and that choice belongs with the person who owns
  the inbox.
