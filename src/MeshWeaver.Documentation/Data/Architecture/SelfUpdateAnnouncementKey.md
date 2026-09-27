---
Name: Self-Update Announcement Key
Category: Architecture
Description: How a deployment announces its own self-update to the control instance with a key of its OWN instead of the fleet-wide inbox secret — the record names the vault object, the inbox accepts it as a per-sender key, the consumer lets it cause nothing but that record's self-update events, and a record that declares one is no longer announceable with the fleet secret. The key is issued on the record page, entered in the deployment's Settings ▸ Control lane, and tested there — no vault command, no mount, no restart.
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
| **Issuing** | the control instance: the deployment's record page → **Announcement key** → **Issue key** | a global administrator of the control instance generates the key on the server. It is stored in the control instance's own encrypted store as `Hosting:PlatformWebhookSecret:<deploymentId>` ([Instance Secrets](../InstanceSecrets)), where the inbox verifies with it at once. A vault copy is filed under the record's `announcementKeySecret` through the governed `SetSecrets` action. The key is shown ONCE. |
| **Entering** | the deployment: **Settings ▸ Control lane** | the deployment's own global administrator pastes the key. It is stored in that instance's own encrypted store as `Hosting:ControlInbox:Secret`, and the self-updater signs with it from the next announcement on, with no restart. |
| **The sender's mount** (optional) | the deployment's pod: `Hosting__ControlInbox__Secret` ← that vault object | the older way to deliver the same key. The self-updater signs with the key entered in the portal if there is one, else with this mount. |
| **The receiver's mount** (optional) | the control instance: `Hosting__PlatformWebhookSecret__<deploymentId>` ← the same vault object | a CHILD of the inbox's shared key — the shape `Hosting:ModuleReportSecret:<deployment>` already has. The key issued on the record page takes precedence over it. |
| **Ingestion** | core `WebhookInbox.Deliver` → `SenderKeyOf` | when the target's shared secret does not verify, the ONE child of the target's `SecretConfigKey` section whose value does is accepted as a **per-sender key** (two children verifying — two senders sharing a value — name nobody and are refused); the delivery is stored and `DeliveryResult.SenderKey` names the child. The inbox only decides whether the bytes are worth storing — it never widens what a delivery may do. |
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

## Issuing, entering and testing the key — in the portal

Nobody mints, copies or mounts the key by hand, and nobody needs vault access or cluster access.

1. **The record declares the key.** `Deployments/<id>` carries `announcementKeySecret` (the name
   of the vault object the copy is filed under, e.g. `pearl-Hosting-AnnouncementKey`). Without the
   declaration, rule 3 refuses every announcement signed with the key, and the record page says so.
2. **Issue** (control instance, a global administrator). On the deployment's record page, the
   **Announcement key** panel → **Issue key**. The server generates a 256-bit key and stores it,
   encrypted, as the per-sender key the inbox verifies. It files a vault copy through the governed
   `SetSecrets` action, which waits for approval, and shows the key ONCE with its fingerprint.
   Copy it and send it to the deployment's administrator over a secure channel.
3. **Enter** (the deployment, its own global administrator). **Settings ▸ Control lane** → paste the
   key → **Save**. The page shows the key's fingerprint, which must equal the one on the control
   instance's record page.
4. **Test** (the deployment). **Test connection** sends a signed test to the control inbox with
   the header `X-MeshWeaver-Verify-Only: true`. The inbox verifies the signature and stores
   nothing. The page shows the control instance's verdict: **Match** (verified as this deployment's
   key) or **Mismatch — re-enter the key** (401: the control instance holds no matching key). The
   result is recorded on both ends. The record page shows it as the key's last verified use, so
   the two panels say "paired" together.

**Rotate** is **Issue key** again (the button reads **Rotate key** once a key exists). The new key is
stored, and the old one keeps verifying until the deployment's first announcement signed with the
new key, or for 14 days at most. That is the dual-verification window, so a rotation is not a flag
day. **Revoke key** disables the key at once; it also suppresses a mounted copy of the same key.

What each end shows is only the status: present or not, the fingerprint, who set it and when, and
the last use. Neither end ever shows the key again.

## For the deployment's administrator — step by step

This is the part to send to a customer who administers their own instance.

1. You receive a key from Systemorph over a secure channel. It is 64 characters of `0–9` and `a–f`.
2. Sign in to your instance as a global administrator, open **Settings**, and choose
   **Control lane** in the **Administration** group.
3. Paste the key into **Control-instance announcement key** and press **Save**. The field empties,
   and the page shows **Set** with a fingerprint such as `sha256:3f9a0c1b2d4e`. The key itself is not
   shown again, not even to you.
4. Press **Test connection**. You should see **✅ Match. The control instance verified this key as
   the key of `<your instance>`.**
5. Read the fingerprint back to Systemorph if asked. It must equal the one Systemorph sees.

If the test says **❌ Mismatch — re-enter the key**, paste the key again. Take care to copy all 64
characters and nothing else, then press **Save** and **Test connection** again. Nothing has to be
restarted at any point: the key is used from the next update announcement on.

## Rollout order

Each step is safe on its own:

1. **Core** — `DeploymentContent.AnnouncementKeySecret`, `WebhookInbox.SenderKeyOf` (#5775), then
   the portal store, the verify-only test and **Settings ▸ Control lane** (#5791).
2. **MeshWeaver.Plugins** — the consumer rules above (`AnnouncementKeys`, MeshWeaver.Plugins#2416),
   then the record page's **Announcement key** panel. It compiles against the sealed platform set,
   so it lands after the core half is sealed.
3. **Systemorph/Memex** — `Deployments/<id>` declares `announcementKeySecret` (the claim, rule 3).
   No vault object is mapped on either end, so no CSI mount can fail on an object that does not
   exist yet.
4. **Issue, enter, test** — in the portal, as described above. No reconcile and no restart.

The declaration switches the fleet secret off for that record (rule 4). For a FLEET instance that
announces with the fleet secret today, issue the key and have its administrator enter it BEFORE the
declaration merges. Otherwise its announcements are refused until the key is entered.

**Where this stands:** read `Deployments/pearl` on the control instance. Its **Announcement key**
panel says whether a key is issued and when pearl last verified with it. That is the reading, not
this page.

## Owner actions — all in the portal

Nobody runs a command to create, copy or inspect this key, and nobody needs a vault permission or
cluster access:

| act | where | who |
|---|---|---|
| issue, rotate, revoke | control instance → `Deployments/<id>` → **Announcement key** | a global administrator of the control instance |
| enter, replace | the deployment → **Settings ▸ Control lane** | a global administrator of that deployment |
| check the pairing | both panels show the fingerprint; **Test connection** on the deployment, and the record page's "last verified" line | either |

The vault copy that **Issue key** files goes through the governed `SetSecrets` action on the same
record page: the plan is shown, it waits for approval, and the operator's writer identity writes it.

**Break-glass only.** If the portal itself is down, reach the vault through the operator's governed
path, never through a personal vault permission. A break-glass write is half an operation:
afterwards, re-issue the key in the portal, so that the portal's store, the fingerprints and the
"last verified" reading describe the key actually in use.

## What is NOT covered

- **The record claim is not part of Test connection.** The test proves that the two ends hold the
  same key. It does not check rule 3: the record page's panel warns when the record declares no
  `announcementKeySecret`.
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
