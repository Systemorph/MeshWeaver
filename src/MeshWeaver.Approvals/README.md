# MeshWeaver.Approvals

Approval workflows over mesh nodes: a **Request Approval** form on any document node, an inline
**Approvals** section on the markdown overview, and per-approval Overview/Thumbnail views with
Approve/Reject actions for the designated approver. Decisions dispatch notifications to the
requester and write an activity entry on the document.

Approvals are stored as `_Approval` satellites of the document node (routed to the partition's
`annotations` table). The `Approval` content record and its satellite-table mapping stay in the
platform (`MeshWeaver.Mesh.Contract`), so existing approval data keeps deserializing and routing
even when this module is not active.

## Activation

A module — list the DLL under the deployment's module list:

```json
"Modules": { "Assemblies": [ "MeshWeaver.Approvals.dll" ] }
```

The registration applies mesh-wide: the Approval node type is registered on the mesh, and every
per-node hub gets the Request Approval menu entry, the form/inline areas, and the approvals data
source. Delisting the module removes the Approvals UI mesh-wide — the markdown overview's embedded
Approvals section self-suppresses (it checks the `ApprovalsEnabled` marker) — while the data and
its satellite mapping remain intact at platform level.

Test fixtures and bespoke hosts opt in with the same registration the module applies:

```csharp
builder.AddApprovals();   // MeshBuilder extension — identical code path to Modules:Assemblies
```
