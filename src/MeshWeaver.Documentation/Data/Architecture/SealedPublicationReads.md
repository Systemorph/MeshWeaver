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
  publication, whole. **Leave its seal alone.** Whether that is red depends on WHOSE publication it
  is — see "Superseded is not always a failure" below.
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

### Superseded is not always a failure — the convergence verdict

🚨 **The postcondition and the sealed-skip answer the same question and used to disagree,
and the disagreement is what made core CD fail.** The skip at the top of `publish_to_target` says a
sealed directory is already-published when the framework identity *and* the source commit match —
the publication key is **content × framework**, and a different run's bytes for the same key are
explicitly acceptable. The postcondition can only ask *"are these MY bytes"*. So a run that finds the
shelf unsealed, uploads, and is then overwritten by a sibling publishing the **same content** was
told it had shipped nothing, when in fact the publication it was asked to make was on the shelf,
whole and sealed.

**Measured — 2026-09-08, the incident that made it loud.** Core CD runs `34205409381` and
`34206854855` both published `plugins` at source `cfac152ef023bc8e16203511aa60b50f581d3161` for
identity `s057b1e7785fba6c6ba079e9d84a0c00d`. `BAKE_PUBLISH_TARGETS` holds two shares; each run won
one and each went red on the other, with the same verdict: *40 of 45 file(s) were overwritten … the
remaining 5 are byte-identical*. The **40** are bundle zips and module packages — the compile is not
reproducible byte-for-byte. The **5** are `source-commit.txt`, `repository.txt`, `architecture.txt`,
`modules/_index` and `platform-surface.json`, byte-identical *because it is the same content*. Both
shares ended sealed with exactly the right bytes; both CD runs failed; the run produced no sealed set
and the platform pin behind it did not move.

So `ours = 0, foreign > 0` now converges instead of failing, on **positive byte-level proof of all
three** — nothing inferred, nothing waited for:

1. every **non-payload** file is byte-identical to this bake's (foreign markers `0`, *and* the
   neutral-marker count reaches `MARKER_COUNT` — the denominator, so it cannot pass having checked
   nothing);
2. every foreign file names **one** publication, and that publication is stamped — an unstamped file
   can never satisfy it, because `<unstamped>` is not a legal token;
3. `_complete` is on the shelf, its digest is the SHA-256 of **this bake's own listing** (so the
   sealed bundle *set* is ours, name for name — a same-content sibling with a wider set is refused)
   and it carries that same publication token (so the seal belongs to the bytes under it, not to an
   earlier publication a third writer left behind).

Anything less is the superseded **red**, unchanged, with the reason printed beside it. Convergence
never touches the **mix** verdict, never seals, never deletes, and never reports a publication that
is not there — it reports that *somebody else made the exact one this run was asked for*, names
them, and counts it as `targets-converged` rather than `targets-published` so no summary claims a
seal this run did not write.

🚨 **What it does not cover, stated so nobody re-derives it:** a sibling that has **not sealed
yet** when this run's sweep ends. In the incident that was 21 seconds on one target and −0.24
seconds on the other — so of those two reds, one converges and one does not. Closing that gap by
looking again later would be a retry, and by looking more slowly would be a bound; the fix is the
generation layout, where the two runs never share a directory and neither has to lose.

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

  **That layout is now designed, and BOTH its reader half and its writer half are landed**:
  [Sealed Publication Generations](../SealedPublicationGenerations) carries the directory shape, the
  resolution rules, the retention rule, and — the part that decides the order — why a *new* writer
  and an *old* writer on one prefix is the half-migration to avoid: the pointer-following reader
  would keep serving its generation and never see the flat writer's newer publication, a stale serve
  with nothing red anywhere. The writer is behind a per-caller `publication-layout` selector that
  **defaults to `flat`**, so nothing anywhere writes a generation until a caller opts in and
  **everything on this page still describes what is live**. What remains is each producer's pin
  reaching the writer, then flipping — `plugins` in ONE change set, because it is the only prefix
  with two producers.

  🚨 That page also records what an **OCI registry** does and does not close, since the fleet is
  moving plugin bundles into one: content-addressed blobs make a mix unrepresentable, but a **tag**
  is a mutable last-writer-wins reference and simply moves the defect unless the seal names
  **digests** — the rule already in force for images via `MW_IMAGE_DIGEST`.
- **A refused publication leaves the prefix unsealed**, which every consumer skips — correct, and
  it means an overlap now costs a red lane and a re-run rather than a portal that renders nothing.
  It is not free: the identity serves nothing until either publisher runs again.
- 🚨 **Expect the reds, and do not read them as noise.** The four same-lane overlaps measured on
  2026-09-06 were, every one of them, a mix being sealed in silence. Under the postcondition each
  becomes a red publish job in the repo that lost the race. That is four reds a day the fleet did
  not have before, and it is the correct number: the alternative is four sealed mixes a day, which
  is what the fleet actually had. Whoever is holding the loser's re-run should not "fix" it by
  loosening the check.
- **How often it bites, with the denominator it needs — measured 2026-09-08.** 30 core-CD runs
  examined; 3 had not reached the bake job, 16 were cancelled before it and 2 decided not to
  publish, leaving **9 core-CD bake jobs that actually executed**. Counting every publication of
  either lane that had a same-identity run overlapping it in time — 4 core-CD plus 5 satellite
  `publish-bake` — the corrected rate is **2 failures / 9 overlapping publications (22%)**, and both
  failures are the two halves of the single mutual supersession above. 🚨 **All 9 overlaps were
  same-lane; zero were cross-lane**, which reproduces the 2026-09-06 finding on a different day and
  is the second independent measurement saying that "one owner per prefix" addresses none of this.
  The convergence verdict takes that 2 to 1; the layout takes it to 0.
- **The window itself remains.** In this layout it cannot be removed — in-place replacement means
  unsealed time, and the alternative is a layout migration every reader must land first (the portal
  boot seeder, the gate's Azure-direct path, and every pinned satellite workflow copy).
- 🚨 **Two Azure-direct readers still address the PREFIX rather than the publication.**
  `compose-sealed-modules.sh` and `node-repo-gate.yml`'s inline `download-batch` compose their paths
  under `prebuilt-bundles/<identity>/<source>/` directly, so they read the flat copy whatever the
  pointer says. Inert while nothing writes a generation, and correct at phase 4 in the ordinary case
  — but a run whose flat copy is refused leaves them on the previous publication, and phase 5 breaks
  them outright. They are named as phase 3's precondition on
  [Sealed Publication Generations](../SealedPublicationGenerations); the publish lane's own two
  readers (`bake-scope.sh`, `carry-forward-bundles.sh`) already resolve it.
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
  holds two bakes" is a fact about the shelf. **47 assertions over eleven cases** — three controls
  that must PASS (settled publish, republish of new content, already-published skip), the other lane
  in flight, the other lane completing inside a gap, a full supersession, an unreadable read-back, an
  unstamped incumbent, two concurrent runs of ONE repo, and the three convergence arms (a sibling
  publishing the SAME content and sealing it → success; the same sibling **not** sealing → still red;
  the same sibling sealing a **different bundle set** → still red). The convergence fixture pins
  `platform-surface.json` to the platform rather than to the bake, because that is what the incident
  measured — a fixture that made it differ per bake could not reproduce a convergence and the case
  would pass having tested the wrong thing. `--expect-defect` runs the same cases against the pre-fix
  script and asserts the opposite; run against `main`'s script with the ordinary arms, 5 of the 47
  fail, which is the negative control for the convergence verdict.
- `carry-forward-bundles.sh --self-test` — eleven cases, now executed by CI for the first time
  (this script runs only inside a node repo's publish lane, so the first execution of an edit used
  to be a production publish). It covers the shrink refusals it has always owed plus the
  one-publication postcondition, the digest mismatch, the fail-closed unreadable stamp, and the
  adoption path where an incumbent predates stamping and the check reports — with numbers — that it
  proved nothing.

Related: [CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch) · [Module Build Architecture](../ModuleBuildArchitecture)
