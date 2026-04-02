---
Name: Workspace References & Streaming
Category: Documentation
Description: How to register custom workspace references, reducers, and patch functions for typed stream access
---

MeshWeaver uses **workspace references** to define typed views over the underlying `EntityStore` data. Each reference type maps to a specific reduction of the data and can optionally support write-back via **patch functions**.

# Built-in Reference Types

| Reference | Reduces To | Purpose |
|-----------|-----------|---------|
| `CollectionReference(name)` | `InstanceCollection` | All entities in a collection |
| `CollectionsReference(names)` | `EntityStore` | Multiple collections |
| `EntityReference(collection, id)` | `object` | Single entity by collection + id |
| `InstanceReference(id)` | `object` | Single entity by id |
| `JsonPointerReference(pointer)` | `JsonElement` | JSON path within a stream |
| `MeshNodeReference()` | `MeshNode` | Hub's own MeshNode with typed write-back |

# Getting Streams

## Local Stream (own hub)

```csharp
// Get the hub's EntityStore stream for a type
var stream = workspace.GetStream(typeof(MeshNode));

// Get typed observable
var nodes = workspace.GetStream<MeshNode>();
```

## Remote Stream (another hub)

```csharp
// Observable of all MeshNodes on a remote hub
var nodes = workspace.GetRemoteStream<MeshNode>(new Address(path));

// Typed reference with write-back support
var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(path), new MeshNodeReference());
```

# Registering Custom References

Custom workspace references enable typed stream access with proper serialization and write-back support. Registration happens in the `DataContext` configuration via `AddData` or `AddMeshDataSource`.

## 1. Define the Reference Type

```csharp
// A workspace reference that returns a specific type
public record MeshNodeReference() : WorkspaceReference<MeshNode>;
```

## 2. Register the Reducer

The reducer maps from a parent stream type to the target type. Registration uses `ForReducedStream` on the `ReduceManager`:

```csharp
config.AddData(data => data
    .Configure(rm => rm
        // Register on the InstanceCollection reduce manager
        .ForReducedStream<InstanceCollection>(reduced => reduced
            .AddWorkspaceReference<MeshNodeReference, MeshNode>(ReduceToMeshNode))
        // Register PatchFunction for write-back
        .ForReducedStream<MeshNode>(reduced => reduced
            .AddPatchFunction(PatchMeshNode))
        // Register stream factory for workspace resolution
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

### The Reducer Function

Maps a `ChangeItem<InstanceCollection>` to `ChangeItem<MeshNode>`:

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

### The PatchFunction (Write-Back)

Converts `JsonElement` back to the typed object when changes propagate from subscriber to owner:

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

# Updating via Streams

## Using UpdateMeshNode Extension

The `UpdateMeshNode` extension handles both local and remote updates:

```csharp
// Update on own hub (uses GetStream locally)
workspace.UpdateMeshNode(null, threadPath, node =>
    node with { Content = updatedContent });

// Update on remote hub (uses GetRemoteStream with MeshNodeReference)
workspace.UpdateMeshNode(new Address(remotePath), remotePath, node =>
    node with { Content = updatedContent });

// Typed content update
workspace.UpdateMeshNode<MyContentType>(new Address(path), path,
    (node, content) => node with
    {
        Content = content with { Title = "Updated" }
    });
```

## Direct Stream Update

For EntityStore streams, use `UpdateMeshNode` on the stream directly:

```csharp
var stream = workspace.GetStream(typeof(MeshNode));
stream.UpdateMeshNode(nodePath, node =>
    node with { Content = updatedContent });
```

# The Reduce Chain

Data flows through a reduction chain from `EntityStore` down to typed objects:

```
EntityStore                    (root store with all collections)
    |
    +-- CollectionReference --> InstanceCollection  (single collection)
    |       |
    |       +-- EntityReference --> object           (single entity)
    |       +-- InstanceReference --> object          (single entity by id)
    |       +-- MeshNodeReference --> MeshNode        (typed MeshNode)
    |
    +-- CollectionsReference --> EntityStore          (subset of collections)
    |
    +-- JsonPointerReference --> JsonElement          (JSON path)
```

Each level can have a `PatchFunction` registered for write-back. Write-back flows in reverse: typed changes are serialized to JSON, sent to the owner hub via `PatchDataChangeRequest` or `DataChangeRequest`, and applied using the registered `PatchFunction`.

# Bidirectional Sync

When a subscriber updates a remote stream:

1. `stream.Update(fn)` posts `UpdateStreamRequest` to the local sync hub
2. The sync hub executes the update and calls `SetCurrent()`
3. The feedback subscription converts the change to `PatchDataChangeRequest` (for JsonElement streams) or `DataChangeRequest` (for typed streams)
4. The change is sent to the **owner hub** via `hub.Post(e, o => o.WithTarget(owner))`
5. The owner hub applies the change using its `PatchFunction`
6. The owner broadcasts to all subscribers

The feedback predicate ensures only **client-originated** changes are sent back (not echoes from the owner).
