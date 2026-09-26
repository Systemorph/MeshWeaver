---
Name: Closed Type Set
Category: Architecture
Description: A process that activates only the NodeTypes its image registers in code — no type definition read from the database, compiled, or adopted. The switch (Mesh:ClosedTypeSet), the four places it acts, what it deliberately does not change, and why the control instance needs it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>
---

# Closed Type Set

A portal normally serves two kinds of NodeType. **Code-registered** types ship in the image: a
module's `MeshNodeProviderAttribute` (or `MeshBuilder.AddMeshNodes`) contributes a `MeshNode` whose
`HubConfiguration` delegate is the type. **In-mesh** types are rows: a `NodeType` node whose
`configuration` is C# inside a JSON string, compiled by the portal at runtime or adopted from a baked
bundle (see [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation)).

A **closed type set** is a process that serves only the first kind. It exists for the fleet's
**control instance**, whose readiness must depend on nothing but its image: on 2026-09-25 one
abandoned in-mesh type in a user partition refused readiness on every pod of the instance that runs
every governed repair, for about seven hours. The control-instance design states the requirement as
two properties — *P1: every NodeType the instance can activate is defined by the image; P2: readiness
reads nothing but the image and the database schema* — and this switch is the platform half of P1.

## The switch

| | |
|---|---|
| Configuration | `Mesh:ClosedTypeSet=true` (environment: `Mesh__ClosedTypeSet=true`) |
| Code | `builder.WithClosedTypeSet()` — wins over configuration |
| Read | `serviceProvider.IsClosedTypeSet()` |
| Default | **open**. An absent or unparsable value is open: closing the set withdraws types, so it is never inferred. |

It is a property of the **image**, not of a deployment record: the control image sets it as a
container environment variable, next to the module that registers its types.

## Where it acts

1. **Activation** — `NodeTypeEnrichmentHelpers.EnrichWithNodeType`. The code-registered fast path
   runs first, unchanged: a static node whose `Path` equals the instance's `nodeType` and that carries
   a `HubConfiguration` is applied directly, with no database read and no compile — which is already
   true on an open mesh. On a closed mesh a type that misses it is **refused at that point**, before
   the existence probe, the row's stream subscription or any compile or adoption. The instance
   activates onto an overlay (copy localized at render time) whose typed requests are NACKed with
   `ErrorType.Rejected` and the reason — never `CompilationFailed`, since nothing was compiled, and
   never `Unavailable`, since retrying cannot change the answer. No self-heal watches for a row to
   appear, because a row appearing can never change the answer.
2. **The pre-warm sweep, the bake probe and the boot seeders** — `DynamicTypePreWarmer.WarmDynamicTypes`
   / `ProbeDynamicTypes`, and the hosted service's `SeedAll` / `SeedPublishedRoot` ahead of them. All
   normally enumerate every NodeType row in every partition. On a closed mesh they enumerate
   **nothing** (not "everything, then filter"): the rows are exactly the input a closed mesh must not
   read, and a broken one must not be able to occupy, slow or fail the pass the readiness gate reads.
3. **Adoption** — `PrebuiltAssemblySeeder.SeedDetailed`, the one write every bundle adoption goes
   through (boot seeders, published root, on demand). A closed mesh answers `NotSeeded`.
4. **Compile dispatch** — the compile watcher, beside `Modules:RequirePrebuilt`. A compile asked of a
   row itself (its Compile button, a release request, a self-heal kick) skips on-demand adoption and
   is **parked** at `Error` with the closed-set reason instead of reaching Roslyn — and, unlike the
   prebuilt gate, is never *held* on a build the row already carries.

Every surface reports one sentence, `ClosedTypeSet.RefusalFor(type, instance)`.

## What it deliberately does not change

- **Records stay records.** Instances are ordinary rows: they read, write, search and sync as before.
  Only their *type* is fixed by the image, so a malformed record is refused as a record and cannot
  change which types exist.
- **Code-registered types resolve exactly as on an open mesh.** The switch adds a refusal after the
  fast path; it does not touch the fast path.
- **The readiness gate is not re-armed.** On the control image the sweep is off anyway
  (`PreWarm:DynamicTypes=false`), so `nodetype_bake` is never registered; the closed set is what
  makes that safe rather than merely quiet — nothing that is off can be turned back on by content.

## Moving a type from the mesh into the image

The code registration must look like what the dynamic compile emits for the row
(`DynamicMeshNodeAttributeGenerator.GenerateAttributeSource`): a `MeshNode` at the type's path with
`NodeType = "NodeType"`, a `NodeTypeDefinition` carrying **no** `Configuration` or `Sources` strings
(otherwise the sweep would count it as compilable), and `HubConfiguration = c =>
userLambda(c.AddMeshDataSource())`. Register the content types on the **mesh** hub's TypeRegistry
too — `WithContentType<T>()` registers only on an activated instance hub, and
`ContentDiscriminatorValidator` refuses a create whose `$type` the mesh registry cannot resolve.

A row left at the same path as a code registration is shadowed for activation but can still surface
in a `nodeType:NodeType` query (the merge de-duplicates by path, first to arrive), so a mesh that
moves a type into its image should not keep the row. The control instance starts from an empty
database and never has one.

## Related

- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — the in-mesh path this closes off
- [Stale State Until a Recycle](/Doc/Architecture/StaleStateUntilRecycle) — a closed mesh has no in-place type change to recycle onto; a new type set is a new image
- Memex `docs/control-instance.md` — the design, P1/P2 and the acceptance test
- MeshWeaver.Plugins `Hosting/FleetControlModule.md` — the module that registers the control types in code
