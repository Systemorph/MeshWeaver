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
modules/activation.d/<Name>/<generation>.<SHA-256 of the record>.json   one landing, never rewritten
modules/activation.d/<Name>.json                                        the STORED entry, still written
modules/activation.json                                                 the legacy aggregate, read only
```

**A landing record** (`ModuleLandingRecord`) carries only the landing's own facts — `name`,
`source`, `packagePath`, `directory` (the generation), `version`, `frameworkMvid`,
`minMeshVersion`, `sourceCommit` — plus two facts of the REQUEST: `yieldsToNewerHead` (true on the
publish route's shelf, false on the adopt path, because the derivation has to replay each lane's own
head rule) and `reArrivalOf` (below). All of these are hashed, length-prefixed in a fixed order, into
the file name with the **full** SHA-256 (`ModuleActivationSidecar.LandingRecordAddress`). One more
field, `linkableHere` — the landing's own link-probe verdict — is stored but deliberately **outside**
the address, because it is a function of the running platform, not of the bytes. The arrival stamp
is the file's own write time and is never serialized. No `Previous*`, no `Enabled`: those are
decisions and state, not facts of a landing.

**The derivation** (`ModuleActivationSidecar.DeriveEntry`) runs inside `Read`, so the 82 call sites
measured below get it with no change. One candidate per generation (the higher label wins, then the later arrival),
then:

- **the head is a replay** — the candidates folded in arrival order through #3996's predicate, which
  keeps the current head only when the arriving landing is a SHELF landing ranking strictly below it
  (both versions SemVer) and the head's bytes are present. That reproduces what a serialised
  sequence of the same landings would have produced — including the arrival-dependent cases the rule
  deliberately keeps (an equal version is a rebuild and moves the head, an unversioned label moves
  it, an adopt landing always moves it). Every replica folds the same files with the same stamps, so
  every replica reaches the same head.
- **the fallback is a rank** over every other generation — bytes present, then `linkableHere`, then
  the higher version, ties keeping the earlier arrival: `KeepsRecordedFallback`'s rule applied to all
  candidates at once.

**Writing** (`WriteLanding`) is create-if-absent: a name that exists already holds this record. The
create does not have to be atomic — .NET's no-overwrite move is `link(2)` where the file system has
it and an existence check plus `rename(2)` where it does not (a CIFS mount) — because two writers of
one name are writing identical addressed bytes. The temp file is staged in `activation.d/` itself, so
the rename moves that directory's write time, which is the fingerprint
`PendingModuleActivations` memoises the activation read behind.

**A re-arrival** is the one case identical records would get wrong: an adopt landing re-installing
the generation the deployment ran before (the Store's rollback) finds its record already on the
volume, and "already there" must not mean "nothing happened". When the landing would take the head
if it arrived now, it writes a NEW record whose `reArrivalOf` names the latest record with the same
facts (address and stamp) — a new file with its own arrival, never a touch of an existing one, and
still identical across two replicas re-landing over the same state. When it would not (an identical
re-publish of the head, an older shelf upload), nothing is written.

### Compatibility with images already deployed

This code runs on every portal pod, and a rolling update or a rollback puts an older image on the
same volume. The format is chosen so that an older image **cannot tell anything changed**:

| | what an image that predates the records does | why it is safe |
|---|---|---|
| reads | enumerates `activation.d/*.json` at the **top level** only | records live in a SUBDIRECTORY and the staged temp ends `.landing.tmp`, so neither is ever parsed as an entry |
| boots | loads the head the stored `<Name>.json` names | every landing still writes that file — as a **projection** of the head it derived, and only when it differs |
| collects garbage | references the generations the stored entries name | the current GC references **both** the derived and the stored generations, a superset of either — it never deletes what an older replica would boot |
| uninstalls | writes a disabled `<Name>.json` and leaves the records alone | a **disabled stored entry is authoritative** over every record (below) |

The projection is still last-writer-wins between two replicas, exactly as before. For a module that
has records, no current image takes a head from it, so that lost update now reaches only an older
image — which had it anyway.

### The layers are a PRECEDENCE, then a ranking

- **Whether** a module is installed is decided by the stored layers, as before: the aggregate, then
  the per-module file winning by name. A disabled stored entry makes the module disabled whatever
  records exist — *an uninstall must beat a stale landing*, including an uninstall written by an
  older image that never removed the records. So no separate "uninstalled" marker was needed:
  enabled state stays where every image already reads it.
- **Which generation** an enabled module runs is decided by the records. A generation the stored
  entry names that **no record names** joins the derivation as one of the **earliest** arrivals — its
  fallback first, its head second, neither yielding — so a pre-records head, or an older image's
  landing during a roll, stays a candidate, and the first landing on a new image keeps the fallback
  the deployment already had. A generation the stored entry names that a record ALSO names adds
  nothing, which is what stops the last-writer-wins projection leaking back into the answer.
- A module with **no** records reads exactly as before, byte for byte. That is every module until its
  first landing on an image that writes them, and it is why no migration pass exists.

An uninstall (`RemoveModule`) writes the disabled entry first and then removes the module's record
files, so the next landing is a first landing — which is what the documented registry rollback,
*uninstall, then publish the older build*, relies on.

### Retention can never change the answer

Without retention, deriving the head trades a race for unbounded growth. `CollectGarbage` retires a
record only when **the entry derived without it is identical** to the entry derived with it
(`PruneLandingRecords`), behind the same fail-closed read-fault counter and the same grace window as
the generation deletes, and never a record of a generation the stored entry names. "Keep the head's
and the fallback's records" would be WRONG: an unversioned landing moves the head whatever it follows,
so a later older shelf landing takes the head from it, and the head then depends on a record that is
neither head nor fallback — retiring it would flip the head five minutes after the last landing with
nothing having landed. The test `Retention_KeepsARecordTheHeadDependsOn_EvenWhenItIsNeitherHeadNorFallback`
pins that case. Generation directories are still reclaimed only by the existing GC rules; a retired
record's generation is simply no longer referenced.

Reclaiming a displaced generation's bytes cannot change the fold either: a displaced record was
displaced by a landing that is non-yielding, unorderable or not below it, and each of those displaces
whatever is current whether that record's bytes are present or not.

### Measured

`ConcurrentModuleLandingTest` runs two `ModuleLandingService` instances — two replicas, each with its
own cap-1 pool — over one landing root, and runs one replica's whole landing inside the other's
recording window (after its bytes land, before it records). Against the code before this change:

| interleaving | before | after |
|---|---|---|
| 1.6.1 interrupted, 1.7.0 lands inside the window | head **1.6.1** — the regression | head 1.7.0, fallback 1.6.1 |
| 1.7.0 interrupted, 1.6.1 lands inside the window | head 1.7.0, fallback **lost** (`null`) | head 1.7.0, fallback 1.6.1 |

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

What the design section priced and the build settled differently: the enabled state did **not** need
a marker of its own — it stays in the stored entry, which every image already reads, and its disabled
state is authoritative over the records; and the fallback's loadability is the landing's own recorded
verdict (`linkableHere`), so `Read` stays platform-blind and boot reads no assembly metadata. The
residue is the one a platform-dependent fact always has: during a rolling update two images can
disagree about `linkableHere` for one record, which can reorder the fallback for the length of the
roll and never moves the head.

## See also

- [Module Adoption Policy](../ModuleAdoptionPolicy) — the head rule this page's cell records, and the
  "not covered: two replicas at once" paragraph that points here
- [Module Set Convergence](../ModuleSetConvergence) — the mesh module set the record feeds
- [Modules](../Modules) — the landing lane end to end
