---
nodeType: Markdown
name: Module Sync Per Manifest Hash
category: Architecture
description: >-
  An instance always syncs every module it has. Each module is judged alone by the content hash in
  its manifest.lock — unchanged writes nothing, changed syncs, a declared platform floor above the
  running platform declines that one module — and the seal decides only whether a NodeType adopts
  prebuilt bytes or compiles. Why the whole-Space seal hold was removed, what each sync lane does
  now, and what this does not establish.
icon: /static/NodeTypeIcons/box.svg
---

# Module Sync Per Manifest Hash

Policy [`module-sync-per-manifest-hash`](../PolicyNotProse), coordinated with
`platform-backwards-compatibility` ([Module Versioning](../ModuleVersioning)):

> An instance **always** syncs every module it has. Nothing holds a whole Space or partition behind a
> per-identity seal. Each module is judged **alone**, keyed by the content hash in its own
> `manifest.lock`. The seal decides only whether a NodeType **adopts** prebuilt bytes or **compiles**
> from the synced source — never whether, or at which commit, the sources arrive.

## Why the whole-Space hold had to go

`SealedSyncGate` used to let a module-bearing repository's sources advance only to the commit sealed
for the running framework identity, and hold them otherwise ([When a Publication Seal Stops
Advancing](../PublicationSealStarvation)). That verdict was per **repository**, and it applied to
every Space of that repository at once.

Under the compatibility ladder the only seal a running identity can use is one produced by a build
at or below the running one. When the newer seals all come from newer platforms, the one usable seal
stays where it is — for as long as the instance is not rolled — and every Space of the repository
stays with it.

Measured on the control instance (memex.systemorph.com, 2026-09-25): image `3.0.0-ci.9218`,
`Hosting/_GitSync` read `lastSyncOutcome: Held` at Plugins commit `7545d355`, and every newer Plugins
publication was sealed only by `3.0.0-ci.9321` or later, which the ladder does not adopt here. The
migrate-first Roll planner (MeshWeaver.Plugins#2219) is **Hosting** code, so it never reached the
control plane. That control plane went on planning its own rolls with the old planner, which skipped
the database migration, so the new pods crash-looped on `DbVersionGate` for about a day. The hold's own
text named the remedy as "the roll". That was a bootstrap deadlock: the thing that plans the roll
depended on the roll.

Under the ladder the hold protected nothing. Sources compile against the RUNNING platform, and bytes
built for a newer platform are refused at adoption, one type at a time, with both versions named. The
hold only ever stopped the sources, and those are the part that was safe to deliver.

## The rule, per module

The pure decision is `ModuleSyncDecision.Decide` (MeshWeaver.GitSync). It runs inside every import,
after the fetch, over each `manifest.lock` in the incoming tree:

| Order | Condition | Outcome | Written |
|---|---|---|---|
| 1 | the module's root `index.json` declares `content.minMeshVersion` **above** the running platform (`PlatformCompatibility.ProducerIsNewer`, the ladder's own comparison; unknown on either side is accepted) | **Declined** — the reason names both versions | nothing for that module; its siblings sync |
| 2 | incoming `moduleVersion` **equals** the one the Space recorded when that module last landed, and the import is not a reconcile or a force | **Unchanged** | nothing |
| 3 | anything else: changed, never recorded, or a manifest that states no hash | **Synced** | the module, at the incoming commit |

- **A declined module is the ONE per-module decline**, and it holds no sibling. Its paths are neither
  written nor pruned, and the Space's commit baseline stays put, the same rule a partially held Space
  follows ([Adopt Then Sync, Per NodeType](../AdoptThenSyncPerNodeType)). Its files are still in the
  next diff, and the attempt is never recorded as final. The decline depends on the running platform,
  so a roll changes it at the same commit.
- **Unchanged** means byte-identical per the manifest, so nothing is written. When every file of the
  tree is under an unchanged or declined module, the import is a no-op: outcome `Skipped`, or
  `Declined` when a module was declined, and the fetched commit counts as seen.
- A tree with **no** `manifest.lock` (a course or content repository) states no module and imports
  exactly as before.

**Recorded hashes** live on the sync config as `ModuleVersions` (module → `moduleVersion`). They
advance only when the import's nodes **all** landed (`MayAdvanceBaseline`), the
[#2229 item C](../SyncRefContract) rule the commit baseline follows. If a module that did not fully
land had its hash recorded, the next attempt would read it as unchanged and the miss would become
permanent. A declined module keeps the hash it had. The first import after this change has no hashes
recorded, so every module syncs.

## What each lane does now

| Lane | Before | Now |
|---|---|---|
| Green-build webhook (`DecideBuild`) | landed on the sealed commit, or held | imports the **built commit**; `SealedCommit` reports whether this identity's bytes were baked from it |
| A person's Update / Re-import (`DecideRequestedImport`) | redirected onto the seal, or held | imports **exactly what was asked** |
| First import (`DecideFirstImport`: discovery and boot install) | landed on the seal, or held | resolves the **configured branch** |
| Seal arrival (`SealedSyncReconcile`) | imported the sealed commit when the source sat elsewhere or had no commit | imports **nothing by itself**. A source on another commit is usually AHEAD of the seal, and a source with no commit yet is brought by its first import or its next green build, which an import at the seal would race. What remains: re-importing a source AT the seal whose types were declined, and releasing a bundle hold at the commit whose sources were held |
| Adopted type whose new sources no bundle carries (`BundleKeyedHold`) | held at its old sources | **compiles from the synced sources**; the hold is kept only on a `Modules:RequirePrebuilt` mesh, where a local compile is refused and a move would park the type |

The decisions keep their public signatures (other repositories pin them). Their answers are now
always a proceed.

### The boot default install: one partition, one bookkeeping

The boot install asks the same first-import question, so it now lists the **configured** ref. That
ref is not proven, so where a sync source keeps the target partition current the install defers to
that writer, as `UnprovenRefHold` (MeshWeaver#4588) already did on the control instance. The sync
source follows green builds per manifest hash and is the partition's **one** writer.

Before this change the boot install wrote the seal's tree into a partition its own repository's sync
source keeps current. Now that the sync source moves past the seal, that would write an **older**
tree into the partition — the two-writer mix [One Partition, One Bookkeeping](../OnePartitionOneBookkeeping)
describes, in reverse. A partition nothing syncs still installs, at the configured ref.

## "Cannot read the index" — what it still refuses, and what it no longer freezes

`RefusedForUnreadableIndex` still says, at Warning, that the publication index could not be read. That
remains an absence of measurement and never reads as "nothing sealed" (#3461). What depends on the
index is **adoption**: an unreadable reading adopts no bytes it cannot verify, so each changed type
compiles from its synced source, or parks under its own name on a `RequirePrebuilt` mesh. The reading
no longer holds every source of every repository. Freezing all sources on one failed file read was a
wider refusal than the thing it protected. The unreadable **bundle shelf** was already treated this
way (see the per-NodeType page).

## Where it is reported

- **The sync config** (`_GitSync`): `ModuleOutcomes` lists every module of the last attempt as
  `Unchanged`, `Synced` or `Declined` with its reason, and `ModuleVersions` holds the recorded hashes.
  `LastSyncOutcome` is the import's own outcome (`Declined` only when every module was declined) and
  never the whole-Space `Held` the gate used to write. `LastSyncNote` names each declined module.
- **The activity**: one keyed Warning line, `activity.gitsync.modulesDeclined`, rendered in the
  viewer's language.
- **The settings tab**: `ui.gitSync.modulesDeclined` and `ui.gitSync.modulesUnchanged`.
- **`/health`** (`publication-seal`): every module's last outcome, by Space. A decline that outlives
  the 45-minute CI job cap reads Degraded, never Unhealthy.

## What this does NOT do

- **It does not skip the fetch.** The decision needs the incoming manifest, so an unchanged module is
  still fetched. What it saves is the writes, the prunes and the recompiles.
- **An unchanged module is not repaired against drift** by an unattended import. A reconcile or a
  force import never takes the Unchanged arm. That is the same trust the importer's content-fingerprint
  `Skipped` already places in a prior import.
- **The seal no longer advances a source on a webhook-less instance.** Such a source advances on a
  person's Update, or on a first import (a discovery scan re-attempts one while no commit is
  recorded). The seal's arrival imports nothing by itself.
- **Production verification is not part of this change.** Whether `Hosting/_GitSync` converges on the
  control instance is decided by the image that carries this code, and by the next green build of
  MeshWeaver.Plugins (or an Update) reaching it.

## How to check it is still true

- `ModuleSyncDecisionTest` states the rule as data, including the control-instance case: a changed
  Hosting module on an instance whose only usable seal is old and whose newer seals are for newer
  platforms **syncs**.
- `SealedSyncGateTest`, `SealedSyncGateLandsOnTheSealTest` and `SealedSyncFollowsTheLadderTest`
  re-express every shape that used to hold or redirect as a proceed.
- `HeldSourceSaysItIsHeldTest`, `APersonsImportLandsOnTheSealTest` and
  `SealArrivalReleasesHeldSourceTest` read the ref that reaches the transport on a real mesh.
- `ANodeTypesSourcesWaitForItsBundleTest` checks both meshes: one that compiles, where the sources
  move, and one with `RequirePrebuilt`, where they wait.

## Related

- [Adopt Then Sync, Per NodeType](../AdoptThenSyncPerNodeType)
- [When a Publication Seal Stops Advancing](../PublicationSealStarvation)
- [The Sync-Ref Contract](../SyncRefContract)
- [Module Versioning](../ModuleVersioning)
- [Module Adoption Policy](../ModuleAdoptionPolicy)
