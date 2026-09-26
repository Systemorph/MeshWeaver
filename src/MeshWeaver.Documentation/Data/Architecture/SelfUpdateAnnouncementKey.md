---
Name: Self-Update Announcement Key
Category: Architecture
Description: How a deployment announces its own self-update to the control instance with a key of its OWN instead of the fleet-wide inbox secret — the record names the vault object, the inbox accepts it as a per-sender key, the consumer lets it cause nothing but that record's self-update events, and a record that declares one is no longer announceable with the fleet secret. With the rollout order and the owner commands.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/></svg>
---

# Self-Update Announcement Key

A portal hands a detected release to the control instance as ONE signed event
([Self-Update on the Control Lane](/Doc/Architecture/SelfUpdateControlLane)). Until this change the
only key that event could be signed with was the fleet-wide inbox secret
(`Hosting:PlatformWebhookSecret` on the control instance, `Hosting:ControlInbox:Secret` on the
sender) — the same secret core CD signs build facts with, every repository's triage lane signs
failures with, and every portal's Feedback plugin signs feedback with. A signature therefore proved
**possession, not identity** (MeshWeaver.Plugins#1913).

That is tolerable for a portal Systemorph administers. It is not for a customer-administered pod
(`pearl`): handing it the fleet secret would let whoever administers that pod forge build facts,
triage events and self-update announcements for **any** deployment. So `pearl` stayed on hand rolls
(MeshWeaver#5757) until it could announce with a key of its own.

## The shape

| part | where | what it does |
|---|---|---|
| **The declaration** | `Hosting/Deployment` record → `announcementKeySecret` (`DeploymentContent.AnnouncementKeySecret`) | names the deployment's OWN vault object — a NAME, never a value. Declaring it is also the binding (below). |
| **The sender's mount** | the deployment's pod: `Hosting__ControlInbox__Secret` ← that vault object | the self-updater signs with whatever `Hosting:ControlInbox:Secret` holds — **no sender-side code changed**. |
| **The receiver's mount** | the control instance: `Hosting__PlatformWebhookSecret__<deploymentId>` ← the same vault object | a CHILD of the inbox's shared key — the shape `Hosting:ModuleReportSecret:<deployment>` already has. |
| **Ingestion** | core `WebhookInbox.Deliver` → `SenderKeyOf` | when the target's shared secret does not verify, the first child of the target's `SecretConfigKey` section whose value does is accepted as a **per-sender key**; the delivery is stored and `DeliveryResult.SenderKey` names the child. The inbox only decides whether the bytes are worth storing — it never widens what a delivery may do. |
| **Authorization** | MeshWeaver.Plugins `PlatformBuildInboxWatcher` → `AnnouncementKeys` → `SelfUpdateRouting` | re-verifies the stored body and decides what the key may cause (the rules below). |

## The rules the consumer enforces

1. **A deployment key authorises ONLY that deployment's self-update events** —
   `self-update-available` (which opens a Roll) and `self-update-restart-pending` (a Restart). A
   build fact, a bundle publication, a triage/feedback event or an aks-ops callback that verifies
   only with a deployment key is refused, logged and deleted: it opens nothing.
2. **The key is selected by the deployment the event NAMES**, so a key can only ever verify events
   naming its own deployment. `pearl`'s key signing an event that names `build` verifies with no key
   and is refused.
3. **The record must claim the key.** A deployment-key announcement is acted on only when the record
   it resolves to IS that deployment and declares `announcementKeySecret`; otherwise it is refused
   with a message naming which half is missing.
4. **A record that declares its own key is no longer announceable with the fleet secret.** This is
   the per-record half of Plugins#1913's phase 3: possession of the fleet secret no longer announces
   for that deployment. A record that declares nothing keeps the fleet-secret path — the migration
   bridge for the instances that hold it today (`memex-cloud`, `build`).

## Rollout order — mount first, declare last

Each step is safe on its own, and none leaves a deployment signing with a key the running control
plane cannot select:

1. **Core** (this change) — `DeploymentContent.AnnouncementKeySecret`, `WebhookInbox.SenderKeyOf`.
   Nothing reads a sender key until the control instance mounts one.
2. **MeshWeaver.Plugins** — the consumer rules above (`AnnouncementKeys`). It compiles against the
   sealed platform set, so it lands after the core half is sealed.
3. **Mint the vault object** (owner act, below). 🚨 Before the Memex change merges: a declared vault
   object the vault does not hold fails the WHOLE CSI mount of the pod that names it — and the
   control instance is one of those pods.
4. **Systemorph/Memex** — `Deployments/pearl` declares `announcementKeySecret` and maps the object to
   `Hosting__ControlInbox__Secret`; the control instance's record and overlay map the same object to
   `Hosting__PlatformWebhookSecret__pearl`.
5. **Reconcile** the control instance, then `pearl` (a Roll renders no SecretProviderClass). The
   acceptance reading is `pearl`'s boot line `apply=control-lane`, then its next announcement
   opening `selfupdate-roll-pearl-…` on the control instance.

For a FLEET instance adopting its own key later, the same order holds with one extra care: the
record's `announcementKeySecret` DECLARATION is what switches the fleet secret off for it (rule 4),
so declare it only after both mounts are live — otherwise its announcements are refused until the
pod is reconciled.

## Owner commands

Minting the value is an operator act; no agent creates or reads a secret value. The value is
generated in the command and never printed (`--output none` — `az keyvault secret set` otherwise
echoes the secret it stored):

```bash
az keyvault secret set --vault-name Systemorph --name pearl-Hosting-AnnouncementKey \
  --value "$(openssl rand -hex 32)" --output none
az keyvault secret show --vault-name Systemorph --name pearl-Hosting-AnnouncementKey \
  --query "{name:name, enabled:attributes.enabled, updated:attributes.updated}" -o table
```

The second command reads back metadata only — never `value`.

## What is NOT covered

- **Rotation** is a flag day for one deployment: replace the vault object's value, then Reconcile
  the control instance and the deployment. A set of identifiers per record would make it ordinary;
  nothing needs it yet.
- **Feedback from a deployment-key sender** is refused by rule 1 — the Feedback hand-over signs with
  the same `Hosting:ControlInbox:Secret`. A deployment that must send feedback needs a triage-scoped
  key of its own, which is not designed here.
- **The fleet secret's own holders** (CD, the repository lanes, the other portals) are unchanged. The
  fleet-wide removal Plugins#1913 describes waits for every portal to announce with its own key.

## Related

- [Self-Update on the Control Lane](/Doc/Architecture/SelfUpdateControlLane) — the hand-over this
  key signs.
- MeshWeaver.Plugins `Hosting/SelfUpdateAnnouncementIdentity` — the original design and the residual
  it closes.
