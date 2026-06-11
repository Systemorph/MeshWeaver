---
Name: Workspace References & Streaming
Category: Documentation
Description: How to register custom workspace references, reducers, and patch functions for typed stream access
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>
---

Every piece of live data in MeshWeaver is accessed through a **workspace reference** — a typed lens over the underlying `EntityStore`. A reference describes *what* you want; the framework does the work of subscribing, reducing, serializing, and keeping values in sync. Custom reference types let you add new lenses with full write-back support.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 340" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
    <marker id="arr-rev" markerWidth="8" markerHeight="8" refX="1" refY="3" orient="auto">
      <path d="M8,0 L8,6 L0,3 z" fill="#f57c00" fill-opacity=".85"/>
    </marker>
  </defs>
  <rect x="290" y="10" width="180" height="44" rx="10" fill="#1e88e5"/>
  <text x="380" y="37" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">EntityStore</text>
  <line x1="310" y1="54" x2="180" y2="98" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="54" x2="380" y2="98" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="450" y1="54" x2="580" y2="98" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="90" y="98" width="180" height="44" rx="10" fill="#43a047"/>
  <text x="180" y="121" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">CollectionReference</text>
  <text x="180" y="136" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">→ InstanceCollection</text>
  <rect x="290" y="98" width="180" height="44" rx="10" fill="#5c6bc0"/>
  <text x="380" y="121" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">CollectionsReference</text>
  <text x="380" y="136" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c5cae9">→ EntityStore (subset)</text>
  <rect x="490" y="98" width="180" height="44" rx="10" fill="#8e24aa"/>
  <text x="580" y="121" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">JsonPointerReference</text>
  <text x="580" y="136" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">→ JsonElement</text>
  <line x1="140" y1="142" x2="100" y2="186" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="180" y1="142" x2="180" y2="186" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="220" y1="142" x2="260" y2="186" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="186" width="150" height="44" rx="10" fill="#26a69a"/>
  <text x="95" y="208" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">EntityReference</text>
  <text x="95" y="223" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b2dfdb">→ object (by id+coll)</text>
  <rect x="185" y="186" width="150" height="44" rx="10" fill="#26a69a"/>
  <text x="260" y="208" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">InstanceReference</text>
  <text x="260" y="223" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b2dfdb">→ object (by id)</text>
  <rect x="95" y="186" width="76" height="44" rx="0" fill="none"/>
  <rect x="355" y="186" width="150" height="44" rx="10" fill="#1e88e5"/>
  <text x="430" y="208" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">MeshNodeReference</text>
  <text x="430" y="223" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">→ MeshNode</text>
  <line x1="180" y1="142" x2="430" y2="186" stroke="currentColor" stroke-opacity=".35" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="270" width="340" height="50" rx="10" fill="none" stroke="currentColor" stroke-opacity=".25" stroke-dasharray="5,4"/>
  <text x="190" y="289" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".65">Subscriber Hub</text>
  <text x="190" y="308" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5">stream.Update(fn) → SetCurrent → PatchDataChangeRequest</text>
  <rect x="400" y="270" width="340" height="50" rx="10" fill="none" stroke="#f57c00" stroke-opacity=".5" stroke-dasharray="5,4"/>
  <text x="570" y="289" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#f57c00" fill-opacity=".85">Owner Hub</text>
  <text x="570" y="308" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5">PatchFunction → broadcast reconciled state</text>
  <line x1="360" y1="292" x2="400" y2="292" stroke="#f57c00" stroke-opacity=".75" stroke-width="2" marker-end="url(#arr-rev)"/>
  <line x1="400" y1="302" x2="360" y2="302" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="380" y="286" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#f57c00" fill-opacity=".8">write</text>
  <text x="380" y="316" text-anchor="middle" font-family="sans-serif" font-size="9" fill="currentColor" fill-opacity=".5">sync</text>
</svg>

*Workspace reference reduction hierarchy (top) and bidirectional owner-subscriber sync (bottom).*

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

`GetRemoteStream<TValue, TReference>` is the generic cross-hub reducer subscription (layout areas, custom references, collections):

```csharp
// Generic typed reference with write-back support
var stream = workspace.GetRemoteStream<UiControl, LayoutAreaReference>(
    new Address(path), new LayoutAreaReference("Overview"));
```

> 🚨 **For a MeshNode by path, do NOT use `GetRemoteStream<MeshNode, …>`** — use `workspace.GetMeshNodeStream(path)` (the shared `IMeshNodeStreamCache` handle, read + `.Update(...)` write-back on one stream). The `GetRemoteStream<MeshNode>` forms are discouraged and reserved for framework plumbing; see [CQRS](/Doc/Architecture/CqrsAndContentAccess) and [Data Access Patterns](/Doc/Architecture/DataAccessPatterns).

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

### `GetMeshNodeStream(path).Update(...)` (the canonical mutation API)

One call handles own, local-collection, and remote nodes — the handle auto-dispatches. It returns a **cold** observable; the write only runs on `Subscribe`:

```csharp
// Own node (this hub):
workspace.GetMeshNodeStream().Update(node =>
        node with { Content = updatedContent })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));

// Any other node — same API; the write routes to the owning hub as a
// RFC 7396 JSON-merge patch via the process-wide IMeshNodeStreamCache:
workspace.GetMeshNodeStream(remotePath).Update(node =>
        node with { Content = updatedContent })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));

// Typed content update — unpacks and repacks Content for you
// (MeshNodeExtensions in MeshWeaver.Graph; delegates to the same handle):
workspace.UpdateMeshNode<MyContentType>(path,
        (node, content) => node with
        {
            Content = content with { Title = "Updated" }
        })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

### Direct stream update

When you already hold an `EntityStore` stream (inside data-source plumbing), `MeshNodeExtensions.UpdateMeshNode` applies the change to the store directly:

```csharp
stream.UpdateMeshNode(node =>
    node with { Content = updatedContent }, nodePath);
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
