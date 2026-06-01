---
Name: MeshNode Versioning
Category: Documentation
Description: How MeshNode.Version is assigned from the owning hub's logical clock — the "1 op = 1 change" model
---

# MeshNode Versioning

Every `MeshNode` carries a `long Version`. It is not a node-local counter, and it is not an optimistic-concurrency token in the classic "increment by one per write" sense. It is a stamp of the **owning hub's logical clock** at the moment the node was last mutated — a record of *which operation* changed the node, not how many times the node has changed.

## The Hub Clock

`MessageHub.Version` increments exactly once per message dispatch — see `MessageHub.HandleMessageAsync` (`++Version` at the top of the method). The clock counts **operations the hub has processed**, not wall time and not writes to any particular node.

```
hub.Version:   1     2     3     4     5    ...
               │     │     │     │     │
message:       M1    M2    M3    M4    M5
```

## One Operation, One Version Stamp

When a `MeshNode` is mutated through the framework write path — `MeshNodeStreamHandle.Update`, for both the own-node and remote-node branches — the framework stamps the result:

```csharp
updated = updated with { Version = _workspace.Hub.Version };
```

The lambda you pass to `Update` does **not** set `Version` itself. The framework owns the clock. One operation (one message handler running one `Update`) produces exactly one new `Version` value, equal to the hub clock at that point.

This has three practical consequences:

- **A node mutated during message N has `Version == N`.** Two different nodes changed by the same message share that version — they were touched by the same operation.
- **`Version` is monotonic per node but not contiguous.** A node updated under message 3 and again under message 47 jumps `3 → 47`. Never assert `newVersion == oldVersion + 1`; always assert `newVersion > oldVersion`.
- **Subscribers rely on monotonicity, not contiguity.** The `UpdateOwn` baseline check and `DistinctUntilChanged(n => n.Version)` both work correctly under non-contiguous version sequences.

## Never-Mutated Nodes Keep Their Seed Version

A node loaded from persistence — or seeded via `AddMeshNodes` / `IStaticNodeProvider` — and **never** written through `Update` keeps whatever `Version` it was created with, typically `0`. The `HandleSaveMeshNode` path persists the node's `Version` verbatim; it does **not** synthesise a bump on save. So a static config node legitimately reads back as `Version == 0`.

## Created Nodes Start at Version 1

`HandleCreateNodeRequest` stamps new nodes as follows:

```csharp
Version = node.Version > 0 ? node.Version : 1
```

A freshly created node gets `Version = 1` unless the caller explicitly supplied a higher value (for example, an import flow replaying historical versions). The reason is serialisation, not semantics: the hub's `JsonSerializerOptions` uses `DefaultIgnoreCondition = WhenWritingDefault`, so `Version = 0` would be **omitted** from the persisted JSON entirely. Starting at `1` guarantees the field is always present on the wire and in storage.

## Version Semantics at a Glance

| Situation | `Version` value |
|---|---|
| Seeded static / config node, never mutated | `0` (its seed value) |
| Node created via `CreateNodeRequest` | `1` (or caller-supplied, if > 0) |
| Node mutated via `MeshNodeStreamHandle.Update` | `hub.Version` at that operation |
| Persisted via `HandleSaveMeshNode` | verbatim — no synthetic bump |

## What This Is Not

This is in-mesh change tracking for the live `MeshNode` graph. It is entirely unrelated to **data versioning** of the *content* held by NodeTypes — historical queries, time-travel, and `@path@V{n}` snapshots are a separate concern covered in [DataVersioning](DataVersioning.md).
