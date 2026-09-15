---
Name: Module Activation Head Ownership
Category: Architecture
Description: Which generation of a module a deployment runs used to be a decision every replica wrote to one shared file, so two replicas landing different content of one module a few seconds apart lost each other's decision. Now every landing writes an immutable record of its own and the head is DERIVED from the records present. The on-disk format, how it stays readable by images already deployed, the retention rule, and why routing the write to an owning hub was not available.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="M5 8h14a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z"/><path d="M7 20h10"/><path d="M9 16v4"/><path d="M15 16v4"/></svg>
---

# Module Activation Head Ownership

A deployment recorded which generation of each landed module it runs in one file per module,
`modules/activation.d/<Name>.json`, on the RWX `/data` volume every replica shares. A landing read
that file at its start, decided against what it read, and replaced it at its end. The replace is
atomic and **unconditional**.

Within one process that is safe — landings are serialised on a cap-1 `IIoPool` slot. Across
replicas it was a lost update. The issue is
[#4026](https://github.com/Systemorph/MeshWeaver/issues/4026), and it is **closed by deriving the
head**: every landing now writes an immutable record of its own facts, and the head and fallback are
computed from every record present. The first section below is the format as built and the
compatibility story; the rest of the page is the measured defect and the design reasoning that
chose this shape, kept because it is why the format looks the way it does.

## As built — the on-disk format (#4026)

```
modules/activation.d/<Name>/<generation>.<SHA-256 of the record>.json       one landing, never rewritten
modules/activation.d/<Name>/uninstalled.<SHA-256 of the tombstone>.tombstone one uninstall, never rewritten
modules/activation.d/<Name>/<generation>.<SHA-256 of the verdict>.verdict    one platform's link measurement
modules/activation.d/<Name>/entry.<SHA-256 of the file's bytes>.snapshot       an older image's entry, preserved
modules/activation.d/<Name>.json                                             the STORED entry, still written
modules/activation.json                                                      the legacy aggregate, read only
```

Every file in `activation.d/<Name>/` is **immutable and content-addressed**: its name is the full
SHA-256 of every field it serializes (length-prefixed, in a fixed order), so a name holds exactly one
content and two writers of one name are writing identical bytes. That is what makes the create safe
without an atomic primitive — .NET's no-overwrite move is `link(2)` where the file system has it and
an existence check plus `rename(2)` where it does not (a CIFS mount). Each file's **arrival** is its
own write time, never serialized. Temp files are staged in `activation.d/` itself, so every create
moves that directory's write time — the fingerprint `PendingModuleActivations` memoises the read
behind.

**A landing record** (`ModuleLandingRecord`) carries only the landing's own facts — `name`, `source`,
`packagePath`, `directory` (the generation), `version`, `frameworkMvid`, `minMeshVersion`,
`sourceCommit` — plus two facts of the REQUEST: `yieldsToNewerHead` (true on the publish route's
shelf, false on the adopt path, because the derivation replays each lane's own head rule) and
`reArrivalOf` (below). No `Previous*`, no enabled flag, no loadability: those are decisions, state,
and a fact about the platform.

**An uninstall tombstone** (`ModuleUninstallRecord`) is the uninstall as an event: `name` and
`after`, the newest event it observed, so a second uninstall is a new file rather than a no-op.

**An older-image snapshot** (`ModuleEntrySnapshot`) is an older image's `<Name>.json`, preserved
before a current image overwrites it: the entry exactly as that image wrote it, and the time it
wrote it (below). Addressed by the SHA-256 of the snapshot file's own bytes, because it carries a
whole entry whose shape may grow.

**A platform verdict** (`ModulePlatformVerdict`) is one measurement of whether a generation's bytes
link on one platform build: `name`, `directory`, `platform` (the live framework identity — the key a
producer records beside its bytes), `linkable`, and `supersedes` (the verdict it replaces on the
same platform). Every landing writes its own image's measurement, before its landing record.

### Installed or not is the ORDER of events

`ModuleActivationSidecar.DeriveEntry` runs inside `Read`, so the 82 call sites measured below get it
with no change. The events are the landing records and the tombstones, each at its arrival — plus
the stored `<Name>.json` **when an image that predates the records wrote it**. Current images mark
every `<Name>.json` they write with `projectionOf` (the newest event it was projected from); an
older image does not know the field and never writes it, so a file **without** it is that image's
own install (a landing of the head it names) or uninstall, at the file's write time. A file **with**
it is only a projection and is never an event.

- The newest uninstall ends everything before it. **Nothing landed after it → uninstalled.**
  Something did → installed, and only the landings after it count — which also makes the next
  landing after an uninstall a first landing (the documented "uninstall, then publish the older
  build" rollback), without deleting a single record.
- So a **stale projection cannot undo an uninstall**, and a stale disabled entry cannot undo a
  landing — Copilot's second finding on #4427. The interleaving: replica A lands and derives
  "installed"; replica B uninstalls (tombstone, then its disabled `<Name>.json`); then A writes the
  projection it derived before the tombstone existed. A current image orders B's tombstone after A's
  record and reads **uninstalled**, whatever `<Name>.json` says. The mirror (B decides an
  uninstall, A lands inside B's window, B's disabled file lands last) reads **installed** at A's
  build. `ConcurrentUninstallTest` pins both.

### 🚨 An older image's event outlives the next current-image write of its file

An image that predates the records records its install or uninstall ONLY as `<Name>.json`
without `projectionOf`, and a current image reads that file as an event at its write time. The
first build of this design let the NEXT current-image write of that file erase the event: from then
on the carried generation was re-dated to the beginning of time — dead behind any tombstone, replayed
first otherwise — and an older image's uninstall vanished outright (the post-merge review of #4427).
Measured on `main` after #4427:

- a current image uninstalls; during a rollback an older image reinstalls 1.7.0; a current image
  shelves 1.6.1 and REPORTS that 1.7.0 stays the head — and the next read derives **1.6.1** with no
  fallback, which `ProposeModuleSet` would carry fleet-wide (#3996 by another road);
- an older image uninstalls; a current image lands 1.6.0 — and the pre-uninstall **1.7.0** comes
  back as the head;
- an older image adopts 1.5.0 over 1.6.0; a current image shelves 1.4.0 — and the head flips back
  to **1.6.0**.

So a current image, before it overwrites a `<Name>.json` that lacks `projectionOf` (a landing's
projection, or an uninstall's disabled entry), first writes that entry as a **snapshot** — the entry
and its write time, read from ONE open handle, so a concurrent replace can never pair one file's
bytes with another's time (`PreserveOlderImageEntry`). The derivation treats every snapshot exactly
as it treats the live file — an install of the head it names, never yielding, at the time that image
wrote it; or an uninstall at that time — so taking the snapshot changes no answer and the overwrite
that follows loses nothing. `ModuleActivationPostMergeTest` pins all three scenarios, each red on
`main` and green here.

The residue is the file's own: an older image's write that lands between a current image's read of
`<Name>.json` and its overwrite is not seen, and is lost — last-writer-wins against that image, as
the file always was, for the length of a mixed-image window.

### 🚨 Fail closed: an unreadable record drops the module, it never changes the answer

The first build skipped an unreadable record, tombstone or snapshot with a log line and derived
from the rest — so the answer CHANGED: an SMB sharing violation on a tombstone at boot loaded an
uninstalled module, one on the head record promoted an older generation, and the landing wave then
proposed that as the mesh's module set. Now:

- a module whose record directory cannot be read WHOLE — any file unreadable, or not matching its
  own address, or the directory not listable — is **dropped from the read** and reported through
  `onCorrupt`, exactly as an unreadable activation entry always was before #4026 ("that ONE module
  is skipped"); the next read that sees it whole restores it;
- so is a module that has records and whose `<Name>.json` exists but cannot be read — that file may
  be an older image's install or uninstall, and a stale legacy-aggregate row behind it must not
  decide in its place (the aggregate's own write time is its time now, never the unreadable file's);
- a `<Name>.json` that does not hold ITS OWN entry — JSON `null`, an entry with no name, or an entry
  naming a different module — is corrupt exactly as a record that does not hash to its own name is:
  reported, keyed by the FILE's name, and never accepted under the other module's name (sorted
  after that module's own file, it would silently replace it);
- the STORED layer's own faults count too (Copilot's review of #4438): if the per-module entry files
  cannot be listed, every module that has records is dropped, and so is every landing or uninstall
  refused — any module's entry may be among them; if the legacy aggregate `activation.json` cannot
  be read, every module that has records and **no readable `<Name>.json` of its own** is dropped —
  its only entry may be in the aggregate. A module WITH its own readable file is not: that file
  outranks the aggregate's row by name, so the aggregate cannot hide it — and the aggregate is never
  rewritten, so an unreadable one could otherwise drop every store module for good;
- if the record directories cannot even be listed, every current-image projection is dropped — it is
  a decision some record may have overtaken;
- `ProposeModuleSet` **refuses** to propose from a read with any fault (it throws; every caller
  already logs and leaves the mesh on its current set), and a landing or an uninstall writes nothing
  derived from a partial read — its record is on the volume, and the next whole read derives it;
- `WriteVerdict` refuses to supersede a verdict it could not read.

### 🚨 What an image that predates the records sees when the two disagree

It reads `<Name>.json` alone, and that file is **last-writer-wins, as it always was** — no ordering
can make a mutable shared file linearizable without a compare-and-swap, and the store has none. So
in the first interleaving an older image sees the module **enabled** (A's stale projection), and in
the mirror it sees it **disabled** (B's stale entry), until the next current-image write of that
module's file (any landing or uninstall of it) — exactly the outcome every image got on `main` for
the same interleavings. In the first case the uninstall's best-effort delete removed the bytes that
projection names unless a pod held them open; the GC references stored generations too, so nothing
it names is reclaimed under a replica that boots from it. The residue is asserted, not assumed:
`ConcurrentUninstallTest` reads the file an older image reads and states what it says. It lasts for
the length of a mixed-image window (a rolling update or a rollback), and it is the same race `main`
has for every image today.

### Which generation: the head is a replay, the fallback a per-platform rank

Among the landings that count (one candidate per generation — the higher label wins, then the later
arrival):

- **the head is a replay** — folded in arrival order through #3996's predicate, which keeps the
  current head only when the arriving landing is a SHELF landing ranking strictly below it (both
  versions SemVer) and the head's bytes are present. That reproduces what a serialised sequence of
  the same landings would have produced, including the arrival-dependent cases the rule keeps
  (equal version = rebuild, unversioned, adopt). Every replica folds the same files, so every
  replica reaches the same head, **whatever image it runs** — the module set (`GenerationsOf`) reads
  heads only, so it is platform-independent too.
- **the fallback is a rank** — bytes present, then loadable on the **reader's own platform** (its
  newest verdict: measured "loads" above "never measured here" above measured "does not load"),
  then the higher version, ties keeping the earlier arrival. This is the one platform-dependent part
  of the answer, deliberately — Copilot's first finding on #4427: a verdict stored once per
  generation, by whichever replica recorded first, froze that image's answer for every image; the
  code was first-writer-wins while the page said last-writer. Now a replica on another image records
  its own verdict beside the first, and each image ranks by its own. Generations a projection names
  that no record does join as the earliest arrivals, so the first landing on a new image keeps the
  fallback the deployment had. `LinkVerdictPerPlatformTest` stands a second image up on one volume
  (a surface whose contract carries a type this build lacks) and pins that the old image falls back
  to 1.6.0 while the new one falls back to 1.7.0 — at the same moment.
- **The modules GC keeps the fallback of a BOUNDED set of platforms** (`ProtectedPlatforms`): the
  platform running the pass, the platform with no verdicts, and the **two** platforms whose newest
  verdict for the module is newest — in a roll, the image being rolled to and the one being rolled
  from — plus every stored entry's generations (`ReferencedGenerations`). The first build protected
  every platform that ever recorded a verdict; the platform key is the framework identity, which
  changes with most builds, so every old build pinned its own fallback record and bytes until
  uninstall. A platform outside the set no longer protects its fallback and its verdicts are retired
  after the grace window; a third image still live on the volume then ranks those generations as
  "never measured", and if the fallback it names has been reclaimed its boot falls through to the
  image baseline. `APlatformThatNoLongerMeasuresTheModule_StopsProtectingItsFallback` pins it.

**A re-arrival** is the one case identical records would get wrong: an adopt landing re-installing
the generation the deployment ran before (the Store's rollback), or any landing of a bundle after an
uninstall that followed its first arrival, finds its record already on the volume. When it would
change the answer, it writes a NEW record whose `reArrivalOf` names the latest record with the same
facts — a new file with its own arrival, never a touch of an existing one. When it would not (an
identical re-publish of the head, an older shelf upload), nothing is written. The hypothetical
arrival it is judged at is "after every event on the volume", never the pod's clock — the stamps it
is compared with are the file server's, and a pod running behind it would otherwise place the
re-landing before the uninstall it follows and land nothing. If the modules GC retired the record
between the landing's existence check and its read, the landing records it again, once.

**Ties.** At the newest uninstall's own tick, what decides is what that uninstall OBSERVED. Its
tombstone's `after` names the newest event it saw — ties at one tick going to the ordinal-greatest
name — so it is a **watermark**: an event at that tick whose name sorts at or below it was seen and
precedes the uninstall; one above it was not and follows it. That covers an older image's install
too: every older-image event has an identity — the file name its snapshot has, or WILL have once a
current image preserves it (`OlderImageEventName`) — which `LatestEventName` includes, so an
uninstall that saw an older image's live entry names it before any snapshot exists (Copilot's
review of #4438; the previous build counted every such tied install as after the uninstall and left
the module enabled). An older image's own uninstall states no `after`, so at its tick every landing
counts as after it. The first build of this design dropped every same-tick landing silently. The
residue is a tie inside the tie: an event the uninstall did NOT see, landing in its watermark's very
tick with a name sorting below it, reads as seen.

### Compatibility with images already deployed

| | what an image that predates the records does | why it is safe |
|---|---|---|
| reads | enumerates `activation.d/*.json` at the **top level** only | every record lives in a SUBDIRECTORY, and the staged temp ends `.landing.tmp` |
| boots | loads the head the stored `<Name>.json` names | every landing still writes that file — as a projection of the head it derived, only when it changes; `projectionOf` is a field it ignores |
| collects garbage | references the generations the stored entries name | the current GC references every platform's derived generations **and** the stored ones, a superset of what any reader reads |
| installs / uninstalls | writes `<Name>.json` without `projectionOf` | a current image reads that as the older image's own event, at the file's write time, ordered against the records |

A module with **no** landing records and no tombstones reads exactly as before, byte for byte. That
is every module until its first landing or uninstall on an image that writes them, and it is why no
migration pass exists.

### Retention can never change the answer

Without retention, deriving trades a race for unbounded growth. `CollectGarbage` retires a landing
record, a tombstone or a snapshot only when **the entry derived without it is identical for every
PROTECTED platform, both with `<Name>.json` as it is and with it overwritten by a projection** (the
next current-image write does that — a snapshot the live file happens to duplicate is therefore never
retired while it is the only thing that would survive the overwrite) (`PruneLandingRecords`), behind
the same fail-closed read-fault counter and grace window as the generation deletes, and never a record
of a generation the stored entry names. A verdict goes once a newer one for its generation and
platform supersedes it, once no remaining record names its generation, or once its platform is no
longer protected. "Keep the head's and the fallback's
records" would be WRONG: an unversioned landing moves the head whatever it follows, so a later older
shelf landing takes the head from it, and the head then depends on a record that is neither head
nor fallback — retiring it would flip the head with nothing having landed
(`Retention_KeepsARecordTheHeadDependsOn_EvenWhenItIsNeitherHeadNorFallback`). Records before the
newest uninstall change nothing and go; the tombstone itself stays while dropping it would change
the answer (for instance while `<Name>.json` still holds a stale projection).

### Measured

Two `ModuleLandingService` instances — two replicas, each with its own cap-1 pool — over one landing
root, one replica's whole operation run inside the other's window. Against the code before each
change (the same seam applied, nothing else):

| interleaving | before | after |
|---|---|---|
| 1.6.1 interrupted, 1.7.0 lands inside its recording window | head **1.6.1** — the regression | head 1.7.0, fallback 1.6.1 |
| 1.7.0 interrupted, 1.6.1 lands inside its recording window | head 1.7.0, fallback **lost** (`null`) | head 1.7.0, fallback 1.6.1 |
| the new image lands 1.7.0 after the old image measured it unloadable | new image's fallback **1.6.0** (the old image's frozen verdict) | new image 1.7.0, old image 1.6.0 |
| an uninstall inside a landing's projection window | module **enabled** (stale projection) | uninstalled |
| a landing inside an uninstall's projection window | module **disabled** (stale disabled entry) | installed at the new build |
| an older image reinstalls 1.7.0 after an uninstall; a current image shelves 1.6.1 | head **1.6.1**, no fallback — on `main` after #4427 | head 1.7.0, fallback 1.6.1 |
| an older image uninstalls; a current image lands 1.6.0 | head **1.7.0** — the pre-uninstall record, back | head 1.6.0, no fallback |
| an older image adopts 1.5.0 over 1.6.0; a current image shelves 1.4.0 | head **1.6.0** | head 1.5.0 |
| an unreadable tombstone / head record / `<Name>.json` over a stale aggregate row | module **loaded** (uninstalled, or at an older generation) | module dropped and reported; no set proposed |
| four builds measured the module; only the oldest ranks 1.5.0 as its fallback | 1.5.0's bytes **kept** | reclaimed; verdicts of builds outside the protected set retired |
| the aggregate is unreadable and the module has records but no `<Name>.json` of its own | module **derived** from its records, an uninstall **tombstoned** over the partial read | dropped and reported; the uninstall refuses |
| an uninstall stamped in the same tick as the older image's install it observed | module **enabled** | uninstalled |
| `<Name>.json` holds `null`, an entry with no name, or another module's entry | read as **absent**, the module derived from its records (the misaddressed entry surfacing as a module of its own) | corrupt: reported, the module dropped |

The second row is worth noticing: even the order that kept the right head lost the older build's
fallback slot, so the modules GC would have reclaimed it five minutes later.

## The mechanism, at file and line

Measured on `main`, 2026-09-14:

| | |
|---|---|
| `ModuleLandingService.cs:724` | `var landedBefore = ModuleActivationSidecar.Read(baseDirectory, …)` — read **once**, at the start. Every decision below is made against that snapshot: `displaced`, `keepsNewerHead`, `PreviousToKeep`, `ShelfOnlyEntry` |
| between them | the link probe, the staging writes and a `Directory.Move` — seconds of IO on the shared volume |
| `ModuleLandingService.cs:1068` | `ModuleActivationSidecar.WriteEntry(baseDirectory, entry)` |
| `ModuleActivation.cs:673` → `:853` | `WriteAtomic` = write a temp file beside the target, then `File.Move(temp, path, overwrite: true)` — an atomic **replace** that never compares what it replaces |

Two replicas inside that window each decide against a head neither has seen the other move, and the
later `File.Move` wins silently.

### The harm is narrower than "two replicas landing the same module"

🚨 **The same-content case is already benign**, and that is worth stating because it is the common
one. [#3656](https://github.com/Systemorph/MeshWeaver/issues/3656) made the generation leaf
content-addressed — `name@<16 hex of SHA-256 over the bytes>` — so two replicas landing the *same
bundle* compute the same generation, the same entry and the same bytes; the second `File.Move`
writes what is already there. Every replica reconciles the same feed at boot and on every
`ModulePublished` broadcast, so this is the shape that actually happens all day, and it is a no-op.

What remains is **two DIFFERENT contents of one module name landing concurrently on two replicas** —
two publishes seconds apart reaching different replicas. The
[#3996](https://github.com/Systemorph/MeshWeaver/issues/3996) incident (Mail 1.7.0 at 02:49Z, 1.6.1
at 03:00Z) was eleven minutes apart and is fixed by the version rule; this is the concurrent residue
under it.

### 🚨 And the regression PROPAGATES into the mesh module set — #3395 delays it, it does not bound it

This is the fact most likely to be read the wrong way, because boot does **not** load the generation
the record's head names. `MemexConfiguration.cs:280` projects the record onto the mesh's module set
(`ModuleActivationBoot.ProjectOntoMeshSet`), which replaces `entry.Directory` with
`meshSet.Generations[name]` whenever the set names one — deliberately, so that a pod booting
mid-wave cannot pin a torn mix ([#3395](https://github.com/Systemorph/MeshWeaver/issues/3395)).

It is tempting to read that as a bound on this defect. It is not. `ModuleSetStore.Propose`
(`ModuleSet.cs:471`) derives the set it proposes from `GenerationsOf(landed)` — **the activation
record**. So a regressed head is carried into the next proposed set, and from there into every
replica that boots after it. What the projection buys is the interval between the regression and the
next `Propose`, during which boot still loads the set that was live. That is a delay, not a barrier.

## Why "route the write to an owning hub" is not available as stated

The actor-model answer to a lost update is to stop sharing the cell: give the module a mesh node and
let the owning per-node hub serialise `GetMeshNodeStream(path).Update(...)`, which is
cluster-singleton by construction. For the WRITE, that works.

🚨 **It does not work for the READ, and the read is the reason the file exists.**
`ModuleActivationSidecar.Read` is called at `MemexConfiguration.cs:265` — **inside the
`MeshBuilder` configuration**, before the mesh exists. The record decides which assemblies are
installed into the mesh being built, so a design in which the record lives on a mesh node is a
cycle: the mesh cannot be built without the record, and the record cannot be read without the mesh.

So the record stays a file that boot can read with no mesh, and the strongest form of the hub answer
is *"the hub owns the write and projects it into the file"*. That buys cluster-wide serialisation of
the decision, and it costs:

- **a mesh dependency on a service that is a pure file-system service today** (`ModuleLandingService`
  is called from boot reconciliation as well as from the publish endpoint), and
- **a fallback question with no good answer**: a landing that cannot reach the owning hub must either
  fail — a module does not ship — or write the file itself, which is the defect unchanged. A gate
  whose failure mode is "the thing you were guarding happens anyway" is the shape this repository
  refuses elsewhere.

That is the design finding: shape A is not *bigger*, it is *unavailable in the form the issue states
it*, and its available form carries a fallback that reintroduces what it closes.

## Why no small fix exists either

Three shapes were considered and each is a narrowing rather than a fix, which is what
[#4026](https://github.com/Systemorph/MeshWeaver/issues/4026) already says and this page confirms
rather than re-derives:

- **Re-read the entry immediately before the rename** and write the rank-max of the two. The write
  becomes monotone, so a lost update can only lose a *lower*-ranked write — except inside the
  interval between that re-read and the `File.Move`, which is exactly the original race, shorter.
- **A file lock or a lease.** There is no lease primitive on this store, and a hand-rolled async gate
  is forbidden here on separate grounds.
- **An atomic compare-and-swap on the file.** `rename(2)` is atomic but unconditional; the
  conditional primitive (`link(2)`, which fails `EEXIST`) has no .NET surface and no dependable SMB
  semantics. So the store offers no CAS to build on.

## The shape that closes it: DERIVE the head

Stop storing the decision and store the **facts**, one file per landing, never replaced:

```
modules/activation.d/<Name>/<generation>.<landing>.json   one landing's own record
modules/activation.d/<Name>.json                          the legacy per-module record, read as a candidate
modules/activation.json                                   the legacy AGGREGATE, read as candidates too
```

🚨 **`<generation>` alone is NOT a unique path, and assuming it was is the first thing to get wrong.**
The generation is a CONTENT hash (#3656), so two landings of the same bytes deliberately resolve to
the same name — which is exactly the property that makes them idempotent — while their *records* can
differ, because a version LABEL is not part of the content. A rebuild of unchanged source republished
at a higher version is a real case the head rule already handles. So a bare `<generation>.json` is a
shared cell again, one level down, and an ordinary atomic replace there would break the immutability
the whole design rests on. Two answers, and they are not equivalent:

| | |
|---|---|
| **a unique landing key** in the file name (above) | keeps *never replaced* literally true; the reader dedupes by generation and ranks the labels. Costs several small records per generation and a dedup rule |
| **exclusive-create with canonical metadata** — the first landing of these bytes writes the record, later ones leave it | one file per generation, but it **loses a higher version label for identical bytes**, and that is a case the head rule exists to serve |

The unique landing key is the one that keeps the claim; the trade is named here so it is a decision
rather than an oversight. (Raised by Copilot's review of this page.)

🚨 **And `<landing>` has to be collision-free ACROSS REPLICAS, or it is the shared cell again with
extra steps.** A per-replica counter, a sequence number or a timestamp can collide — two replicas
landing in the same second is the normal shape here, not a rarity — and a collision under an
ordinary atomic replace puts the lost update back exactly where it was. The key that needs no
coordination and keeps idempotence is **the content address of the RECORD**:

```
modules/activation.d/<Name>/<generation>.<FULL SHA-256 of the record's canonical bytes>.json
```

Two landings writing the **same** record collide **benignly** — same name, same bytes, a no-op —
which is the property #3656 already relies on one level up; two writing **different** records get
different names and neither is replaced. A random 128-bit id would also be collision-free but is
strictly worse: identical re-landings would each mint a record, so the common idempotent case would
accumulate files for ever.

🚨 **The FULL digest, not the 16-hex truncation the generation leaf uses.** 16 hex is 64 bits, and a
64-bit digest cannot be called collision-free — a collision here would let one replica replace the
other's record and put back exactly the lost update this design removes. The generation leaf can
afford the truncation because a collision there means two different module payloads sharing a
directory, which the bytes' own verification catches; a record file has no such second check. (If a
shorter name is ever wanted, the alternative is explicit: exclusive-create plus a byte-equality
check on collision — write only if absent, and accept an existing file only when its bytes are
identical. That is a rule, not a shorter hash.)

🚨 **And the record that is addressed must be the LANDING's facts only.** The content address is
worth nothing if the content is not a function of the landing, and today's
`ModuleActivationEntry` is not: the four `Previous*` fields are populated from `landedBefore` and
`PreviousToKeep` (`ModuleLandingService.cs:724`, `:872`), i.e. from **whichever head that replica
happened to observe**. Two replicas landing the same bytes at the same version would therefore
write different records, get different names, and accumulate files — the idempotence would be lost
exactly where it is needed.

That is not a flaw to work around; it is the design pointing at itself. `Previous*` **is the
fallback decision**, and the whole point of deriving is that the head and fallback are computed at
read time rather than stored. So a per-landing record carries only the landing's own facts —
`Name`, `Source`, `PackagePath`, `Directory` (the generation), `Version`, `FrameworkMvid`,
`MinMeshVersion`, `SourceCommit` — every one of which is a function of the bytes and the request.
`Enabled` is per-module state and lives in its own marker (item 2 below); `Previous*` is not stored
at all. Those fields carry no timestamp and no per-process value, so the address is stable —
and **adding a non-deterministic field later would silently break it**, which is a rule to write
beside the type rather than a note on a page. (Both halves raised by Copilot's review.)

Two replicas then write disjoint files and nothing is ever replaced, so there is no lost update to
have. It is [#2090](https://github.com/Systemorph/MeshWeaver/issues/2090)'s move — *remove the shared
cell rather than guard it* — one level down: that change split one shared `activation.json` into a
file per module; this one splits a file per module into a file per landing.

### 🚨 The derivation is TWO steps, not one, and the head rule is VERSION-first

An earlier draft of this page said the ranking is *"loadability on this platform first, then
version"* and that it lives in `ModuleActivationSidecar.Read`. **Both halves were wrong, and the
first would roll a registry backwards** — Copilot's review of this page caught it.

- **The HEAD rule is version-first and deliberately platform-blind.** `keepsNewerHead`
  (`ModuleLandingService.cs:861`) compares versions and asserts the head's bytes are present; nothing
  about loading enters it. [Module Adoption Policy](../ModuleAdoptionPolicy) states the reason: *"a
  newer head that does not link on the registry's own platform — the head STAYS"*, because the shelf
  warehouses modules for newer platforms and boot runs the fallback. Ranking the head by loadability
  would take an older loadable shelf entry as the head the moment a newer one did not link here,
  which is #3996 by another road.
- **Loadability-first is the FALLBACK rule** (`KeepsRecordedFallback`), and that distinction must
  survive the derivation intact.
- **And `Read` cannot apply the platform half as it stands.** It takes only a root and a corruption
  callback; the platform surface is built by `ModuleLandingService` from the entries *after* reading.
  So the derivation splits in two: `Read` enumerates and ranks by the **pure** rules (version, bytes
  present, enabled) — where the 82 call sites already are — and a separate, explicitly platform-aware
  step resolves the fallback. That is a real increase on the estimate below, not a rewording.

🚨 **A second property falls out, and it is an improvement rather than a side effect.** Today one
shared file records a fallback chosen by whichever replica happened to write last, including its
*loadability verdict*, measured against that replica's platform. A derived fallback is computed by
each reader against its own platform. Every replica of a deployment runs one image, so the two agree
in the normal case — but the abnormal case (a rolling update, two images live) stops being a shared
file's coin toss.

### What it costs — measured, not estimated

| | measured on `main`, 2026-09-14 |
|---|---|
| `ModuleActivationSidecar.Read(` call sites | **82 occurrences in 19 files** — 5 under `src/` + `memex/`, 14 test files |
| `ModuleActivationSidecar.WriteEntry(` call sites | 6 occurrences in 4 files — **TWO in production**, both in `ModuleLandingService.cs`: the landing at `:1068` and `RemoveCore` at `:1269` |
| `.PreviousDirectory` readers | 27 occurrences in 6 files |

🚨 **That second row read "exactly ONE in production" until Copilot's review, and the error is worth
keeping visible because of its shape**: the census counted FILES and the conclusion was drawn about
CALL SITES, so one file holding two calls was reported as one call. The one it hid is
`RemoveCore` — **the uninstall path**, which is precisely the path item 2 below says needs a
module-level marker of its own. A measurement whose denominator is a different unit from its claim
is the fleet's dominant defect, committed here in a page about measuring.

So the write surface is two call sites and the read surface is one method plus a new
platform-aware resolution step. What is genuinely not local is the rest:

1. **The GC's reference set.** `CollectGarbage` reclaims a generation no entry references. If the
   head is derived FROM the present generations, "unreferenced" stops existing as a concept and
   retention needs an explicit rule instead. Getting that wrong deletes the generation that is
   serving. 🚨 That failure mode is **not hypothetical and not measured here**: the 2026-09-13
   reading on [#4026](https://github.com/Systemorph/MeshWeaver/issues/4026) attributes it to #2509
   and #2303, and it is carried forward as attributed rather than re-derived — check it before
   pricing the rule, not after.
2. **Enabled / uninstalled state is per MODULE, not per generation.** `RemoveModule` needs a marker
   of its own; the existing `.unloadable` / `.refused` / `.tier-refused` marker pattern fits, but it
   is another file and another thing `Read` must union.
3. **A migration for BOTH legacy formats, and they are LAYERED, not ranked.**
   `ModuleActivationSidecar.Read` (`ModuleActivation.cs:308-329`) unions the legacy AGGREGATE
   `modules/activation.json` — still read for deployments that carry one, never written by the
   landing lane — with the per-module `activation.d/<Name>.json`. A derivation that kept only the
   per-module file as a candidate would **boot an older deployment without its stored modules**.

   🚨 **But "both are candidates" is not "both are rankable", and conflating them would reverse an
   uninstall.** The union is a PRECEDENCE, stated in the code: the per-module files are applied LAST
   and win by name, *"an uninstall must beat a stale enabled row"*. Rank a stale enabled aggregate
   row against a disabled per-module record and the disabled one can lose — a module the operator
   removed comes back. So the derivation is layered first and ranked second:

   | layer, lowest precedence first | ranked within the layer? |
   |---|---|
   | the legacy aggregate `modules/activation.json` | no — one row per name, as today |
   | the legacy per-module `activation.d/<Name>.json` | no — it REPLACES the aggregate's row for that name |
   | the per-landing records `activation.d/<Name>/…` | **yes** — this is the only layer the ranking applies to |

   A name with any per-landing record ignores the two legacy layers for that name entirely; a name
   with none keeps today's answer, byte for byte. That is what makes the migration cost nothing and
   need no rewrite pass. (Both halves raised by Copilot's reviews of this page.)

### What does NOT have to change

`ModuleSet.GenerationsOf` reads `ModuleActivationList` and is unaffected: it consumes `Read`'s
answer, so a derived head flows into `Propose` exactly as a stored one does. The boot projection
(`ProjectOntoMeshSet`) is likewise untouched.

## Verdict

The defect was real, reachable only by two different contents of one module landing concurrently on
two replicas, and unbounded once it happened (it propagated into the proposed module set). **Deriving
the head was the only one of the named shapes that adds no new cell two replicas can both write**, and
routing the write to an owning hub was not available in the form the issue stated it, because boot
reads the record before the mesh exists. It is built as described in the *As built* section at the
top of this page: the on-disk record changes shape additively, an
older image reads and writes exactly what it always did, and retention is stated as an invariant
(*it never changes what `Read` answers*) rather than a list of records to keep.

What the post-merge review of #4427 changed: an older image's event is PRESERVED as a snapshot
before its file is overwritten; an unreadable record DROPS its module instead of changing its
answer, and nothing is proposed from such a read; the platforms that protect a fallback are BOUNDED
to the pass's own, the one with none, and the two most recent to measure the module.

What the design section priced and the build settled differently, after Copilot's review of
#4427: enabled state is **not** read from the stored entry's flag — an uninstall is a tombstone
ordered against the landings, because the stored entry is a projection any replica may overwrite
with a stale decision; and loadability is **per platform** (`ModulePlatformVerdict`), because a
single stored verdict froze the first image's answer for every image. The residue is the one a
mutable shared file always has: an image that predates the records reads `<Name>.json` alone,
last-writer-wins, for as long as a mixed-image window lasts.

## See also

- [Module Adoption Policy](../ModuleAdoptionPolicy) — the head rule this page's cell records, and the
  "not covered: two replicas at once" paragraph that points here
- [Module Set Convergence](../ModuleSetConvergence) — the mesh module set the record feeds
- [Modules](../Modules) — the landing lane end to end
