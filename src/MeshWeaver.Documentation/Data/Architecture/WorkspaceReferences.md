---
Name: Workspace References & Streaming
Category: Documentation
Description: How to register custom workspace references, reducers, and patch functions for typed stream access
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>
---

Every piece of live data in MeshWeaver is accessed through a **workspace reference** — a typed lens over the underlying `EntityStore`. A reference describes *what* you want; the framework does the work of subscribing, reducing, serializing, and keeping values in sync. Custom reference types let you add new lenses with full write-back support.

---

## Built-in Reference Types

The platform ships with six reference types that cover the most common access patterns:

| Reference | Reduces To | Purpose |
|---|---|---|
| `CollectionReference(name)` | `InstanceCollection` | All entities in a named collection |
| `CollectionsReference(names)` | `EntityStore` | A named subset of collections |
| `EntityReference(collection, id)` | `object` | A single entity by collection + id |
| `InstanceReference(id)` | `object` | A single entity by id |
| `JsonPointerReference(pointer)` | `JsonElement` | A JSON path within a stream |
| `MeshNodeReference()` | `MeshNode` | The hub's own `MeshNode`, with typed write-back |

---

## Getting Streams

### Local stream (own hub)

```csharp
// Full EntityStore stream for a type
var stream = workspace.GetStream(typeof(MeshNode));

// Typed observable shorthand
var nodes = workspace.GetStream<MeshNode>();
```

### Remote stream (another hub)

```csharp
// Observable of all MeshNodes on a remote hub
var nodes = workspace.GetRemoteStream<MeshNode>(new Address(path));

// Typed reference with write-back support
var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(path), new MeshNodeReference());
```

> **Tip:** Prefer the two-type overload (`GetRemoteStream<TValue, TReference>`) whenever you need to write back. It wires the `PatchFunction` automatically, so `stream.Update(...)` propagates changes to the owning hub.

---

## Registering Custom References

Custom workspace references give you a named, typed projection of data with proper serialization and optional write-back. Registration is a three-step pattern inside `DataContext` configuration.

### Step 1 — Define the reference type

A reference type is a simple record that inherits `WorkspaceReference<T>`:

```csharp
public record MeshNodeReference() : WorkspaceReference<MeshNode>;
```

### Step 2 — Register the reducer

The reducer maps a parent stream into the target type. You register it on the `ReduceManager` via `ForReducedStream`:

```csharp
config.AddData(data => data
    .Configure(rm => rm
        // Reducer: InstanceCollection → MeshNode
        .ForReducedStream<InstanceCollection>(reduced => reduced
            .AddWorkspaceReference<MeshNodeReference, MeshNode>(ReduceToMeshNode))
        // PatchFunction: write-back from subscriber to owner
        .ForReducedStream<MeshNode>(reduced => reduced
            .AddPatchFunction(PatchMeshNode))
        // Stream factory: resolves MeshNodeReference requests at runtime
        .AddWorkspaceReferenceStream<MeshNode>(
            (workspace, reference, configuration) =>
            {
                if (reference is not MeshNodeReference) return null;
                var collectionStream = workspace.GetStream(
                    new CollectionReference(nameof(MeshNode)));
                return (collectionStream as ISynchronizationStream<InstanceCollection>)
                    ?.Reduce((WorkspaceReference<MeshNode>)reference, configuration);
            })));
```

**The reducer function** maps `ChangeItem<InstanceCollection>` → `ChangeItem<MeshNode>`. For patch events it forwards the relevant `EntityUpdate` rather than re-reducing the whole collection:

```csharp
private static ChangeItem<MeshNode> ReduceToMeshNode(
    ChangeItem<InstanceCollection> current, MeshNodeReference reference, bool initial)
{
    var node = current.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();
    if (initial || current.ChangeType != ChangeType.Patch)
        return new(node, current.StreamId, current.Version);

    // For patches, forward the relevant EntityUpdate
    var change = current.Updates.FirstOrDefault();
    if (change == null) return null!;
    return new(change.Value as MeshNode, current.ChangedBy, current.StreamId,
        ChangeType.Patch, current.Version, [change]);
}
```

**The patch function** deserializes a `JsonElement` back to the typed object when a subscriber writes a change. It must produce an `EntityUpdate` so the owning hub can apply the mutation correctly:

```csharp
private static ChangeItem<MeshNode> PatchMeshNode(
    ISynchronizationStream<MeshNode> stream, MeshNode current,
    JsonElement updated, JsonPatch? patch, string changedBy)
{
    var updatedNode = updated.Deserialize<MeshNode>(stream.Hub.JsonSerializerOptions);
    return new(updatedNode!, changedBy, stream.StreamId, ChangeType.Patch,
        stream.Hub.Version,
        [new EntityUpdate(nameof(MeshNode), updatedNode?.Path, updatedNode)
            { OldValue = current }]);
}
```

---

## Updating via Streams

### `UpdateMeshNode` extension (recommended)

`UpdateMeshNode` handles both local and remote cases with a single, consistent call:

```csharp
// Update on own hub (uses GetStream locally)
workspace.UpdateMeshNode(null, threadPath, node =>
    node with { Content = updatedContent });

// Update on a remote hub (uses GetRemoteStream with MeshNodeReference)
workspace.UpdateMeshNode(new Address(remotePath), remotePath, node =>
    node with { Content = updatedContent });

// Typed content update — unpacks and repacks Content for you
workspace.UpdateMeshNode<MyContentType>(new Address(path), path,
    (node, content) => node with
    {
        Content = content with { Title = "Updated" }
    });
```

### Direct stream update

When you already hold an `EntityStore` stream, you can call `UpdateMeshNode` directly on it:

```csharp
var stream = workspace.GetStream(typeof(MeshNode));
stream.UpdateMeshNode(nodePath, node =>
    node with { Content = updatedContent });
```

---

## The Reduce Chain

Data flows downward from `EntityStore` through successive reductions. Each level adds a finer-grained projection:

```
EntityStore                      (root store — all collections)
    │
    ├── CollectionReference  →  InstanceCollection   (one collection)
    │       ├── EntityReference    →  object          (single entity by collection + id)
    │       ├── InstanceReference  →  object          (single entity by id)
    │       └── MeshNodeReference  →  MeshNode        (typed MeshNode)
    │
    ├── CollectionsReference →  EntityStore           (subset of collections)
    │
    └── JsonPointerReference →  JsonElement           (JSON path)
```

Each level can register a `PatchFunction` for write-back. Write-back travels the chain in reverse: typed changes are serialized to JSON, dispatched to the owner hub via `PatchDataChangeRequest` or `DataChangeRequest`, and applied using the registered `PatchFunction`.

---

## Bidirectional Sync

When a subscriber calls `stream.Update(fn)` on a remote stream, the framework executes a six-step round trip to keep the owner authoritative:

1. `stream.Update(fn)` posts an `UpdateStreamRequest` to the local sync hub.
2. The sync hub executes the update function and calls `SetCurrent()`.
3. The feedback subscription detects the change and converts it to `PatchDataChangeRequest` (JSON element streams) or `DataChangeRequest` (typed streams).
4. The change is forwarded to the **owner hub**: `hub.Post(e, o => o.WithTarget(owner))`.
5. The owner applies the change using its registered `PatchFunction`.
6. The owner broadcasts the reconciled state to all subscribers.

A feedback predicate ensures only **client-originated** changes travel back — owner broadcasts are filtered out, preventing echo loops.
