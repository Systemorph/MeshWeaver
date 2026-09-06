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
created under it. That heal used to be a **second** trigger for partition creation:
`HealPartitionRoot` (then named `ProvisionAndCreateRoot`) called `EnsurePartitionProvisioned` and
then wrote a `Space` root, behind `OwnsPartitionProvisioningValidator`'s back and past
`PartitionWriteGuardValidator`'s "no partition, no write" rule (System passes both).

That is where the visible half of the incident came from. The four deleted roots reappeared as bare
`Space` nodes named after their path segment, each with a `PartitionAccessPolicy` child 67 ms later:
something wrote `{partition}/_Policy` into a partition whose schema was still there, and the
bootstrap conjured the root to hold it. The new policy granted Delete to nobody who could have
deleted the original — so the resurrected partition was **undeletable through the ordinary API**
(`Delete permission denied for 'AgenticPrimerDe'`) and invisible as damage in a Space listing.

### 🚨 The seam the existing guards could never see: provisioning is not a write

#3436's first answer was a probe: skip the heal when **every** provider definitively reports the
store gone. That closed the steady state and nothing else, and the reason is worth stating exactly,
because it is what MeshWeaver#3451 turned out to be.

Two exclusions already existed and neither applies:

| Mechanism | Lifetime | What it guards |
|---|---|---|
| `RecentlyDeletedRegistry.BeginSubtreeDeletion` | opened before the deletion plan, released after step 5 — so it **does** cover the drop | `IStorageAdapter` writes, via `SubtreeDeletionGuardStorageAdapter` |
| `RecentlyDeletedRegistry.MarkDeleted` ("delete wins") | 30 s TTL from the delete, superseded by a real re-create | per-node-hub resurrecting SAVES |

Both guard **writes**. The resurrection did not happen through a write. `EnsurePartitionProvisioned`
— the API whose own contract calls it *"the ONLY trigger for partition creation"* — is DDL. It
crosses no storage adapter, so no scope, no tombstone and no guard was ever consulted for it, and the
bootstrap called it on every heal.

And a probe cannot substitute for an exclusion, because it is **read at one instant and acted on at
another**. The drop is step 5, *after* the drain, so a probe taken while the delete was draining
answers a truthful `true` — and authorises a `CREATE SCHEMA` that runs after the `DROP`. Measured on
the in-memory harness, the provider's ledger for one deleted partition read:

```
provision:P, drop:P, provision:P, provision:P
```

— the store came back **twice**, from a repair, after its own teardown had removed it.

### What replaced it

Two changes, and the first is the structural one:

1. **A repair cannot provision.** `HealPartitionRoot` no longer calls `EnsurePartitionProvisioned` at
   all. In every state where the heal is legitimate that call was a **no-op by construction** — a
   ghost row means the store it was read from exists, and an absent root over a live store means the
   store is already provisioned — so removing it costs nothing and removes the capability. What
   remains is a plain row write into whatever store already routes the partition: refused by the
   subtree guard while the delete is in flight, and failing loudly (Postgres `42P01`) afterwards
   instead of silently minting a shell over a schema it re-created itself.
2. **The heal consults the delete record, at the point of effect.**
   `MeshExtensions.PartitionRemovalOnRecord` asks the registry two questions — *is a deletion of this
   partition in flight?* (`IsUnderActiveDeletion`, the hard invariant) and *was it just deleted and
   not re-created since?* (`IsRecentlyDeleted`, the tombstone). The second is the half that
   **outlives the drop**, which is what #3436 asked for: a probe-based decision taken before the drop
   is still refused when it lands after it, because the tombstone was already on record when the
   probe ran. It gates the creator GRANT as well as the root — `{P}/_Access/{creator}_Access` written
   into a partition that is going away is the "fresh policy 67 ms later" half of the incident.

No new state, no new timer, no widened bound: both records already existed and are already written
synchronously at the delete source. `RecentlyDeletedRegistry` is registered unconditionally by
`MeshBuilder` at the mesh ROOT, so the check resolves with `GetRequiredService` and **always
decides** — it has no arming condition, which is the failure mode #3436's first coverage-gate draft
had (it armed only when a writable provider existed, and went silent on the read-only Monolith host).

The store probe stays where it was, as the second half of the refusal, and still fails OPEN on an
indeterminate answer so a probe hiccup cannot block a legitimate repair. The GHOST-root branch is
exempt from it by construction: a durable row means the store it was read from exists.

A legitimate delete-then-recreate is unaffected: re-creating the partition goes through
`OwnsPartitionProvisioningValidator` or the installer, whose root write crosses the storage seam and
supersedes the tombstone.

`PartitionResurrectionTest` (MeshWeaver.Graph.Test) pins all of it, including the positive control —
a missing root over a live store is still healed.

## The install record must not outlive its partition

A package's install record lives at `Plugins/{packageId}` — in the RECORDS partition, never in the
package's own — so deleting the installed partition left the record behind, aimed at nothing.
`InstalledPackageRepairService` then re-drove `PackageInstaller.EnsureDeclaredAccess` at that dead
partition on **every boot**: a permanent per-boot error for every partition anyone had ever deleted
— and the #3436 census counted **21 partition definitions with no live root** on the systemorph
staff portal, which is the upper bound on how many such records a single portal can be carrying —
and, before the change above, the writer that re-armed the resurrection race on every restart.

Core deliberately knows nothing about `Plugins/Package`; teaching the delete pipeline about it would
re-introduce exactly the coupling the structural teardown removed. So **the discriminator comes from
the record side**, through the same seam:

- **On delete — the state becomes unreachable.** `AddPluginCatalog` registers
  `InstallRecordPartitionTeardownHandler`, an `INodePostDeletionHandler` matching the same structural
  `PartitionDefinition.IsPartitionRoot` predicate (minus the mirrors and minus `Plugins` itself). It
  removes every install record targeting the deleted partition via
  `PackageInstaller.RemoveInstalledRecord`, the one sanctioned removal route. It resolves the records
  from two sources unioned: an authoritative point read of `Plugins/{partition}` (the shape of nearly
  every install, and a STORE read, so CQRS lag cannot hide a record written seconds ago) plus a
  listing for records whose `TargetPartition` differs from their id.
- **On boot — the records that already dangle are reported, not written to.**
  `InstalledPackageRepairService` asks `TargetPartitionIsGone` before re-asserting: a CONJUNCTION of
  three signals that each always answer — no provider reports the store present, the root node does
  not read back, and the partition has no child paths. A false "gone" would need a partition with no
  store, no root and no content, which is not a partition; any fault reads as the safe answer
  (present). A record that fails all three is **skipped and named at Warning**, with the remedy (the
  admin orphan list). It reports rather than deletes deliberately: the delete path knows exactly
  which partition went, in-process, right now, so removing the record there is deterministic; a boot
  pass that silently deleted install records on a three-probe heuristic would be a worse failure than
  the one it fixes.

`InstallRecordFollowsItsPartitionTest` (Memex.Portal.Shared.Test) pins the delete-side half, with
both controls: a nested node's deletion leaves the record alone, and deleting one partition does not
touch another's record.

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
4. **The install records** — `search 'namespace:Plugins nodeType:Package scope:children select:id,content'`.
   A record whose target partition is not in list 2 is a dangling reference from before the teardown
   handler existed; the boot pass names each one at Warning and an admin clears it from the
   catalog's orphaned-records list. New deletions cannot add to this list.

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
