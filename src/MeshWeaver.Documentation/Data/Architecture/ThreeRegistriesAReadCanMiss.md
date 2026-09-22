---
Name: Three Registries a Read Can Miss
Category: Architecture
Description: A read can fail for three different "not registered" reasons — the hub's type registry, the workspace's mapped collections, and the reduce manager's reference table — and they are three different sets. A guard that interrogates one while the read uses another passes having checked nothing, and a refusal that names neither the hub nor the asker cannot be followed up.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="5" rx="1"/><rect x="3" y="11" width="18" height="5" rx="1"/><path d="M3 20h12"/></svg>
---

# Three Registries a Read Can Miss

"The type is not registered" is three unrelated facts in this platform, each with its own table, its
own owner and its own failure site. A view that renders empty, an area that reports a defect, and a
subscription that is refused can all be the same sentence in a triage report and none of them the same
bug.

| # | The table | What it answers | Filled by | Failure site |
|---|---|---|---|---|
| 1 | `ITypeRegistry` (per hub) | what a `$type` discriminator means here | `WithType`/`WithTypes`, `DataContext.Initialize`, and serialization itself as a side effect | a value degrades to `JsonElement` — **silent** |
| 2 | `DataContext.TypeSources` / `DataSourcesByCollection` | which COLLECTIONS this workspace can stream | a data source's `WithType<T>(…)` | `WorkspaceStreams.CreateWorkspaceStream` throws `ArgumentException` |
| 3 | `ReduceManager` | which `(reference → reduced)` pairs this workspace can serve | `AddWorkspaceReference` / `AddWorkspaceReferenceStream`, i.e. the data source extensions (`AddMeshDataSource` registers `MeshNodeReference → MeshNode`) | `JsonSynchronizationStream.CreateSynchronizationStream` throws `DataSourceConfigurationException` |

The sets are **nested only by convention, never by construction**. Registry 1 is the widest by a long
way: `DataContext.Initialize` registers every mapped type source into it, *and* `PolymorphicTypeInfoResolver`
adds any non-collectible type the hub merely serialises, *and* every content type a NodeType declares
lands there when its instances are read. Registry 2 holds only what a data source maps. Registry 3
holds only what a data source extension declared.

## Rule 1 — a guard interrogates the registry the read will USE

A precondition check against a neighbouring table is not a check. `DomainLayoutAreas.Catalog` resolved
its `ITypeDefinition` from registry 1 and then handed it to a render that streams
`Workspace.GetStream(new CollectionReference(collectionName))`, i.e. registry 2. For the mapped types
the two agree, which is why it passed review and passed every test; for a content type that lives only
on individual nodes they do not, and the guard let that through to die three frames deeper:

```text
System.ArgumentException: Collections Harness are not mapped to any source.
   at MeshWeaver.Data.WorkspaceStreams.CreateWorkspaceStream[TReduced,TReference](…)
   at MeshWeaver.Data.ReduceManager`1.ReduceStream[TReduced](…)
   at MeshWeaver.Layout.Domain.DomainCatalogLayoutArea.RenderCatalog(…)
```

Two things go wrong at once there, and only the first is about registries:

- **The area aborts** instead of declining. An unhandled throw out of a render reaches
  `LayoutAreaHost`'s top-level `Catch`, which logs at `Error` — the level that auto-files an incident —
  and renders the exception's own text. So an AUTHORING mistake (`@@Catalog/Harness` naming a type that
  is not a workspace collection) is reported as an engineering defect, and the viewer is shown a
  sentence about the framework's collection map.
- **It is the same class the classifier already demotes.** `AreaErrorClassifier` exists precisely to
  keep user-action and authoring outcomes off the error dashboard — `IsAccessDenied` and
  `IsNodeGoneNotFound` both log at `Warning` with viewer-appropriate copy. An unmapped collection
  belongs in that company, and the cheapest way to put it there is to never throw: decline in the area,
  in the viewer's language.

The fix is to ask registry 2 — `area.Workspace.DataContext.GetTypeSource(collection)` — because it is
*the same map, keyed the same way*: `DataContext.Initialize` builds `TypeSources` from
`typeSource.CollectionName` and `DataSourcesByCollection` from `TypeRegistry.GetCollectionName(mappedType)`
in one pass. It is also the set `AddTypesCatalogs` enumerates to OFFER catalog links, so the accept set
and the offer set become one set — which is the invariant that stops the guard drifting from the read a
second time.

## Rule 2 — a refusal names the hub, the asker and both types

Registry 3's refusal used to read:

```text
No reducer defined for MeshNodeReference from  MeshNodeReference
```

The same type parameter twice — the "from" half was meant to be the REDUCED type, and the doubled space
is the other half of the same copy-paste — and neither the hub that refused nor the subscriber that
asked. A reader gets the one thing they already knew from the stack frame and none of the three things
they need: which hub, asked by whom, for what.

The condition is specific and worth stating plainly, because it is a real and reachable configuration
shape rather than an impossible one:

> A hub configured with `AddData()` HAS a workspace, so `Workspace.SubscribeToClient` runs and reaches
> the reducer lookup — but only `AddMeshDataSource` registers `MeshNodeReference → MeshNode`.

The mesh's infrastructure hubs are exactly that shape. `portal/nodeops-{meshId}` (node CRUD execution)
and `portal/reads-{meshId}` (read issuing) carry a workspace and deliberately own **no mesh node of
their own** — see [Transient Node Probes](../TransientNodeProbes) for the same invariant on a different
family of addresses. So a `SubscribeRequest` carrying a bare `MeshNodeReference` addressed at one of
them is accepted, routed, and then has nothing to reduce with.

**Registering a reducer there would be the wrong fix.** Those hubs own no node on purpose: a
`CreateNodePermissionAttribute` anchors its check at the RECEIVING hub's path, which is why
`WithNodeOperationExecution` must never land on a per-node hub. Giving one a MeshNode data source would
hand it a node it must not have. The question the refusal has to make answerable is instead *who asked
an address with no node for its node*, and that is the subscriber — which is why the subscriber is now
in the message.

## Rule 3 — a recovery failure names its subject

`ObjectAsExtensions` is the sanctioned reader for an `object` payload, and it reports an unconvertible
value rather than returning a silent null. Two of its three report sites named the subject the caller
passed (`for {What}` — a node path, a control name); the third, the one reached when stored JSON will
not bind to the target at all, dropped it:

```text
As<MarkdownContent> could not recover value: JsonException. Content and exception details withheld.
```

Withholding CONTENT and the exception MESSAGE is load-bearing: a custom converter can put the whole
value into an exception, and a `JsonException` path can carry user-controlled dictionary keys. The
SUBJECT is neither. Without it the line is a fact with no referent — not the node, not the partition,
not which of the many writers of markdown content sent it — and production carried it for twelve days
with nobody able to act.

The general form: **a diagnostic exports no new class of information by naming the thing it is about.**
If a path is already safe in the sibling branch of the same method (and it was, in both overloads), it
is safe here.

## Distinguish this from an absent NODE

All three registry misses are about CONFIGURATION, and they look from the outside like the unrelated
case of a node that is not there. That one has its own machinery and its own answer:

- A point read of a path with no node is answered by routing with
  `DeliveryFailureException: No node found at 'X'. Closest ancestor is 'Y' …`, which
  `AreaErrorClassifier.IsNodeGoneNotFound` recognises, `TryGetMissingNodePath` unwraps, and
  `AreaStreamRetry` deliberately does NOT retry (a gone node does not come back on its own).
- A node-bound BINDING does not mint that failure at all: `MeshNodeBindingExtensions.Bind` gates the
  content read behind a live `scope:children`-style existence query, so an optional-node field draws
  empty and follows the node if it later appears, and the point read that would have NotFound-stormed
  the path is never issued.

So "the view is empty" is at least four distinct roots. The instrument that separates them is the
exception TYPE plus the frame that threw, never the symptom.

## See also

- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why existence and content are two reads
- [Transient Node Probes](../TransientNodeProbes) — addresses that can never carry a node
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the process-wide per-path mirror these reads go through
- [What the DataContext Init Time-Box Bounds](../DataContextInitializationTimeout) — the registries above
  are filled during the init this bounds
- [JSON Serialization](../Serialization) — registry 1's other job
