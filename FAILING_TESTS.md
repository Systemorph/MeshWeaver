# Failing Tests Tracker (2026-03-21)

## Changes Made
1. `ThreadMessages` → `Messages` rename (Thread property)
2. Removed `CreateThreadRequest`/`CreateThreadResponse` — replaced with `CreateNodeRequest`/`ThreadNodeType.BuildThreadNode`
3. Moved `WithNodeOperationHandlers()` from mesh hub to `AddMeshDataSource()` (node hubs handle CreateNodeRequest)
4. Added `AddMeshTypes()` to `WithNodeOperationHandlers()` so node hubs can deserialize responses
5. Added `AddMeshTypes()` to `MonolithMeshTestBase.ConfigureClient()` so test clients can deserialize responses
6. Fixed `ChatHistorySelector.razor` — `GetFirstUserMessage` returns null (Messages is now IDs only)
7. Fixed `ThreadPermissionTest.CreateThread_WithoutUpdatePermission_IsDenied` — expects DeliveryFailureException

## Status After Fixes

### Threading.Test: 24 → 2 failures (43 pass)
- Remaining 2 need investigation (likely the second ThreadPermission test + one timeout)

### Data.Test: 1 failure (pre-existing timeout)
- `DataTest.Delete` — timeout (not related to our changes)

### Layout.Test: 2 failures
- `EditorTest.TestEditorWithResult` — needs investigation

### Hosting.Test: 2 failures
- `PartitionedFileSystemPersistenceTest.Query_NoNamespace_FansOutToAll` — needs investigation

### Graph.Test: 2 failures
- `MeshNodeCompilationServiceTest` — 2 timeout failures (compilation)

### FutuRe.Test: 12 failures
- Likely dynamic compilation/Organization type issues (same as Acme)

### Acme.Test: 22 failures
- "No hub configuration for node 'ACME' (NodeType: Organization)" — dynamic compilation issue

## NOT YET RUN
- Auth.Test
- PathResolution.Test
- Persistence.Test
- Content.Test
- Query.Test
- NodeOperations.Test
- Security.Test
- Hosting.Monolith.Test
- AI.Test

## Passing (confirmed)
Serialization, Messaging.Hub, BusinessRules, DataSetReader, Hierarchies, Json.Assertions,
ContentCollections, Import, Kernel, Search, Northwind, Todo, StorageImport,
Markdown.Collaboration, Hosting.Blazor, AccessControl
