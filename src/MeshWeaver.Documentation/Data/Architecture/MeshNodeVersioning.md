---
Name: MeshNode Versioning
Category: Documentation
Description: How MeshNode.Version is assigned from the owning hub's logical clock — the "1 op = 1 change" model
---

# MeshNode Versioning

Every `MeshNode` carries a `long Version`. It is **not** a node-local
counter and **not** an optimistic-concurrency token in the classic
"increment by one per write" sense. It is a stamp of the **owning hub's
logical clock** at the moment the node was last mutated.

## The hub clock

`MessageHub.Version` increments exactly once per message dispatch — see
`MessageHub.HandleMessageAsync` (`++Version` at the top of the method).
So the hub clock counts **operations the hub has processed**, not wall
time and not writes to any particular node.

```
hub.Version:   1     2     3     4     5    ...
               │     │     │     │     │
message:       M1    M2    M3    M4    M5
```

## 1 op = 1 change

When a MeshNode is mutated through the framework write path
(`MeshNodeStreamHandle.Update`, both the own-node and remote-node
branches), the framework stamps:

```csharp
updated = updated with { Version = _workspace.Hub.Version };
```

The lambda passed to `Update` does **not** set `Version` itself — the
framework owns the clock. One operation (one message handler running one
`Update`) produces one new `Version` value, equal to the hub clock at
that point.

Consequences:

- **A node mutated during message N has `Version == N`.** Two different
  nodes mutated under the same message share that version — they were
  changed by the same operation.
- **`Version` is monotonic per node** but **not contiguous**: a node
  updated under message 3 and again under message 47 jumps `3 → 47`.
  Never assert `newVersion == oldVersion + 1`; assert
  `newVersion > oldVersion`.
- Subscribers ordering changes (e.g. the `UpdateOwn` baseline check, or
  `DistinctUntilChanged(n => n.Version)`) rely on monotonicity, not on
  contiguity.

## Never-mutated nodes stay at their seed version

A node that is loaded from persistence (or seeded via `AddMeshNodes` /
`IStaticNodeProvider`) and **never** goes through `Update` keeps the
`Version` it was created with — typically `0`. `HandleSaveMeshNode`
persists the node's `Version` verbatim; the save path does **not**
synthesise a bump. So a static config node legitimately reads back as
`Version == 0`.

## Created nodes start at ≥ 1

`HandleCreateNodeRequest` stamps:

```csharp
Version = node.Version > 0 ? node.Version : 1
```

A freshly-created node gets `Version = 1` unless the caller explicitly
supplied a higher one (e.g. an import flow replaying historical
versions). The reason is serialisation, not semantics: the hub's
`JsonSerializerOptions` uses `DefaultIgnoreCondition = WhenWritingDefault`,
so `Version = 0` would be **omitted** from the persisted JSON entirely —
and downstream readers that expect the field to be present would break.
Starting at `1` guarantees the field is always serialised.

## Summary

| Situation | `Version` value |
|---|---|
| Seeded static / config node, never mutated | `0` (its seed value) |
| Node created via `CreateNodeRequest` | `1` (or caller-supplied, if > 0) |
| Node mutated via `MeshNodeStreamHandle.Update` | `hub.Version` at the op |
| Persisted via `HandleSaveMeshNode` | verbatim — no synthetic bump |

## What this is *not*

This is in-mesh change tracking for the live `MeshNode` graph. It is
unrelated to **data versioning** of the *content* held by NodeTypes
(historical query, time-travel, `@path@V{n}` snapshots) — that is a
separate concern, see [DataVersioning](DataVersioning.md).
