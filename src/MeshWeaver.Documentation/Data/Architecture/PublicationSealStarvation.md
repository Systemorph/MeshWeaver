---
Name: When a Publication Seal Stops Advancing
Category: Architecture
Description: A GitSynced Space converges on a green build only while the publication sealed for THIS instance's framework identity keeps reaching the built commit. When the instance's identity and the lane that publishes for it drift apart, that condition stops being satisfiable — every green build is held, forever, and until now the held source was byte-identical to a settled one. The measurement from both production portals, the branch-by-branch elimination that located it, what the node says now, and what is still missing for "published" to imply "live".
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="11" width="16" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0"/><path d="M12 15v2"/></svg>
---

# When a Publication Seal Stops Advancing

[GitHub Sync](/Doc/Architecture/GitHubSync) keeps a Space current without polling, and
[The Sync-Ref Contract](/Doc/Architecture/SyncRefContract) says which commit an unattended import may
read. This page is about the gate that sits between them, and about the state it can enter from
which nothing ever releases it.

The short version:

> A green build authorizes an import. For a **module-bearing** repository it does not perform one:
> the source advances only to a commit the registry has **sealed for this instance's own framework
> identity**. That identity is a property of the image the instance is RUNNING; the seal is written
> by a lane that bakes against the image its pin RESOLVES. While those are the same image the gate
> is invisible. When they diverge, the gate's release condition becomes **permanently unsatisfiable**
> — and the only thing that then converges a Space is a pod restart, which re-reads the same stale
> seal and imports nothing new.

## 1. The gate, and why it exists

`SealedSyncGate.Decide` (`src/MeshWeaver.GitSync/SealedSyncGate.cs`) answers one question per sync
source: *may this source import the green build at `headSha`?*

- No sealed publication is attributable to the repository → **Go**. The instance runs no publication
  of it (a course repo, a content repo), so the gate does not apply.
- Some publication of that repository, sealed under **this instance's framework identity**, is at
  `headSha` → **Go**. The seal and the build agree on the tree.
- Otherwise → **Hold**, with the reason stated.

It exists because the alternative was measured. On 2026-09-06 a MeshWeaver.Plugins `main` run
finished after `main` had moved past the Payments split, both production portals imported the new
`Store/*` sources against a platform carrying neither `IPaymentProvider` nor the Payments module,
and four `Store` NodeTypes sat in compile `Error` for about five hours. A green build proves a tree
compiles *somewhere*; the seal proves it compiles **here**.

The gate is right. What follows is about what happens when its release condition can no longer
occur.

## 2. The identity the gate keys on is the instance's, and it drifts

`SealedPublicationIndex.ReadFor(publishedRoot, identity)` reads
`<PreWarm:PrebuiltBundleRoot>/<identity>/<source>/` and nothing else. `identity` is
`PrebuiltAssemblySeeder.LiveFrameworkMvid` — the API-surface identity of the assemblies **this
process is running**, the same value `/health`'s bake report prints as `framework=…`.

The seal on the other side of that path is written by `publish-bake-bundles.sh` under the identity
of the platform image the publishing lane resolved. Two different clocks:

| | what fixes it | when it moves |
|---|---|---|
| the instance's identity | the image the portal was last **rolled** to | on a deploy |
| the seal's identity | the platform image the repo's bake **resolves** | when the platform release it tracks moves |

The identity is breaking-change-keyed, so an ordinary core merge re-resolves the **same** hash and
the two stay equal without anyone arranging it. That is why this is invisible almost all the time.
The moment core's public surface changes and the portal has not been rolled onto the new image, they
separate — and from then on every publication of every module-bearing repository lands in a
directory the portal never reads.

`mine` is then non-empty (the portal's own identity still holds the *last* publication made for it)
and nothing in it is ever at `headSha` again. **Hold, on every green build, for ever.**

## 3. Measured — both production portals, 2026-09-11/12

- Last automatic convergence: `24c2d024` (MeshWeaver.Plugins #1663), imported 18:53:55Z on
  memex.systemorph.com and 18:55:16Z on memex.meshweaver.cloud.
- Green MeshWeaver.Plugins `main` builds after it: 20:17, 21:55, 23:58, 23:59, 00:53, 02:29, 03:39,
  03:44Z. **None** produced an import on either portal. `Hosting/v1.17.1` and `v1.18.0` were tagged
  by the 23:59Z run; `DeepSign/v1.1.0` by the 03:44Z run. Neither reached a mesh until a person ran
  `git_hub_sync update` by hand at 03:47Z.
- Both portals: `/api/version` = core `74d4c852`, `/health` = `framework=s6649734…`.
- `Hosting/PlatformBuilds/plugins`, written by the publishing lane at 03:44:43Z:
  `identity: s8e1f76cc22bcd7051a7ad998debbfeb3`, `version: 3.0.0-ci.8392`.

Two identities, one letter apart in nothing. The portals had not been rolled onto the image the
bake resolves, so their `plugins` seal froze at `24c2d024` and the gate held every commit after it.

### The roll is both the last convergence and the start of the freeze

Core `74d4c852` is *"Merge pull request #4044 from Systemorph/feat/per-module-deploy"*, committed
17:05:45Z on 2026-09-11 — and it is the image both portals are running, 50 core commits behind
`main` by the next morning. The 18:53:55Z / 18:55:16Z imports were that roll's own boot sweep:
`SeedPublishedRoot` → `SealedPublicationSyncReconciler` → every source brought onto the commit
sealed for the freshly-booted identity, `24c2d024`, which the 18:27:53Z green build had just sealed.

That is the whole shape in one sentence: **the roll converged every Space, and then core's surface
moved while the portals did not.** The next Plugins bake resolved a newer platform image, sealed
under `s8e1f76cc…`, and the first green build the gate held was `7660ca73` at 21:55:18Z. Everything
after it — `222853d4` (which tagged `Hosting/v1.17.1` and `v1.18.0`), `4b97be19` (which tagged
`DeepSign/v1.1.0`) — was held the same way.

So the break is dated **between 18:27Z and 21:55Z on 2026-09-11**, which is *after* core's
per-module deploy (the portals RUN it) and *before* the Plugins half of it (`MeshWeaver.Plugins#1676`,
merged 02:37Z on 09-12). The per-module publish path is not the cause; the missed 23:59Z `Hosting`
publication already said so, four hours before #1676 existed.

### The branch-by-branch elimination

`MatchingBuildTargets` can drop a candidate for four reasons, and only one of them leaves no trace.
Each of the others had a **positive control** on the live meshes:

| branch | control | reading |
|---|---|---|
| the webhook never arrived / was refused | `Admin/_Build/Systemorph.MeshWeaver.Plugins` rewritten at 03:44:49Z on BOTH portals, 0.5 ms apart | it arrived and was accepted |
| the config query or repo match returned nothing | the issue fan-out wrote `*/_Issue/1678` across 34 Plugins-sourced spaces at 02:07:07Z — the same `ConfigsTargeting` seam | it matches |
| `SkipReason` | the node's own fields: `lastSyncCommitSha` = `lastAttemptedCommitSha` = `24c2d024` ≠ any later `headSha`; branch `main` = `main`; direction not `ExportOnly` | no arm fires |
| `SealedSyncGate` | — | **the only branch left** |

That table is the method, not just the answer: three of the four branches are indistinguishable from
the fourth in the node's recorded state, and each needed an artefact somewhere else to rule it out.

## 4. The defect that made it cost nine hours: a hold looked exactly like "settled"

`GitHubSyncConfig` has fields for all of this, and the boot-time reconciler already used them:
`GitHubSyncService.RecordHold` writes `lastSyncOutcome: "Held"` plus the reason in `lastSyncNote`,
and clears the attempt pair so a hold never leaves a
[#3945 "already attempted, final"](/Doc/Architecture/GitSyncTriggerCost) licence standing.

The green-build path — the one that actually runs, on every delivery — recorded **nothing**. Its
whole trace was one `Warning` per delivery in a log an operator must already suspect a hold to go
looking in. So the node read:

```json
"lastSyncOutcome": "Imported",
"lastSyncCommitSha": "24c2d024…",
"lastAttemptedCommitSha": "24c2d024…",
"lastAttemptWasFinal": true
```

which is, field for field, what a genuinely settled source looks like. `lastAttemptWasFinal` is
documented as the field that separates settled from stuck, and here it said *settled* about a source
that had not been attempted since the previous evening. Two sessions read it that way before the
Space's `_Activity` list — which had no `Update Hosting to the built commit …` entry after
18:55:17Z — gave the hold away.

`GitHubWebhookProcessor.RecordSealHold` now writes the hold where the outcome is, through the same
`RecordHold` the reconciler uses: once per **reason** rather than per delivery (the reason names the
built sha, so a re-run or a `schedule` probe of the same commit writes nothing), best-effort, and
cleared by the next attempt that actually runs.

## 5. How to read a source's state

1. `get @<Space>/_GitSync`. `lastSyncOutcome: "Held"` with a `lastSyncNote` naming a sealed commit is
   the state this page is about — the note says which commit the instance IS sealed at.
2. Compare `/health`'s `framework=…` with the `identity` on `Hosting/PlatformBuilds/<source>`. If they
   differ, the instance is not being published for, and no publication will release the hold.
3. `search 'namespace:<Space>/_Activity scope:descendants sort:lastModified-desc'`. A green-build
   import appears as `Update <Space> to the built commit <sha>`; the boot reconciler's appears as
   `Reconcile <Space> with the sealed commit <sha>`. An empty tail is a Space nothing has touched,
   whatever the config says.
4. `Admin/_Build/<owner>.<repo>` says whether the webhook itself is alive. It is written by the
   delivery, not by CI, so a fresh `lastModified` proves the chain up to the import decision.

**Do not read `lastSyncOutcome: Imported` + `lastAttemptWasFinal: true` as settled without step 3**
on any instance that has not been rolled recently. That pair is what nine hours of undelivered
releases looked like.

## 6. What is still missing

Recording the hold makes the freeze **findable**. It does not make it **converge**, and nothing in
the platform today does:

- The seal-triggered reconciler (`SealedPublicationSyncReconciler`) is the designed answer to "the
  seal landed after the webhook" — but it is wired only into `ShippedPrebuiltBundles.SeedPublishedRoot`,
  which `DynamicTypePreWarmerHostedService` calls **once, at `ApplicationStarted`**. The release
  valve is a process boot.
- A boot re-reads the same identity, so when the identity itself is the stale thing, the boot
  converges nothing either. The only true release is a **roll onto the image the bake resolves**.
- Nothing measures or reports the divergence. Neither portal's health, nor any gate, states "this
  instance's identity has no publication of repository X at or after its last green build".

Tracked as [Systemorph/MeshWeaver#4063](https://github.com/Systemorph/MeshWeaver/issues/4063).
Related: [Module Publication Gate](/Doc/Architecture/ModulePublicationGate),
[Sealed Publication Reads](/Doc/Architecture/SealedPublicationReads),
[Bake Identity Mismatch](/Doc/Architecture/BakeIdentityMismatch),
[What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost).
