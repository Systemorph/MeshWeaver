---
Name: Content-Type Registration
Category: Architecture
Description: A NodeType's content type must register because the definition is known, never because an instance happens to exist — and why the compiled types are deliberately left out of that sweep.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 7h-9"/><path d="M14 17H5"/><circle cx="17" cy="17" r="3"/><circle cx="7" cy="7" r="3"/></svg>
---

# Content-Type Registration

A NodeType declares the CLR shape of its content inside its own hub configuration:

```csharp
config.AddMeshDataSource(source => source.WithContentType<PluginContent>())
```

`WithContentType` records the mapping in the mesh-wide `IMeshContentTypeRegistry`, and every read
seam consults that registry to turn a stored `$type` back into the CLR type. A type the registry
has never heard of is not an error: the polymorphic converter degrades it to a raw `JsonElement`
by design, because an unresolvable discriminator is indistinguishable from an unknown one. The
value then reads as absent, the view renders empty, and a reactive wait never completes — with no
exception and nothing to grep. See [Serialization](../Serialization) for that failure mode in
general, and [CQRS and Content Access](../CqrsAndContentAccess) for reading content correctly.

## The defect: registration used to follow the INSTANCE

The configuration lambda above runs when an instance hub of that NodeType **cold-activates**. That
makes registration a side effect of somebody having created a node — which is a coincidence, not a
guarantee.

**Measured on a local portal, 2026-09-01.** Not one node carried `nodeType: Store/Plugin`; an
installed course root is re-typed to `Space`, and nothing else instantiates the type. So
`PluginContent` was never registered, every Store cover computed **no action buttons** — no Get,
no Install, no Update — and the Subscribe page reported the product was not for sale. With the
Update lane gone, installed content could not be refreshed either. One missing registration
disabled the whole commerce surface, and deployments that happened to hold a live instance escaped
by luck, which is what let the gap hide.

The lesson generalises past the Store: **any type whose nodes are all created by an installer, a
migration or another partition can be defined, compiled and completely absent from the registry.**

## The cure: registration follows the DEFINITION

`ContentTypeRegistrationSweep` (`src/MeshWeaver.Graph/ContentTypeRegistrationSweep.cs`) is an
`IHostedService` wired beside the root-hub reply stream in both transports
(`MonolithRegistryExtensions`, `OrleansServerRegistryExtensions`). At start it walks every static
node definition that carries a `HubConfiguration` and, for each one the registry does not yet know,
builds a short-lived probe hub:

```csharp
meshHub.GetHostedHub(
    new Address($"content-type-registration/{Guid.NewGuid():N}"),
    c => hubConfig(c.WithNodeTypePath(nodeTypePath))
        .AsTransientNodeProbe(startDataSources: false));
```

Two properties make this cheap and safe, and both are load-bearing:

1. **Registration is a side effect of the configuration BUILD**, not of anything running. Building
   the data context executes `WithContentType`, which is all that is needed.
2. **`AsTransientNodeProbe(startDataSources: false)` starts nothing** — no sync streams, no control
   plane. The probe is disposed immediately. This is the same mechanism the schema probes already
   use; see `ReadFromContentType` in `MeshOperations`.

`WithNodeTypePath` stamps the path so the registration lands under the NodeType the definition
describes rather than under the probe's own address.

## 🚨 Compiled NodeTypes are deliberately NOT swept

The obvious extension — sweep the runtime-compiled types too, by loading each one's cached assembly
and reading its configurations — was implemented, failed CI, and was **removed rather than
repaired**. Two independent reasons, either sufficient:

- **It destroys the content bake.** Resolving a compiled type's configurations loads its assembly,
  and `NodeAssemblyLoadContext.LoadNodeAssembly` **deletes the file** when the load throws
  `BadImageFormatException` ("deleting for regeneration",
  `src/MeshWeaver.Compiler.Pipeline/CompilationCacheService.cs`). Probing an adopted-but-not-yet-loadable
  bake therefore removes the store's bytes and forces a re-adoption on the next boot.
  `ShippedPrebuiltBundlesTest` (in `MeshWeaver.PluginCatalog.Test`, which lives in
  `MeshWeaver.Plugins`) caught this to the tick — an unchanged bundle was restamped where the boot
  contract requires it to be skipped. Note that it is NOT in this repo's solution: a core change
  can break it without any local build saying so.
- **It reintroduces per-NodeType boot cost.** Opening every compiled type's bytes at start is
  exactly what the CI content bake removed: 43 re-adopted assemblies cost 13.5 s of a 101 s boot
  before that work. See [NodeType Compilation](../NodeTypeCompilation).

The scope limit is also principled, not merely pragmatic: a compiled type registers the moment one
of its instance hubs activates, and a compiled type with **zero** instances has no stored payload
carrying its discriminator — so there is nothing for the registry's absence to degrade. The gap only
bites built-in types, whose content is written by installers and other partitions, and those are
precisely the static definitions the sweep covers.

## 🚨 Registration is an EVENT — a degrade must never be terminal

The sweep above answers *"the type is defined but nothing instantiates it"*. It does not answer the
other way a read can find the registry empty: **the type is not registered YET**.

A compiled NodeType registers its content type when its instance hub cold-activates, and that
happens only once Roslyn has produced the assembly. Loading nodes and compiling NodeTypes are
concurrent, so an instance's content can be read a few hundred milliseconds **before** its own type
exists. Every read seam handles that correctly for the emission in front of it — it asks the
registry, gets "unknown", and emits the content as an untyped `JsonElement`, which is the honest
answer at that instant.

The defect was what happened next: **nothing**. `Register` was a silent side effect — no event, no
observable — so a subscription that opened on the losing side of the race held the untyped value for
the life of the hub. The node itself never changes, so no further emission ever arrives to
re-convert it. The view renders empty, `edit_content` refuses the content, and every reactive wait
for the typed shape times out. Nothing about it is random; it is a race whose loser never recovers,
which is exactly why re-running a failing test "fixed" it
([#2952](https://github.com/Systemorph/MeshWeaver/issues/2952)).

**`IMeshContentTypeRegistry.Registrations` makes registration observable**, and the read boundary
waits on it:

```csharp
// MeshNodeStreamHandle.TypedContentObserver — arms only when the conversion DEGRADED
contentTypeRegistry.Registrations
    .StartWith(Unit.Default)          // closes the gap between the conversion and this Subscribe
    .Select(_ => TryRetype(raw))      // re-ask; keep the answer only if it is now typed
    .Where(n => n is not null)
    .Take(1)
    .Subscribe(n => observer.OnNext(n!), observer.OnError);
```

Four properties are load-bearing, and each of them is the reason a *different* wrong shape was not
used:

- **It is a subscription to the real EVENT, not a poll.** No timer, no interval, no re-subscribe
  loop. A watchdog that re-checked "has the type shown up yet" would be the band-aid; the type
  becoming known is an actual thing that happens, so it is published.
- **It arms only on the already-degraded path**, and at most one wait exists per subscription. A
  node that types on the first try pays nothing, and the wait is disarmed by the next emission, by a
  terminal, and by disposal.
- **It re-asks; it never force-fits.** The notification carries no content. The seam re-runs its own
  conversion and keeps the result only when it is genuinely typed, so a registration for an
  unrelated type is a no-op and an unresolvable discriminator stays an untyped `JsonElement`,
  exactly as before.
- **Notifications are delivered OFF the registering thread.** `Register` runs inside a
  `MessageHubConfiguration` build; handing a subscriber's render to that thread would re-enter hub
  construction from inside itself. `Registrations` therefore observes on the task pool, the same way
  a storage change notification arrives.

### Where the wait lives, and why one place is enough

`MeshNodeStreamHandle.Subscribe` is the single boundary every `workspace.GetMeshNodeStream(path)`
read passes through — own-hub and cross-hub, server-side and Blazor — and every emission already
goes through its `TypedContentObserver`. Putting the wait there covers both the cache read
(`MeshNodeStreamCache.GetStream`, whose conversion runs *upstream* of the handle and hands the
handle a still-degraded `JsonElement`) and the owning hub's own read of its workspace copy.

Two seams are deliberately left alone:

- **`MeshNodeTypeSource.ResolveJsonElementContent`** stores the degraded node in the owning hub's
  workspace, which is where the untyped value physically lives. It is *not* re-materialised there,
  because putting a repaired instance back into the workspace is a WRITE: it mints a version for
  what was only ever a read, and that is the `#1432`/`#2008` phantom-revision class. Every consumer
  of that copy reads it through the handle above, which now re-types on the way out.
- **`MeshNodeStreamCache.GetQuery`** is a query snapshot, not a node binding. Its consumers re-issue
  the query; a single held emission is not the shape of the defect.

### 🚨 What the late re-type does NOT fix — the same race at the ENRICHMENT seam

Content typing and layout-area registration are **two side effects of one configuration build**. A
compiled NodeType's `configuration` is a single expression:

```json
"configuration": "config => config.WithContentType<PandasExplorer>().AddLayout(layout => layout.AddPandasExplorerLayoutAreas().WithDefaultArea(\"Explorer\"))"
```

So an instance hub that did not bind the compiled configuration has **neither** the content type
**nor** the areas — and re-typing the content afterwards does not add a renderer. The two symptoms
travel together and have a common cause, but they need different cures, and only the first is fixed
here.

The second cure belongs at `NodeTypeEnrichmentHelpers.ApplyStreamResult`
(`src/MeshWeaver.Graph/Configuration/NodeTypeEnrichmentHelpers.cs`). Its "no compile lifecycle
attached" branch —

```csharp
if ((def.CompilationStatus is null || def.CompilationStatus == CompilationStatus.Unknown)
    && typeNode.HubConfiguration is null)
    return Observable.Return(node);        // bare node: no overlay, no WithOverlaySelfHeal
```

— returns the instance **unwrapped by `WithOverlaySelfHeal`**, unlike every sibling branch
(in-flight compile, missing bytes, execution refused). The instance falls to the mesh default hub
chain, and the re-enrichment short-circuit at the top of `EnrichWithNodeType`
(`if (node.HubConfiguration != null) return …`) pins it there for the grain's lifetime;
`NodeTypeRebindWatcher` recycles only on a change of `MeshNode.NodeType`, never on a compile
transition. A type whose first build has not been *kicked off* yet is therefore indistinguishable
from one that will never compile — and the framework already has a predicate for the difference (a
`Configuration`/`HubConfiguration` source string, or a recorded `CompilationStatus`, is what
`NodeTypeLayoutAreas.AppendSweepSummary` counts as "participates in compilation"). Until that branch
uses it, an instance that activates before its type's first build is stamped is answered with the
TERMINAL `area-not-found` where the transient `compile-progress` is true.

## Diagnosing a suspected miss

1. **Symptom**: a view that should render structured content renders empty, or a `ContentAs<T>`
   returns `null` for a node whose stored JSON plainly holds the fields. No exception is logged.
2. **Confirm** the stored JSON carries a `$type` and that the type is declared by some NodeType's
   `WithContentType`.
3. **Ask where the read happens.** A payload read on a hub that never registered the type is
   untyped by construction. The durable fix is often to move the read to the owning hub rather than
   to register the type more widely — see [Serialization](../Serialization).
4. **Count the instances.** If the answer is zero and the type is a built-in one, this page is your
   defect; the sweep should have covered it, so check that the transport's registry extension calls
   `AddContentTypeRegistrationSweep()`.

## Related Topics

- [Serialization](../Serialization) — `$type` discriminators and registering types on both ends
- [CQRS and Content Access](../CqrsAndContentAccess) — reading a node's content correctly
- [NodeType Compilation](../NodeTypeCompilation) — the bake, adoption, and why boot cost matters
- [Static Node Providers](../StaticNodeProviders) — where the static definitions the sweep walks come from
