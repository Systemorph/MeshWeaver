---
Name: Control Lane
Category: Architecture
Description: The signed control→instance lane — how the control instance runs a governed, approved operation (Recycle, DeleteSpace) IN another instance's mesh. One request signed with the target's own key, verified for signature, expiry and single use; the target computes its own plan and refuses any plan the approval did not bind; every step is audited on the target and reported back signed. Where it lives, the key, the checks in order, and the owner commands.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h12"/><path d="m12 6 6 6-6 6"/><rect x="18" y="4" width="3" height="16" rx="1"/><circle cx="4" cy="12" r="2"/></svg>
---

# Control Lane

The control instance governs the fleet through `Hosting/InstanceAction` nodes. Until this lane,
every lane ran the OTHER way or somewhere else:

| lane | direction | reaches |
|---|---|---|
| the signed inbox (`/api/hooks/Hosting/PlatformBuilds`) | instance → control | the control instance's mesh |
| the operator (`aks-ops.yml`, the in-cluster Job) | control → cluster | Kubernetes, helm, the databases |
| **the control lane (`/api/control-lane`)** | **control → instance** | **the target instance's mesh** |

So an operation that has to act INSIDE another instance's mesh — recycle an address, delete a
space nobody may delete — could only be filed on that instance's own mesh, which needs the Hosting
package there. The public instance does not run Hosting. This lane closes that gap with the smallest
surface that is still safe: two operations, one signed request per step, and the target as the
authority over what it runs.

## The shape

```
control instance                                            target instance
Hosting/InstanceAction (DeleteSpace, deployment=<public>)
  │ 1. dry run: POST /api/control-lane  ─── signed, K_dep ──►  verify · admit · claim Admin/ControlLane/{id}
  │                                                              plan (as system, writes nothing)
  │ ◄── report Planned {plan, digest}  ─── signed, K_dep ────  POST {control}/api/hooks/Hosting/PlatformBuilds
  │ 2. parks WITH that plan; an approver approves its digest
  │ 3. real run: POST /api/control-lane {planDigest, approvedBy} ► verify · admit · claim
  │                                                              plan again; digest ≠ approved → Refused, nothing touched
  │ ◄── Progress … Done {audit line} ───────────────────────  execute as system through the SAME engine
```

| part | where | what it does |
|---|---|---|
| **The request** | `ControlLaneRequest` (core, `MeshWeaver.Graph.ControlLane`) | names the target `deployment`, the `operation`, the `target` (and its `confirmation`), `dryRun`, the approved `planDigest`, `requestedBy`, `approvedBy`/`approvedAt`, the control `action` node, `issuedAt`/`expiresAt`, and a `requestId`. Signed as sent: `X-Hub-Signature-256` = HMAC-SHA256 over the raw body. |
| **The endpoint** | `POST /api/control-lane` (`ControlLaneEndpoints`, `Memex.Portal.Shared`) | anonymous — the signature IS the authentication — and answers the verdict as the status code. An acceptance is signed back with the same key. |
| **The receiver** | `ControlLaneReceiver` (core) | verifies, admits, claims, runs, reports (below). Registered on every portal by `AddControlLane()`; armed on none until configured. |
| **The operations** | `IControlLaneOperation` — `RecycleOperation`, `DeleteSpaceOperation` (core) | each computes its plan AS SYSTEM, writing nothing, and executes exactly that plan. |
| **The ledger** | `Admin/ControlLane/{requestId}` (`ControlLaneRecord`) on the target | created BEFORE anything runs — its creation is the single-use claim — and appended to at every step. |
| **The report** | `ControlLaneReport` → the control instance's inbox | every status, signed with the same key, posted to the control instance's CONFIGURED inbox — never to a URL the request names. |
| **The control half** | `ControlLaneClient` (core) + MeshWeaver.Plugins `Hosting/InstanceAction` | signs with the deployment's own key, parks the action with the target's plan, sends the approved run, and folds the reports onto the action node. |

## Where it lives, and why core

The executor is in **core** (`src/MeshWeaver.Graph/ControlLane`, the endpoint in
`memex/Memex.Portal.Shared`), not in a Plugins package:

- **Every portal carries it without installing anything.** The target must not need the Hosting
  package; core is in every image, including a customer's.
- **It is security code a remote request invokes as system.** Compiled, reviewed and shipped in the
  image — never in-mesh source a node editor could change at runtime.
- **The SAME engine serves both paths.** `SpaceDeletion` is the space-deletion engine the in-process
  `DeleteSpace` instance action already ran (MeshWeaver.Plugins `Hosting/DeleteSpaceAction`), moved
  to core so the in-mesh runner delegates to it and the lane executes it; `RecycleOperation` is the
  one framework surface, `hub.RecycleNode`. Both depend only on core surfaces (the recycle cascade,
  the partition teardown, the framework's deletes).

The InstanceAction integration — parking, approval, folding reports — stays where the actions are,
in MeshWeaver.Plugins `Hosting`.

## The key

The lane uses **one key per deployment**, from the vault, never the fleet-wide inbox secret. It
mirrors the [announcement key](/Doc/Architecture/SelfUpdateAnnouncementKey) in reverse:

| mount | where | as |
|---|---|---|
| the target's lane key | the target's pod | `ControlLane__Key` — mounting it is what ARMS the lane there |
| the deployment's own key slot | the control instance | `Hosting__PlatformWebhookSecret__{deployment}` — signs requests to it, verifies its reports |
| the declaration | the target's `Hosting/Deployment` record | `controlLaneKeySecret` — the vault object's NAME; the binding |

- **The record must claim the key** (`ControlLaneKeys.BindingRefusal`). A record that declares no
  `controlLaneKeySecret` is never sent a request. A record that also declares an
  `announcementKeySecret` must name the same object — the control instance holds one key per
  deployment in that slot.
- **The control half signs ONLY with the deployment's own key** (`ControlLaneKeys.ControlKeyFor`):
  never the shared `Hosting:PlatformWebhookSecret`, and never a child that equals it.
- **The target refuses to arm** (`ControlLaneKeys.ArmingRefusal`) without `ControlLane:Key` and
  `Hosting:Deployment`, and when the lane key EQUALS any secret its own webhook inbox verifies with —
  on a fleet portal that is the fleet secret, which every CI lane and every portal holds.
- **One key, two directions, told apart inside the signed body.** A request carries
  `"kind": "control-lane-request"`, a report `"event": "control-lane-report"`; each parser requires
  its discriminator to be PRESENT, so a report can never be replayed as a command.
- **Why its own mount name on the target.** `Hosting:ControlInbox:Secret` (the announcement
  signer) may still hold the fleet secret on an instance that has not migrated — and its Feedback
  hand-over signs with it, which a deployment key may not do. The lane key is therefore mounted under
  a name nothing else reads.

## What the target checks, in order

The order is contract: nothing is parsed before the signature verifies, nothing is written before
admission, nothing runs before the claim.

| # | check | refused as | HTTP |
|---|---|---|---|
| 1 | the lane is armed (`ArmingRefusal`) | `not-armed` — OUR misconfiguration | 503 |
| 2 | the signature verifies with `ControlLane:Key` | `signature-invalid` — says nothing more | 401 |
| 3 | it is a request, envelope version 1, a well-formed request id | `malformed` | 400 |
| 4 | it names THIS deployment (`Hosting:Deployment`) | `wrong-deployment` | 403 |
| 5 | issued ≤ now + 2 min, now < expiry, lifetime ≤ 15 min | `expired` | 410 |
| 6 | a registered operation claims it; a plain target path; a reason; an action node; a real run carries an approved digest and an approver; the operation's own shape rule (DeleteSpace: one segment, confirmation repeats it exactly) | `refused` | 422 |
| 7 | the ledger node `Admin/ControlLane/{requestId}` is CREATED — a second create is the replay | `replayed` | 409 |

Then it answers `202` with a signed body and runs on the mesh's off-router execution hub, which
outlives every target:

1. **Plan** — the operation reads what it would act on, as system, writing nothing. A refusal
   (a protected partition, a user's home, a package's partition, a reading that was a floor, a
   target that is not there, nothing left to delete) is reported `Refused`. Otherwise `Planned`,
   carrying the plan and its digest. A dry run ends here.
2. **Compare** — 🚨 the plan the target computes NOW must have the digest the approval bound; any
   other plan is reported `Refused`, both digests named, and NOTHING is touched.
3. **Execute** — the same engine as the in-process action, as system; one `Progress` report per
   step; the last line is the audit line (who asked, who approved, when, what was removed).
4. **Done** or **Failed** — terminal, reported and written to the ledger.

## Operations: an open vocabulary, closed executors

`ControlLaneOperation` is an open set of string constants (policy
`open-vocabulary-string-constants`): `Recycle` and `DeleteSpace` are the platform's starting set,
and a module adds its own by registering an `IControlLaneOperation` that claims a new value. An
unclaimed value is refused BY NAME. The one deliberate difference from the rule's usual shape: the
executors are registered in CODE, not as rule nodes — a node anyone with write access could edit
would make this lane a remote system shell.

## The plan is bound by digest

`ControlLanePlan.Digest()` is the digest MeshWeaver.Plugins' `ActionPlanSnapshot.Digest` computes:
length-prefixed and injective, with no namespace, no image and executor `control-lane`.

🚨 **A plan never contains a listing.** Every set a step acts on is a TARGET
(`ControlLanePlanTarget`): an anchored, scoped query with its count. Examples are the space's
NodeTypes, its grants, its GitSync nodes and its content roots. The whole subtree is a query with NO
count, because its rows move while a stranded space waits. The outside dependents and a NodeType's
dependency network are counts in the command. A plan whose steps carry targets digests as
`action-plan/v2`, which binds each query and count but never a label. A plan without targets stays
`action-plan/v1`.

The control instance parks the action with the reported plan, the approval binds that snapshot's
digest, and the real run carries it. The control side recomputes the digest over the reported steps
and refuses a report whose stated digest differs (`ControlLaneClient.VerifyReport`). A drift between
the two implementations is therefore a loud refusal at dry-run time, never an approval that can
never execute. `ControlLaneTest.ThePlanDigest_IsTheActionPlanV1Encoding` and
`APlanWithTargets_IsTheActionPlanV2Encoding` pin both encodings.

## What each operation binds

- **Recycle** uses a complete `scope:children` listing of the target's parent only to establish that
  the target path exists, then reads the current node from its stream before deciding whether it is a NodeType.
  It never treats an exact-path index query as proof of presence or absence. It binds the target (a `path:` query, count 1) and,
  for a NodeType, the EXACT address set of its dependency network (its digest is in the step's
  command, never the addresses). The cascade recomputes the network when the dispose lands, so the run derives it once
  more right before the dispose and refuses unless it is the bound set; an INCOMPLETE network is
  refused at planning, before anything is disposed.
- **DeleteSpace** binds what the in-process action binds: the space, schema, root shape, every grant,
  GitSync node and NodeType, the outside dependents and the teardown. Row counts are shown, never
  bound. The content, grants and store go in ONE `PartitionTeardown.TearDownPartition` as system,
  whatever the size — never a per-node recursive delete, whose pre-validation fan-out stalled on a
  31,138-descendant space ([Partition Teardown](../PartitionTeardown) → *The direct teardown*). The
  plan is offered only after `SpaceDeletion.Preflight` — rights as system, the per-node GitSync leg
  within `PerNodeDeleteBound`, the no-store sweep within `SweepBound`, the teardown's own refusal —
  answers none (policy `governed-action-preflight`). The query index is used only to list nodes and establish path existence (by
  listing a parent and filtering for its child, never by an exact-path query); the
  root and `Admin/Partition/{space}` definition are then read from their live node streams before
  their type/creator or table mappings enter the plan. A framework delete that completes WITHOUT an
  answer fails the run — no answer is not "already gone".

## Forwarded events

The lane's third kind, beside a request and a report: a **forwarded event**
(`ControlLaneEvent`, `"kind": "control-lane-event"`). The control instance received and verified a
delivery on its own inbox, and hands it to the instance that consumes it. The first use is the ONE
GitHub organisation webhook: it posts pull-request and check events to the control instance, which
forwards them to the build instance, where the PR steward's heal/observe half
(MeshWeaver.Plugins `Hosting/PrBabysitter`) consumes them (policy `pr-babysitter-cadence`).

| part | what it is |
|---|---|
| **The envelope** | `eventId` (16–64 letters, digits, dashes), `deployment`, `source` (open vocabulary, `ControlLaneEventSource.GitHub`), `name` (for GitHub, the `X-GitHub-Event` value), `target` (a local inbox owner), `payload` (the verified body, verbatim), `issuedAt`/`expiresAt`. Signed exactly like a request, with the target deployment's OWN key. |
| **The control half** | `ControlLaneClient.NewEvent` + `ControlLaneClient.Forward`. It uses the same key rule (`ControlKeyFor`), the same transport and the same signed-acceptance check as `Send`. |
| **The target half** | `ControlLaneReceiver.Receive`, on the same endpoint. Checks 1 and 2 (armed, signature) are shared. Then `ControlLaneEvents.Admit` runs: a well-formed envelope, this deployment, a window of at most 15 minutes, a target this instance DECLARED under `ControlLane:EventTargets`, and a payload within the inbox's size cap. Last, `ControlLaneEvents.Store` creates `{target}/_Inbox/{eventId}` as a `WebhookEvent`. |
| **Single use** | The inbox node's CREATION is the claim, so a replay answers `replayed` (409). |

What it deliberately does NOT have, and why that is safe:

- **No operation, plan, approval or report.** The target runs nothing for it. Its whole effect is
  one node in an inbox the target opened to the lane. The consumer treats the payload as a trigger
  and re-reads the live state itself. It never takes an action on the payload's word.
- **Not the public inbox list.** `ControlLane:EventTargets` is separate from `WebhookInbox:Targets`.
  An armed lane with no declared event target accepts no event, and a lane target need not be
  reachable from the internet.
- **No inbox signature.** The stored node carries `X-Control-Lane-Event`, `X-Control-Lane-Source` and
  `X-GitHub-Event`, but no `X-Hub-Signature-256`. A consumer that verifies an inbox HMAC (the
  platform-build watcher) therefore drops a forwarded event, even if one were misrouted to it.

Wiring a target (Systemorph/Memex record): the lane key as above, plus
`ControlLane__EventTargets__0` naming the inbox owner (build: `Hosting/Babysitter`). The node must
exist on the target, or the event is refused.

## Audited on both sides

- **Target:** the ledger node carries the verified request, every status and step, and whether each
  report reached the control instance; every status is also a `[ControlLane]` log line (Warning for
  Accepted/Planned/Refused/Done, Error for Failed and for a report the control inbox did not
  accept).
- **Control:** the action node carries the requester, the approver, the plan the approval bound and
  every report folded into its log.

## Owner commands

Minting a key is a GUI act on the control instance, never a vault command, and no agent creates or
reads a secret value (policy `secrets-write-only-entry`,
[Secrets: Write-Only Entry, Split Identities](../SecretsWriteOnlyEntry)). On `Deployments/<id>`,
use **Set Key Vault secrets…** → **Generate** for `<id>-Hosting-ControlLaneKey`. Both ends of the
lane read the vault, so the value is minted in the operator Job and never shown. The status the page
shows (present, enabled, updated, and the `mw-fp` fingerprint) comes from vault metadata alone.

## Rollout order — mint first, declare last

1. **Core** — the lane (this page). Inert everywhere: nothing is armed.
2. **MeshWeaver.Plugins** — the portal maps `/api/control-lane`; `Hosting/InstanceAction` routes a
   Recycle/DeleteSpace whose record is another instance through the lane; the inbox watcher folds
   `control-lane-report` onto the action node.
3. **Mint the vault object** (above). 🚨 Before the Memex change merges: a vault object the vault
   does not hold fails the WHOLE CSI mount of every pod that names it — the control instance's too.
4. **Systemorph/Memex** — the target's record declares `controlLaneKeySecret` and maps the object to
   `ControlLane__Key`; the control instance's record maps it to
   `Hosting__PlatformWebhookSecret__{deployment}`.
5. **Reconcile** the control instance, then the target. The acceptance reading is a dry-run action
   on the control instance reaching `Planned` with the target's plan.

## What is NOT covered

- **Rotation** is a flag day per deployment: replace the vault object's value, Reconcile both ends.
- **Other operations.** Two exist; anything else is a new `IControlLaneOperation` in an image.
- **Liveness.** A target that never reports leaves the control action waiting until the request
  expires; the action then says it heard nothing — it does not retry.

## Related

- [Self-Update Announcement Key](/Doc/Architecture/SelfUpdateAnnouncementKey) — the per-deployment key this lane
  reuses in reverse.
- [Self-Update on the Control Lane](/Doc/Architecture/SelfUpdateControlLane) — the instance→control hand-over.
- [Stale State Until Recycle](/Doc/Architecture/StaleStateUntilRecycle) — what `Recycle` does and does not do.
- MeshWeaver.Plugins `Hosting/DeleteSpaceAction`, `Hosting/RecycleAction` — the in-process kinds.
