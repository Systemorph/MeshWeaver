---
nodeType: Markdown
name: Adopt Then Sync, Per NodeType
category: Architecture
description: >-
  Why landing a Space on the sealed commit is not enough — the seal is a REPOSITORY fact and
  adoption is a per-NodeType one — and the design that holds one type's sources while the rest of
  the Space imports; how a held type is decided, what a partially-held Space means for
  lastSyncCommitSha, and what releases it.
icon: /static/NodeTypeIcons/box.svg
---

# Adopt Then Sync, Per NodeType

[The Sync-Ref Contract](../SyncRefContract) gets a Space onto the commit this instance's bundles were
baked from: every import of a repository whose publication is sealed for the running framework
identity lands on that publication's `source-commit.txt`, whoever asked. That closes the *tree*
question — MeshWeaver#3845 holes 2 and 3, and the webhook lane.

It does not close the *type* question, and this page is about the difference.

## The seal is a REPOSITORY fact; adoption is a per-NodeType one

`SealedPublicationIndex` answers, per bake source (`plugins`, `education`, …):

```
SealedSource(Source, Repository, SourceCommit, IsSealed, Refusal)
```

`IsSealed` means *the completion sentinel is present and every bundle it lists is on disk*. That is a
statement about a DIRECTORY. What `PrebuiltAssemblySeeder` adopts, and what
`NodeTypeCompilationHelpers` judges, is a per-type pair: one bundle entry's
`SourceFingerprint` against the NodeType's own `CurrentSourceFingerprint`
(see [Node Type Compilation](../NodeTypeCompilation)). The two are not the same question, and the
seal cannot answer the second:

- **A publication sealed at the right commit can lack the bundle a type needs.** Measured on #3461
  (2026-09-13): the two producers of `prebuilt-bundles/<identity>/plugins` compose DIFFERENT module
  sets for one identity — core CD 4 modules / 45 files, the MeshWeaver.Plugins lane 5 / 46 (it adds
  `MeshWeaver.Graph.Views`). Whichever publishes last is the publication; a type whose bundle only
  the other lane composes is sealed-and-absent.
- **A type's compile input is not confined to the Space.** `shared=@…` queries and `@@` includes pull
  Code nodes from other namespaces, so two honest readers can compute different fingerprints over the
  same commit ([Query Provider Parity](../QueryProviderParity) is the sibling trap one layer down).

So the acceptance criterion of #3845 — *the affected type's `CurrentSourceFingerprint` does not move
until a bundle for the portal's framework identity whose fingerprint matches exists* — is a per-type
gate, and the gate's unit was a sync source at one commit. Hole 4 is that mismatch.

## The rule

> **An import never moves an ADOPTED NodeType's compile input onto a fingerprint no bundle for this
> instance's framework identity carries.** That type's source set is HELD — neither written nor
> pruned — and the rest of the Space imports.

Held is not failed and not refused: the bytes keep serving, the sources keep matching them, and the
type stays `AdoptedVerified` rather than passing through `StaleAdopted`. The Space is left holding
the commit it genuinely holds, which is the older one — see *What a partially-held Space is*, below.

## The decision, per NodeType

Pure (`BundleKeyedHold.Decide`), over four inputs: the INCOMING tree's nodes, the CURRENT partition's
nodes, the live `NodeTypeDefinition`, and the bundle inventory for this identity.

| # | the reading | the answer |
|---|---|---|
| 1 | the type does not exist on this mesh yet | **proceed** — nothing is adopted, so nothing can be held in step |
| 2 | `BuildProvenance` is not `AdoptedVerified` | **proceed** — `Compiled` has no bytes to keep in step; `StaleAdopted` / `AdoptionRefused` are already behind, and holding protects nothing; `AdoptedUnverified` was never compared, so there is no verified state to preserve |
| 3 | the fingerprint computed over the CURRENT nodes ≠ the live `CurrentSourceFingerprint` | **proceed**, and say so: this import cannot reproduce the live fold (a compile input reaching outside the Space, a lagging snapshot), so it cannot judge a move either. A calibration that fails is never a licence to hold |
| 4 | the fingerprint over the INCOMING nodes == the one over the CURRENT nodes | **proceed** — the type's input does not move |
| 5 | a bundle entry for this type under this identity records the INCOMING fingerprint | **proceed** — the release the import already performs (`ReleaseAffectedNodeTypes` → `SeedForTypes`) adopts it, so the type goes `AdoptedVerified` → `AdoptedVerified` |
| 6 | otherwise | **HOLD** the type, naming the fingerprint it waits for |

Step 3 is the calibration that makes the rest honest, and it is deliberately self-checking: rather
than introspecting a type's queries to guess whether they reach outside the Space, the gate
RE-COMPUTES the fingerprint it can already compare against. Equal ⇒ the incoming computation is
trustworthy; different ⇒ abstain. A gate that cannot reproduce today's answer must not act on
tomorrow's.

The fingerprints are the SAME function the bake and the owner use — `NodeSet.ResolveSources` +
`NodeTypeSourceFingerprint.Compute` over the resolved set and the `@@`-include closure — because a
second implementation of "which files count" would make a shape difference look like staleness
(#2813's whole point).

### What "the type's source set" means, and why holding it closes over sharing

A held type's HELD PATHS are its own node, plus the union of its current and incoming resolved source
paths and include closures. Two consequences:

- **The type node is held with its sources.** Its `configuration` lambda is compile input that the
  fingerprint does not cover, so importing a new configuration over held sources would compile a
  combination neither the bake nor this mesh has ever seen.
- **Sharing closes over the hold.** A source node shared by a held type and an unheld one is held, so
  the unheld type's input cannot move either — and it is held too, transitively to a fixed point,
  with the reason naming the type it shares with. Holding one side only would move the sharer's
  fingerprint onto a fold no bundle carries, which is the very thing this gate exists to prevent.

## What a partially-held Space is — the scope call

Every existing consumer of `GitHubSyncConfig.LastSyncCommitSha` assumes a Space is at ONE commit.
The decision is therefore to keep that true rather than to redefine it:

**`LastSyncCommitSha` keeps meaning "the commit whose content the mesh genuinely holds", so a Space
with held types does NOT advance it.**

That is not a new rule; it is the rule `GitHubSyncService.MayAdvanceBaseline` already applies to a
two-way import that preserved server-newer nodes (#675/#677) and to one whose nodes did not all land
(#2229 item C). A bundle hold is a third member of the same family, and every consumer keeps working
without knowing about it:

| consumer | what it reads | why it stays correct |
|---|---|---|
| the git-diff scope (`GetChangedPaths` base) | the unadvanced baseline | the held files stay IN the next diff, so the import is cumulative and self-healing |
| `GitHubWebhookProcessor.CommitSkipReason` | `LastSyncCommitSha`, `LastAttemptedCommitSha` + `LastAttemptWasFinal` | the attempt pair records the commit, so a re-delivery of it is free; the baseline being behind does not make it re-fetch |
| `SealedSyncReconcile.Decide` | the same pair, plus the held list (below) | a source with held types is re-attempted when — and only when — the inventory can release one |
| `ModuleDiscoveryService.Evaluate` | a config with NO `LastSyncCommitSha` | a first import has no adopted types, so it can never be held; the trigger is untouched |
| the settings tab | the three facts it already shows, plus the held note | "at commit C0" is the honest answer, and the note names what is held |

**And the hold is on the record, not only in a log.** `BundleHeldNodeTypes` names each held type, the
fingerprint it is waiting for, and the framework identity the judgement was made under — the #4063
lesson applied one level down: a hold that exists only as a log line is indistinguishable from a
source that is up to date.

## What releases a hold

Three events, and they are the three the rest of this mechanism already has:

1. **A new commit of the repository.** The baseline never advanced, so the next import's diff still
   carries the held files; if its fingerprint has a bundle, they land.
2. **A publication arriving** (`PublicationSealArrivalService` → `SealedPublicationSyncReconciler`) or
   **the boot seed** (`ShippedPrebuiltBundles.SeedPublishedRoot` → the same reconciler). The
   reconciler re-attempts a source with held types when the inventory now carries a wanted
   fingerprint — asked BEFORE re-fetching, so an unrelated publication costs nothing.
3. **A roll.** A held entry records the identity it was judged under; on an instance running a
   different identity the judgement is void, so the source is re-attempted and re-judged against the
   new identity's inventory.

🚨 **The residual is the same one every lane here has**: an instance that receives no build webhooks
and no publication announcements reduces all three to its next start. That is
[#4063](https://github.com/Systemorph/MeshWeaver/issues/4063)'s shape, stated rather than claimed
away.

## What this does NOT do

- **It does not hold a type that was never adopted.** A `Compiled` type's sources move as they always
  did and the type recompiles; a mesh that cannot compile has nothing to fall back to either way.
- **It does not repair drift that already happened.** A type already at `StaleAdopted` is behind by
  construction; the gate keeps a type from LEAVING `AdoptedVerified`, it does not bring one back.
- **It does not judge a type whose compile input it cannot reproduce** (step 3). Such a type imports
  as before and may go `StaleAdopted` — the honest, serving, announced intermediate state #3583
  describes — and the reason is logged with the type's name, never silently.
- **It is not a second adoption gate.** The bytes are still judged where they always were, by the
  owner, against the node's own fingerprint. This only decides whether the SOURCES may move.

## How to check it is still true

- `test/MeshWeaver.Hosting.Test/ANodeTypesSourcesWaitForItsBundleTest.cs` — the real-mesh acceptance:
  a type adopted at the sealed commit, an import that would move it with no matching bundle (its
  source text and `CurrentSourceFingerprint` do not move, the rest of the Space does, the config
  keeps the older commit and names the held type), then a publication carrying the wanted fingerprint
  and the same import landing it.
- `test/MeshWeaver.Documentation.Test/BundleKeyedHoldTest.cs` — the six rows above as data, both
  directions.
- On a live portal: `get @{space}/_GitSync` shows `bundleHeldNodeTypes` beside the older
  `lastSyncCommitSha`; the import's activity names each held type and the fingerprint it waits for.

## Related

- [The Sync-Ref Contract](../SyncRefContract) — which COMMIT an import lands on, and why a person's
  Update is gated too.
- [Bundle Delivery Stages](../BundleDeliveryStages) — the four stages; this page is stage ④'s
  per-type half.
- [Node Type Compilation](../NodeTypeCompilation) — what `AdoptedVerified` / `StaleAdopted` mean and
  who writes them.
- [Sealed Publication Reads](../SealedPublicationReads) · [Sealed Publication Generations](../SealedPublicationGenerations)
  — how a publication is read while it is being replaced, and why every read resolves `_current`.
