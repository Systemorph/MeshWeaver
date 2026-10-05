---
nodeType: Markdown
name: Sources Sync on Push
category: Architecture
description: >-
  A GitSync'd Space takes its sources on every push to the configured branch, at the pushed commit,
  without waiting for a green build — each module judged alone by its manifest hash, an incompatible
  module declined alone. A periodic branch reconcile repairs a lost webhook. An import prunes only
  the nodes the repository put there, never runtime state. Why a red branch froze the fleet, why a
  broken commit is safe to take, and what risk remains.
icon: /static/NodeTypeIcons/box.svg
---

# Sources Sync on Push

Two policies, both registered in [Policy Not Prose](../PolicyNotProse):

> **`sources-sync-on-push`** — An instance takes a repository's sources on every **push** to a sync
> source's configured branch, at the pushed commit. It never waits for a green build, sealed or
> otherwise. Each module is judged alone by `ModuleSyncDecision.Decide`: unchanged writes nothing,
> changed syncs, a declared platform floor above the running platform declines that one module with
> both versions named. A periodic branch reconcile repairs a lost delivery. A red build holds
> nothing.

> **`prune-requires-provenance`** — An import prunes a node only when the repository **put it
> there**, meaning the node is in the partition's prior import manifest. A node created in a synced
> partition at runtime is never pruned by a source sync. Unknown provenance is never prunable.

They apply [Module Sync Per Manifest Hash](../ModuleSyncPerManifestHash) to the trigger and to the
prune. That policy already said an instance always syncs every module it has. The trigger and the
prune each brought back, through a side route, a hold or a deletion the policy had ruled out.

## What happened (control instance, 2026-10-04/05)

| Time (UTC) | Fact |
|---|---|
| 10-04 14:39 | MeshWeaver.Plugins `main`: `Plugin Catalog CI` red, and red on every push after |
| 10-04 14:56 | `AI/_GitSync` and `Hosting/_GitSync` last attempted |
| 10-04 15:37, 10-05 04:55 | `main` moved; no import, because no green build arrived |
| 10-05 05:29 | a manual `update` imported at once: AI `178ff85c → b5c88490`, Hosting 39 nodes, 18 NodeTypes recompiled, **0 compile errors** |

For 15 h every instance ran the stale AI 1.20.4 beside a Hosting module that called a member AI
1.20.4 did not have. The PR reviewer died fleet-wide.

The cause was `GitHubWebhookProcessor`. It imported only on a **green build** delivery
(`workflow_run`, conclusion `success`) and ignored `push`. One red `Plugin Catalog CI` therefore
held every module of every Space of the repository, for as long as the branch stayed red. That is a
whole-repository hold, the shape `module-sync-per-manifest-hash` exists to forbid.

The same manual import also **pruned 16 nodes** "absent from the repo", among them
`Hosting/Babysitter` (the PR babysitter's live state, nodeType `Hosting/PrWatch`) and every
`Hosting/Queues/*` entry. The partition ran the default `FullReplace`, which pruned every node not
in the repository. The repository could never have carried state the portal wrote at runtime.

## The trigger: push, at the pushed commit

| Delivery | Before | Now |
|---|---|---|
| `push` to a sync source's configured branch | logged, imported nothing | imports **at the push's `after` sha** (`UpdateToPushedCommitFromGitHub`), for every source of that repository and branch not already on it |
| `workflow_run` green, content CI, default branch | recorded `BuildCompletion` **and** imported at `head_sha` | records `BuildCompletion` only (the plugin catalog's fact) |
| `workflow_run` red or cancelled | nothing | nothing; it never held anything, and now nothing waits on it |
| periodic branch reconcile | did not exist | every `GitSync:BranchReconcileMinutes` (default 10) |

The rest of the import is unchanged:

- **The commit is named, never resolved at fetch time.** The [Sync-Ref Contract](../SyncRefContract)
  still binds: a machine trigger imports the sha it names. A push names `after`. The reconcile names
  the head it resolved with one ref lookup (`GitHubSyncService.GetBranchHead`).
- **The per-source skips are the same ones.** "Already at this commit" makes a redelivery free. "A
  final verdict at this commit" stops a non-converging source re-cloning on every delivery (#3945).
- **The per-module judgement is the same one.** It runs inside the import, after the fetch. A module
  whose `index.json` declares `content.minMeshVersion` above the running platform is **Declined**
  alone, its reason names both versions, and its siblings sync.
- **The import is diff-scoped** from the source's last commit, so a push that touched nothing under a
  source's subdirectory imports as a cheap no-op. The source still records the new commit, which keeps
  the reconcile from fetching it again.

### Why the green-build import was retired rather than kept beside the push

It added nothing, and it could do harm. Adoption of prebuilt bytes does not depend on which trigger
imported the sources. A NodeType adopts a bundle by fingerprint whenever one is on the shelf, and a
publication arriving later is handled by the seal-arrival reconcile (`PublicationSealArrivalService`).

What it could do is move a Space **backwards**. Builds finish out of order. A green build of commit
A, finishing after a push of B had already landed, would import A over B. The Space would then stay
on A if B's build went red.

### The lost-delivery reconcile

GitHub does not redeliver a failed webhook by itself. A push whose delivery never reached the
instance (a pod restarting, an ingress fault, a briefly misconfigured hook) would otherwise wait for
the next push. `GitSyncBranchReconcileService` handles that case:

1. It reads every sync config.
2. It groups the configs by repository and branch.
3. It resolves each branch head with one ref lookup, as the first config's creator (or the GitHub
   App).
4. It runs the push's selection at that head.

A source already on the head costs the lookup and nothing else. The passes are serialised, and a
failed pass is logged and does not stop the schedule.

This is not a watchdog over a mesh state that "should not happen". A webhook is an at-most-once
delivery from a system the instance does not control. The only way to learn that one was lost is to
ask the source of truth.

## Why a broken commit is safe to take

Taking a commit whose CI is red is safe because the protection is **per NodeType** and **per
module**, where the damage would be. It is not per repository.

- **A type whose new sources do not compile keeps serving its last good build.** The per-NodeType
  compile records `compilationStatus: Error` with its diagnostics. The type's existing release goes
  on serving until a compile succeeds ([NodeType Compilation](../NodeTypeCompilation)).
- **The readiness gate refuses a type that REGRESSED** on a new image (`nodetype_bake`,
  `NodeTypeBakeEntry.WasHealthy`), so a roll does not proceed past a broken compile it caused.
- **A module that needs a newer platform is declined alone** (`ModuleSyncDecision`, policy
  `package-min-mesh-version`). Its siblings still sync.
- **Unchanged modules write nothing.** A red commit that touched one module touches only that
  module's NodeTypes.

### The remaining risk, stated

- **Content that compiles and is wrong lands.** A red CI may be red because a test caught a
  behaviour regression in code that compiles. That code now reaches the instance before anyone fixes
  it. The green-build gate caught this case, and nothing in the import does. The mitigation is
  the repository's own discipline: a red `main` is fixed or reverted, and the next push brings the
  fix within one delivery.
- **Non-code content has no compile step.** A broken Markdown page or a malformed JSON node is
  imported as it is. A refusal that is a verdict about the bytes is recorded and not re-attempted
  at the same commit (#3945).
- **Out-of-order push deliveries can still move a Space backwards.** If GitHub delivers B's push
  before A's, A lands last. The next push or reconcile pass brings the Space to the head, so the
  window is bounded by the reconcile interval. Nothing compares ancestry before importing.
- **Replicas each run the reconcile.** Two replicas can find the same source behind in the same
  minute and both import it. The import holds the Space's root while it writes (#3510), and the
  second import then finds the source already at the commit. This duplication is not prevented, it
  is made cheap.

## The prune: only what the repository put there

`StaticRepoImporter.ComputePrunableNodes` decides what an import deletes. It now requires
**provenance** in every pruning mode. A candidate is pruned only when its path is in the prior import
manifest (`{partition}/_Activity/import-manifest`, the `{path → token}` ledger each import writes of
the source nodes it evaluated).

| Node | `FullReplace` before | Now (`FullReplace` and `Additive`) |
|---|---|---|
| in the prior manifest, absent from the source | pruned | pruned (the repository deleted it) |
| never in any manifest (runtime state, a user's page) | **pruned** | kept |
| no manifest at all (first import, unreadable manifest) | every extra pruned | nothing pruned: unknown provenance |

Every older guard still applies on top: complete listing (#3589), governance, claimed subtree,
mesh-minted `Release/`, SyncIgnore, the two-way "server-newer" protection, and the held NodeType with
instances (#2993).

What this costs: a node the repository deleted, but whose provenance was lost, stays until someone
deletes it. Two cases lose provenance:

- the manifest was lost;
- a diff-scoped run never evaluated the node and no earlier run had recorded it.

The direction is deliberate. A lingering node is visible and recoverable. A pruned runtime node, such
as the babysitter's state or a queue entry, is lost work and has no source to restore it from.

`FullReplace` and `Additive` now prune the same set. Both values remain because they are persisted
and configurable (`Features:StaticRepoSync:Modes:{Partition}`).

## Webhook registration

Register a synced repository's webhook with **Pushes** (required: the sync trigger), **Workflow
runs** (the build record the plugin catalog consumes), and **Issues** / **Issue comments**.

## What this does NOT establish

- **Production behaviour after the roll.** That `Hosting/_GitSync` and `AI/_GitSync` follow pushes
  on the control instance depends on the image carrying this change. It also depends on the hook
  delivering `push` events, which this change does not check.
- **That no existing manifest is missing provenance.** A partition whose manifest is absent or
  partial keeps its stale extras until they are deleted by hand. Nobody has counted how many such
  partitions exist.
- **The 16 nodes already pruned are not restored** by this change.
- **An end-to-end decline of an incompatible module in the test suite.** The test process runs a
  local `-ci.0` build, which the floor comparator treats as advisory. The decline is therefore pinned
  on the pure decision, read from a pushed tree, rather than through a live import.

## How to check it is still true

- `BuildTriggeredSyncPinsTheBuiltCommitTest` covers the trigger, each case with its negative control:
  - a push imports at its `after` sha while the branch's build is red;
  - a green build records and does not import, while a push at the same sha does;
  - a lost delivery is caught by `ReconcileBranches`, and a settled source is not fetched again;
  - an incompatible module is declined while its sibling syncs.
- `StaticRepoImporterSyncModeTest` and `ARuntimeNodeSurvivesAnImportTest` cover the prune. In the
  second, a runtime node survives a `FullReplace` import that prunes the retired source node (the
  positive control).

## Related

- [Module Sync Per Manifest Hash](../ModuleSyncPerManifestHash)
- [The Sync-Ref Contract](../SyncRefContract)
- [Static Repo Import](../StaticRepoImport)
- [Prune Requires a Complete Listing](../PruneRequiresACompleteListing)
