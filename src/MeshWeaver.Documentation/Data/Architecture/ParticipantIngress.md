---
NodeType: Markdown
Name: "Participant Ingress — what a client connection may post"
Abstract: "SignalR and gRPC forward any delivery to any address, and the client writes the whole envelope, Sender included. Mesh infrastructure — the per-node raw save, the partition-storage requests, a compile trigger, hub and stream plumbing — therefore carries [InfrastructureOnly], the ingress stamps every delivery it accepts, and the receiving hub refuses a stamped infrastructure message before any handler sees it."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#b71c1c'/><path d='M4 12h9M10 8.5 13.5 12 10 15.5' stroke='white' stroke-width='1.8' fill='none' stroke-linecap='round' stroke-linejoin='round'/><rect x='14.5' y='6' width='5' height='12' rx='1' fill='none' stroke='white' stroke-width='1.6'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Security"
  - "Ingress"
---

A **participant** is anything that reaches the mesh over a connection instead of running in it: a
browser or React Native client on gRPC-web, a .NET client on SignalR, a Python or Node gate on the
trusted loopback gRPC port. The connection endpoints (in MeshWeaver.Plugins:
`SignalRConnectionRegistry.Deliver`, `GrpcConnectionRegistry.Deliver`) inject what the participant
sends into the mesh under the identity the endpoint validated — an unauthenticated connection as
`Anonymous`.

## The exposure

An ingress forwards **any delivery to any address**. The client serialises the whole envelope, so it
chooses the target, the message type and the `Sender`. The identity is re-stamped; nothing else is.
That is fine for an application request: `CreateNodeRequest`, `DataChangeRequest`,
`PatchDataRequest`, `DeleteNodeRequest` and the rest carry `[RequiresPermission]` or check inside
their handler, under the caller's own identity.

It is not fine for the mesh's own plumbing, whose handlers trust the message because only the mesh
was ever meant to send it. Measured in source (not exercised against a live portal):

| Message | What its handler did for any sender |
|---|---|
| `SaveMeshNodeRequest` | wrote the named node to storage raw — no permission check, no validator |
| `WriteBatchRequest`, `DeleteBatchRequest` | wrote / deleted rows on a partition-storage hub raw |
| `ReadNodeRequest`, `ExistsRequest`, `ListChildPathsRequest`, `ListDescendantPathsRequest` | read the partition store past row-level security |
| `DispatchCompileTrigger` | compiled a NodeType from the node snapshot in the PAYLOAD |
| `TrackActivityRequest` | recorded activity for whatever user id the payload named |
| `InitializeHubRequest`, `ShutdownRequest`, `SetCurrentRequest`, `UpdateStreamRequest` | drove a hub's or a synchronisation stream's internal state |

`DeleteMeshNodeRequest` was the same raw shape and was closed first, by forwarding it to the
validated delete (core #5976).

## The fix: two layers

**1. `[InfrastructureOnly]` + the ingress stamp.** Every type above carries
`InfrastructureOnlyAttribute`. An ingress calls `delivery.FromParticipant(user, "signalr")` (or
`"grpc"`, `"grpc-trusted"`) instead of a bare `SetAccessContext`: that stamps the validated identity
AND the `ParticipantIngress` property, after the client's envelope has been read, so a client cannot
leave it out. The receiving hub's `MessageService.UnpackIfNecessary` — on target, with the message
typed — refuses a stamped delivery of an `[InfrastructureOnly]` type with
`ErrorType.Forbidden`, and the participant is answered with that `DeliveryFailure`.

The check sits on the RECEIVING hub on purpose. A type the ingress hub has not registered crosses
the mesh as raw JSON and is typed only where it lands, so a filter at the ingress alone would wave
through exactly the messages it cannot name. `ParticipantIngress.Refuses` is the one predicate;
an ingress may call it early on an already-typed delivery to save the routing.

The trusted gRPC port is stamped too. A gate runs user-authored Code nodes, so what it sends is a
participant's message like any other.

**2. The handler stops trusting the message.** `SaveMeshNodeRequest` writes raw only for the hub's
OWN persistence post — the sampler and the deferred re-post, whose sender is the receiving hub and
which carry no ingress stamp. Any other sender is asking for a node write and gets the checked one:
the save is forwarded as a `CreateOrUpdateNodeRequest` under the delivery's own access context
(Create or Update permission by existence, every node validator), and a delivery with no identity
is refused. The snapshot's version travels with it — see the next section. Layer 2 is what stops in-mesh code and other hubs, which no ingress stamp reaches; layer
1 is what stops a client that writes the target's own address as its `Sender`.

## The forwarded save keeps its version

Forwarding closed the hole and opened a lost update. What a save carries is a
whole-node **snapshot**, and the two paths treat its `Version` differently:

| | What decides a snapshot taken before the node's latest write |
|---|---|
| The raw save (the hub's own post — and, before the forwarder, every sender) | `HandleSaveMeshNode` drops a save at or below the version the post-commit flush already wrote (`PostCommitFlushRegistry.HighWater`), and `MonotonicWriteGuardStorageAdapter` treats a strictly lower version as a conflict against the durable row, keeping the newer value of every member it cannot merge. The older snapshot never replaced the row. |
| The forwarded save, as first shipped | `CreateOrUpdateNodeRequest` in full-instance mode takes `Content` wholesale (`UpdateAccordingToSourceNode`) and sets `Version = Math.Max(live.Version, existing.Version)`. The incoming `Version` is never read, the owner mints above the stored one, and the older snapshot **wins** — acknowledged as a success. |

So the checked write was stricter about WHO and silent about WHEN. The forwarder now states the
snapshot's version as the upsert's precondition — `CreateOrUpdateNodeRequest.SnapshotVersion` — and
the update branch refuses a stale snapshot instead of applying it:

- **Stale** means the node carries a strictly higher version than the one offered **and** the
  snapshot would change it. The version is read twice — the durable row the handler read, and the
  node as the handling hub holds it inside the write lambda — because each can trail the owner.
  Both only ever trail, so a higher version on either is proof; see
  [Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs) for why a guard on a mirror is
  sound only in that direction.
- **A snapshot at the current version writes, and so does one carrying a higher version** — the
  storage guard's strict-regression rule, unchanged.
- **An older snapshot that would change nothing is acknowledged as unchanged.** A cross-hub
  `stream.Update` hands its caller the node it computed locally, at the *base* version (only the
  owner mints — [MeshNode Versioning](../MeshNodeVersioning)). "Write, then save what the write
  returned" therefore always offers a version one behind the row it just produced, carrying
  exactly what that row holds; there is nothing for it to put back.
- **The refusal is answered.** The upsert fails with `FailureKind = StaleSnapshot` and a sentence
  naming the offered and the stored version, and the save's sender receives a `DeliveryFailure`
  (`ErrorType.Rejected`) — a `DeliveryFailureException` for a caller that observes its post. A
  caller that only posts sees nothing; the refusal is in the owning hub's log at Warning. Every
  other refusal of a forwarded save is answered the same way (`Forbidden` for a caller who may not
  write).
- **The version is not disclosed to a caller who may not write.** The precondition is evaluated
  only once the requester is established to hold Update on the node; anyone else is refused as
  unauthorised whatever version they offered, so the answer cannot be used to find a node's
  version by offering one after another.

There is no force flag. A caller that wants its change to land re-reads the node and applies the
change to what it holds now — which is what `GetMeshNodeStream(path).Update(current => …)` does
by construction, and why a whole-node save from a non-owner is the wrong tool to begin with.

**Measured** (MeshWeaver.Plugins `main` at `43d5f84a8`, 2026-10-02, against the first platform set
containing the forwarder): `ProviderCredentialSeedTest.AKeyStoredInPlaintext_IsEncryptedInPlace_AndKeepsWorking`
failed **16 of 40** runs. The seed encrypts a provider key in place; an older snapshot still
carrying the plaintext was posted through `SaveMeshNodeRequest`, landed after the repair, and put
the plaintext back. With both of the seed's whole-node write-backs removed it was **0 of 120**;
removing either one alone was not enough. A second effect of the same mechanism: a key rotated
right after the repair was reverted by the seed's own write-back, **20 of 20**. Pinned in core by
`AForwardedSaveKeepsItsVersionTest`, whose first test fails in about a second without the
precondition — on the save's content arriving in storage — and passes with it.

**Who meets the refusal.** Every non-owner `SaveMeshNodeRequest` in the fleet is the same idiom —
`stream.Update(…)` followed by a save of the node the update returned, "to force persistence":
`ApiTokenService.RevokeToken` (this repo, `memex/`), and in MeshWeaver.Plugins
`ModelProviderService.RotateKey`, `ModelProviderLayoutAreas.SaveKey`, `ProviderCredentialSeed`'s
seed and re-protect writes, and the `ProvidersApp` setup area. The update's acknowledgement
already chains off the durable write, so the save persists nothing the update did not. While
nothing else has written the node it is acknowledged as unchanged, exactly as before; when a
later write got there first it is now refused where it used to put the older content back. None
of them relies on last-writer-wins — the save is redundant in each — and none observes its post,
so the refusal reaches them as a log line only.

## Also closed here

- `CreateReleaseRequest` now requires **Update** on the NodeType and `RunTestsRequest` **Execute**:
  both used to start a compile or a test run for any sender.
- A `MeshNode` ENTERING a per-node hub's workspace through a `DataChangeRequest` is a create
  (`MeshNodeTypeSource` writes it raw and announces `Created`), and was checked by
  `RlsDataValidator` alone. `MeshNodeCreationDataValidator` now runs the create-validator chain for
  it under the request's identity — for an entry of `Creations`, and for an entry of `Updates` whose
  path storage does not hold, since the type source writes both the same way. It is the counterpart
  of `MeshNodeDeletionDataValidator` (see [Moving Nodes](../MovingNodes)).

## Not closed here

- The version precondition is evaluated by the hub that HANDLES the upsert, against the durable
  row and its own view of the node — not inside the owning hub's turn. When both trail the owner
  the write still leaves as a merge patch whose changed leaves carry their base values, so the
  owner refuses a leaf it has since changed and the re-attempt meets the precondition; a leaf the
  owner changed that the snapshot does not touch is left alone. An owner-side precondition on the
  patch itself would make that one check instead of two mechanisms.
- A forwarded save of a node that does NOT exist takes the upsert's create branch, where there is
  no stored version to compare. The raw path dropped a save to a just-deleted path
  (`RecentlyDeletedRegistry`); whether the create path refuses that resurrection for a forwarded
  save was not examined here.

- An UPDATE of an existing node through a raw `PatchDataRequest` / `DataChangeRequest` runs RLS on
  the owner, but the app-integrity `INodeValidator`s (those not marked
  `IOwnerEnforcedNodeValidator`) run only client-side, in `NodeUpdatePipeline` — so a participant
  posting the patch directly skips them. Filed for triage separately.
- `DisposeRequest` stays participant-postable: a recycle is a legitimate remote operation and the
  root mesh hub already refuses it. Disposing a per-node hub costs its next access an activation,
  not data.
- In-process code (a NodeType's compiled source, a script) runs with the process's trust and can
  resolve the storage adapter directly; no message filter reaches it.

## Adding a message type

If its handler writes or reads storage raw, compiles or executes from a payload, or drives a hub's
own state, mark it `[InfrastructureOnly]` and add it to
`ParticipantIngressRefusesInfrastructureTest`. If a client legitimately sends it, give it
`[RequiresPermission]` (or check inside the handler under `request.AccessContext`) instead — never
both, and never neither.
