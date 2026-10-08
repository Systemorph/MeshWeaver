---
nodeType: Markdown
name: An Adoption That Changes Nothing Writes Nothing
category: Architecture
description: >-
  Why a prebuilt adoption that would leave the build unchanged must upload nothing and write
  nothing, and why two bundles for one NodeType must converge instead of replacing each other —
  the version churn of MeshWeaver#6038, its measurement, the rule, and how to check it holds.
icon: /static/NodeTypeIcons/box.svg
---

# An Adoption That Changes Nothing Writes Nothing

A prebuilt adoption (`PrebuiltAssemblySeeder.SeedDetailed`) uploads a bundle's bytes under the
NodeType node's **current** version and stamps the record; the owning hub then fulfils the source
stamp. That is **two node versions and a fresh assembly-store generation per adoption**. Before
MeshWeaver#6038 nothing asked whether the adoption changed anything, so every caller that offered
the same bundle again — and every pair of bundles offered for one type — wrote.

## What was measured

On memex.systemorph.com, `Feedback/Feedback` (created 2026-08-25) stood at version **323,396** on
2026-10-08 with an unchanged source fingerprint. Reading its versions back:

| Version | Time (UTC) | Bundle | MVID | Provenance written |
|---|---|---|---|---|
| 323389–90 | 15:11 | module 1.2.0, no fingerprint | `97cc…` | AdoptedUnverified |
| 323391–92 | 15:23 | module 1.2.0 — **the same bytes again** | `97cc…` | AdoptedUnverified |
| 323393–94 | 15:26 | module 1.6.8, fingerprint = live | `1fe1…` | AdoptedVerified |
| 323395–96 | 15:36 | module 1.2.0 | `97cc…` | AdoptedUnverified |

Two defects, one symptom:

1. **Same bytes, same stamp, written again** (15:23). Each re-offer minted a new store key
   (`v323390-…` → `v323392-…`) for identical bytes.
2. **Two bundles alternate.** Each writer's "would my stamp change the record" test is true *by
   construction* while the other writer's stamp is in place, so the verified 1.6.8 build and the
   unverifiable legacy 1.2.0 build replaced each other for as long as both kept being offered —
   downgrading the record from `AdoptedVerified` to `AdoptedUnverified` every time.

## The rule

Every gate passed, the seed now asks `PrebuiltAssemblySeeder.StandingBuildKept` before it uploads.
It writes **nothing** — outcome `SeedOutcome.AlreadyServed`, which every caller counts as covered
(`PrebuiltAssemblySeeder.IsCovered`) — when the standing build is usable here
(`NodeTypeBakeStatus.Classify` = `Baked`, i.e. the store still resolves its bytes) and either:

- the incoming bytes are the **same MVID under the same stamp** (dependency record, module
  version, source fingerprint); or
- the standing build is **verified against the live source** (adopted with a fingerprint equal to
  the live one, or compiled here from exactly the live source versions) and the incoming bundle
  cannot improve on it: it carries no fingerprint or a different one — or, over a standing
  ADOPTED verified build, the same one at a module version that is not strictly newer. Between two
  equally proven adoptions the standing one wins — that is what makes two writers converge. A
  verified bundle of the live source still replaces a *local compile* of it: that is the ordinary
  one-time adoption, and since a compile only follows a source move it cannot alternate.

Anything that is not `Baked` here — bytes missing on this process, a framework or dependency roll,
a failed compile — is replaced exactly as before. A store that cannot answer reads as "no bytes",
so it never keeps a type on bytes nobody can load.

## What this does NOT do

- It does not decide **which** two writers were offering the 1.2.0 and 1.6.8 bundles. Plugins#3005
  (`Signature/RequestSignatureMenu`, 146 versions alternating between two commits' content) has the
  same two-writer shape on the **GitSync import** path, not the adoption path, and is not closed
  by this rule.
- It does not touch the compile path. A locally compiled build already writes once per compile;
  this rule is about adoptions.

## How to check it still holds

- `AnAdoptionThatChangesNothingWritesNothingTest` (MeshWeaver.Compiler.Pipeline.Test) seeds the
  same bytes twice on a real mesh and asserts no new version; its negative control seeds different
  bytes and asserts they are adopted. `StandingBuildKeptTest` pins each branch of the rule.
- Live: `get_version` on a NodeType a few adoptions apart. Consecutive versions alternating
  between two `adoptedModuleVersion` / `latestAssemblyMvid` values, or the same MVID under a new
  `latestAssemblyPath`, is this defect back.

## Related

- [Adopt Then Sync, Per NodeType](../AdoptThenSyncPerNodeType)
- [NodeType Compilation](../NodeTypeCompilation)
- [Stale State Until Recycle](../StaleStateUntilRecycle)
