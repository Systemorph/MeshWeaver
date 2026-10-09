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

Measured on the control instance (2026-09-25): image `3.0.0-ci.9218`,
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
| 1 | the module's root `index.json` declares `content.minMeshVersion` **above** the running platform (`PlatformFloor.Evaluate` — the ONE floor decision every package consumer uses, policy `package-min-mesh-version`; unknown, unreadable or unorderable on either side, or a local `-ci.0` build, is accepted) | **Declined** — the reason names both versions | nothing for that module; its siblings sync |
| 1b | the module's root `index.json` declares a `content.requires` entry (`AI@^1.21.0`) that the dependency's **loaded** module does not satisfy (`ModuleSyncDecision.DeclineUnmetRequirements`, against `ILoadedPackageModules`; an unknown loaded version, an uninstalled dependency or an unreadable range is not judged; the image's OWN copy is judged at the version its `module.seed.json` stamp states — see below) — #6067 | **Declined** — the reason names the requirement, the loaded module and its version; `UnmetRequirement` on the outcome | nothing for that module; its siblings sync |
| 2 | incoming `moduleVersion` **equals** the one the Space recorded when that module last landed, and the import is not a reconcile or a force | **Unchanged** | nothing |
| 2b | the module's floor is **not stamped for these sources** — its `mesh-floor.lock` witness records a `contentHash` other than the incoming content's (`ModuleFloorWitness.ContentHash`, the stamp's own rule) — **and** this instance knows a platform newer than it runs (`INewerPlatformReading`, the self-update's newest recorded tag) (`ModuleSyncDecision.HoldUnverifiedFloors`) | **Declined** — `FloorUnverified` on the outcome, `Floor` names the newer platform, the reason names both hashes, the stale floor and both platforms | nothing for that module; its siblings sync |
| 3 | anything else: changed, never recorded, or a manifest that states no hash | **Synced** | the module, at the incoming commit |

### A floor nobody stamped for these sources (rule 2b)

A package's floor is **stamped after** the commit that changes its sources: main's green run stamps
it, and until that stamp lands every package whose sources moved still declares the floor of its
PREVIOUS sources ([PackageFloors](https://github.com/Systemorph/MeshWeaver.Plugins/blob/main/Hosting/PackageFloors.md)).
So rule 1 judged a floor that said nothing about what it let through. Measured 2026-10-09 on
memex.systemorph.com (running `3.0.0-ci.10310`, `3.0.0-ci.10319` available): MeshWeaver.Plugins
`73e5065d` carried the approvals inbox on the data-bound row selection (Plugins#3214) while `Hosting`
still declared `3.0.0-ci.10305`; the import synced it at 12:07Z, the `3.0.0-ci.10310` image had no
renderer for the selection, and nothing in `Hosting/Approvals` could be selected. The stamp for exactly
that content, at 11:57Z, was `3.0.0-ci.10317` — but `73e5065d` predates the stamp commit.

The witness (`<Package>/mesh-floor.lock`, `contentHash` + `verifiedOn`) says which sources its floor
vouches for, and the import now recomputes that hash for the incoming tree exactly as the stamp did:
the package's files by raw-byte sha256 (all but `manifest.lock`, `.DS_Store` and the top-level
witness), the out-of-folder entries `manifest.lock` records (a mixed package's `src/` project), and
`index.json` hashed without its `minMeshVersion` value as Python's `json.dumps(sort_keys=True,
ensure_ascii=False)` writes it. Pinned against the node repository's own witnesses
(`ModuleFloorWitnessTest`: two real packages copied byte for byte, 76 of 76 matching at Plugins
`0b8f8754`, 23 detected pending at `73e5065d` — `Hosting` among them).

What a stale floor holds, and what it does not:

- **A lagging instance** — one whose self-update has recorded a newer platform tag, whether its roll
  onto it is available or held — does not take the module. Its NodeTypes keep serving their last good
  build, the baseline stays, and the next import judges again: after the stamp lands (rule 1 then
  reads a true floor) or after the platform rolls (the instance is no longer behind).
- **An instance on the newest platform it knows of** (or one that cannot tell) takes every push, as
  before — policy `sources-sync-on-push`: nothing waits for a stamp, a seal or a green build there.
- **A floor the witness verifies** is judged by rule 1 alone, and a reading nothing can verify (no
  witness, a hash that cannot be computed) is not judged.

What it still cannot see: a source that needs a renderer only an image NEWER than the newest one the
instance knows of ships. The instance on the newest platform takes it, and renders it without that
renderer until its next roll; only a floor that names the first image carrying the renderer closes
that, which is the stamp's job in the node repository.

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
- **Rule 1b judges the image's own copy too** (Systemorph/MeshWeaver.Plugins#2715). The loaded
  reading (`LoadedPackageModuleReader.ResolveWithImageCopies`) states a landed generation at the
  version its activation entry recorded, and a module that loaded from the IMAGE's copy at the
  version that copy's `module.seed.json` states (`ImageModuleSeed`), keyed by the package the stamp
  names. It used to leave the image copy out as "not judged", and that was the commonest copy there
  is. Measured on the control instance on 2026-10-05: its image (Plugins `29bfaefb`) ships AI
  stamped `1.21.1`; Hosting's tree declared `AI@^1.22.0` and called `LanePolicy`, which only AI 1.22
  carries. With no reading for AI the import wrote Hosting's sources, and twenty Hosting NodeTypes,
  `InstanceAction` among them, parked at `CS0246 LanePolicy`. A seed stamp can only UNDERSTATE the
  image's bytes (an unsettled `manifest.lock` keeps its number while the sources move on), so this
  can hold an import the image would have satisfied, but it never admits one the image cannot
  satisfy. An image copy with no stamp, a substituted load and an ambiguous load stay unjudged.

**Recorded hashes** live on the sync config as `ModuleVersions` (module → `moduleVersion`). They
advance only when the import's nodes **all** landed (`MayAdvanceBaseline`), the
[#2229 item C](../SyncRefContract) rule the commit baseline follows. If a module that did not fully
land had its hash recorded, the next attempt would read it as unchanged and the miss would become
permanent. A declined module keeps the hash it had. The first import after this change has no hashes
recorded, so every module syncs.

### A dependency floor the loaded build does not meet (#6067)

**Measured 2026-10-04 on the control instance.** Hosting 1.56 declared `requires: ["AI@^1.21.0"]`.
Its sources were GitSync-imported and Roslyn-compiled at 20:00:50Z while AI 1.20.4 was the loaded
build, and every thread start — reviews, watchdog fixers, the bug pool — then threw
`MissingMethodException` (`ThreadPreparation.set_Group`). The module-set proposal already refused a
set with an unmet floor ([Module Set Convergence](../ModuleSetConvergence)); the import that put the
sources in front of the compiler never asked.

So rule 1b runs in the same decision, after rule 1: a **Synced** module whose requirement the
loaded dependency does not satisfy becomes **Declined**, exactly like a platform floor. Its sources
are neither written nor pruned, so its NodeTypes keep serving their last good build; the baseline
stays, so its files remain in the next diff; the decline is on the sync config
(`LastSyncNote`, `ModuleOutcomes[].UnmetRequirement`) and in the `/health` module census, which
escalates a decline that persists. Nothing has to be armed to release it: the next import judges
again, and the import after the restart that activates a satisfying dependency syncs the module.

🚨 **Loaded, never landed.** `LoadedPackageModuleReader` (MeshWeaver.PluginCatalog) maps each
package to the generation its module actually LOADED from — the activation head's version when the
head loaded, the retained previous generation's when that one did, and nothing otherwise. A landing
is restart-as-activation, so judging against the head would let an import compile sources against a
1.21 that is landed but not running — the very shape this rule exists to stop. A mesh with no module
host registers no reader and judges nothing, which is the behaviour before the rule.

The range rule is `PackageRequirement` (MeshWeaver.Plugin.Packaging), shared with the proposal's
`ModuleDependencyFloor`: one reading, so the two checks can never disagree about the same range.

## What each lane does now

| Lane | Before | Now |
|---|---|---|
| Green-build webhook (`DecideBuild`) | landed on the sealed commit, or held | imports the **built commit**; `SealedCommit` reports whether this identity's bytes were baked from it. **Superseded** by policy `sources-sync-on-push`: the `push` webhook (and a periodic branch reconcile) imports at the pushed commit, and a green build only records the build — see [Sources Sync on Push](../SourcesSyncOnPush) |
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
  `LastSyncOutcome` is the import's own outcome (`Declined` only when every module was declined, and
  at least one on its platform floor; `RequirementUnmet` when every decline was an unmet `requires` —
  a loaded dependency below the range, whose remedy is loading that dependency, not a roll) and
  never the whole-Space `Held` the gate used to write. `LastSyncNote` names each declined module.
- **The activity**: one keyed Warning line per kind of decline, rendered in the viewer's language —
  `activity.gitsync.modulesDeclined` for a platform floor, `activity.gitsync.modulesRequirementUnmet`
  for an unmet requirement.
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
