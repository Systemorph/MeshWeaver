---
Name: One Partition, One Bookkeeping
Category: Architecture
Description: A partition written by both a GitSync source and the registry installer keeps two independent records of one mesh, and the second delta is computed against a record that stopped describing it — the measured Store mix of 1.10.3 and 1.11.1, the invariant, and the two gates that hold it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V5a1 1 0 0 1 1-1h6l2 2h6a1 1 0 0 1 1 1v2"/><path d="M3 10h18v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/><path d="M9 15h6"/></svg>
---

# One Partition, One Bookkeeping

A mesh partition can have **two writers that keep separate books about it**, and neither can see the
other's. When that happens the partition does not go wrong loudly — it ends up holding a MIX of two
content identities that no CI ever compiled together, and every instrument involved reads as healthy.

This page states the invariant, the measured case it was learned from, and the two gates that hold it.

## The invariant

> **A partition has ONE content bookkeeping.** Either the installer owns the content — in which case
> its install record *is* a description of the mesh, an unattended apply may write into it, and a
> delta may be diffed against it — or another writer owns the content, in which case the installer
> must neither silently become a second writer nor diff against a baseline it does not own.

Equivalently, in the form it is usually needed: *whichever writer lands content must leave the other
writer's record consistent — or the partition must have one writer.*

## The two writers

| | Writer | What it writes | What it books it in | What gates it |
|---|---|---|---|---|
| 1 | **GitSync + the seal reconciler** (`SealedPublicationSyncReconciler` → `ReconcileAtProvenCommitFromGitHub`) | the whole partition, at the commit sealed for the running framework identity — including pruning nodes the commit does not carry | `{partition}/_GitSync.lastSyncCommitSha` | [the seal](../SealedPublicationReads) — the tree whose bundles this instance actually runs |
| 2 | **The registry installer** (`RegistryUpdateReconciler` → `PackageUpdateReconciler.Apply` → `CatalogLayoutAreas.InstallOrUpdate`) | the files whose hash moved since its own last install | `Plugins/{package}.installedFiles` + `.moduleVersion` | the package's own update policy |

Writer 1 does not touch the install record. Writer 2 computes its next delta *from* that record. So
after writer 1 has run, writer 2's baseline is a claim about a mesh that no longer exists — and a
delta computed from it skips exactly the files it believes did not change.

## What it cost to learn (measured, memex.systemorph.com, 2026-09-14)

`Store` is both: a GitSync space held by `SealedSyncGate` at the sealed Plugins commit `627fb3cd`
(Store sources 1.10.3), and the target of the registry-installed package `Plugins/Store` with
`autoUpdate: true`.

1. **~14:0xZ** — the registry auto-update advanced the install record to Store **1.10.14**, whose
   `installedFiles` records `Store/Core/Source/StoreTexts.cs = 8c9df35c…`.
2. **14:23Z** — a pod restart's boot sweep declined the Store bundles on their source fingerprint,
   and the seal reconciler re-imported the partition at `627fb3cd`
   (`Store/_Activity/19835cc6`: *"Re-imported Imported (200 node(s))"*, *"Pruned 9 node(s) absent
   from the repo"*). Every Store source went back to 1.10.3 content — `StoreTexts` to `559e54e5…`.
   **The install record still said 1.10.14.**
3. **20:25Z** — the registry served **1.11.1**. `StoreTexts.cs` is byte-identical between 1.10.14 and
   1.11.1, so the diff declared it unchanged and **never fetched it**; `Catalog`, `Installer`,
   `Maintenance`, `Order` and `Plugin` had all moved since 1.10.14 and **were** written, at 1.11.1.
4. `Store/Catalog` then recompiled against 1.10.3's texts and **PARKED**:

```text
CS1061 Error (line 3948): 'StoreTexts' does not contain a definition for 'ExploreCta'
CS0117 Error (line 3956): 'CoverContract' does not contain a definition for 'StartButtonLabel'
NodeType 'Store/Catalog' PARKED after compile failure — further activations serve the cached error
```

Three clocks disagreed about one partition — `Plugins/Store.installedFiles` (1.11.1),
`Store/_GitSync.lastSyncCommitSha` (`627fb3cd` = 1.10.3) and the mesh content (a mix of both) — and
every one of them, read alone, looked correct.

🚨 **The registry lane also bypassed the seal.** Its 1.11.1 sources are a tree this instance has no
proven bundle for, so every Store type's prebuilt assembly was DECLINED on its source fingerprint and
compiled in-mesh instead:

```text
Prebuilt assembly for Store/Catalog DECLINED before writing (#2813): the bundle records source
fingerprint 583f715172aae092 but the live sources are eb2e12083a7c85c0
[RegistryUpdateReconciler] Install: Store: adopted NO prebuilt assembly for any of 2 installed
type(s) — every one of them compiles in-mesh.
```

That is the same class the [Sync-Ref Contract](../SyncRefContract) removed from every GitSync path and
[#4259](../DeclaredIsNotLanded) removed from the boot default install: an unattended writer landing a
tree nothing on this instance authorised.

## Why the obvious remedies are worse

Three fixes suggest themselves. Two of them produce a consistent partition and a **thrashing** one:

| Remedy | Result |
|---|---|
| The seal reconcile also resets `installedFiles`/`version` | The next registry pass installs 1.11.1 in FULL — consistent, but unproven for this image, so the next boot's seal reconcile reverts it and the registry re-applies. A ping-pong, entrenching the seal bypass. |
| `InstallOrUpdate` diffs against the MESH's content rather than the record | Same outcome as above: the mesh converges on the registry's newest tree, which is precisely the tree the seal exists to keep out. |
| **One owner per partition** | The registry lane holds; the mesh stays wholly at the sealed commit; every bundle is adopted; nothing thrashes. |

So the ownership question comes first, and the bookkeeping repair is what covers the lanes that still
write such a partition *by design*.

## The two gates

### `PartitionContentOwnership` — who owns this partition's content

One decision, in `MeshWeaver.PluginCatalog`, answered from the platform's existing one-bit seam
`IPartitionSourceTracking` — the same seam the compile control plane already consults for a closely
related question ([#3583](../NodeTypeCompilation): compiling the live source is honest only on a mesh
that syncs it). The reasoning transfers exactly: *writing* a package's content into a partition is
honest only where the installer is that partition's source of truth. A second implementation of the
rule is how the two would come to disagree about a partition.

Three answers, and only one is a pass:

| `providerCount` | `tracked` | Owner | Meaning |
|---|---|---|---|
| 0 | — | `Installer` | This mesh registers no sync layer at all (a local mesh, CI's disposable meshes, the bake host). There is no second writer, and behaviour is unchanged. |
| ≥ 1 | `false` | `Installer` | A real negative: nothing tracks this partition. |
| ≥ 1 | `true` | `SyncSource` | A `{partition}/_GitSync` names a repository. The installer is not the writer here. |
| ≥ 1 | `null` | `Undetermined` | The seam faulted or did not answer inside its budget. 🚨 **Not a pass** — kept apart from `Installer` so a failed read is never spelt like a real negative. |

Each caller resolves `Undetermined` in **its own** conservative direction, and the two directions are
different — which is the point of one shared "cannot tell":

### Gate 1 — the unattended apply is not the second writer

`PackageUpdateReconciler`'s `Auto` branch consults the ownership verdict before applying. Where the
installer does not own the content the apply is **held and degraded to the reminder** — the same
idempotent, once-per-candidate notification the `Notify` policy raises, with a body naming the hold.
It is never a silent skip: an auto-update that will never land here is exactly the quiet *"my plugin
never updates"* this lane must not become.

Only the **unattended** lane is gated. A human clicking Update is the documented escape — the
Sync-Ref Contract draws the same line — and that click goes through gate 2.

### Gate 2 — a delta is never diffed against a baseline the installer does not own

`CatalogLayoutAreas.InstallOrUpdateCore` consults the same verdict before choosing the incremental
path. Where the installer does not own the content (or ownership could not be established) the
incremental path is **not available**: a FULL install writes every file the package ships and stamps
a record that is true of the mesh again.

This is what covers the lanes that still write such a partition by design — the seal-pinned boot
install and a human's Update click — so no lane can diff against a record that has stopped describing
the partition. The cost is one full package fetch, paid only when an update is actually landing (the
hash-equal skip path is untouched) and only on a synced partition.

## What the fix deliberately does not do

- **It does not stop, defer or weaken the seal reconcile.** The freeze class of
  [#4063](../SealedPublicationReads) is a sync that silently stops; this holds the *other* writer and
  leaves the reconciler's trigger, decision and cadence exactly as they were.
- **It does not add a bypass of the proven-commit guarantee** — it removes one. A held apply is an
  apply that does not land sources this instance has no bundle for.
- **It changes nothing on a mesh with no sync layer.** `providerCount == 0` keeps the pre-fix
  behaviour, so local meshes, CI meshes and the bake host are untouched.

## How to recognise this in the field

Read all three clocks and compare them, never one:

```text
get Plugins/<Package>            → version, moduleVersion, installedFiles, installedAtUtc
get <Partition>/_GitSync         → lastSyncCommitSha, lastSyncOutcome, lastAttemptedCommitSha
get <Partition>/<Type>           → compilationStatus, adoptedModuleVersion, currentModuleVersion,
                                   currentSourceFingerprint vs adoptedSourceFingerprint
```

The tell is a type whose `currentSourceFingerprint` ≠ its `adoptedSourceFingerprint` while its
`adoptedModuleVersion` and `currentModuleVersion` name two different releases, beside an install
record at a third. A `CS1061`/`CS0117` naming a member a sibling source *does* define is the same
tell one step later: the two files are from different trees.

## Related

- [Declared Is Not Landed](../DeclaredIsNotLanded) — the sibling defect: a delta that never observes the
  mesh at all, so a node lost after an install survives every later update. It repairs an **absent**
  node; this page is about a node that is **present with the wrong content**, which no presence check
  can see.
- [The Sync-Ref Contract](../SyncRefContract) — an unattended import reads a commit a build proved.
- [Install Completeness](../InstallCompleteness) — what a record declares, compared against the mesh.
- [Node Type Compilation](../NodeTypeCompilation) — the bundle decline and the in-mesh recompile that a
  drifted source triggers.
