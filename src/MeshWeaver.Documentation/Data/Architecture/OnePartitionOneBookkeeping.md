---
Name: One Partition, One Bookkeeping
Category: Architecture
Description: A partition written by both a GitSync source and the registry installer keeps two independent records of one mesh, and whichever writer diffs second lands a mix — the measured Store mix of 1.10.3 and 1.11.1, the Hosting mix that blocked a roll, the invariant, and the gates that hold it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V5a1 1 0 0 1 1-1h6l2 2h6a1 1 0 0 1 1 1v2"/><path d="M3 10h18v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/><path d="M9 15h6"/></svg>
---

# One Partition, One Bookkeeping

A mesh partition can have **two writers that keep separate books about it**, and neither can see the
other's. When that happens the partition does not go wrong loudly — it ends up holding a MIX of two
content identities that no CI ever compiled together, and every instrument involved reads as healthy.

This page states the invariant, the two measured cases it was learned from, and the gates that hold it.

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

## The gates

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

Two more properties of the seam are load-bearing, and both cost a silent skip when they are missing
(the callers are all `SelectMany`s, so a sequence that completes with **no** verdict runs no arm at
all — neither the install nor the hold):

- **Exactly once, whatever the providers do.** One leg that completes without emitting completes the
  whole `CombineLatest` with no value, and `Timeout` does not fire on a sequence that *completed*; a
  provider whose `IsTracked` throws **synchronously** does so while the sequence is being
  constructed, escaping every operator attached after it. Each leg is therefore `Defer`red, bounded
  and caught on its own, with an empty leg mapped to "did not answer".
- **A positive is decisive; a negative needs everybody.** One seam saying "a source tracks this" is
  knowledge whatever a second seam failed to say. Only when nothing said yes does it matter whether
  everyone answered — otherwise a seam that stopped answering would read as a clean partition.

Each caller resolves `Undetermined` in **its own** conservative direction, and the directions are
different — which is the point of one shared "cannot tell":

### Gate 1 — the unattended apply is not the second writer

`PackageUpdateReconciler`'s `Auto` branch consults the ownership verdict before applying. Where the
installer does not own the content the apply is **held and degraded to the reminder** — the same
idempotent, once-per-candidate notification the `Notify` policy raises, with a body naming the hold.
It is never a silent skip: an auto-update that will never land here is exactly the quiet *"my plugin
never updates"* this lane must not become.

Only the **unattended** lane is gated. A human clicking Update is the documented escape — the
Sync-Ref Contract draws the same line — and that click goes through gate 2.

### Gate 1b — the module half takes the same hold

A package update has two halves — its node **content** and its compiled **module bundle** — and the
platform's own rule is that *"a package never lands one half without the other"*: both follow the
package's update policy, in `PackageUpdateReconciler` and in `RegistryUpdateReconciler` respectively.

A content hold that stopped at the content would break exactly that promise. The partition's synced
content would stay on the sealed tree while the unattended module lane advanced the package's
**code** — the same sources-and-bundle split `MeshWeaver.Plugins#1430` removed, one level over. So
the ownership hold rides the module lane's existing `policyDecline` seam, in the one place both
unattended module lanes share (`AdoptOne`, used by the boot pass *and* the `ModulePublished`
broadcast drain). The package's own policy still speaks first, because it is the more specific
answer.

Such a partition is not left without a module: it has a delivery path already — the sealed
publication its content is held to — and a human's manual Update lands both halves together.

### Gate 1c — an unattended install never lands an UNPROVEN ref in such a partition

Gates 1 and 1b hold the unattended *update*. The **boot default install** was left as a lane that
writes such a partition *by design*, because [#4259](../DeclaredIsNotLanded) had pinned it to the
sealed commit: two writers landing the SAME tree do not mix. That design has a residue, and the
residue is where this class came back.

`InstanceAutoRegistrationService.ProvenRef` can only attribute a seal to a **repository**. A source
with no `RepoPath` — a remote registry, a registered `IPackageSource` — names no repository, so it
answers *"no repository to attribute a seal to"* and the lane lists at the configured ref. The
control instance's plugin source is exactly that, and its configured ref is the default, `HEAD`.

#### What it cost the second time (measured, memex.systemorph.com, 2026-09-17, [#4588](https://github.com/Systemorph/MeshWeaver/issues/4588))

1. **14:34:30Z** — the boot install stamped `Plugins/Hosting` 1.22.1 with **`installedFromRef: HEAD`**
   and a 222-file map, having written `main`'s tree into `Hosting`.
2. `Hosting/_GitSync` re-imports that same subdirectory every few minutes at the commit sealed for
   the running framework — then `061976bc` (2026-09-15), which does not carry the five files only
   `main` had, among them `Hosting/Issue/Source/FleetWatchCadence.cs`.
3. Thirteen minutes later the record **claimed** that file and the node was **absent**;
   `Hosting/DeploymentStatus` and `Hosting/InstanceAction` reported
   `MISSING SOURCES: 1 of N declared source queries … matched NO nodes` and `CS0246`. The roll onto
   the 3.0.0 candidate could not converge: the new replica's readiness refuses a type that regressed
   on its image.
4. **16:03Z** — the seal advanced to `d98fc2ac`. Its import is a delta **from the previous seal**,
   so it wrote `Issue/Source/IssueLayoutAreas.cs` (changed between the two seals) and left
   `Issue/Test/IssueTests.cs` alone (unchanged between them) — where the installer's `main` copy was
   still sitting. The partition kept one file from each tree:

```text
CS0117 Error: 'IssueLayoutAreas' does not contain a definition for 'Facts'
CS0117 Error: 'IssueLayoutAreas' does not contain a definition for 'StatusBadge'
```

🚨 **That step 4 is the reason gate 2 alone cannot close this class.** Gate 2 makes the INSTALLER's
delta full; the mix here was produced by the OTHER writer's delta, which has the identical blind
spot — it diffs its own baseline, not the mesh. Whichever writer diffs second lands a mix, so the
remedy has to be the one the table above already names: **one writer**.

#### The gate

`InstanceAutoRegistrationService.Install` asks the ownership question for a candidate whose ref this
boot could NOT prove, and holds where the installer does not own the content
(`UnprovenRefHold`). `Undetermined` holds too, and the asymmetry is the point: a hold that was wrong
is re-derived and lifted at the next boot, while an install that was wrong has already put a tree
into a partition it does not own, which no later pass takes back.

- **A proven ref is not held.** The seal named the commit, so the two writers land the same tree —
  #4259's design, untouched, and the reason the gate keys on provenance rather than on "this
  partition has a second writer".
- **A hold is loud, and it is not spelt like a skip.** Nothing is fetched and no module is adopted;
  the package is recorded as **HELD** — its own list on the summary and the seed ledger
  (`DefaultInstallHold`), said once at Warning. A `DefaultInstallSkip` is an authorization this lane
  can never obtain, and the summary renders those *"authorization, not retried"*; a hold is a fact
  about THIS boot's ref and THIS partition's writer, re-derived from scratch on the next pass. One
  list for both would advertise a permanent refusal where there is a transient one.
- **The declared access is still re-asserted.** It is create-only and writes nothing in the steady
  state, so withholding it would trade a content defect for an access one.
- **Nothing is left without content.** The partition's own writer delivers it, at the commit sealed
  for this instance, and a human's Update click remains the documented escape.

#### Only a writer counts as a writer

The question the installer asks is narrower than the compile control plane's, and the two are
separate members of the same seam:

| question | member | an `ExportOnly` source |
|---|---|---|
| *does this partition's content track an external source?* (#3583 — may I compile the live source?) | `IPartitionSourceTracking.IsTracked` | **yes** — the mesh IS the truth, so its live source is current and the type must compile rather than park |
| *does anything else WRITE this partition's content?* (#4588 — may I install here?) | `IPartitionSourceTracking.ImportsContent` | **no** — `mesh → repo` rejects imports, so it can neither revert nor prune what an installer wrote |

`ImportsContent` defaults to `IsTracked`, so a provider that cannot tell the directions apart keeps
the conservative answer; the shipped GitHub provider overrides it and excludes `ExportOnly` alone.
Collapsing them back into one bit would either hold an install for a writer that cannot write, or
park a type whose sources are current.

🚨 **The residues this leaves, both named:**

- A source that is a **local checkout** keeps today's behaviour ([#3359](../SyncRefContract) — there
  is no commit to pin and the operator IS the authority), so an operator who mirrors a working tree
  into a partition they also connected to git still has two writers. That is a configuration a
  person chose twice, like a human's Update click; it is not an unattended lane landing a tree
  nobody asked for.
- A **proven** ref is not compared against the repository the partition's own writer syncs. #4259's
  design assumes they are the same repository — which is the fleet's shape — but a package whose
  target partition is connected to a DIFFERENT repository would still get two writers with two
  trees. Closing that needs a seam that can name the tracked repository, not just answer a bit.

### Gate 2 — a delta is never diffed against a baseline the installer does not own

`CatalogLayoutAreas.InstallOrUpdateCore` consults the same verdict before choosing the incremental
path. Where the installer does not own the content (or ownership could not be established) the
incremental path is **not available**: a FULL install writes every file the package ships and stamps
a record that is true of the mesh again.

This is what covers the lanes that still write such a partition by design — the seal-pinned boot
install (gate 1c holds the UNPINNED one) and a human's Update click — so no lane can diff against a
record that has stopped describing the partition. The cost is one full package fetch, paid only when an update is actually landing (the
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
