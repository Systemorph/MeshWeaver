---
Name: Self-Update on the Control Lane
Category: Architecture
Description: Where the update decision is made and who may act on it once no portal holds a credential that changes the cluster — detection stays on the instance, the apply becomes one signed event to the control instance, the chart's one declaration binds the self-patch Role to the poller's intent, and the transition runs namespace by namespace through the maintainer's Reconcile. With the design for deriving the registry pairing at render time and one trust rule for the bundle client.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="m4.93 10.93 4.24 4.24"/><path d="M2 18h6"/><path d="M19.07 10.93l-4.24 4.24"/><path d="M22 18h-6"/><circle cx="12" cy="18" r="3"/></svg>
---

# Self-Update on the Control Lane

**Maintainer decisions (2026-09-12, verbatim, MeshWeaver.Plugins `Hosting/AksOperationsViaActions`):**
*"let's see that the only place to alter aks is the systemorph-com app"*, *"take rights away from me
as well"*, *"approval in mesh is good"*. This page is the core half of phase **P3b** of that design
([MeshWeaver#4098](https://github.com/Systemorph/MeshWeaver/issues/4098)): every portal loses its
`memex-portal-self-update` Role, and self-update becomes **detect → control inbox → Roll**. It also
settles the two issues that share the question *where is the update decision made, and who may act
on it* — [#4093](https://github.com/Systemorph/MeshWeaver/issues/4093) (an instance on the fleet
registry cannot self-update) and [#4123](https://github.com/Systemorph/MeshWeaver/issues/4123)
(derive the registry/validator pairing once, on the control instance).

## The model in one paragraph

The instance keeps **detecting** exactly as before: the registry watch, `Admin/UpdatePolicy`, the
release-availability gate and the combo gate all still decide *which* tag is worth rolling to
(`Doc/Architecture/ReleaseStrategy`, `SelfUpdateTargetSelection`). What changes is the **apply**.
Instead of a Kubernetes `PATCH` of its own Deployments, the instance sends **one signed event** into
the control instance's inbox — `self-update-available`, naming its record, the image it runs and the
image it selected — and the control plane turns that into a `Hosting/InstanceAction` **`Roll`** on
the instance's record, executed by `aks-ops.yml` through the `systemorph-com` App. A Roll to the tag
the record already pins is an **unattended restore**; a Roll to a newer tag **waits for an approval
in the mesh**. So a `Continuous` policy means *one approval per release*, and a merge to
`Systemorph/Memex` that moves `pinnedImageTag` is what makes a roll unattended. **A portal that
cannot patch itself is the correct state, not a degraded one.**

🚨 **A routed Roll carries no `confirmation`, and must not**
([#4607](https://github.com/Systemorph/MeshWeaver/issues/4607)). The action node a person files must
repeat the deployment id, because a person aims at a row and can aim at the wrong one. This lane
never aims — the instance is RESOLVED from the announcement — so the id the router could type is the
one it just resolved: a check that cannot fail, and indistinguishable from a person's answer to
every later reader. Measured on the control instance 2026-09-17: **five routed runs on `memex`
refused in one day** (rolls for 8816, 8820, 8831, 8834 and one activation Restart), each within
seconds of being filed, for exactly that field — the lane detected, routed, and could never execute,
with nothing but a terminal state on a node nobody reads to say so. The routed run therefore
declares its LANE (`origin: "self-update"`), honoured only on a node the framework stamped
`createdBy: system-security`; the record's `updatePolicy: None` refuses a routed roll outright; and
the approval is armed **whatever the operator executor**, so a roll onto a tag the record does not
pin parks at *awaiting approval* rather than running unattended on an installation that gates
nothing. The rule, the rejected alternative and the instrument limits are in MeshWeaver.Plugins
`Hosting/AksOperationsViaActions` → *"What a MACHINE puts in `confirmation` — nothing"*.

## Who applies — three states, one pure rule

`SelfUpdateHandover.ApplyModeFor(chartCanPatch, updaterCanPatch, route)`:

| `SelfUpdateApply` | When | What the check does with a selected tag |
|---|---|---|
| **`SelfPatch`** | the chart says `SelfUpdate:CanPatch=true` **and** the updater can patch (`IDeploymentUpdater.CanPatch`) | the pre-P3b roll: migration Job, then `PATCH` — kept only while a chart still renders the Role |
| **`ControlLane`** | not both of the above, and a route to the control inbox exists | announces the release; patches nothing; records `HandedOverTag/At/To` on `Admin/UpdatePolicy`; verdict `HandedOver` |
| **`DetectOnly`** | neither | records the release for a person; the verdict **names the missing key** (`Hosting:Deployment`, `Hosting:ControlInbox:Url`/`Hosting:ReportTo`, `Hosting:ControlInbox:Secret`) |

🚨 **Both halves of the right are required for a self-patch, and the chart's half is the one that
moves.** `SelfUpdateOptions.CanPatch` is rendered by the chart from the SAME value that renders — or,
by default, does not render — the self-patch Role (`selfUpdate.canPatch`), so the right and the
intent to use it are one declaration: an install whose chart renders no Role also renders
`SelfUpdate__CanPatch=false` and never issues a `PATCH` the cluster would answer `403` to. The C#
default is `true` — deliberately the opposite of the chart's `false` — so an image carrying this
member under an **older** chart, which renders no key, behaves exactly as before. The fleet moves
when the **chart** moves, namespace by namespace, through the maintainer's Reconcile — the same act
that deletes the Role (a `helm upgrade` removes what the previous release created). That ordering
is what keeps a `Continuous` instance from freezing on the image that introduced this: `memex-cloud`
already carries `Hosting__ControlInbox__Url` (for the Feedback hand-over), and a C# default of
`false` would have flipped it to the control lane on its next self-roll — before any control plane
could route the event.

The manual **Apply available update now** button on Settings → Updates takes the same decision and
sends the same event (trigger `Manual`), so the control plane cannot tell a click from a check — and
it now honours the **combo gate** as the poller does (#2274): a candidate whose recorded verdict says
a module this install runs fails against it is refused from the button too, naming the reason. The
tab renders where a handed-over release WENT (`HandedOverTag/At/To`, in the viewer's zone) instead
of "update available" for ever.

## The control instance patches itself

🚨 **`Local` is a write into the control instance's OWN mesh — so for the control instance the
hand-over depends on exactly the thing a roll most often has to recover.** The fleet rule is right
for every other install: a satellite POSTs one signed event and the control plane does the rest. The
control instance's route is `Local`: the event is stored into its own `Hosting/PlatformBuilds` inbox,
the watcher opens a `Roll` node, the operator Job reads the deployment record — every hop a mesh
read or write on the instance that is trying to update. When its mesh is degraded, that chain is the
first thing to fail, and the image that carries the fix is the one it cannot reach.

This is MeshWeaver#1020's lesson, undone for one instance by #4098. #1020 had the availability
bookkeeping write chained ahead of the patch: the `Admin/UpdatePolicy` hub was unreachable, every
tick died in that write, and memex sat 37 h on a stale image while the registry check kept
succeeding — *"the update it would not apply is exactly what recovers a degraded pod, so the write
must never gate it."* The fix moved the k8s PATCH ahead of every mesh write. #4098 then replaced the
PATCH with the hand-over, and on 2026-09-22 the control instance sat four hours behind six sealed
builds carrying core#5151 — the fix for the router saturation it was suffering — logging
`[SelfUpdate] the hand-over of 3.0.0-ci.9168 to the control lane FAILED; the next check announces it
again`, `could not record available tag … on Admin/UpdatePolicy; applying the update anyway` (it
could not: applying IS the hand-over), and `self-update-available for 'memex-cloud' could not be
routed` — because the satellites' events land in the same wedged inbox, the whole fleet's
self-update was down with it. The roll that ended it was one hand-over that happened to land on the
one silo that could still write.

So the control instance keeps the pre-#4098 apply: the deployment record declares
`selfPatch: true`, the in-mesh `HelmValues` renders it as the chart's `selfUpdate.canPatch`, the
chart renders the Role and `SelfUpdate__CanPatch=true` from that one value, and
`ApplyModeFor(true, true, Local)` answers `SelfPatch` — the k8s API, no mesh write on the path,
`Failed_availability_write_never_blocks_the_roll_forward` and the hand-over resilience cases holding
it there. Every other record leaves `selfPatch` unset and hands over as before. The chart's guidance
("set it `true` ONLY on a standalone install with no control instance to hand to") already describes
the control instance: it has none but itself.

## The channel — the one every portal already has

The instance → control-instance channel is the pair the Feedback hand-over introduced
(MeshWeaver.Plugins#1713), so a portal declares its control inbox **once**:

| key | meaning |
|---|---|
| `Hosting:Deployment` | this instance's `Hosting/Deployment` record id — **required on every route**; the control plane routes by it, and an event naming no record is not a hand-over |
| `Hosting:ControlInbox:Url` | the inbox URL, `https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds` |
| `Hosting:ReportTo` | the control instance the inventory report goes to; when `ControlInbox:Url` is absent the inbox URL is **derived** from it (`+ /api/hooks/Hosting/PlatformBuilds`) — one declaration of "who is my control instance" |
| `Hosting:ControlInbox:Secret` | the HMAC secret the inbox verifies — byte-identical to the control instance's `Hosting:PlatformWebhookSecret`; read at delivery time, never captured, never logged |

Routes (`SelfUpdateHandover.RouteFor`, pure): **`Post`** — record id + URL + secret; **`Local`** —
this IS the control instance: it declares NO control inbox, and `Hosting/PlatformBuilds` is a listed
`WebhookInbox:Targets` entry that **declares** a `SecretConfigKey` whose secret is present, so the
event is delivered into its own inbox in-process through `WebhookInbox.Deliver`; **`None`**
otherwise.

🚨 **A declared control inbox is EXCLUSIVE** (#4098): an instance that names one is a
*consumer*, whatever it also lists, so the only routes it admits are `Post` (secret present) and
`None` (secret absent, and the missing sentence names the key). It never falls through to `Local`.
The shape that made this load-bearing is `build`: its inbox URL is **derived** from
`Hosting:ReportTo`, it maps no `Hosting:ControlInbox:Secret`, and it legitimately **lists**
`Hosting/PlatformBuilds` with a declared key whose secret is mounted — it owns the fleet's build
queue. Falling through stored the release in **build's own** inbox, where
`PlatformBuildInboxWatcher.PlanFor` classifies a `self-update-available` event as a non-build event
and **deletes** it, while the boot line read `apply=control-lane (… handed to this instance's own
Hosting/PlatformBuilds inbox …)` with a *verified* delivery and `Missing()` named nothing: a control
plane talking to itself, read off a step that could not fail. Mounting the secret on `build` would
have fixed one record and left the silent self-delivery reachable for the next instance that owns an
inbox, so the refusal lives in the route. `Missing()` takes the **same** "a URL is declared" test, so
an instance that declared neither URL key is never blamed for them.

🚨 A listed target that declares no `SecretConfigKey` is an **unsigned** target by the inbox's
own contract (#3312), never "the default key": delivering to it would store the event with its
signature never checked, so it is `None`, and the missing sentence says so. A URL that carries userinfo or is not `http(s)`
declares **no** inbox rather than half of one — the same whole-value rule the registry pairing uses
(`SelfUpdateRegistryCredential`). The missing sentence names the key that was actually read: a URL
derived from `Hosting:ReportTo` is blamed on `ReportTo`, a listed local target on its own declared
secret key.

🚨 **Only an answer that says the delivery was ACCEPTED and the signature VERIFIED is a
hand-over — both halves.** The inbox answers `{"status":"accepted","signature":"verified"|"not-required"}`
(#3312); `not-required` means the control instance declares no `SecretConfigKey` for the target and
checked nothing — the pairing degraded silently, and the consumer may still drop the event — a
verified signature on a status other than `accepted` is a delivery the inbox did not take, and a
body that is not that contract is an inbox this sender does not know. All are `HandoverFailed`,
naming what came back (`SelfUpdateHandover.InboxAnswerOf`); the local route requires
`DeliveryResult.SignatureVerified` the same way, and `RouteFor` itself requires the local target's
declared key, so settings assembled by hand cannot reach local delivery without one. A recorded hand-over is therefore
always a delivery the receiver checked. (`WebhookInbox.Deliver` itself was moved off the #1790
`Observable.Using(ImpersonateAsSystem)` shape onto `RunAsSystem` in the same change: an in-process
caller on a pool thread must not stay latched as System after delivering.)

**What the fleet's records carry today** (read on the control instance, 2026-09-14): `memex-cloud`
declares `Hosting__ControlInbox__Url` and mounts `Hosting__ControlInbox__Secret`; `build` declares
`Hosting__ReportTo` + `Hosting__Deployment` and mounts the fleet secret **as** `Hosting__PlatformWebhookSecret`
(its own inbox), not under the `ControlInbox` key; `pearl` declares `Hosting__ReportTo` +
`Hosting__Deployment` and mounts no secret; `memex` is the control instance (`Local`). So `build` and
`pearl` need one Key Vault mapping each — `Hosting__ControlInbox__Secret` → the vault object holding
the fleet webhook secret — before they can hand over; until then they are `DetectOnly` and their
verdict says exactly that.

## The event — the inbox contract the control plane routes

```json
{
  "event": "self-update-available",
  "deployment": "build",
  "instance": "https://build.meshweaver.cloud",
  "currentVersion": "3.0.0-ci.8411+c84c6c0",
  "newVersion": "3.0.0-ci.8460",
  "currentImage": "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411",
  "newImage": "cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8460",
  "policy": "Continuous",
  "pattern": "3.0.0-ci*",
  "trigger": "SafetyNet",
  "detectedAt": "2026-09-14T08:00:00Z",
  "reporter": "self-update"
}
```

Signed GitHub-style — `X-Hub-Signature-256: sha256=<hex HMAC-SHA256 of the raw body>` — with the
inventory report's own signer (`DeploymentReportService.Sign`), camelCase, nulls omitted, `event`
first. A second event, **`self-update-restart-pending`** (no `newVersion`/`newImage`, a `reason`),
announces a landed module generation waiting for its activation restart (#3650): a restart re-creates
the pods the record declares — the **unattended** class on the control lane — so a module still
activates without a person, exactly as an install that patched itself did it. A check that handed a
release over considers no restart: the Roll it becomes restarts the pods, and a second request for
the same instance would only race it (`SelfUpdateVerdict.MayRestartAfter`). A hand-over that
**failed** handed nothing to anyone, so the pending restart is still considered — on the same
broken inbox it is reported as unavailable naming the cause, never silently skipped.

🚨 **Idempotency lives on the control plane, not on the instance.** Every check that selects a target
announces it — the safety net makes that at most hourly — and the control plane treats
`(deployment, newImage)` as ONE request while an action for it is open or done. Re-delivery is what
closes a lost event without a watchdog: an event the control instance accepted and could not route
(the router not yet live) is announced again by the next check, and the first control plane that
can route it does. The instance owns detection; the roller owns pacing — which is why the roll floor
(`MinRollInterval`) does not apply to a hand-over: the floor paces pod restarts, and this install
restarts nothing.

**A failed hand-over is its own verdict** (`HandoverFailed`, Warning): the check succeeded, the
release is known and recorded, nothing was patched, the next check announces again — and a `401`
names the pairing to check (`Hosting:ControlInbox:Secret` vs the control instance's
`Hosting:PlatformWebhookSecret`), never a value. The inbox's `signature: "not-required"` body is
carried into the verdict too: it means the control instance declares no `SecretConfigKey` for the
target and verified nothing (#3312).

**Threat, stated.** The secret authenticates POSSESSION, not identity: any instance holding the
fleet secret could announce a release for another deployment. What bounds it is the control plane's
class rule, not the signature — a Roll to a newer tag waits for an approval in the mesh, a restore to
the pinned tag is unattended but rate-limited (3 per deployment per hour) and re-applies a state a
reviewed PR already declared. The control plane must still refuse an event whose `deployment` names
no record it holds.

**A deployment that must not hold the fleet secret announces with its OWN key** (MeshWeaver.Plugins#1913).
A customer-administered pod (`pearl`) handed the fleet secret could also sign build facts and triage
events. Instead its record names a vault object of its own (`announcementKeySecret`), the pod mounts
it as `Hosting__ControlInbox__Secret` — so the self-updater above signs with it unchanged — and the
control instance mounts the same object as `Hosting__PlatformWebhookSecret__<deployment>`, which the
inbox accepts as a **per-sender key** (`WebhookInbox.SenderKeyOf`). What such a key may cause is the
consumer's decision: its own record's self-update events only, never a build or control event, and a
record that declares one no longer accepts the fleet secret for itself. Full design, rollout and the
owner commands: [Self-Update Announcement Key](/Doc/Architecture/SelfUpdateAnnouncementKey).

## The chart — one declaration, one `helm upgrade`

| | default (`selfUpdate.canPatch: false`) | `selfUpdate.canPatch: true` |
|---|---|---|
| `rbac.yaml` | renders **nothing** — a `helm upgrade` DELETES the Role and RoleBinding a previous release created | the Role (`get,patch` on the two Deployments, `create,get,list,delete` on jobs) and its binding |
| `config.yaml` | `SelfUpdate__CanPatch: "false"` | `SelfUpdate__CanPatch: "true"` |
| `serviceaccount.yaml` | `memex-portal-sa` — the pod runs as it; workload identity for ACR listing only | unchanged |

Verified with `helm template deploy/helm` (no Role, `"false"`) and `--set selfUpdate.canPatch=true`
(Role + RoleBinding, `"true"`). `HelmValues` (MeshWeaver.Plugins) renders nothing for
`selfUpdate.canPatch`, so every fleet record takes the default. Set it `true` ONLY on a standalone
Kubernetes install that has no control instance to hand to; `memex-local` does not need it — its
auto-roll is host-side (`deploy/homebrew/README.md`), it never used the in-pod patch.

**The acceptance evidence #4098 asks for** — `kubectl auth can-i patch deployment/memex-portal-deployment
-n <ns> --as=system:serviceaccount:<ns>:memex-portal-sa` → `no` on every namespace — is produced by
a Reconcile of each namespace against a chart pin that carries this change. That is a cluster read,
so it is the maintainer's to take (break-glass otherwise); the portal-side reading that agrees with
it is the boot line: `[SelfUpdate] starting … canPatch=False, apply=control-lane (a detected release
is handed to https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds; the control plane rolls)`.

## What is left, in order — and who owns each step

1. **Core (this change).** The poller hands over; the chart's default renders no Role and
   `SelfUpdate__CanPatch=false`; the Updates tab hands over on the button; the verdicts, the policy
   node's `HandedOver*` fields and the boot line say which of the three states an install is in.
2. **MeshWeaver.Plugins — the router. ✅ LANDED** (Plugins#1845, and the routed-action rule in
   Plugins#2038). `PlatformBuildInboxWatcher.PlanFor` gained a route for
   `self-update-available` → a `Hosting/InstanceAction` `Roll` on `Deployments/<deployment>` with
   `imageTag = newVersion` (refusing a `deployment` that names no record; idempotent on
   `(deployment, newImage)` against open or done actions), and for `self-update-restart-pending` →
   `Restart`. Before it, the watcher verified the event, logged `ignoring non-build event` and
   deleted it — so an instance switched to the control lane detected and announced while nothing
   rolled. 🚨 **"The router is live" turned out not to be sufficient, and the second failure was NOT
   visible on both ends:** with the router shipped (Plugins#1845) every routed Roll was refused for
   the missing `confirmation` (#4607, fixed in Plugins#2038) — an outcome that reads as a validation
   working as intended and lives only on the action node. So when this lane is reported working, the
   evidence is a run that reached `AwaitingApproval` or `Done`, never the presence of the route. The Hosting
   package's pre-install of `MeshWeaver.SelfUpdate.Aks` keeps the ACR tag lister (detection on an
   ACR-based instance needs it) and its `KubernetesDeploymentUpdater` becomes inert by the chart's
   declaration; retiring the patcher half is that repo's call.
3. **Systemorph/Memex — declarations, then the roll.** `Deployments/build` gains the
   `Hosting__ControlInbox__Secret` vault mapping to the fleet secret (Memex#567); `Deployments/pearl`
   gains the same KEY mapped to its OWN announcement key instead, because a customer-administered pod
   must not hold the fleet secret ([Self-Update Announcement Key](/Doc/Architecture/SelfUpdateAnnouncementKey)) (and, for #4093,
   `SelfUpdate__RegistryValidationUrl` — see below); the chart pin moves to a core commit carrying
   this change; the maintainer Reconciles each namespace, which deletes the Role and renders the
   key. Order: the router (2) first, or accept that `Continuous` deliveries on the switched
   namespaces wait for it.
4. **Then #4093 closes** on its restated condition — `build` detects a newer image, hands it to the
   control instance, a Roll is opened and executed — and #4098 on the `can-i … no` reading.

## #4093 and #4123 — the pairing, and where the decision about it lives

Detection on a fleet-registry instance still needs the instance key presented to
`cr.meshweaver.cloud`, and `SelfUpdateRegistryCredential` is unchanged: the pairing is a
**declaration** (`SelfUpdate:RegistryValidationUrl`), never host resemblance, and an absent
declaration refuses. #4094 made the declaration possible; the config-repo declaration on `build` and
`pearl` is step 3 above. What #4123 adds — *design, recorded here; not implemented in this change,
which was scoped to P3b* — is **who writes it**:

- **Derive at render time, on the control instance.** `HelmValues` (MeshWeaver.Plugins) already
  derives `selfUpdate.registry` from the image host. The same render can derive
  `selfUpdate.registryValidationUrl`: given the hosting records the control instance holds, the one
  whose `registry.host` equals the consumer's image host carries the `validationUrl`
  (`Deployments/memex-cloud` → `https://memex.meshweaver.cloud/api/instances/token`). That needs
  `HelmValues.Render` to receive the registry records (or the resolved pairing) from
  `InstanceActionPlan`, which reads them on the control instance — a Plugins change, pure at the
  render, no network in the update path. The consumer-side key stays the wire; a hand-written
  `SelfUpdate__RegistryValidationUrl` on a record keeps winning, so nothing already declared changes.
- **One trust rule for the bundle client.** `PluginBundleClient.DownloadArtifact` presents the plugin
  registry's key to whatever host the catalog's bundle index advertises. The same pairing, read from
  the mount's side, is: the key held for registry mount **M** may go to artifact host **H** only when
  **H is M's own host, or M itself declares H as its artifact registry**. The declaration belongs to
  the party whose key it is — the registry — and is best carried on the **bundle index** (an
  index-level `artifactRegistry`, sourced from the registry record's `registry.host`, not from a
  publisher's bundle entry), so a compromised publisher lane cannot redirect consumer keys by
  writing a foreign artifact URL into one bundle. 🚨 It must **not** be keyed on
  `SelfUpdate:Registry`: the control instance `memex` pulls its image from ACR and still adopts
  bundles sealed on `cr.meshweaver.cloud`, so a rule tied to the image registry would refuse its
  every bundle and turn adoption into boot-time compiles. Scope: core (`PluginBundleClient`, the
  index shape) plus the registry's index endpoint — a scope call the maintainer has not made, and
  the reason #4123 stays open past this change.

**An alternative that would retire both issues — recorded, not taken.** Detection could move to the
control plane altogether: every instance already reports its `platformVersion` and `updatePolicy`
hourly (`DeploymentInventory`), the control instance holds the registry's record, and a FleetWatch
tick comparing each instance's version with the newest tag its policy admits could open the Roll
with no per-instance registry credential at all — no pairing to declare, nothing to derive. It
changes the maintainer's decision that *"SelfUpdate keeps detect"*, so it is a scope call for him,
not a follow-up.

## Related

- `Hosting/AksOperationsViaActions` (MeshWeaver.Plugins) — the whole design, phases P1–P3c.
- [Release & Self-Update Strategy](../ReleaseStrategy) · [Self-Update Target Selection](../SelfUpdateTargetSelection)
  · [The Self-Update Registry Credential](../SelfUpdateRegistryCredential) · [The Self-Update Schema Wall](../SelfUpdateSchemaWall).
- [DeploymentInventory](../DeploymentInventory) — the sibling channel (`Hosting:ReportTo`, `Hosting:Deployment`).
- `deploy/helm/templates/memex-portal/rbac.yaml`, `config.yaml`, `values.yaml` — the chart half.
- `memex/Memex.Portal.Shared/SelfUpdate/SelfUpdateHandover.cs`, `SelfUpdateHostedService.Apply` — the code;
  `test/Memex.Portal.Shared.Test/SelfUpdateHandoverTest.cs` (the pure rules),
  `SelfUpdateHandsOverToTheControlLaneTest.cs` (against a real mesh and an in-process inbox).
