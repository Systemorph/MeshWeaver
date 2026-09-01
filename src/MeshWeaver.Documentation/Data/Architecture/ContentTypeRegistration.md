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
  `src/MeshWeaver.Graph/Configuration/CompilationCacheService.cs`). Probing an adopted-but-not-yet-loadable
  bake therefore removes the store's bytes and forces a re-adoption on the next boot.
  `ShippedPrebuiltBundlesTest` caught this to the tick — an unchanged bundle was restamped where
  the boot contract requires it to be skipped.
- **It reintroduces per-NodeType boot cost.** Opening every compiled type's bytes at start is
  exactly what the CI content bake removed: 43 re-adopted assemblies cost 13.5 s of a 101 s boot
  before that work. See [NodeType Compilation](../NodeTypeCompilation).

The scope limit is also principled, not merely pragmatic: a compiled type registers the moment one
of its instance hubs activates, and a compiled type with **zero** instances has no stored payload
carrying its discriminator — so there is nothing for the registry's absence to degrade. The gap only
bites built-in types, whose content is written by installers and other partitions, and those are
precisely the static definitions the sweep covers.

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
