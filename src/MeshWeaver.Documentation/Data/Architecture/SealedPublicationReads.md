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

The seal's file open decides whether it is present (#3876). A separate `File.Exists` observation
cannot protect a later read: a publisher can remove the seal between those two operations. The
catalogue's seal readers therefore handle `FileNotFoundException` and `DirectoryNotFoundException`
at the read and report an absent seal. Other I/O failures still surface. An existing source without
a seal gets `503` with `Retry-After`; an absent source directory gets `404`. Tests remove the seal
or its parent at the read operation, after an existence check could have observed it. HTTP tests
also remove it between the module routes' first and second catalogue reads. A structured
`ModuleSetReading.PublicationUnavailable` result preserves the transient response on that second
read, distinct from a sealed publication with no module index. All four routes are exercised.

🚨 **An existence check leading an open is the DEFECT SHAPE, not the `_complete` file.** Fixing the
seal readers left two more `File.Exists`-then-open pairs on the very same routes, each throwing the
same unhandled `FileNotFoundException` from a slightly different frame — which is why the incident
kept recurring after it had twice been "fixed". Both are now closed:

- **The module set's own seal.** `SealedModulesOf` probed `modules/_index` and then read it
  unguarded. That index is written strictly *before* `_complete`, so the removal that takes one
  takes the other. An index absent **at the open** is now discriminated by re-reading the seal, and
  the ordering is what makes that sound: the publisher unseals first and retention unseals before
  removing an identity, so a seal that *still reads* means the publication is intact and simply has
  no module set — it **predates module sealing**, permanent, `404`, republish the source — while a
  seal that has gone too means the publication is **being replaced right now**, which sets
  `PublicationUnavailable` and rides the existing transient mapping. The two answers are opposite,
  and collapsing either into the other is the bug.
- **The serve itself.** `Results.File(path, …)` resolves the path *again* when the result executes,
  after the handler has returned — so the listing that stat'ed the file present is several steps in
  the past, and a file removed in between threw out of result execution, past every handler. The
  open now happens **in the handler**, and the already-open handle is what the response streams:
  on POSIX the bytes stay readable through a descriptor after the directory entry is gone, so a
  publication replaced mid-response is served whole from the generation its `ETag` pins rather than
  merely being reported as torn. The share is opened `FileShare.Delete` so a read in flight never
  blocks the publisher.

Both route their failure into the **same** discrimination the seal readers use — source directory
present ⇒ `503` + `Retry-After`, identity directory gone ⇒ `404` — so there is one mapping, not
three. The probe that decides it is `Directory.Exists`, which is **total**: it returns `false` for
every failure rather than throwing, so the race where the directory is removed between the failed
open and the probe answers `404` (the correct answer for a removed identity) and can never produce
a `500`. In the opposite order — present at the probe, removed after — the caller gets `503` and
its next read gets `404`; the consumer contract is built to re-read, so that converges.

🚨 **The bytes are served under the SEAL's spelling, never the request's.** The name match is
case-insensitive and the share is not, so composing the requested name served `store.zip` out of a
publication that sealed `Store.zip`: an open that fails on Linux for a permanent client mistake,
which would now wear the transient answer and have that caller retry for ever. The listing verified
one exact name present; that is the name the response opens.

🚨 **The BOOT SEEDER reads the same seal, and it is the reader with no status to return.**
`ShippedPrebuiltBundles.CompletePublishedBundlesOf` walks every source under one identity and skips
an unsealed one deliberately — the sweep compiles it instead. It kept the racing `File.Exists`
after the catalogue's readers were fixed, and the consequence there is worse than a wrong status:
the `FileNotFoundException` left the loop and reached `SeedBundles`' outer `Catch`, which abandons
**the whole identity's adoption pass**. One source being replaced during a boot therefore made every
*other* sealed source on that identity recompile as well — a publication window costing far more
than the publication it was in. The seeder now reads through the same operation and skips only the
source whose seal went away, and its warning says WHICH absence it saw: a publication directory
that is present but unsealed (it died before the seal, or is being replaced right now) versus one
that has been removed. Two implementations of one classification is how the divergence happened, so
there is now exactly one — `ShippedPrebuiltBundles.ReadSealLines`, beside the sentinel's own name,
which `PublishedBundleCatalogue` delegates to (`MeshWeaver.PluginCatalog` depends on
`MeshWeaver.Hosting`, so the shared operation can only live on that side).

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

### The four consumers

| consumer | reads | lives in |
|---|---|---|
| `node-repo-gate.yml`, the upstream seed | the bundle index + each bundle | this repo, pinned by each satellite's `uses:` |
| `.github/scripts/compose-sealed-modules.sh` | the module-set index + each module | this repo, fetched at each satellite's `platform-ref` |
| `memex build plugin` (`BuildPluginCommand`) | both | `src/MeshWeaver.Cli` |
| the REGISTRY, on behalf of a consumer one lane ahead of it | one package's bundle, for the CALLER's identity | `memex/Memex.Portal.Shared/Api/PluginBundleEndpoints.cs` |

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

### The fourth consumer: a portal one image ahead of its registry

The first three read the share directly, over a credential of their own. The fourth reads it
**server-side, inside the registry**, on behalf of a portal that has none — and it exists because a
sealed publication is the only thing a registry can hand a consumer whose framework build identity
is not its own.

**Why a registry cannot answer an off-lane caller from its mesh.** Since #1751 the bundle route
resolves an off-lane caller's assemblies through each NodeType's `Release` node, which records, per
`(identity, architecture)`, which assembly-store version holds bytes *proven* built for that lane.
That rule is right and it stays. What it cannot do is FIND bytes:

- a `ReleaseArtifact` is minted in exactly ONE place — a compile, in this mesh, stamping the
  compiling process's own `FrameworkVersion` and `ReleaseArchitecture.Live`
  (`NodeTypeBuildState.TryCreateReleaseNode`). There is no append path and no route by which a
  portal records an artifact for an identity it does not run;
- adoption mints none at all: `PrebuiltAssemblySeeder.Seed` writes `LastCompiledVersion` and never a
  release, which is precisely why the own-lane branch reads `LastCompiledVersion` rather than a
  release;
- so the only artifacts that exist for a lane the registry no longer runs were written by a compile
  **in a pod that has since been replaced** — and an in-portal compile writes
  `collection: "local"`, which `FileSystemAssemblyStore` defines as *"the bytes live in the local
  filesystem cache only; cross-silo readers must recompile"*.

Measured on the fleet registry on 2026-09-11: `Store/Plugin` did hold a release naming the
consumer's identity — `s3e3c8023…`, written 01:11Z — and its artifact was
`collection: "local", contentPath: "Store_Plugin/v13725-s3e3c802-…dll"`. Resolvable, and
unreachable. **Off-lane serving out of the mesh is empty by construction, not by accident.**

**What the registry does have** is the publication the producing repo's bake sealed for that
identity, on the share it already mounts and already serves at
`…/prebuilt/{identity}/{source}/{bundle}`. `SealedLaneBundles` (in `MeshWeaver.PluginCatalog`) is the
lookup that lets the PACKAGE route reach it, so the decision stays inside the per-package grant —
the same reasoning `ServedModuleBytes` records for the module half (#3244): a consumer
fetching the prebuilt route itself would need a whole-source grant, and a whole-source grant
deliberately bypasses plan tiering.

### The stamp the index puts on that answer

The index's top-level `frameworkMvid` says **which lane the bundles listed under it resolve for**. It
used to say something narrower and, since #1751, untrue: *this portal's own bake*. Because
`PluginBundleClient.Adopt` compares that value ONCE and declines the whole index before requesting
any package, the download route's lane-awareness and the module half's sealed read were both
unreachable for exactly the consumer they exist for.

Measured on the fleet's own portals, twice, with a different identity pair each time:

| when | consumer | registry | `bundle_adoption` |
|---|---|---|---|
| 2026-09-09 | memex.systemorph.com `s414bfb2…` | memex.meshweaver.cloud `s72c27af…` | 25 attempts, **0 adopted**, 25 `FrameworkDeclined` |
| 2026-09-11 | memex.systemorph.com `s3e3c802…` | memex.meshweaver.cloud `s01d65c9…` | 25 attempts, **0 adopted**, 25 `FrameworkDeclined` |

Both readings are the whole population of that process's attempts, not a truncation: the ledger's
capacity is 500 and the payload names ten then counts the rest. The second was taken on portals
already running #3946, which fixed a *different* decline (a dependency record's module entry) — so
that fix does not touch this one, and the count did not move.

On 2026-09-11 the registry's share held **561** identity directories, and
`s3e3c80238a740a8cb895a72b0bfb9cd6` — the consumer's own live identity — was among them with
`plugins` sealed at 00:33Z. **The bytes were on the registry's own disk while every consumer
adoption was declined.**

So the rule is now:

1. a caller that states no lane gets this portal's identity, byte for byte as before — which is
   every already-deployed consumer;
2. a caller that states its lane and for which this registry **holds a sealed publication** is told
   THAT lane, and each package is served out of that publication;
3. a caller that states a lane this registry holds nothing for is told **this portal's own
   identity** — so it declines and compiles, exactly as before.

🚨 **Rule 3 is not a leftover, it is the point.** Claiming the caller's lane with nothing sealed for
it would turn one cheap, named `FrameworkDeclined` into N downloads that each resolve nothing — a
bake gap wearing a serving offer's colours. And nothing in any of this relaxes the adoption gate:
the archive states the identity it was sealed for, and `PrebuiltAssemblySeeder.DeclineReason`
(ordinal equality) still decides, first for the archive and then per assembly as it seeds.

Both directions are pinned by `test/Memex.Portal.Shared.Test/PluginBundleSealedLaneTest.cs`, which
asks the same endpoint with a sealed lane and an unsealed one and asserts the two different answers
— a change that served everybody would pass only the first.

### Three refusals the lookup carries, and why each is not optional

Reaching the share from the PACKAGE route turns a value that used to be compared into a value that
selects a directory and a file. Each of the three checks below closes something that the route did
not previously have, and each has a control that reddens when it is removed.

1. **The identity must be a bare name.** It arrives on a query string and is composed under the
   published root, so `?identity=../…` would read outside the lane it names. The route answers
   `400` — the same rule and the same answer the `/prebuilt/{identity}/{source}` segments already
   applied — and `SealedLaneBundles.IsBareName` restates it at the layer that builds the path, so an
   entry point added later inherits the check rather than the hole.
2. **The lookup is scoped to the source the ENTITLEMENT decision resolved.** The grant is a
   `(source, package)` pair and `PackageOriginAnchor` is the authority on which source carries a
   package. A lookup that scanned every source directory and took the first matching file name would
   let a caller granted one source receive another's bytes whenever two sources publish the same
   package id — a grant boundary crossed inside something shaped like a file search. Only a
   genuinely unknown binding (no anchor, no stamped record) widens the scan; a named source that
   matches no directory answers "nothing sealed for you here", and the caller compiles.
3. **The archive's OWN manifest identity is checked before anything is served**, through
   `PrebuiltAssemblySeeder.DeclineReason` — the same function the consumer applies to the same
   bytes. The directory a bundle is filed under is a filing convention; the manifest is the
   producer's claim, and only the claim may be believed. Without this a mislabelled archive would be
   restamped with the requested identity on the way out, and the consumer's gate — seeing a manifest
   that agrees with its own live framework — would adopt bytes baked for another one. That is the
   single outcome this entire lane exists to prevent.

The read itself runs on the filesystem `IIoPool`, not the request thread: the publication is a
mounted share and a bundle is the whole weight of a package, so concurrent boot downloads would
otherwise hold request threads on a slow mount.

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

### Reproduced a third time, on a different day — 2026-09-10

The two conclusions above are load-bearing enough to be worth an independent re-measurement rather
than a citation. **2026-09-09 20:00Z → 2026-09-10 18:09Z, 22 hours: 61 bake jobs that ran to a
terminal conclusion** (33 core CD, 28 satellite), every job log fetched, none expired, **zero
unreadable identities**.

| | core CD `plugins-bake` | satellite `publish-bake` |
|---|---|---|
| executed to `success`/`failure` | 33 | 28 |
| **sealed a publication** | 16 | 20 |
| skipped — already published, content × framework | 2 | 7 |
| published nothing (failed before the publish) | 15 | 1 |

36 distinct sealed publications over **19 distinct prefixes**. And:

- 🚨 **7 of the 19 prefixes were written by BOTH lanes — and in all 7 the two lanes carried
  DIFFERENT source commits.** So the premise of #3461 holds exactly: the framework identity is a
  property of the *platform image's* reference surface, it carries no content commit, and `plugins`
  is a constant, so the two lanes address one directory routinely. When they meet there the
  content × framework sealed-skip **cannot** fire, because the content differs — each lane genuinely
  unseals and overwrites the other's publication.
- 🚨 **Concurrent publications on one prefix: 4. All four SAME-lane. Cross-lane: 0.** That is the
  same answer as 2026-09-06 and 2026-09-08, from a third day and a differently built measurement —
  three independent reproductions. All four carried different content, so all four are cases where
  the #3496 postcondition is the only thing between the two runs and a sealed mix.

The one cross-lane pair that came close is worth writing down because it shows the *mechanism* that
keeps them apart, which is not luck about timing. On identity `s546f29f9…`, core CD run
`34430130924` sealed at **03:27:37.327Z**; satellite run `34430082656` reached its own publish at
**03:30:57Z**, found *"holds a COMPLETE publication of THIS content"* on both shares and skipped.
They were baking the **same** plugins commit (`3f7686da2…`), because core CD resolves the plugins
tip at its gate — so the common cross-lane case is redundant work that the skip absorbs, and the
dangerous case needs the satellite to have moved on, which is the same-lane shape by another road.

**Nothing here changes the design.** A rule about which *repository* owns the prefix would have
addressed **0 of the 4** contentions measured on this day, as it would have addressed 0 of the ones
measured on the previous two. What removes them is the layout — a publication written into its own
directory is disjoint from every other publication whoever wrote it — which is
[Sealed Publication Generations](/Doc/Architecture/SealedPublicationGenerations).

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

### What the postcondition costs — one process per phase, not one per file

Every file is read back, never a sample — a sample is a guard that passes on the files nobody
overwrote. What changed on 2026-09-08 is how that is paid. Until then the script published with one
`az storage file upload` **process per file** and verified with one `az storage file show` process
per file: for the ~46-file publication the lanes produce that was **184 CLI launches for two
targets**, each paying the CLI's start-up, a token lookup and a fresh connection for one request, at
1–3 s apiece — so "Publish bundles to every portal target" took **5–10 minutes** of a bake whose
compile is ~7. The maintainer's directive, after asking why the bake queue ran so slowly: *"how many
are there? this must be one bulk query"*.

It is now `publish-bake-files.py` beside the script, **one process per phase per target** on the
Azure SDK with the same CLI identity (`AzureCliCredential` + `token_intent=backup`, the SDK's form of
`--auth-mode login --backup-intent`):

| phase | before | after |
|---|---|---|
| upload | 46 processes per target, sequential | 1 process: parent directories ensured, then every file through a 12-wide thread pool, stamped `digest` + `publication` |
| read-back | 46 processes per target, sequential | 1 process: the destination and `modules/` **listed once**, then every manifest file's properties over one connection pool, one TSV row per file |
| seal | 1 process | 1 process (a one-line plan through the same uploader) |
| per target | ~93 launches | 3 launches + a handful of `az` decisions (sentinel present? marker content?) |

🚨 **The listing cannot carry the stamps.** Azure Files' *List Directories and Files* returns names
and sizes, never metadata, so the per-file `get_file_properties` inside one process **is** the bulk
read; the helper prints `verified N file(s) … in S s (L listed in K listing call(s))` so the cost
stays measured rather than assumed. The verdict logic did not move: bash still sorts every row into
*ours* / *neutral* / *foreign* / *unreadable*, asserts the denominator, and refuses exactly as above
— only the **input source** of the sweep changed. A row is five tab-separated fields (path, state,
digest, publication, length) with `-` for an absent stamp and `absent` / `error` as states in their
own right, so a transient fault can never read as "no digest recorded"; the parse asserts the field
count and refuses on any other shape. The SDK is installed **by the script**, pinned, followed by
`sdk-check` as the positive signal — a satellite fetches the script at `platform-ref` and runs the
lane at its `uses:` pin, and the two move independently, so a workflow-side install would leave the
newer script without its dependency.

Measured locally against the overlap harness's fake share: the whole suite — about twenty-five full
publications (upload, read-back, seal, plus `sdk-check` each, the generation-layout cases writing
two directories apiece) — in 25 s wall; under a second per publication, where the CLI shape spent
minutes per target.

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

## The seal triggers the sync — the publication and the sources are ONE unit

A sealed publication is bytes compiled from **one commit** of the producing repository
(`source-commit.txt`). The instance's SOURCES for that repository live in the mesh and advance only
through the git sync. They must be the same commit, or every bundle is — correctly — declined on its
source fingerprint (#2813) and the instance compiles every type from whatever it holds.

Measured on memex.systemorph.com, 2026-09-08, two ways they were NOT the same commit:

1. **The hook fires before the seal.** The `workflow_run` green-build hook arrives when the
   repository's build goes green, which is BEFORE its publish-bake job seals the bundles for this
   identity. `SealedSyncGate` held the source — "not sealed for this instance; it advances when it
   is" — correctly. Nothing re-fired when the seal landed minutes later, so *when it is* was never.
2. **"At the commit" was a claim, not a fact.** The config recorded `lastSyncCommitSha = 76cdb553b`
   with outcome `Skipped`: the importer's content marker matched, so it never read the partition.
   Three `Crm/Source/Mail*` files a commit had deleted (Crm#54) were still in the mesh — kept from
   the prune because the conflict horizon was frozen and, judged by timestamp alone, the previous
   import's own writes read as "newer on the server". 25 live sources against a bake of 22; no bake
   of that repository could ever match again.

What closes both, in one seam (`IPublicationSyncReconciler`, implemented by the git-sync layer as
`SealedPublicationSyncReconciler`, invoked by `ShippedPrebuiltBundles.SeedPublishedRoot` after every
publication sweep with what it sealed and what it declined):

- a source **behind** the seal is imported at the sealed commit — the seal IS the evidence the gate
  was waiting for (`SealedSyncReconcile.Action.ImportAtSealedCommit`);
- a source **at** the sealed commit whose types were nevertheless declined on their source
  fingerprint is re-imported at that commit with the content-skip bypassed
  (`ImportConflictPolicy.Reconcile` → `GitHubSyncService.ReconcileAtCommit`) — every conflict
  protection stays armed;
- and **only a person's write is a server edit** (`ImportConflictPolicy.IsHumanEdit`): a node the
  import itself wrote (system identity, or no author) is never preserved from the prune, however
  new its clock. That is what lets a repository's deletion reach an instance whose horizon is held.

A hold is written onto the config (`GitHubSyncConfig.LastSyncNote`, outcome `Held`) and logged at
Warning **once per reason**, never per delivery. The pure decisions are pinned in
`SealedSyncReconcileTest` and `ImportConflictPolicyTest`.

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
  **everything on this page still describes what is live**. **Generation retention — the stated
  precondition on flipping — has landed too**: the portal's own `PrebuiltBundleStore` sweep now
  collects a generation no `_current` names, whose pointer resolved cleanly, whose own seal could be
  read, and that is older than the 30-day window, applying the identity rules' fail-closed discipline
  one level down. What remains is each producer's pin reaching the writer, then flipping — `plugins`
  in ONE change set, because it is the only prefix with two producers.

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
  The convergence verdict takes that 2 to 1; the layout takes it to 0. **2026-09-10 makes it three**
  — 4 same-lane contentions, 0 cross-lane, over 61 executed bake jobs; see "Reproduced a third time"
  above, which also measures the thing the earlier two did not: **every** prefix the two lanes shared
  that day, they shared carrying *different* content, so the sealed-skip cannot separate them and the
  premise of #3461 is confirmed rather than narrowed.
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
  holds two bakes" is a fact about the shelf. Both I/O edges are substituted over ONE filesystem
  share — the stub `az` on PATH for the per-target decisions, and a fake share backend the bulk
  helper loads from `PUBLISH_BAKE_FAKE_SHARE_BACKEND` for the uploads and the read-back — with the
  upload pool one wide so the second-publisher hooks fire at a named file; one control runs the
  default (parallel) pool. The helper's row rendering is pinned by running the helper itself, and
  `sdk-check` runs against the REAL pinned SDK. **82 assertions** (the generation-layout cases
  included — see [Sealed Publication Generations](../SealedPublicationGenerations)) — three controls
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

### In production — and why a retrospective log sweep cannot settle it

The harnesses above prove the three answers in a TestServer. They cannot prove a deployed portal
stopped throwing, and #3876 stayed open on exactly that bar after its four readers and two route
opens were fixed. What the portal can and cannot answer, measured 2026-09-11:

- **The durable counter is the incident node, not the log.** `Admin/_LogIncident/<fingerprint>` on
  the **control instance** (memex.systemorph.com — the same path on memex.meshweaver.cloud answers
  `Not found`) carries `occurrences`, `firstSeen`, `lastSeen` and the pod names, and it outlives
  Loki's retention. For `af1ee515fdf60bd1` it read **7 occurrences, last 2026-09-10T02:35:29Z**.
- 🚨 **The ISSUE is not that counter, and can silently stop tracking it.** The node also carries
  `occurrencesAtLastComment` plus, when the automation cannot post, `status: "Failed"` and the
  reason — here `"Resource not accessible by integration"`, stuck at **5** while the count had
  reached 7. Two recurrences after the issue was filed were never folded into it, so **a quiet
  issue thread is not a quiet incident**. Read the node, never the thread.
- **What discriminates a pre-fix occurrence from a live one is the pod's ReplicaSet hash**, not the
  timestamp. All 7 fall on `77cfc55bfc` and `bf77c847`, while the fixes merged 2026-09-10 00:47Z
  (#3877), 12:07Z (#3885) and 18:57Z (#3957) — the last occurrence is 1 h 48 m after the first of
  those and still on the *previous day's* ReplicaSet, so it ran an image predating it. Confirm what
  a replica carries by FILE/SYMBOL against the commit `/api/version` reports, never by merge
  ancestry (a queue-merged commit fails `is-ancestor`).
- 🚨 **`Logs`' `sinceMinutes` is a REQUEST, not a coverage guarantee, and the run never reports the
  window it actually covered.** `{ "requestedAction": "Logs", "query": "_complete",
  "sinceMinutes": 2880 }` against `memex-cloud` returned `entryCount: 0` carrying its `logQl` — an
  answer by the rule in [Operating From The Portal](../OperatingFromThePortal), and still worth
  nothing here: the positive control, the same query for a string that *does* occur, landed **21
  lines spanning only 05:29Z–06:24Z**. The zero evidences under an hour rather than the two days
  asked for, and the known occurrences are simply outside what Loki still holds. **Date the oldest
  line a positive control returns before reading any `Logs` zero as absence** — against a window a
  ~90 s republish can hide in, a retrospective sweep cannot settle this at all.

So the verification that remains is a **live** one — a satellite gate reading the route through a
real mid-replace window, or through a pruned identity, which is permanent and never self-heals —
and the instrument that records it either way is the incident node's own counter.

Related: [CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch) · [Module Build Architecture](../ModuleBuildArchitecture)
