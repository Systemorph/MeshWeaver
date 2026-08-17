# MeshWeaver.Approvals

Approval workflows over mesh nodes: a **Request Approval** form on any document node, an inline
**Approvals** section on the markdown overview, and per-approval Overview/Thumbnail views with
Approve/Reject actions for the designated approver. Decisions dispatch notifications to the
requester and write an activity entry on the document.

Approvals are stored as `_Approval` satellites of the document node (routed to the partition's
`annotations` table). The `Approval` content record and its satellite-table mapping stay in the
platform (`MeshWeaver.Mesh.Contract`), so existing approval data keeps deserializing and routing
even when this module is not active.

## 🚨 Relocating to Systemorph/MeshWeaver.Plugins — this copy is the MASTER until the flip

(Task 72 of the 2026-08 modularization program — a program-plan task, not a GitHub issue number.
Copies the pattern task 63 / MeshWeaver.SocialMedia#31 established for the Social module.)

This module's sources ALSO live in
[MeshWeaver.Plugins `src/MeshWeaver.Approvals`](https://github.com/Systemorph/MeshWeaver.Plugins/tree/main/src/MeshWeaver.Approvals),
where the Approvals package is a MIXED package (node content + compiled module:
`Approvals/index.json` declares `content.module` and its CI builds + module-packs the bundle).
**The module's tests relocated WITH it** (maintainer directive on task 72):
`test/MeshWeaver.Graph.Test/ApprovalModuleTest.cs` moved to the satellite's
`src/MeshWeaver.Approvals.Test`, where the satellite CI executes it against a pinned platform ref.
The platform keeps the platform-contract tests — `ApprovalAndNotificationTest` (the `Approval`
record is `MeshWeaver.Mesh.Contract`) and `ApprovalsLegacySurfaceCompileTest` (the legacy
`MeshWeaver.Graph` surface below is a PLATFORM compat contract). During the double-ship transition:

- **Change the module here first, then mirror the `.cs` files to the satellite verbatim** — every
  file EXCEPT `GraphLegacySurface.cs`, which is deliberately platform-only: it restores the
  pre-#1654 `MeshWeaver.Graph`/`MeshWeaver.Graph.Configuration` surface for in-mesh sources and
  dies when that content migrates, not when the module relocates. (The satellite's csproj also
  differs — it project-references a platform checkout, and its `InternalsVisibleTo` names the
  relocated test assembly `MeshWeaver.Approvals.Test`.)
- **Do NOT delete this project yet.** The flip is blocked by, and its PR must resolve, ALL of:
  1. In-mesh sources call the Approvals surface — BY CONSTRUCTION (the legacy surface exists
     because 11 NodeTypes across the SocialMedia and UWDeepfield partitions regressed to
     CompileError when #1654 first shipped): `SocialMedia/{Post,PostsHub,Profile}.json`
     configuration lambdas call `.AddApprovals()` (MeshWeaver.SocialMedia repo), and
     `UWDeepfield/TreatySubmission/Source/…` + `UWDeepfield/UWDeepfieldHome/Source/UwHomeShims.cs`
     use `ApprovalExtensions.ApprovalPartition` / `ApprovalNodeType.NodeType` /
     `ApprovalsView`-shaped reads via `using MeshWeaver.Graph` (MeshWeaver.Reinsurance repo).
     NodeType compilation references only TRUSTED_PLATFORM_ASSEMBLIES, so a modules/-only assembly
     is invisible to it — the module must stay in the app closure until those callers migrate to
     the module registration AND the live meshes are re-swept (`content/ samples/*/Data`, node
     JSON, `search_chunks` — the #683 rule).
  2. `Memex.Portal.Shared` holds the ships-the-bits `ProjectReference` (the app-closure lane —
     this module rides no `@(MeshModule)` entry in `memex/MeshModulesPublish.targets`; the loader
     falls back to the app folder).
  3. The modularization program's satellite-bundle rollout (program-plan task 74), so consumers
     land the Plugins-built bundle automatically (until then the registry serves no satellite
     bundles and the double-shipped app closure is the only distribution).
  When it flips: remove this project + the `Memex.Portal.Shared` reference; the
  `Modules:Assemblies` entries STAY (the runtime then loads the landed module from `modules/`),
  and `GraphLegacySurface.cs` + `ApprovalsLegacySurfaceCompileTest` go only when the in-mesh
  callers in (1) are migrated and re-swept.

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
