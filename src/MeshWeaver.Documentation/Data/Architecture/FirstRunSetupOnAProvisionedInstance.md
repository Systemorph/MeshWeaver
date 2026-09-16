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

## Who runs it, and who ends up administering

Maintainer, 2026-09-16: *"and we hand it out and admin the subs"*.

| | SME tier | Enterprise tier |
|---|---|---|
| Who runs the wizard | **Systemorph.** We choose the sign-in provider, enter the model keys, select the plugins | the client's own people — the estate, the directory and the subscription are theirs |
| What the client receives | a portal that already works, and **nothing in Azure**: no subscription role, no cluster, no vault | their own estate, which they may administer |
| Who administers the instance | **the client's person, named during setup** | whoever they name, usually themselves |
| The setup link | never leaves Systemorph: minted at Provision, read out of the vault by memex, used minutes later from the maintainer's machine ⇒ default lifetime **4 hours** | has to reach another organisation ⇒ longer, still bounded (3 days) |

So **the wizard must be able to name an administrator who is not the person completing it.** A flow
that can only crown the current session is wrong for the tier we actually operate. The named person
is granted platform admin through the existing onboarding write path, and — when they are not the
one at the keyboard — invited through the **platform's invitation path**, never a hand-written mail:
the instance stays invitation-only afterwards, so a Pending invitation is what admits them.

**Hand-out is an explicit end state, not an implied one.** Setup ends with: an administrator, the
instance's configuration, its plugins installed, and a report of *what the client will see at first
sign-in* — which provider's button, which plugins, and anything still unconfigured. A hand-over
nobody can describe is a hand-over that comes back as a support question.

## What is hard-coded today, and where each key should live

Maintainer: *"essentially everything you have now hard-coded in the config, e.g. that msft login is
being used. ⇒ unhardcode and get through wizard."* Measured on `mesh/Deployments/pearl.json` and its
overlay, 2026-09-16:

| Key, as it appears today | Today | Should be |
|---|---|---|
| `signIn.provider` = `Custom`, `signIn.microsoftClientId` | record | **wizard**, record for an instance we pre-configure. Which provider a client signs in with is theirs, not a line in our file |
| `Authentication__Microsoft__ClientSecret` → `pearl-Authentication-Microsoft-ClientSecret` | vault object, named on the record | **wizard → vault.** The name stays derived, so a collected value lands where a provisioned one would |
| `email.enabled` = `false` | record | **wizard.** Without mail an invitation-only instance cannot admit anybody |
| model/LLM providers and keys (`pearl-OpenRouter-ApiKey`, never created) | nothing — the record deliberately states none | **wizard → vault** |
| `pluginRepos[]` (one mount, `Plugins` → memex.meshweaver.cloud) | record | **both.** The wizard offers the catalog and can ADD a mount; the record keeps what a pre-configured instance ships with |
| `preInstall` = `["Essentials"]` | record | **both**, with the manifest's `preInstalled` flag shown for what it is (below) |
| `ConnectionStrings__memex` → `pearl-db-connection` | vault object, operator-written | **record + platform.** On SME the database is ours: provided, never asked |
| `Ai__KeyProtection__MasterKey` | vault object, operator-written, never regenerated | **platform.** It seals the instance's stored values; a wizard must not offer to change it |
| `PluginCatalog__RegistryToken` | vault object, operator-written | **platform** |
| `Features__Onboarding__InvitationOnly`, `Hosting__Deployment`, `Hosting__ReportTo`, `PreWarm__*` | record | **record.** Fleet decisions about how the instance is operated, not about the client |

The rule behind the table: **what the client's world answers goes in the wizard; what the fleet
decides about operating the instance stays on the record; what the estate supplies is platform.** An
instance that states none of the wizard half must be fully configurable through the wizard rather
than refusing to start; an instance we pre-configure keeps stating them.

## Which requirements are asked, by tier

Maintainer, correcting an earlier draft: *"let's leave db fixed for sme tier."*

| Requirement | SME | Enterprise |
|---|---|---|
| database, storage and file shares, registry, observability, certificates | **platform-provided.** Shown as provided: not a field, not a "create it for me" offer, not a blank that can be got wrong | the client's estate supplies them, so the wizard asks and guides |
| the sign-in provider, its client id and secret | asked | asked |
| model/LLM keys | asked | asked |
| mail | asked | asked |

The tier is read from the deployment record, and it drives both *which* requirements are asked and
*who is expected to answer them*. Asking everything everywhere is how a person ends up typing a
connection string for a database they do not own.

## The plugin catalog in the wizard

The wizard shows the catalog of the registry the instance is mounted on, lets the person select what
to install, and lets them add further registry mounts — the record's `pluginRepos` concept extended,
not a second one invented.

**`preInstalled: true` is respected and shown, never silently overridden.** A package whose manifest
carries it installs itself whatever anyone ticks. That flag is how pearl came up with
`GoogleMaps/Gallery` and `MyAi/Panel` — content whose store-delivered module never arrived — and
therefore with two NodeTypes it could not compile, a readiness gate that refused, and a 503 at the
edge. So such a package appears as **always installed** rather than as an unticked box that installs
anyway, and the surface says out loud when a package needs a store-delivered module no image ships.
MeshWeaver.Plugins#1959 turns the flag off for `Hosting`, which is fleet operations and has no
business on every instance; as manifests stop claiming it, entries become real choices with no
change to the selection logic.

### Plugins declare what they need

A plugin can need configuration before it works — *"plugins may require additional config, e.g.
database requires setup. we can give help there on how to get it set up, in azure"*. The declaration
belongs in the same `index.json` that already carries `preInstalled`, `tier`, `module` and
`requires`: each configuration key, whether it is a **secret** (vault) or plain (record), what stops
working without it, and — where the requirement means *"there must be a resource"* — how to obtain
it: the `az` command or portal path, what to copy back, and what it costs, kept next to the field it
fills.

- **Adoption is additive.** A manifest gains a `configuration` block and the wizard starts asking.
- **A plugin that declares nothing** behaves exactly as today: installed, nothing asked.
- **Selection and satisfaction are separate states.** A selected plugin whose configuration is
  incomplete is visible as such — installed-but-unconfigured — in the **health surface's own
  vocabulary** (`required_modules`, the bake gate), not a second one. This is the one rule that
  keeps the wizard from rebuilding pearl's shape: a requirement nothing could satisfy, reported
  Degraded from first boot, with a 503 at the edge and no explanation.
- **"Create it for me", where we legitimately can.** On the SME tier the instance runs inside
  Systemorph's estate, so the control instance can provision such a resource through the existing
  operator path instead of making a person do it by hand. It is an OPTION on the requirement, never
  the only path, and it is **unavailable on enterprise tier**, whose estate is the client's own
  subscription.

## The vault, and what a prefix is not

Each client instance gets **its own Key Vault**, the one its record already names — not a shared
vault with per-instance prefixes. Measured 2026-09-16: one managed identity is federated into every
namespace of the shared cluster (atioz, memex, memex-cloud, build, pearl), so a prefix is a **naming
convention, not a boundary** — every instance's pod can read every other instance's objects.

🚨 **A per-client vault isolates nothing on its own.** The prerequisite is the identity half: an
instance needs **its own identity, federated only to its own namespace**, before a separate vault is
a separate trust domain. Until that lands, a per-instance vault is better bookkeeping and the same
blast radius — worth saying plainly rather than implying a boundary that is not there.

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
