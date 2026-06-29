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

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 320" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#1e88e5"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#43a047"/>
    </marker>
    <marker id="arr-orange" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#f57c00"/>
    </marker>
  </defs>
  <text x="380" y="26" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".85">Hub Logical Clock — One Operation, One Version Stamp</text>
  <rect x="20" y="44" width="720" height="44" rx="8" fill="#263238" fill-opacity=".5" stroke="currentColor" stroke-opacity=".2" stroke-width="1"/>
  <text x="36" y="61" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".5">hub.Version</text>
  <rect x="100" y="50" width="52" height="32" rx="8" fill="#37474f"/>
  <text x="126" y="71" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">1</text>
  <rect x="220" y="50" width="52" height="32" rx="8" fill="#37474f"/>
  <text x="246" y="71" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">2</text>
  <rect x="340" y="50" width="52" height="32" rx="8" fill="#37474f"/>
  <text x="366" y="71" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">3</text>
  <rect x="460" y="50" width="52" height="32" rx="8" fill="#37474f"/>
  <text x="486" y="71" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">47</text>
  <rect x="580" y="50" width="52" height="32" rx="8" fill="#37474f"/>
  <text x="606" y="71" text-anchor="middle" font-family="sans-serif" font-size="14" font-weight="bold" fill="#90a4ae">48</text>
  <line x1="152" y1="66" x2="218" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="272" y1="66" x2="338" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="392" y1="66" x2="414" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5"/>
  <text x="420" y="70" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#90a4ae" fill-opacity=".5">…</text>
  <line x1="430" y1="66" x2="458" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="512" y1="66" x2="578" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="632" y1="66" x2="700" y2="66" stroke="#90a4ae" stroke-opacity=".4" stroke-width="1.5"/>
  <text x="710" y="70" font-family="sans-serif" font-size="11" fill="#90a4ae" fill-opacity=".5">…</text>
  <rect x="60" y="128" width="130" height="44" rx="10" fill="#1e3a5f" stroke="#1e88e5" stroke-width="1.5"/>
  <text x="125" y="146" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">NodeA</text>
  <text x="125" y="162" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#90caf9">Version = 1</text>
  <rect x="300" y="128" width="130" height="44" rx="10" fill="#1b5e20" stroke="#43a047" stroke-width="1.5"/>
  <text x="365" y="146" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">NodeB</text>
  <text x="365" y="162" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#a5d6a7">Version = 3</text>
  <rect x="420" y="128" width="130" height="44" rx="10" fill="#e65100" stroke="#f57c00" stroke-width="1.5"/>
  <text x="485" y="146" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">NodeC</text>
  <text x="485" y="162" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffcc80">Version = 3</text>
  <rect x="540" y="128" width="130" height="44" rx="10" fill="#4a148c" stroke="#8e24aa" stroke-width="1.5"/>
  <text x="605" y="146" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">NodeA</text>
  <text x="605" y="162" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ce93d8">Version = 47</text>
  <line x1="126" y1="82" x2="126" y2="127" stroke="#1e88e5" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-blue)"/>
  <line x1="366" y1="82" x2="366" y2="127" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-green)"/>
  <line x1="486" y1="82" x2="486" y2="127" stroke="#f57c00" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-orange)"/>
  <line x1="606" y1="82" x2="606" y2="127" stroke="#8e24aa" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-orange)"/>
  <text x="192" y="152" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#43a047" fill-opacity=".8">same message →</text>
  <text x="192" y="164" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#43a047" fill-opacity=".8">same Version</text>
  <line x1="190" y1="148" x2="300" y2="152" stroke="#43a047" stroke-opacity=".5" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="190" y1="148" x2="180" y2="150" stroke="#43a047" stroke-opacity=".5" stroke-width="1"/>
  <rect x="60" y="220" width="130" height="44" rx="10" fill="#263238" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5"/>
  <text x="125" y="238" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity=".7">Static Node</text>
  <text x="125" y="254" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".45">Version = 0</text>
  <text x="125" y="276" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".4">(never mutated)</text>
  <rect x="300" y="220" width="130" height="44" rx="10" fill="#263238" stroke="#546e7a" stroke-width="1.5"/>
  <text x="365" y="238" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity=".8">New Node</text>
  <text x="365" y="254" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#80cbc4">Version = 1</text>
  <text x="365" y="276" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".4">(CreateNodeRequest)</text>
  <line x1="125" y1="175" x2="125" y2="218" stroke="currentColor" stroke-opacity=".2" stroke-width="1" stroke-dasharray="3,3"/>
  <line x1="365" y1="175" x2="365" y2="218" stroke="#546e7a" stroke-opacity=".4" stroke-width="1" stroke-dasharray="3,3"/>
  <text x="72" y="304" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".35">Version = hub.Version at mutation time — monotonic, not contiguous. Non-mutated nodes keep seed = 0. New nodes start at 1.</text>
</svg>

*Hub logical clock: each message increments the clock once; mutated nodes are stamped with the clock value at that operation — two nodes changed by the same message share a version, and a node skips from 3 to 47 if untouched in between.*

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

## Every Change Is Stamped — Including Updates — and Only by the Owner

The stamp is not optional and not add-only. The owning hub's data source (`MeshNodeTypeSource`) re-stamps `Version = _workspace.Hub.Version` on **every** change it emits — a freshly *added* node **and** an *update* to an existing one. Both branches must stamp, because the stamp is what advances the version on the emitted frame, and that advance is what makes the change propagate:

- the change feed and every subscriber's **monotonicity guard** key off `Version`; a frame whose version did **not** advance is indistinguishable from a duplicate and is dropped, so it never reaches subscribers' mirrors;
- a node that an UPDATE left at its incoming version therefore emits a "nothing-new" frame and the reconciled value is silently lost — **the read-your-writes-after-update bug**. Create worked (adds were stamped); update/patch did not. The fix stamps the update branch too. Pinned by `MeshNodeStreamEmissionTest` (a node-stream re-emit + replay regression).

**Only the owner mints a version.** A client/subscriber writing a node it does not own (a cross-hub `GetMeshNodeStream(path).Update(...)`) **carries the base version it last observed** and lets the owner assign the fresh value on apply — it never increments client-side. A pre-incremented client version (the old `Math.Max(existing, …) + 1`) ships a frame whose base is already out of date by the time it lands, and the owner's version-guarded merge mishandles it. See [DataSyncAndCrdt](/Doc/Architecture/DataSyncAndCrdt) §2 ("a subscriber never mints a version").

**Write through the live lambda parameter.** `stream.Update(node => node with { … })` must transform the node it is handed — the live, owner-reconciled value — never discard it and slam a separately-read full node (`_ => fetchedNode`). The owner computes the diff it applies against that live value; a discarded parameter bases the diff on a stale snapshot and can clobber a concurrent edit.

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

This is in-mesh change tracking for the live `MeshNode` graph. It is entirely unrelated to **data versioning** of the *content* held by NodeTypes — historical queries, time-travel, and `@path@V{n}` snapshots are a separate concern covered in [DataVersioning](/Doc/Architecture/DataVersioning).
