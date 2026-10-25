---
Name: Type Registry Layers
Category: Architecture
Description: >-
  A hub's type registry is a parent-chained overlay. It holds only what the hub adds to the chain or
  overrides in it, and resolves everything else through its parent, up to the mesh registry. An
  identical WithType stores nothing. Measured on Markdown node hubs and their sync/ stream hubs:
  5,751 own entries before (344 per node hub, 19 per sync/ hub), 0 after.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2 2 7l10 5 10-5-10-5z"/><path d="m2 17 10 5 10-5"/><path d="m2 12 10 5 10-5"/></svg>
---

# Type Registry Layers

Every hub owns an `ITypeRegistry`. It maps CLR types to the names they serialise under (`$type`),
names back to types, and types to their key functions. Every hub's registry is chained to its
parent's, up to the mesh registry that `MeshBuilder` registers. **The chain is the registry.** A hub's
own layer holds only what the hub adds or overrides, and every lookup walks the chain. It is the same
shape as the per-hub service container ([Hub Service Layers](../HubServiceLayers)):

```
sync/{id} hub      (own layer: empty)
   └─ node hub      (own layer: empty, unless the node type overrides something)
        └─ mesh hub (own layer: the data, layout and graph types AddData/AddLayout/AddGraph add)
             └─ mesh registry (root: basic types, mesh types)
```

## The rules

**Lookups walk own → parent → … → root.** By type (`GetTypeDefinition(Type)`, `TryGetCollectionName`,
`GetKeyFunction(Type)`), by name (`TryGetType`, `GetTypeDefinition(string)`) and by key
(`GetKeyFunction(string)`). The walk reads each layer exactly as registered. It does not create or
synthesise anything on the way up. A closed generic that no layer registers is named from its parts
(`UpdateStreamRequest[MeshNode]`) at the registry that was asked, and its name parses back the same way.

**Only overrides are re-registered.** `WithType(type, name)` stores something only when the type is new
to the chain, or when it is genuinely different: another name for the type, or another key
(`WithKeyFunction`). The following are no-ops that return the registry and store nothing:

- the chain already registers the type under that name, and the name resolves to that definition;
- the name is the type's full name, the chain registers the type under another name, and the full name
  already resolves to it (every bound definition indexes its full-name alias);
- a closed generic under the name the chain synthesises for it.

An override is stored in the hub's own layer and wins there. The parent and the hub's siblings never
see it.

**Enumeration is the merged view.** `Types` yields the parent's entries first, with an own entry in
place of the parent's entry of the same name, then the own layer's additions. `OwnTypes` yields the own
layer alone. It is the census instrument.

**A hub never copies a parent definition.** It follows that a later change to the parent shows through:
a key function added on the parent, or a module rebinding a name while the mesh runs, is what the hub
resolves from then on. What the hub overrode stays its own.

**Collectible types stay where they were registered.** A type from a collectible load context (a
compiled NodeType) is held by the layer that registered it. That layer listens to the context's
`Unloading` and demotes its entries into a weak shadow. The root never learns a hub's collectible type,
so it never pins one. A hub that resolves the type through its parent holds nothing and listens to
nothing.

**Thread safety without locks.** The own layer is one immutable snapshot of three maps (name →
definition, type → name, full-name alias → definition). A write replaces it by compare-and-swap.
Readers never lock and writers never block. When two writers race to add the same new type, the first
one wins. A registry that adds nothing holds the shared empty snapshot.

## Key functions

The `[Dimension]`-tuple key is a **convention** of every `KeyFunctionBuilder`, after `[Key]`, `Id` and
`SystemName`. It used to be a provider that each `DataContext` added to its hub's registry. That made
every data hub's key functions differ from its parent's, so no data hub could resolve a definition
through its parent. A builder chains to its parent registry's builder. A factory registered with
`WithKeyFunctionProvider` applies to that registry and the registries below it. When it keys a type that
the parent leaves keyless, the registry binds a keyed definition of its own. That is an override.

An explicit key that only restates the convention is not an override worth holding. `MeshNode` is
keyed by its `[Key] Id`, so the mesh data sources no longer call `WithKey(n => n.Id)` or
`WithKeyFunction` for it.

## What re-registered parent types per hub, and what changed at the source

| Call | Per hub before | Now |
|---|---|---|
| `MessageHub` constructor: `WithType(PingRequest)`, `WithType(PingResponse)`, then `Register<PingRequest>` registered both again | two definitions of each, per hub | the explicit calls are gone. `Register<PingRequest>` resolves both through the mesh |
| `CreateMessageHub(...).WithTypes(address.GetType())` after the configuration constructor already registered it | a second registration | removed |
| `AddMeshTypes` / `WithGraphTypes`: "full-name read alias first, short name last" pairs | two own definitions per type per node hub | the full-name call is a no-op where the chain already resolves the full name |
| `ConfigureSynchronizationHub` and its stream handlers: `EntityStore`, `JsonElement`, `SynchronizationStream<T>.UpdateStreamRequest`, `SetCurrentRequest` | 19 own entries per `sync/` hub | `AddData` registers `JsonElement` and the two stream messages on the hub that adds data. Closed generics are synthesised |
| `MeshDataSource`, `MeshNodeTypeSource`, `MeshHubBuilder`: `WithKey(n => n.Id)` | a keyed `MeshNode` override per node hub | removed. `[Key] Id` is the convention |
| `DataContext`: `WithKeyFunctionProvider([Dimension] tuple)` | every data hub's builder differed from its parent's | a convention of every builder |

## Measured

`HubTypeRegistryHoldsOnlyItsOverridesTest` (`MeshWeaver.Graph.Test`) creates 12 Markdown nodes in a
Monolith test mesh and activates them through `GetMeshNodeStream`, holding the subscriptions. It then
walks every hosted hub (84: 12 node hubs, 69 `sync/` hubs, 3 others) and reads each hub's `OwnTypes`
against its parent's registry. Run it with `-showLiveOutput` for the census.

| | Before | After |
|---|---:|---:|
| Own entries, all 84 hubs | 5,751 | 0 |
| Own entries per node hub | 344 | 0 |
| Own entries per `sync/` hub | 19 | 0 |
| Entries that are the parent's very definition | 1,152 | 0 |
| Entries that re-build a definition the parent holds under the same name | 4,392 | 0 |

**Retained heap.** A local probe that was not committed, on the same test mesh, created 99 Markdown
nodes, activated one to warm it, and then activated the other 98, holding their subscriptions. It took
`GC.GetTotalMemory(true)` after three full collections, before and after. The 98 activations added 588
hubs.

| | Before | After |
|---|---:|---:|
| Retained by 98 activations | 232.9 MB | 181.0 MB |
| Per activation | 2,376,683 B | 1,847,184 B |
| Per hub (588 new hubs) | 396,113 B | 307,864 B |

That is about 88 KB less per hub. The figure is whole-process growth divided by the hub count, so it
includes everything else an activation holds. It is not a per-owner breakdown like the ClrMD walk in
[The Portal Heap Is Hubs](../PortalHeapIsHubs).

The unit tests are in `TypeRegistryIsARecursiveOverlayTest` (`MeshWeaver.Messaging.Hub.Test`):

- a hub that re-registers N parent types adds 0 definitions;
- an identical re-registration returns the existing definition;
- a plain hosted hub holds 0 entries;
- an override wins locally and leaves siblings and the parent alone;
- lookup works through a three-level chain;
- a collectible type registered on a hub is not retained by the root.

Five of the six fail on the registry before the overlay. The collectible test passes on both and is a
guard. The heap context is in [The Portal Heap Is Hubs](../PortalHeapIsHubs).
