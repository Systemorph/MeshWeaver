---
Name: Partition Teardown
Category: Architecture
Description: Deleting a partition ROOT drops the partition's backing store — the structural rule that replaced two hand-maintained per-NodeType registrations, the boot gate that keeps it structural, and how to audit a portal for schemas orphaned before the fix.
Icon: /static/DocContent/Architecture/icon.svg
---

> 🚨 **TL;DR — teardown is keyed on the SHAPE of the deleted node, never on its NodeType.**
> A partition is a first path segment, so a top-level node **is** its partition's root, whatever its
> type. Deleting one drops the partition's backing store on every `IPartitionStorageProvider` and
> removes `Admin/Partition/{id}`. Keying that on a NodeType string left four partitions on the
> systemorph staff portal with live Postgres schemas after their roots were deleted.

This page is the deletion-side companion to [Partition Storage Routing](../PartitionStorageRouting)
and [Postgres Schema Architecture](../PostgresSchemaArchitecture).

## The two halves of a partition's life

| | Creation | Deletion |
|---|---|---|
| Trigger | `OwnsPartitionProvisioningValidator` (a top-level create of an `OwnsPartition` type) **and** `PackageInstaller.EnsurePartitionsProvisioned` (an install's target partition) | `PartitionDropPostDeletionHandler` |
| Registered | once, by `AddRowLevelSecurity` | once, by `AddGraph` |
| Keyed on | `NodeTypeDefinition.OwnsPartition` / the installer's own manifest | **the deleted node's SHAPE** — `PartitionDefinition.IsPartitionRoot` |
| Does | `EnsurePartitionProvisioned` on every provider, before the root write | `DeletePartition` on every provider, then delete `Admin/Partition/{id}` |

Note the asymmetry in the first row: **creation has two triggers, and only one of them consults
`OwnsPartition`.** That is why the deletion side cannot be driven off `OwnsPartition` either — see
below.

## Why the teardown is structural

`PartitionDropPostDeletionHandler` used to be registered **once per NodeType**, with the matched
type supplied at the registration site — `AddSpaceType` → `Space`, `AddUserType` → `User`. Anything
else rooted a partition that nothing ever tore down. The failure is silent by construction: the
recursive delete removes the `mesh_nodes` rows and answers `Ok`, and only the schema is left behind.

It happened twice.

- **2026-07-19, memex-cloud.** A `User` home was deleted and its whole partition was left behind.
  The fix applied was a **second** hand-written registration, in `AddUserType`.
- **2026-09-06, systemorph.** `AgenticPrimerDe`, `DataImportExport`, `DataModeling` and
  `ThinkInStreams` — four partitions rooted at **`Store/Plugin`** nodes — were deleted. Their nodes
  went; their schemas and their `Admin/Partition` definitions stayed. Every `Space`-rooted partition
  in the same run (the whole Reinsurance family, `AdvancedBusinessRules`, `AgenticEngineering`) was
  removed completely.

### 🚨 Driving the registration off `OwnsPartition` would NOT have fixed it

The obvious generalisation — "register the teardown for every NodeType whose definition sets
`OwnsPartition: true`" — is wrong twice over, and both reasons matter:

1. **`Store/Plugin` is declared in mesh CONTENT, not in `src/`.** It ships in the plugins package,
   so no compile-time scan, allow-list or source-generated registration can enumerate it. A guard
   built that way passes while the actual victim is uncovered — a guard that cannot fail in the case
   it exists for.
2. **`Store/Plugin` does not set `OwnsPartition` at all.** Measured on the live node
   (`get @Store/Plugin`): its `NodeTypeDefinition` carries no `ownsPartition` field. It does not
   need one — `PackageInstaller` provisions the target partition itself. So a runtime scan for
   `OwnsPartition == true` would have skipped it too.

The same is true of `Crm/Client` (`ATIOZ`, `HowdenRe`, `PartnerRe`, `PearlTechnology`, `Scheuchzer`,
`VIGRe`, `PG3`) and of `Store/Catalog` (the `Store` partition itself) — three families of in-mesh
partition-root types on one portal, none of them visible to `src/`.

The only predicate with no blind spot is the structural one:

```csharp
// PartitionDefinition
public static bool IsPartitionRoot(MeshNode? node)
    => node is not null
       && string.IsNullOrEmpty(node.Namespace)
       && IsValidPartitionSegment(node.Id);
```

Nothing about the type is consulted, so a NodeType that arrives from the mesh **after boot** is
covered by construction. `_`-prefixed and malformed segments are excluded by
`IsValidPartitionSegment`: a global satellite namespace (`_Access` → `system_access`) is registered
with an explicit schema and is never a partition derived from its name.

`INodePostDeletionHandler` grew one member to express this:

```csharp
bool Matches(MeshNode deletedNode) =>
    !string.IsNullOrEmpty(deletedNode.NodeType)
    && NodeType.Equals(deletedNode.NodeType, StringComparison.OrdinalIgnoreCase);
```

The default **is** the historical rule, so per-type handlers (`NodeTypeUnparkPostDeletionHandler`)
are untouched; the partition teardown overrides it with `IsPartitionRoot`, minus the system-managed
mirror partitions (`Auth`, the `User` lookup mirror), which are created by the migration and
populated by a trigger.

### What bounds the blast radius

A delete only reaches the teardown for a root whose subtree is already gone: a non-recursive delete
of a node with children is refused (`NodeDeletionRejectionReason.HasChildren`), and a recursive one
has drained the subtree before step 5 runs. So "the partition root was deleted" and "the partition is
empty" are the same statement by the time the drop fires.

## The ordering contract

Store-drop **then** definition-delete, sequentially (`.Concat`, like provisioning, so concurrent DDL
never races):

- a failed drop leaves `Admin/Partition/{id}` in place, so the partition stays visible for a retry
  rather than becoming an invisible orphan schema;
- the definition delete runs under `ImpersonateAsSystem` — infrastructure cleanup in the `Admin`
  partition, which the deleting user legitimately may not hold rights on;
- it is best-effort: an absent definition (a bootstrap-created partition never got one) is not a
  failure, because the backing store is already gone, which is the part that matters.

100% reactive — the async DDL edge is sealed inside each provider's `IIoPool`.

## The guards, and what each can actually catch

Two, deliberately, because they fail in different places.

**1. `PartitionTeardownCoverageGate` — a boot REFUSAL.** Registered by `AddGraph`; on
`StartAsync` it resolves every `INodePostDeletionHandler` and asks whether any of them matches a
synthetic partition root. If none does it logs `Critical` and throws, and the host does not start.

- **The probe carries a NodeType no registration can have enumerated**
  (`PartitionTeardownProbe/UnknownInMeshType`). A probe typed `Space` would have been green through
  the entire incident. This is the point of the guard, not a detail of it.
- **It has no arming condition.** An earlier draft only armed when a *writable*
  `IPartitionStorageProvider` was registered — and measurably went silent on the Monolith test host,
  whose providers are all read-only, so a deliberately broken registration booted clean. That is
  "a gate never tests its own inputs" in runtime form. Calling `AddGraph` **is** the statement that
  this mesh has partitions, so once the gate is registered it always decides.
- **A refusal, not a warning.** A portal that cannot tear a partition down leaks a database on every
  space deletion, silently, while every delete reports `Ok`. Nobody reads a warning about that.

**2. A `LogCritical` in the delete pipeline itself.** `MeshExtensions.ResolvePostDeletionHandlers`
logs `Critical` when the deleted node **is** a partition root and **no** handler matched it, naming
the path, the NodeType and the partition. It lives in the pipeline, not in a registration chain, so
it fires on every host — including one that never called `AddGraph` and therefore never registered
the gate.

## No resurrection: the bootstrap may repair a root, never re-create a partition

`MeshExtensions.EnsurePartitionBootstrap` heals a partition whose root row is missing when a child is
created under it. Left unbounded that heal is a **second** trigger for partition creation:
`ProvisionAndCreateRoot` calls `EnsurePartitionProvisioned` and then writes a `Space` root, behind
`OwnsPartitionProvisioningValidator`'s back and past `PartitionWriteGuardValidator`'s
"no partition, no write" rule (System passes both).

That is where the visible half of the incident came from. The four deleted roots reappeared as bare
`Space` nodes named after their path segment, each with a `PartitionAccessPolicy` child 67 ms later:
something wrote `{partition}/_Policy` into a partition whose schema was still there, and the
bootstrap conjured the root to hold it. The new policy granted Delete to nobody who could have
deleted the original — so the resurrected partition was **undeletable through the ordinary API**
(`Delete permission denied for 'AgenticPrimerDe'`) and invisible as damage in a Space listing.

So the ABSENT-root branch is now gated: when **every** provider definitively reports the backing
store gone, no root is created and a warning names the partition. The fold is the global OR the write
guard already uses — any `true` means it exists somewhere, every provider `false` means confirmed
absent, and anything else (a `null`, a probe fault, no providers) is indeterminate and allows the
heal. Fail OPEN, so a probe hiccup can never block a legitimate repair. The GHOST-root branch is
exempt by construction: a durable row means the store it was read from exists.

🚨 **This does not make resurrection unreachable, only much narrower.** The remaining window is a
child write that lands while the delete is still draining, before the drop: the partition's store is
genuinely still there, the probe says so, and the bootstrap correctly heals a root. Closing that
needs the delete to hold a partition-scoped exclusion that outlives its own drop — the
`RecentlyDeletedRegistry` subtree scope covers the drain but is released with the operation. It is
also worth noting what re-arms the write in the first place: `InstalledPackageRepairService` re-asserts
`EnsureDeclaredAccess` for **every recorded install** on every boot, and a package's install record
lives in the `Plugins` partition, so deleting the installed partition does not remove it.

## Auditing a portal for orphans

A Space listing **cannot** find these. Absence of a shell is not evidence the partition was dropped —
whether a shell reappears depends on whether anything later wrote into the partition, while the
orphaned schema is there either way. Reconcile three lists instead:

1. **The partition definitions.** `search 'namespace:Admin/Partition scope:children select:name,nodeType,lastModified'`
2. **The live partition roots**, per root type — the set is NOT just `Space`:
   `search 'nodeType:Space scope:children partitions:all'`,
   `search 'nodeType:Store/Plugin scope:children partitions:all'`,
   `search 'nodeType:Crm/Client scope:children partitions:all'`, plus `Store/Catalog` and any other
   in-mesh root type the portal has installed.
3. **The schemas themselves** — `SELECT nspname FROM pg_namespace ORDER BY 1` on the portal's
   database. This is the only authoritative list: a definition can be missing while the schema is
   there, and vice versa.

🚨 **Confirm every candidate with an exact-path read** (`get @{partition}`), never with the query
alone. `scope:children` listings are partition-scoped and RLS-filtered — the `Admin` partition, for
one, does not appear in an unscoped `nodeType:Space scope:children` even though its root exists — so
a query's `count: 0` is not by itself evidence of absence.

`V03_DropRogueSchemas` (MeshWeaver.Plugins, `src/Memex.Database.Migration/Migrations/`) is the
precedent for the cleanup shape when a migration is warranted. A cleanup is a separate, deliberate
decision from this fix, which only stops NEW orphans.

## See also

- [Partition Storage Routing](../PartitionStorageRouting) — how a path becomes a schema.
- [Postgres Schema Architecture](../PostgresSchemaArchitecture) — what is actually in the database.
- [Partitioned Persistence](../PartitionedPersistence) — the routing layer in front of it.
- [Access Control](../AccessControl) — `PartitionAccessPolicy`, and why a fresh one can lock out the
  identity that deleted the original.
