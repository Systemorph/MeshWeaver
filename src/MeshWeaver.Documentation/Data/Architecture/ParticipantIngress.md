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
is refused. Layer 2 is what stops in-mesh code and other hubs, which no ingress stamp reaches; layer
1 is what stops a client that writes the target's own address as its `Sender`.

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
