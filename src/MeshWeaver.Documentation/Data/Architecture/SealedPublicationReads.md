---
nodeType: Markdown
name: Sealed Publication Reads
category: Architecture
description: How a sealed bundle publication is written and read while it is being replaced — the writer's byte-level postcondition that refuses to seal a mix, the three read answers (404, 503, 412), the generation that pins one publication instance, and what is still not closed.
icon: /static/NodeTypeIcons/box.svg
---

# Sealed Publication Reads

A **sealed publication** is one producing repo's baked content for one framework identity, on the
shared storage every portal mounts and the registry serves:

```
<root>/_releases/<platform-version>       → that release's framework identity
<root>/<identity>/<source>/<bundle>.zip   → the NodeType bundles
<root>/<identity>/<source>/modules/…      → the module bundles the bake composed, + _index
<root>/<identity>/<source>/_complete      → the seal, written strictly LAST
```

[CI Content Bake](../CiContentBake) covers what produces it and what boot does with it. This page is
about the other half: **reading one while it is being replaced.** That is not an edge case — it is
the normal state of the `plugins` prefix for a minute and a half at a time, several times an hour
during a release.

## The window is deliberate, and it cannot be closed in this layout

`publish-bake-bundles.sh` replaces a publication in place, and its first act is to **remove the
seal**:

```bash
# Readers must never seed a mid-replace mix of old and new bundles under a stale sentinel
az storage file delete ... --path "$dest/_complete"
... upload bundles, modules, markers ...
# LAST write — the atomic completeness marker.
az storage file upload ... --source "$SENTINEL_LOCAL"
```

Unsealing first is correct: it is what makes "sealed" mean "every listed bundle is here", so a
reader can never be handed a mix of old and new bundles under a stale sentinel. The cost is a window
in which the directory is deliberately unreadable, and it is **per target** — `BAKE_PUBLISH_TARGETS`
names several shares and the publisher walks them in order, so each gets its own.

Measured on 2026-09-06 (MeshWeaver.Plugins run 34029118937, one publish, two targets):

```
11:56:42  → target 1: prebuilt-bundles/sa55f8190…/plugins (36 bundle(s), 4 module(s))
11:58:11  sealed: target 1/_complete (36 bundle(s))
11:58:18  → target 2: prebuilt-bundles/sa55f8190…/plugins        ← unsealed here
11:58:24  a satellite gate's read lands, 6 seconds in            ← MeshWeaver#3401
11:59:47  sealed: target 2/_complete (36 bundle(s))
```

🚨 **And the prefix has SEVERAL writers.** Core CD's `plugins-bake` job and the MeshWeaver.Plugins
satellite's own `publish-bake` both publish `bake-source: plugins`, so both write
`prebuilt-bundles/<identity>/plugins/`. Attributing a window to "core CD is rolling" is therefore
wrong roughly half the time — the 2026-09-06 red was the satellite's own bake, while two core CD
runs were in flight publishing a *different* identity. And the more frequent overlap is not between
the lanes at all — see "How many writers, measured" below.

## Three answers where there used to be one

`PublishedBundleCatalogue.SealedPublicationOf` reads the seal and returns either the listing plus a
**generation**, or a reason. The prebuilt routes turn that into three distinct statuses, and the
distinction is the whole point — before it, all three were `404`:

| status | meaning | what a consumer does |
|---|---|---|
| `404` | the seal does not list this name | refuse — a permanent mistake |
| `503` + `Retry-After` | the publication exists and is **being replaced right now** | wait it out; it is self-healing |
| `412` | the caller pinned a generation and the publication has since moved | **re-read the publication that now applies** |

🚨 **A `404` for a name the index just listed used to be the *only* signal for all of this, and it
named the wrong thing.** The route re-evaluates the seal on every request, so a `404` on
`Export.zip` was equally consistent with *some other* bundle having gone absent a moment earlier —
the name in the error was where the fetch loop happened to be, not what broke. That is what cost the
original #3401 investigation its first two rounds.

## The generation: one publication INSTANCE

A consumer does **N+1 reads** — the index, then each bundle it names — of a directory that can be
resealed underneath it. Statuses alone cannot express that: each individual read is served from a
complete, self-consistent seal, and only the *set* is torn.

So the index carries a `generation` (also the `ETag`), and a bundle fetch pins it with `If-Match`:

```
GET …/prebuilt/<identity>/plugins            → {"bundles":[…], "generation":"a1b2c3d4"}
GET …/prebuilt/<identity>/plugins/Export.zip    If-Match: a1b2c3d4
```

The token folds the seal's **listing** together with the **instant the seal was written**, so a
reseal to an identical bundle list is still a different generation — which it is, because the bytes
behind those names may have changed. It is **opt-in**: a registry that predates it publishes no
generation, the consumer then sends no `If-Match`, and behaviour is unchanged. That is what keeps a
satellite on an older portal working.

## The consumer contract

Every consumer of a sealed publication obeys the same four rules.

1. **Read the index, then pin what it said.** Every bundle fetch carries the generation its listing
   came from.
2. **On `503`, wait** — the existing `15 30 60 90` backoff. This is not a retry papering over a
   fault: `503 + Retry-After` is the server saying "this exists and is being replaced", and waiting
   is what that status means.
3. **On `412`, read the publication that NOW applies** — re-read the index and start the fetch over,
   at most `RESTARTS` (2) times, then go **RED naming #3401**. The bound is what keeps this a read
   rather than a poll: a publisher resealing faster than a read completes is a real condition, and
   it must be reported, not absorbed.
4. **Discard the half you hold.** A bundle from the superseded publication is deleted before the
   re-read. Without this, a name the rejected seal listed and the new one does not would linger in
   the seed — a bundle from a publication the consumer refused, which is exactly the mix the
   precondition exists to prevent.

🚨 **Rules 3 and 4 are a pair.** Detecting the move and then giving up trades a *silent* mix for a
*guaranteed red* on every overlapping publish — a satellite's pin-move PR going red for a defect
that is not in it. Detecting the move and continuing on the half already fetched is worse still.

### The three consumers

| consumer | reads | lives in |
|---|---|---|
| `node-repo-gate.yml`, the upstream seed | the bundle index + each bundle | this repo, pinned by each satellite's `uses:` |
| `.github/scripts/compose-sealed-modules.sh` | the module-set index + each module | this repo, fetched at each satellite's `platform-ref` |
| `memex build plugin` (`BuildPluginCommand`) | both | `src/MeshWeaver.Cli` |

The `registry_get` backoff is **duplicated on purpose** between the workflow and the script: the
workflow is pinned by the caller's `uses:` while the script is fetched at `platform-ref`, so a
shared file would be a third independently-pinned artefact — the skew that once let an old workflow
drive a new script and seal an empty module set. **Change one, change both.**

### Why the module lane matters most

A mixed *bundle* seed usually fails loudly at compile. A mixed **module set** does not: module bytes
from two publications are the mvid mismatch that makes the boot seeder DECLINE every NodeType
assembly built against them —

```
dependency record mismatch — built against mvid:…, live is mvid:…
```

— which is invisible in CI and surfaces only when a portal boots and renders nothing. That is the
failure `assert-bake-consumption.sh` exists to catch, and it is why the module lane is pinned even
though its window is the same size as the bundle lane's.

## How many writers, measured

The issue that opened this ([#3461](https://github.com/Systemorph/MeshWeaver/issues/3461)) names two
lanes and asks which should own the prefix. Measuring first changed the answer, so the numbers are
here rather than in a comment. **2026-09-06, one day: 25 core-CD publish jobs and 19 satellite ones
examined; every job log fetched, none expired.**

| | core CD `plugins-bake` | satellite `publish-bake` |
|---|---|---|
| distinct framework identities written | 20 | 2 |
| identities written by BOTH lanes | 2 | 2 |
| closest cross-lane approach on one identity | \-- | 24 min |
| strict cross-lane time overlaps | 0 | 0 |
| **same-lane time overlaps on one identity** | \-- | **4** |

Both shared identities were the satellite unsealing core's publication and republishing it from a
different source sha (`sf09fa2c9…`: `3e81b03960` → `9717e13e49`; `sa55f8190…`: `bbb893d673` →
`88f1d44a19`). They collide because the identity is **breaking-change-keyed**: four consecutive
image pairs in that same day resolved the *same* `s<hash>`, which is exactly what lets a frozen
satellite pin land on an identity core CD is still publishing to.

🚨 **Two conclusions, and the second is the one that decides the design.**

1. **Neither lane can be removed.** Core CD wrote 20 identities, 18 of them reachable by no other
   producer — a surface change mints a new identity that the satellite, pinned to an older core,
   cannot publish to until its pin moves. The satellite wrote the only fresh *content* for its own
   identity, and core CD batches, so dropping it would make plugin content delivery wait on core
   merges. "Give the prefix a single owner" costs real coverage in both directions.
2. **The single owner would race itself anyway.** The only overlaps actually measured were
   *within* one lane — three concurrent bakes on `sa55f8190…` at 08:03Z, two of which sealed 24
   minutes apart, plus three more pairs the same day — against zero cross-lane ones. A rule about
   which *repository* owns the prefix does not address that at all.

So the fix is publisher-agnostic: it asks *"are the bytes on the shelf the ones I uploaded"*, never
*"which repo is the other one"*.

## The writer's postcondition

`publish-bake-bundles.sh` stamps every file it uploads with two metadata values —

```
digest       the SHA-256 of the exact local bytes uploaded
publication  a token unique to this run (repository + run id + attempt), which also NAMES it
```

— and reads all of them back immediately before writing `_complete`. Each file lands in one of three
buckets, and the buckets are the verdict:

| bucket | meaning |
|---|---|
| **ours** | digest matches *and* our token wrote it — only this run could have put those bytes there |
| **neutral** | digest matches, another publication wrote it. Compatible with both; distinguishes nothing |
| **foreign** | digest differs, or there is none. Somebody else's bytes |

- **foreign = 0** → seal. The directory is byte-for-byte this publication.
- **ours = 0, foreign > 0** → *superseded*. Nothing distinguishing survived; the shelf is another
  publication, whole. **Leave its seal alone** and go red — a run reporting "published" having
  shipped nothing is the silent-nothing outcome this script exists to prevent.
- **ours > 0, foreign > 0** → a **mix**. Refuse, and delete any sentinel over it, because a
  publisher can finish and seal inside a gap in our uploads: refusing to write *our* sentinel is not
  enough when the one already there covers a directory we have since partly overwritten.

🚨 **The neutral bucket is not a nicety.** `architecture.txt` is `linux-x64` in every bake,
`modules/_index` is the same list whenever the module set is unchanged, and a bundle a narrowed bake
did not touch is byte-identical across two source shas. Counting those as *ours* turns a clean
supersession into a false "mix" — measured while building this, `architecture.txt` alone made
`ours = 1` and the core lane deleted the satellite's perfectly good seal.

🚨 **A claim token cannot do this job**, and it is the obvious thing to reach for. "Stamp a marker
before unsealing, re-read it before sealing" is check-then-act on one mutable cell with the wrong
asymmetry: whoever stamps *last* re-reads its own marker and seals happily over the other's bytes.
The loser detects the winner; the winner detects nothing. No arrangement of a single marker fixes
that, because a marker records who wrote last, not whose bytes are on the shelf.

The same two stamps close the carry-forward (below): `carry-forward-bundles.sh` verifies each
bundle it downloads against the digest the publication records, and refuses when the carried set
names more than one `publication` — which is what "the publication was replaced while I was reading
it" looks like from inside a narrowed bake.

## What is NOT closed

Stated plainly, because a page that only lists what works is how the next session repeats this.

- 🚨 **The postcondition is a postcondition, not mutual exclusion.** It leaves one window: between
  the last verification read and the `_complete` upload. A publisher that overwrites a file inside
  that single-upload window still lands under this run's seal. That shrinks the exposure from the
  whole ~90-second publication to one file upload, and it is why the *layout* question stays open —
  a generation directory plus an atomic pointer swap removes republication in place entirely, and
  is the shape the read side already pins.

  **That layout is now designed and its reader half is landed**:
  [Sealed Publication Generations](../SealedPublicationGenerations) carries the directory shape, the
  resolution rules, the retention rule, and — the part that decides the order — why a *new* writer
  and an *old* writer on one prefix is the half-migration to avoid: the pointer-following reader
  would keep serving its generation and never see the flat writer's newer publication, a stale serve
  with nothing red anywhere. Phase 1 (every reader tolerates a pointer) is in; the writer is not, so
  **everything on this page still describes what is live**.
- **A refused publication leaves the prefix unsealed**, which every consumer skips — correct, and
  it means an overlap now costs a red lane and a re-run rather than a portal that renders nothing.
  It is not free: the identity serves nothing until either publisher runs again.
- 🚨 **Expect the reds, and do not read them as noise.** The four same-lane overlaps measured on
  2026-09-06 were, every one of them, a mix being sealed in silence. Under the postcondition each
  becomes a red publish job in the repo that lost the race. That is four reds a day the fleet did
  not have before, and it is the correct number: the alternative is four sealed mixes a day, which
  is what the fleet actually had. Whoever is holding the loser's re-run should not "fix" it by
  loosening the check.
- **The window itself remains.** In this layout it cannot be removed — in-place replacement means
  unsealed time, and the alternative is a layout migration every reader must land first (the portal
  boot seeder, the gate's Azure-direct path, and every pinned satellite workflow copy).
- **The Azure-direct read path carries no generation.** `az storage file download-batch` against the
  share has no server to ask, so `--storage-target` consumers still do unpinned N+1 reads. The lanes
  all pass `--registry-url`; the storage path is the OIDC fallback.

## Verification

Both harnesses provoke the interleave deterministically rather than racing it, and both fail when
the pin is removed:

- `test/Memex.Portal.Shared.Test/SealedModuleCompositionTest.cs` — the REAL registry endpoints in a
  TestServer, the REAL `memex build plugin` composition against them, and a publisher that reseals
  between the first module fetch and the second. Unpinned, it composes `MeshWeaver.AI` from
  generation 1 and `MeshWeaver.Essentials` from generation 2 and says nothing.
- `.github/scripts/test-sealed-module-compose.py` — runs the REAL `compose-sealed-modules.sh`
  against a stub registry that republishes mid-composition. Four cases: settled, resealed once,
  reseal storm, and a registry publishing no generation at all. It asserts `If-Match` was actually
  sent, because a script that stopped pinning would still compose two files and pass on the outputs
  alone.
- `.github/scripts/test-publish-bake-overlap.py` — runs the REAL `publish-bake-bundles.sh` **twice,
  interleaved**, against a stub share, and reads the verdict off the BYTES rather than off the
  script's own log: each fixture bundle names the bake that produced it, so "the sealed directory
  holds two bakes" is a fact about the shelf. Twenty-one assertions over eight cases — three
  controls that must PASS (settled publish, republish of new content, already-published skip), the
  other lane in flight, the other lane completing inside a gap, a full supersession, an unreadable
  read-back, an unstamped incumbent, and two concurrent runs of ONE repo. `--expect-defect` runs
  the same cases against the pre-fix script and asserts the opposite; on `main` before the fix,
  every overlap case sealed a mix.
- `carry-forward-bundles.sh --self-test` — eleven cases, now executed by CI for the first time
  (this script runs only inside a node repo's publish lane, so the first execution of an edit used
  to be a production publish). It covers the shrink refusals it has always owed plus the
  one-publication postcondition, the digest mismatch, the fail-closed unreadable stamp, and the
  adoption path where an incumbent predates stamping and the check reports — with numbers — that it
  proved nothing.

Related: [CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch) · [Module Build Architecture](../ModuleBuildArchitecture)
