---
Name: Sealed Publication Generations
Category: Architecture
Description: The layout that makes a mixed bundle publication unrepresentable — a generation directory per publication plus a one-line pointer swapped last — the reader contract, the retention rule, and the ordered migration that gets there without a window in which some readers use the pointer and some do not.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7v10a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-6l-2-2H5a2 2 0 0 0-2 2z"/><path d="M8 13h8"/><path d="M8 17h5"/></svg>
---

# Sealed Publication Generations

[Sealed Publication Reads](../SealedPublicationReads) describes how a publication is written and read
today, and ends with what is **not** closed. This page is that remainder: the layout that closes it,
what each reader must do, and the order the migration has to land in.

It is a design plus a landed first phase, not a finished migration. Every section says which phase
it belongs to, and ["Where this stands"](#where-this-stands) says exactly what is live. The order the
phases have to land in is a property of **who publishes and what they pin**, which is measured
below rather than assumed.

## The defect this removes

A publication is replaced **in place**. `publish-bake-bundles.sh` deletes `_complete`, uploads over
the live files, and re-seals — deliberately, because unsealing first is what makes "sealed" mean
"every listed bundle is here".

Interleave two publishers on one prefix and the sealed-skip's answer is stale for whichever loses
the race: both unseal, both upload, and the last to seal writes a sentinel over a directory holding
**some of each one's bytes**. That is one seal with one generation — self-consistent to every
consumer, and wrong. The read-side generation cannot see it (there is nothing stale to refuse), the
boot seeder cannot see it (the sentinel is present and every listed bundle exists), and the first
symptom is `dependency record mismatch — built against mvid:…, live is mvid:…` on a portal that
renders nothing.

🚨 **The publisher's byte-level postcondition (#3496) turns that from a silent mix into a loud
refusal, and it is not the same thing as preventing one.** It re-reads every uploaded file's digest
immediately before the seal and refuses when any is foreign. What it cannot cover is the interval
between that last read and the `_complete` upload: a publisher overwriting a file inside that single
write still lands under this run's seal. The exposure drops from a whole ~90-second publication to
one file upload — a smaller window, still a window. **A postcondition is not mutual exclusion.**

🚨 **And "give the prefix one owner" is not the answer either — that was measured.** Of the overlaps
actually observed on 2026-09-06, *zero* were between the two lanes and **four** were two runs of the
*same* lane on one identity. A rule about which repository owns a prefix does not address a lane
racing itself. The numbers are in
[Sealed Publication Reads](../SealedPublicationReads) → "How many writers, measured".

## The layout

Each publication is written into its **own directory**, and a one-line pointer says which one
applies:

```
<root>/<identity>/<source>/_current                  the pointer: one line, a bare directory name
<root>/<identity>/<source>/<generation>/…            one publication INSTANCE
<root>/<identity>/<source>/<generation>/_complete    its seal, still written strictly last
<root>/<identity>/<source>/<generation>/modules/…    its module set, + _index
```

`<generation>` is the publisher's **publication token** — `<repository>-<run id>-<attempt>`, already
minted by `publish-bake-bundles.sh` and already restricted to `[A-Za-z0-9._-]` so it is a legal bare
name on every backend. It is unique per run by construction.

That one property is the whole design:

- **Two publishers never write the same bytes.** Disjoint directories, so there is nothing to
  interleave. A mix becomes **unrepresentable**, not merely detectable.
- **Publishing ends with one small write.** A reader either sees the previous generation — intact,
  sealed, still on the shelf — or the new one.
- **A reader that resolved early is not torn.** Its generation is still there; the pointer moving
  does not delete it.

### No pointer means the flat layout

A source directory with **no** `_current` **is** its own publication directory. That is exactly
today's behaviour, it stays legal for as long as any reader needs it, and it is what makes the
migration below possible at all. Resolution is opt-in **by the writer**, never by the reader.

### Why a pointer and not an atomic directory rename — measured, 2026-09-08

The obvious design is to stage the publication and then **rename it over** the live one. It is not
available on this store through this client, and that is a measurement rather than a recollection.

Read off `azure-cli 2.90.0` — the same version the publisher's read-back query was measured
against:

| group | commands |
|---|---|
| `az storage file` | copy · hard-link · metadata · symbolic-link · delete · delete-batch · download · download-batch · exists · generate-sas · list · resize · show · update · upload · upload-batch · url |
| `az storage directory` | create · delete · exists · list · show |

There is **no `rename`** in either, `directory delete` is documented *"Delete the specified **empty**
directory"*, and there is no `lease` command under either group — so a lease per identity is not
reachable from this lane either. And even in the REST API, where `Rename Directory` has existed
since API version 2021-04-10, a rename cannot **replace** an existing directory: the swap would be
rename-away plus rename-in, two operations with a gap in which the live path does not exist at all.

🚨 **So the smallest write this store actually offers is one small FILE, and that is what the
pointer is.** A design that assumed an atomic directory rename would have reintroduced a
partial-visibility window wearing a better story.

### What is still a window, stated honestly

The pointer is one small file, and writing it is not atomic on every backend: `az storage file
upload` is a create-then-put-range, so a reader can catch it empty or short.

**That does not produce a mix.** An unreadable, empty, escaping or dangling pointer resolves to the
source directory — see the resolution rules below — so the worst case is:

| phase | a torn pointer read gives | what the reader does |
|---|---|---|
| flat copy still present | the previous publication, whole | serves it; correct, just not the newest |
| flat copy gone | a directory with no `_complete` | "being republished right now" → `503` + `Retry-After`, which every consumer already waits out |

So the trade is: **~90 seconds in which a mix can be sealed** becomes **the duration of one small
file write in which a reader may be told to come back**. The failure *mode* changes, not only its
size — from "a sealed mix nobody can detect" to "read it again". Where the backend offers an atomic
rename (Azure Files' `Rename File`, present in the REST API since 2021-04-10 though not exposed by
every `az storage file` build), the writer phase should publish the pointer that way and remove even
that.

## The reader contract

One function resolves the pointer, and **every path is composed under its result**:

```csharp
ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory, logger)
```

🚨 **The one way to get this wrong is silent, so the API is shaped against it.** A reader that
resolves the pointer for the *listing* and then composes its file paths under the **source**
directory serves the flat publication's bytes under the generation's token — the very mix the
generation exists to prevent, wearing a token that says it is not. During the migration both paths
exist, so it is a wrong answer rather than a missing one. That is why `SealedPublicationOf` and
`SealedModulesOf` **hand the resolved directory back** on the reading (`Directory`) instead of
leaving each caller to resolve a second time.

**Resolution rules.** It never throws; it falls back. Each of these resolves to the source
directory:

| pointer | why it falls back |
|---|---|
| absent | the flat layout — the normal state today |
| empty or blank | being replaced right now; the previous generation still applies |
| unreadable (`IOException`) | same, mid-write |
| `.`, `..`, anything with a separator, anything rooted | 🚨 a pointer is a NAME. It must never be able to address bytes outside its own source directory — refused and logged as a warning, because a publisher wrote something this reader will not follow |
| names a directory that is not on disk | a retention sweep outran the pointer; logged as a warning |

### Retention

A pointer swap must never delete what a reader is mid-way through: a consumer doing N+1 reads may
have resolved the previous generation seconds ago.

- Never delete the generation `_current` names.
- Keep the previous generation for at least as long as the slowest consumer's N+1 read plus its
  backoff. The `503` ladder is `15 30 60 90`, so **24 hours** is a bound with three orders of
  magnitude of headroom and no measurable storage cost at ~43 files per publication.
- The sweep is the **publisher's**, at the end of its own run, after the pointer moves. Nothing else
  knows when a publication stopped applying.
- 🚨 A retention sweep is not a garbage collector to be tuned down when the share fills. Deleting a
  generation a reader still holds re-creates a torn read — which is the state this layout exists to
  make impossible.

## Who actually publishes — measured, 2026-09-07

The migration order is decided by this table, so it is a measurement rather than a recollection.
Every row was read off the producing repository's own `ci.yml` (or `main-cd.yml`) on 2026-09-07.

| producer | prefix (`bake-source`) | which `publish-bake-bundles.sh` it runs (`platform-ref`) | behind core `main` |
|---|---|---|---|
| core CD `plugins-bake` (`main-cd.yml`) | `plugins` | **this run's own core commit** (`needs.gate.outputs.sha`) | 0 — it IS the tip |
| MeshWeaver.Plugins `ci.yml` | `plugins` | `aa40758329216d57dcefc2d8e8a52101efd5f225` | 231 commits |
| MeshWeaver.Reinsurance | `reinsurance` | `1b5350d547473a5e2ca81e793e774cc962acfeb3` | 284 commits |
| MeshWeaver.SocialMedia | `socialmedia` | `1b5350d5…` | 284 commits |
| MeshWeaver.Manufacturing | `manufacturing` | `1b5350d5…` | 284 commits |
| MeshWeaver.Education | — | — (it calls no `node-repo-publish-bake`) | — |

Three things follow, and each of them changes the plan:

1. 🚨 **`plugins` is the ONLY prefix with two producers.** Every other prefix has exactly one, and
   Education publishes no bake at all. The coordination this migration needs is therefore **one
   pair** — core CD and MeshWeaver.Plugins — not five repositories. Everything else flips on its own
   schedule, one repository at a time, with nobody to coordinate with.
2. 🚨 **Core CD has no pin to move.** It checks the platform out at its own gate sha, so the day the
   writer merges, core CD runs it. If the writer were unconditional, the `plugins` prefix would
   become a new-writer/old-writer pair that same day, against a MeshWeaver.Plugins 231 commits
   behind — exactly the half-migration this page exists to prevent. **That is what makes the
   selector below mandatory rather than tidy.**
3. **Nothing is near the tip.** The nearest producing pin is 231 commits back and none of the four
   carries phase 1. A plan that assumes a pin will "have moved by then" is assuming something that
   has not happened in a month.

### 🚨 The correction: "past phase 1" is not a satisfiable precondition

This page used to say phase 2 was *"every producing repo's publish-bake pin moves past phase 1"*.
Measured against what phase 1 actually changed (`a4109d422`), that instruction is a **no-op**: it
touched `src/` — the portal image's readers — plus documentation and one comment block in the
publish script. It changed nothing a pinned lane executes. A producer whose `platform-ref` moves past
it runs byte-identical behaviour, so the condition can be satisfied by the whole fleet without
bringing the migration one step closer.

What a producer must be past is **the writer commit itself** — and a writer that is on by default
cannot be got past, because it takes effect the moment a pin reaches it. Hence the selector, and
hence the order below.

## The migration, in order

### Phase 1 — readers tolerate the pointer *(landed, `a4109d422`)*

`PublicationDirectoryOf` plus every read routed through it: the boot seeder,
`PublishedBundleCatalogue`, `ServedModuleBytes`, and the registry's four prebuilt routes. Behaviour
on a share with no pointer is unchanged, byte for byte.

Nothing writes a pointer yet, so this changes nothing observable — which is why it ships with a
suite that *builds the generation layout by hand* and asserts the readers serve it, including the
arm that catches the compose-under-the-source-directory mistake.

### Phase 2 — the writer LEARNS the layout, selected per caller, defaulting to flat *(landed)*

`publish-bake-bundles.sh` gains generation publishing behind an explicit selector: a
`publication-layout` input on `node-repo-publish-bake.yml`, carried into the script as an environment
variable, valued `flat` (the default) or `generation`. `bake-scope.sh` and `carry-forward-bundles.sh`
resolve the pointer in the SAME commit, because `node-repo-publish-bake.yml` fetches all three at one
`platform-ref` and no pin can carry half of them.

At `flat` the script must behave **byte-identically to today**, and that is provable rather than
asserted: `test-publish-bake-overlap.py` runs the real script, and its three control cases (a settled
publish, a republish of new content, an already-published skip) are the regression suite for the flat
path. The new mode earns its own cases — two interleaved publishers each seal their OWN directory,
neither directory holds a byte of the other, and `_current` names exactly one of them.

At `generation` the writer uploads into `<source>/<publication token>/`, verifies there (the #3496
postcondition still applies, now over a directory nobody else writes), seals it, **also writes the
flat copy** so a portal image that predates phase 1 keeps working, and moves `_current` last.

Every read that decides *what is already published* — the architecture marker, the sentinel, the
source-commit marker, the module index — resolves the pointer first and reads inside the resolved
directory, or the writer and the readers disagree about which publication is live. The resolution
rules are the reader's, unchanged: an absent, blank, unreadable, escaping or dangling pointer means
the source directory. `carry-forward-bundles.sh` must read the generation the *listing* came from,
which is the shell analogue of the reader's `If-Match`.

🚨 **Any new marker file is LISTED and UPLOADED before `architecture.txt`.** That file is the LAST
upload before the postcondition, and the overlap harness hooks its second publisher onto it — a
marker written after it silently stops the harness detecting overlaps while every case still reads
green. (`repository.txt` was added under this rule and says so in place.)

#### What actually shipped, and the one deliberate deviation

- The selector is `publication-layout` on `node-repo-publish-bake.yml`, defaulting to `flat`,
  carried into the script as `BAKE_PUBLICATION_LAYOUT`. An unrecognised value is **refused**, never
  silently read as flat: the value decides where a publication is written and which directory every
  reader resolves.
- `bake-scope.sh` and `carry-forward-bundles.sh` resolve the pointer in the SAME commit, by the same
  rules, so the writer and the two Azure-direct readers cannot disagree about which publication is
  live. `carry-forward-bundles.sh` resolves it **itself** rather than being handed the directory —
  its caller may be pinned to a workflow copy that knows nothing about generations, and the
  one-publication postcondition it already carries is what pins it to a single generation if the
  pointer moves between the listing and the downloads.
- 🚨 **The pointer moves BEFORE the flat compatibility copy, not after it.** "Last" in this page is
  about the *generation*: a reader must never be pointed at a directory still being filled in, and
  moving the pointer straight after the seal satisfies that exactly. The flat copy is a different
  audience — readers that cannot follow a pointer at all — and it is the one part of a generation
  publication another publisher can still be writing. Ordering it after the pointer means an overlap
  on the flat copy costs the *compatibility copy* (refused, as today, and the run goes red) instead
  of costing a publication that is already whole, sealed and disjoint. Ordering it before would let
  a race on the OLD layout withhold a publication that is correct on the NEW one — which would make
  flipping a prefix deliver nothing at all until the flat copy is dropped.
- 🚨 **Retention is NOT implemented, and that is a precondition on flipping rather than a follow-up.**
  Generations accumulate at ~45 small files each until a sweep removes the ones nothing names, and
  that sweep DELETES from the production share — it must land as its own reviewed change, with its
  own harness. Nothing the writer does today deletes anything it did not create.

### Phase 3 — every producer's pin reaches phase 2 *(open — the next step)*

Only now is the condition both satisfiable and meaningful: a producer past phase 2 *can* write
generations and is still writing flat. Core CD needs no pin move. The four satellites move theirs the
way they always do.

### Phase 4 — flip, one prefix at a time *(open)*

Set `publication-layout: generation` on every producer of one prefix, **in one change set where a
prefix has more than one producer**. That is `plugins` and nothing else: core CD's `plugins-bake` and
MeshWeaver.Plugins' `publish-bake`, flipped together, platform half first. The single-producer
prefixes each flip in their own repository's PR.

🚨 **The residual window is a pin bump inside one lane.** Even a single-producer prefix has a moment
where a run started before the flip is still in flight while a run after it writes a generation. The
loser leaves the pointer naming a stale generation until that lane publishes again — which happens on
its next merge, so it is self-healing and bounded by one publication rather than permanent. Worth
knowing before reading such a serve as a defect.

### Phase 5 — drop the flat copy *(open)*

Once no deployed portal predates phase 1. From here the mix is unrepresentable and the republish
window is gone; what remains is the sub-second pointer write described above, and an atomic rename
removes even that.

🚨 **Until then the flat copy is still replaced IN PLACE, so it still races.** Phase 4 removes the
window for readers that follow the pointer; the compatibility copy the writer keeps making for
pre-phase-1 images is unsealed, rewritten and re-sealed exactly as today, and can still be sealed as
a mix. The #3496 postcondition is what covers it, and it covers it only as a postcondition. A report
that says "the window is closed" at phase 4 is describing the pointer-following readers only.

## If the publication moves to an OCI registry

The fleet now has its own registry (`cr.meshweaver.cloud`) and a program to push plugin bundles into
it as OCI artifacts. It is worth writing down exactly what that does and does not close, because
"content-addressed" is easy to read as "the race is gone".

**What it closes by construction.** Blobs and manifests are addressed by the digest of their own
bytes. Two publications pushing byte-identical content collide **benignly** — same digest, a no-op —
and two publications pushing different content get *different* blobs, which cannot overwrite each
other. A blob is immutable once pushed; there is no partial overwrite to interleave. So the **mix**
— a set holding some of each publisher's bytes — becomes unrepresentable at the byte level, which is
the same property the generation directory buys, obtained more cheaply.

🚨 **What it does NOT close: a tag is a mutable, last-writer-wins pointer.** If the publication is
addressed by tag, the defect simply moves from a directory prefix to a tag, and the migration
inherits it. Three conditions close it, and they are the rule already in force for images via
`MW_IMAGE_DIGEST`:

1. **Every bundle is pushed and recorded by DIGEST**, never by tag alone.
2. **Each publication also gets an immutable, identity-qualified tag** — so a publication can be
   named without that name being reassignable to different bytes later.
3. **The sealed set is a list of `(package, digest)` pairs**, so a mixed set cannot be written at
   all: the set names exact bytes, and a publisher that did not assemble those bytes cannot produce
   that list.

🚨 **Content addressing does NOT make two bakes of one commit converge.** The bundle compile is not
byte-reproducible — measured 2026-09-08, 40 of 45 files differed between two bakes of the same
source commit — so two publications of one commit produce different blobs and therefore different
manifest digests. OCI does not merge them; what it does is make each one a complete, immutable,
self-consistent object, so the only contention left is which one the reference names. That is a
last-writer-wins between two *correct* sets, which is a different and far weaker thing than a mix.

🚨 **Two invariants the migration must carry across, or it reopens something worse than it closed:**

- **The framework identity must stay in the reference.** Today the directory is keyed
  `prebuilt-bundles/<framework-identity>/<source>/`, and that is not decoration: a bundle is only
  adoptable by a portal that resolved the *same* identity, which is why the publisher refuses when an
  incumbent's `architecture.txt` disagrees. A repository path of the shape
  `plugins/<source>/<package>:<version>` carries no identity, so two platform surfaces' bakes would
  collide on one reference and a portal would adopt bytes built against a surface it does not have —
  `dependency record mismatch — built against mvid:…`, this issue's original symptom by another road.
- **The release marker must survive.** `prebuilt-bundles/_releases/<version>` → `<identity>` is the
  only way anything outside the image learns a release's framework identity, and two release gates
  HOLD on its absence.

**And the refusal must not be weakened, in either world.** On the share it reads "never seal a
sentinel over another publication's bundles". In OCI terms it is the same sentence one level up:
**never publish a reference to a set you did not assemble.** That refusal is the only reason this
was ever visible instead of silently shipping a mixed set.

## Where this stands

- **Phase 1 is landed** (`a4109d422`) — the readers resolve the pointer, and the fallback is the
  previous behaviour exactly.
- **Phase 2 is landed** — the writer can publish generations, behind `publication-layout`, which
  defaults to `flat`. Nothing anywhere writes a generation until a caller opts in, and the three
  control cases in `test-publish-bake-overlap.py` are the regression suite proving the default path
  is byte-identical to what it was.
- **Phases 3–5 are open**, tracked on
  [#3461](https://github.com/Systemorph/MeshWeaver/issues/3461). Until the writer flips, **the window
  is shrunk, not closed**: the publisher's postcondition still carries the whole load, and the
  interval between its last verification read and the seal is still live.
- 🚨 **What the postcondition costs while this is open, measured 2026-09-08.** Of 30 core-CD runs,
  9 executed the bake job; of the 9 publications (either lane) that had a same-identity run
  overlapping them in time, **2 failed** — 22%, and both were the two halves of ONE mutual
  supersession: core CD runs `34205409381` and `34206854855` publishing the same content
  (`cfac152ef…`) for the same identity, each winning one storage target and reddening on the other.
  Both shares ended sealed with the right bytes and both CD runs failed, so the run produced no
  sealed set and the platform pin did not move. **All 9 overlaps were same-lane; zero cross-lane** —
  the same finding as 2026-09-06, on a different day.

  The [convergence verdict](../SealedPublicationReads) removes the half of that which is a false
  red — a supersession by a publication *proved* to be this bake's own content, sealed. It takes 2
  to 1 on that incident. The **residual is a sibling that has not sealed yet when this run's sweep
  ends** (21 seconds, measured), and that is not shrinkable by any amount of checking: it is what
  phases 2–5 exist for.
- **The next change is phase 3** — each producer's `platform-ref` reaches the writer commit. Only
  then is flipping a prefix both possible and meaningful. `plugins` flips in ONE change set because
  it is the only prefix with two producers; the rest flip one repository at a time.
- 🚨 **Flipping `plugins` does not by itself stop the publish reds.** The flat compatibility copy is
  still replaced in place and still races, so an overlap still costs that copy and still fails the
  job — the postcondition covering it is unchanged. What the flip buys immediately is that the
  *publication* survives an overlap intact and pointed-to instead of being lost. The reds go when the
  flat copy does (phase 5), or when the publication moves to digest-addressed artifacts.
- The reds the postcondition produces are the correct number and must not be loosened away — see
  [Sealed Publication Reads](../SealedPublicationReads) → "What is NOT closed".

## Verification

- `.github/scripts/test-publish-bake-overlap.py` — **67 assertions**, executing the REAL publish
  script against a stub share and reading every verdict off the BYTES. The writer half is covered by
  five generation cases: one publisher writes and seals under its own token and the pointer names it;
  **two interleaved publishers each seal their OWN generation, neither directory holds a byte of the
  other, both are complete, and `_current` names exactly one of them**; "already published" is
  resolved *through* the pointer rather than off the prefix; every unusable pointer shape (escaping,
  rooted, `..`, blank, dangling) degrades to the prefix; and an unrecognised selector is refused. The
  three flat controls are unchanged and are the regression suite for the default.
  🚨 **Negative control:** run against the pre-change script, **16 of the 67 fail**.
- `bake-scope.sh --self-test` — four pointer-resolution assertions, and the positive one is
  discriminating by construction: the flat copy and the generation record *different* baselines (a
  diverged commit versus an ancestor), so the verdict itself says which was read. A resolver that
  ignored the pointer answers `full`; one that follows it answers `narrowed` with the generation's
  commit. The dangling-pointer control asserts the fallback SAYS so, so silence cannot pass for
  resolution.
- `carry-forward-bundles.sh --self-test` — a decoy bundle is planted at the prefix under the same
  name and different bytes, so carrying the publication's own bytes forward is only possible by
  following the pointer.
- `test/Memex.Portal.Shared.Test/PublicationGenerationTest.cs` — builds the generation layout on
  disk and asserts what is served. The fixtures make the flat copy and the generation differ in
  **bytes under the same file names**, so "which publication was read" is a fact off the archive
  rather than an inference from a path. It covers the pointed-to read, the flat fallback, every
  escape shape, a dangling pointer with and without a flat copy behind it, a blank pointer, a
  retained older generation, and that moving the pointer moves the generation token (without which
  the `412` that stops an N+1 read spanning two publications never fires).
- **Negative controls, run against this tree.** With `PublicationDirectoryOf` reduced to the
  pre-#3461 reader (`return sourceDirectory`), 3 of 15 cases fail — the three that require
  resolution — and the other 12 hold, because they pin the fallback, which is unchanged. With the
  name validation removed but resolution kept, 4 more fail: the escape shapes. A resolver that
  cannot fail either check is not a resolver.

Related: [Sealed Publication Reads](../SealedPublicationReads) ·
[CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch)
