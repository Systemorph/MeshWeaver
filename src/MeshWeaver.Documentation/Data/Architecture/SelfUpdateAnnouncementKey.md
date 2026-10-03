---
Name: Self-Update Announcement Key
Category: Architecture
Description: How a deployment announces its own self-update to the control instance with a key of its OWN instead of the fleet-wide inbox secret — the record names the vault object, the inbox accepts it as a per-sender key, the consumer lets it cause nothing but that record's self-update events, and a record that declares one is no longer announceable with the fleet secret. The key is generated on the deployment (/Admin/Settings/ControlLane), registered on the control instance, and tested on the deployment — no vault command, no mount, no restart.
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
(an SME client instance, `fabrikam`): handing it the fleet secret would let whoever administers that pod forge build facts,
triage events and self-update announcements for **any** deployment. So that instance stayed on hand rolls
(MeshWeaver#5757) until it could announce with a key of its own.

## The shape

| part | where | what it does |
|---|---|---|
| **The declaration** | `Hosting/Deployment` record → `announcementKeySecret` (`DeploymentContent.AnnouncementKeySecret`) | names the deployment's OWN vault object — a NAME, never a value. Declaring it is also the binding (below). |
| **Generating** | the deployment: **/Admin/Settings/ControlLane** → **Generate key** | the deployment's own global administrator generates the key ON the deployment, on its server. It is stored in that instance's own encrypted store as `Hosting:ControlInbox:Secret` ([Instance Secrets](../InstanceSecrets)), and the self-updater signs with it from the next announcement on, with no restart. The key is shown ONCE, with its fingerprint. |
| **Registering** | the control instance: **/Hosting/Integrations/Deployment/<id>** → **Announcement key** → paste → **Save** | a global administrator of the control instance pastes the key the deployment's administrator sent over a secure channel. It is stored in the control instance's own encrypted store as `Hosting:PlatformWebhookSecret:<deploymentId>`, where the inbox verifies with it at once. No vault copy is written. The fingerprint shown must equal the one the deployment's administrator sent separately. |
| **The sender's mount** (optional) | the deployment's pod: `Hosting__ControlInbox__Secret` ← that vault object | the older way to deliver the same key. The self-updater signs with the key entered in the portal if there is one, else with this mount. |
| **The receiver's mount** (optional) | the control instance: `Hosting__PlatformWebhookSecret__<deploymentId>` ← the same vault object | a CHILD of the inbox's shared key — the shape `Hosting:ModuleReportSecret:<deployment>` already has. The key registered in the portal takes precedence over it. |
| **Ingestion** | core `WebhookInbox.Deliver` → `SenderKeyOf` | when the target's shared secret does not verify, the ONE child of the target's `SecretConfigKey` section whose value does is accepted as a **per-sender key** (two children verifying — two senders sharing a value — name nobody and are refused); the delivery is stored and `DeliveryResult.SenderKey` names the child. The inbox only decides whether the bytes are worth storing — it never widens what a delivery may do. |
| **Authorization** | MeshWeaver.Plugins `PlatformBuildInboxWatcher` → `AnnouncementKeys` → `SelfUpdateRouting` | re-verifies the stored body and decides what the key may cause (the rules below). |

## The rules the consumer enforces

1. **A deployment key authorises ONLY that deployment's self-update events** —
   `self-update-available` (which opens a Roll) and `self-update-restart-pending` (a Restart). A
   build fact, a bundle publication, a triage/feedback event or an aks-ops callback that verifies
   only with a deployment key is refused, logged and deleted: it opens nothing.
2. **The key is selected by the deployment the event NAMES**, so a key can only ever verify events
   naming its own deployment. `fabrikam`'s key signing an event that names the build instance verifies with no key
   and is refused.
3. **The record must claim the key.** A deployment-key announcement is acted on only when the record
   it resolves to IS that deployment and declares `announcementKeySecret`; otherwise it is refused
   with a message naming which half is missing.
4. **A record that declares its own key is no longer announceable with the fleet secret.** This is
   the per-record half of Plugins#1913's phase 3: possession of the fleet secret no longer announces
   for that deployment. A record that declares nothing keeps the fleet-secret path — the migration
   bridge for the instances that hold it today (the public instance, the build instance).

## Generating, registering and testing the key — in the portal

The key is generated WHERE IT IS USED TO SIGN — on the deployment, by its own administrator — and
the control instance only registers it. Nobody mints, copies or mounts it by hand, and nobody needs
vault access or cluster access.

1. **The record declares the key.** `Deployments/<id>` carries `announcementKeySecret` (e.g. `fabrikam-Hosting-AnnouncementKey` — the claim; it names
   the object an OPTIONAL mount would read, below). Without the
   declaration, rule 3 refuses every announcement signed with the key, and the Announcement key section says so.
2. **Generate** (the deployment, its own global administrator). **/Admin/Settings/ControlLane** →
   **Generate key**. The server generates a 256-bit key, stores it encrypted, and shows it ONCE with
   its fingerprint. The administrator sends the key to Systemorph over a secure channel (never plain
   e-mail) and the fingerprint separately. Until step 3, **Test connection** reads as a mismatch and
   no update is handed over.
3. **Register** (the control instance, a global administrator). **/Hosting/Integrations/Deployment/<id>**
   → **Announcement key** → paste the key → **Save**. The inbox verifies with it at once; nothing is written to a
   vault. The fingerprint shown must
   equal the one the deployment's administrator sent — if it does not, the key was mangled on the way.
4. **Test** (the deployment). **Test connection** sends a signed test to the control inbox with
   the header `X-MeshWeaver-Verify-Only: true`. The inbox verifies the signature and stores
   nothing. The page shows the control instance's verdict: **Match** (verified as this deployment's
   key) or **Mismatch** (401: the control instance holds no matching key yet). The result is
   recorded on both ends, so the control side shows it as the key's last verified use.

**Rotate:** the deployment's administrator clicks **Generate new key** and sends the new key; the
control instance registers it with **Register new key**. The control instance keeps accepting the
previous key until the deployment's first announcement signed with the new one, or for 14 days at
most (the dual-verification window). Between generating and registering, the deployment signs with
the new key only, so its announcements are refused until the registration — register promptly.
**Revoke** on the control side disables the key at once; it also suppresses a mounted copy.

A key handed the OTHER way (the control instance's administrator pastes one on the deployment with
**Save**) still works — the tab accepts a pasted key — but the documented flow is the one above.

What each end shows is only the status: present or not, the fingerprint, who set it and when, and
the last use. Neither end ever shows the key again.

## For the deployment's administrator — step by step

This is the part to send to a customer who administers their own instance.

1. Sign in to your instance as a global administrator and open **/Admin/Settings/ControlLane**
   (Admin ▸ Settings ▸ Control lane).
2. Click **Generate key**. A dialog shows the new key — 64 characters of `0–9` and `a–f` — and its
   fingerprint, such as `sha256:3f9a0c1b2d4e`. **It is shown only this once.** Copy both.
3. Send the key to Systemorph over a secure channel (never plain e-mail), and the fingerprint
   separately. Close the dialog.
4. When Systemorph confirms it has registered the key, click **Test connection**. You should see
   **✅ Match. The control instance verified this key as the key of `<your instance>`.**

Nothing has to be restarted at any point. If the test still says **Mismatch** after Systemorph has
registered the key, the key was mangled on the way: click **Generate new key** and send it again.

## Rollout order

Each step is safe on its own:

1. **Core** — `DeploymentContent.AnnouncementKeySecret`, `WebhookInbox.SenderKeyOf` (#5775), then
   the portal store, the verify-only test and the Control lane tab, now **/Admin/Settings/ControlLane** (#5791, #5807).
2. **MeshWeaver.Plugins** — the consumer rules above (`AnnouncementKeys`, MeshWeaver.Plugins#2416),
   then the **Announcement key** section of the Integrations app (`/Hosting/Integrations/Deployment/<id>`).
   It compiles against the sealed platform set,
   so it lands after the core half is sealed.
3. **Systemorph/Memex** — `Deployments/<id>` declares `announcementKeySecret` (the claim, rule 3).
   No vault object is mapped on either end, so no CSI mount can fail on an object that does not
   exist yet.
4. **Generate, register, test** — in the portal, as described above. No reconcile and no restart.

The declaration switches the fleet secret off for that record (rule 4). For a FLEET instance that
announces with the fleet secret today, have its administrator generate the key and register it BEFORE the
declaration merges. Otherwise its announcements are refused until the key is registered.

**Where this stands:** read `/Hosting/Integrations/Deployment/<id>` on the control instance. Its
**Announcement key** section says whether a key is registered and when that deployment last verified with it. That is the reading, not
this page.

## Owner actions — all in the portal

Nobody runs a command to create, copy or inspect this key, and nobody needs a vault permission or
cluster access (policy `secrets-write-only-entry`, [Secrets: Write-Only Entry, Split Identities](../SecretsWriteOnlyEntry)):

| act | where | who |
|---|---|---|
| register, register a new key, revoke | control instance → **/Hosting/Integrations/Deployment/<id>** → **Announcement key** | a global administrator of the control instance |
| generate, generate again | the deployment → **/Admin/Settings/ControlLane** | a global administrator of that deployment |
| check the pairing | both panels show the fingerprint; **Test connection** on the deployment, and the Integrations app's "last verified" line | either |

**Break-glass only.** If the portal itself is down, reach the vault through the operator's governed
path, never through a personal vault permission. A break-glass write is half an operation:
afterwards, generate and register the key again in the portal, so that the portal's store, the fingerprints and the
"last verified" reading describe the key actually in use.

## What is NOT covered

- **The record claim is not part of Test connection.** The test proves that the two ends hold the
  same key. It does not check rule 3: the control side's Announcement key section warns when the record declares no
  `announcementKeySecret`.
- **Feedback from a deployment-key sender** is refused by rule 1 — the Feedback hand-over signs with
  the same `Hosting:ControlInbox:Secret`. A deployment that must send feedback needs a triage-scoped
  key of its own, which is not designed here.
- **The fleet secret's own holders** (CD, the repository lanes, the other portals) are unchanged. The
  fleet-wide removal Plugins#1913 describes waits for every portal to announce with its own key.

## Related

- [Self-Update on the Control Lane](/Doc/Architecture/SelfUpdateControlLane) — the hand-over this
  key signs.
- [Control Lane](/Doc/Architecture/ControlLane) — the control→instance lane, which uses a per-deployment
  key of the same shape in the other direction.
- MeshWeaver.Plugins `Hosting/SelfUpdateAnnouncementIdentity` — the original design and the residual
  it closes.
