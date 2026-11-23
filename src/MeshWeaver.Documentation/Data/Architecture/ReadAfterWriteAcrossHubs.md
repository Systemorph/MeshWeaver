---
Name: Read After Write Across Hubs
Category: Documentation
Description: The one rule for reading a node you just wrote from a different hub — a write's terminal means the node is readable — why it was false, and what it does not cover
---

# Read After Write Across Hubs

**The rule: once a node write's terminal has been delivered, a read of that node issued from ANY hub afterwards sees the write.** Order the read after the write by composing it onto the write's terminal (`Update(...).Take(1).SelectMany(_ => hub.GetMeshNodeOutcome(path))`). Nothing orders a read against a write you only *posted*: a read and a patch from different hubs travel different routes and the mesh promises no arrival order between senders.

## Why it was false (Plugins#5816, cross-hub read-after-write triage item)

The owner of a node keeps **two** reduced streams of it, each its own `sync/{id}` hub with its own turn queue:

| Stream | Used by |
|---|---|
| `workspace.GetStream(reference)` — the plain reduce | the cross-hub patch handler (`ApplyMeshNodePatchInTurn`), which waits on it for the commit echo |
| `workspace.GetNullableStream(reference)` — the shared **read view** | every `GetDataRequest`, i.e. `GetMeshNode` / `GetMeshNodeOutcome` (`HandleGetDataRequest`) |

They are separate since #3432 (a read used to mint a permanent hub per call). The owner posted its `PatchDataResponse` when the *first* stream echoed the write, so the ack could leave while the *second* still held the node as it stood before the write. A read issued after the writer's terminal (from `portal/reads-{meshId}`, or any hub) then answered the pre-write node. Measured with a held read view: write terminal delivered, read after it answered `"before"`.

`UpdateUnifiedReferenceRequest` already fenced on the shared read view (`WaitForSharedReadVersion`); the cross-hub **patch** did not.

## The fix

`ApplyMeshNodePatchInTurn` now composes `ReadViewHoldsTheWrite` into the ack: the `PatchDataResponse` leaves when **both** the durable flush and the shared read view hold the stamped write (same entity id, `Version` at or past the minted stamp). The write's terminal therefore implies readability. The leg is total: a missing stream, a stream that ends, or `DataExtensions.ReadViewBound` (5 s) elapsing all let the ack leave, logged as `READ_VIEW_OUTLIVED_BOUND` — the commit is the verdict (#3112), this only decides *when* the ack leaves.

Pinned by `AWriteTerminalMeansTheNodeIsReadableFromAnyHubTest`: with the read view's turn held, the terminal must not be delivered (red before the fix); a negative control with nothing held shows terminal and read agree.

## What this does NOT cover

- **A read issued before the terminal** (fire-and-forget write, then read). Unordered by design; subscribe to the terminal.
- **Non-MeshNode patches** (the deferred `ApplyJsonMergePatchAndUpdate` path) — not changed.
- **Create / delete then read.** Their acknowledgements come from the node-operations path; not re-measured here.
- **An ack on the flush bound** (10 s) when storage is slow acks without a further wait; the read-view leg is bounded at 5 s so it has finished by then.

## Inventory (2026-11-23): writes followed by a read of the same node through another hub

Found by scanning core `src/` and Plugins for a `GetMeshNode`/`GetMeshNodeOutcome` within 40 lines after an `Update`/`CreateNode`; each was read for whether the read is composed onto the write's terminal.

- **Patch (`Update`) then read, composed on the terminal — covered by the fix:** Plugins `Feedback/Feedback/Source/FeedbackHandover.cs:723→747`, `Hosting/Queue/Source/JobQueues.cs:966→999`, `src/MeshWeaver.CloudSync/Engine/CloudSyncEngine.cs:128→153`, `src/MeshWeaver.ContentCollections.Indexing.Graph/Storage/IngestionRuns.cs:476→482`, `src/Memex.Portal.Shared/Email/InvitationDispatcher.cs` (`Claim`→`ReadOwner`, the #5816 poll).
- **Create then read, composed on the terminal — not re-measured, not changed:** core `MeshWeaver.Graph/Configuration/StepUpService.cs:90→118,183→187`, `MeshWeaver.Mesh.Contract/DurableStreams/DurableStreamHub.cs:343→368`, `MeshWeaver.PluginCatalog/InstanceAutoRegistrationService.cs:952→988`, `RegistrationKeyService.cs:208→217`; Plugins `Hosting/Deployment/Source/InstanceSecretWrites.cs:642→655,674→690`, `src/Memex.Portal.Shared/Authentication/{MeshWeaverInstanceService.cs:136→139,924→931, StepUpFactorStore.cs:72→77, StepUpPendingStore.cs:29→46, StepUpSingleUse.cs:134→138}`, `src/MeshWeaver.CloudSync/CloudSyncService.cs:86→102`, `src/MeshWeaver.ContentCollections.Indexing.Graph/Storage/{IngestionRuns.cs:485→514, StorageAttach.cs:131→137, StorageConnectionNodeType.cs:313→351,386→392}`, `src/MeshWeaver.Mcp/RecycleJobIntake.cs:117→119`.
- **Not the same node (false positives of the scan):** `IngestDocumentNodeType.cs:147→177` (reads the connection, writes the document), `CellSurfaceAssemblyProvider.cs:138→178` (an in-memory update).
- **Write not awaited before the read:** `src/MeshWeaver.Blazor.Chat/ThreadChatView.razor.cs:1475→1502` — a UI create whose read follows on its own schedule; unordered by design.

A caller that still polls for its own write (Plugins `InvitationDispatcher.ReadOwner`, #5816) can drop the poll once its resolved platform contains this change.
