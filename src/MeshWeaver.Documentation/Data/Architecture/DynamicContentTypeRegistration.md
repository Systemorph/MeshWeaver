---
Name: Dynamic Content Type Registration
Category: Architecture
Description: A dynamic NodeType's content CLR type registers only as a side effect of one of its instances activating in THIS process — so a type whose few instances live on another replica is untypeable here, with a usable assembly, a clean bake and a clean census. The mechanism, the measurements that separate it from a declined bundle, why the obvious on-demand fix must not ship, and the paced registration-only pass that closes it off the readiness path.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 17.5h7"/><path d="M17.5 14v7"/></svg>
---

# Dynamic Content Type Registration

**A replica can hold a NodeType's assembly, count it baked, report a clean census — and still be
unable to type a single node of it.** Not because the bundle was declined, not because the bytes
are missing, but because nothing in that process ever *registered* the CLR type. This page is
about the third cause, which is the common one, and which no instrument named until now.

## The mechanism

A dynamic NodeType — in-mesh `Source/*.cs`, compiled by Roslyn at runtime — declares its content
type inside its own hub configuration. The CLR type enters the process-wide
`IMeshContentTypeRegistry` as a **side effect of that configuration being built**
(`MeshDataSource.WithContentType`), and the configuration is built when a **per-node hub
cold-activates**. Three consequences follow, and none of them is obvious from any one of them:

1. **A per-node hub is a single activation cluster-wide.** A node's hub lives on exactly one
   silo. Building it registers the type *there*.
2. **Every other replica reads the same nodes through its own `MeshNodeStreamCache`**, receives
   the payload as JSON, asks its own registry for the `$type`, and is told nothing. The content
   stays a bare `JsonElement`.
3. **The boot passes did not close the gap.** `DynamicTypePreWarmer`'s adopt-only pass *asks, never
   builds*, and it is the default path for every deployment. Where the compiling sweep does run, a
   type the shared assembly store already holds is reported `AlreadyBaked` and — in the warmer's
   own words — *"never activated"*. That is the normal case for **every replica after the first**,
   and for every replica of a roll onto an identity that is already baked. The
   registration-only pass described below now runs after them.

So the exposure is precisely: **a dynamic NodeType with FEW instances, all of whose instances
happen to activate elsewhere.** A type with many instances gets activated on most replicas sooner
or later and registers itself. A type with zero instances has nothing to read. A type with four —
`Hosting/Deployment`, `Hosting/DeploymentStatus`, `Store/Subscription`, `InitechReporting/Fund` — is
the one that breaks, and it breaks differently on each replica.

### The premise that was false

`ContentTypeRegistrationSweep` registers static definitions at boot and deliberately excludes
dynamic ones, for two good reasons (the per-NodeType boot cost the CI content bake removed, and
the loader's corrupt-file self-heal tripping on an adopted-but-not-yet-loadable bake) and one
that does not hold:

> A dynamic type registers the moment an instance hub activates — and a dynamic type with **zero
> instances** has no payload carrying its discriminator, so there is nothing to degrade.

True for zero. False for few. The sentence quietly assumes one process.

## What it looks like from outside

It looks like a declined bundle, and it is not one. `content-types` on `/health` says:

> N node type(s) whose content this replica cannot type — the module that declares the type is not
> loaded here (**its prebuilt bundle was declined or its compiled assembly is not on this
> replica**), so their pages render empty

Both stated causes are real, and neither was what was measured. The discriminating readings, taken
on two live portals on 2026-09-21:

| instrument | the control instance (core `746b4e48`) | the public instance (core `026442ff`) |
|---|---|---|
| `bake-report` | **Healthy** — 229 of 230 types carry a usable assembly | Healthy — 242–302 of 366 with a verdict, all usable |
| the ONE / THREE types with **no** usable assembly | `BinaryClickerV2` (CompileError) | 3 × CompileError, all in partition `MeshWeaver` |
| live record census | **clean** — 0 untyped, 0 foreign, 230 of 230 | clean on each replica sampled |
| `content-types` | `Hosting/DeploymentStatus ×30`, `Store/Plugin ×8` | 8–11 types per replica, **a different set on each**, incl. `Hosting/Deployment` |

The provenance of a named type closes it: `Store/Plugin` on the control instance reads
`compilationStatus: Ok`, `compiledFrameworkVersion: se271838…` (the live identity) and
**`buildProvenance: AdoptedVerified`** — adopted from the share for exactly this framework, so the
pre-warm reported it `AlreadyBaked` and never activated it. The assembly is verified present and
the type is unregistered: those are not in tension, they are the same sentence read twice.

🚨 **The two sets are DISJOINT.** The types with no usable assembly are not the types that cannot
be typed, and the types that cannot be typed all have usable assemblies. That single comparison
falsifies both stated causes and is the cheapest way to recognise this failure — it needs one
`/health` body and no cluster access.

### Reading the instrument correctly

- **An entry's PRESENCE is a live verdict, not history.** `ContentTypeHealthCheck` calls
  `ContentDegradationRegistry.Unresolved`, which re-asks the registry per entry at probe time
  (both routes: the NodeType path and the stored `$type`). A boot-race entry disappears the moment
  its type registers, and `Clear` removes one the moment a read of that type succeeds. Only the
  **×count** is cumulative since boot. Measured: over 2.7 h on one deployment the named set
  CHANGED and members left it — which a counter that never decayed could not do.
- **Each entry prints its WINDOW** — `Store/Tier ×377 between 2026-09-21T07:45:16Z and
  2026-09-21T07:47:04Z (last Admin/Tiers/free)`: the instants its first and its last counted read
  degraded ([Plugins#2812](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2812)). Without it
  the bare `×377` read as "failing now" when every one of those reads fell in the two minutes after
  boot. A window whose end is long past is a type that is STILL unregistered here (the presence is
  re-checked) but that nothing has read since — a latent gap, not reads failing now. A window whose
  end is the last few seconds is reads degrading NOW. One probe tells them apart.
- **A GROWING count is positive evidence, and it is the reading to take.** The ×count only rises
  when a read degrades, so comparing two probes turns the weakest part of this instrument into its
  strongest: measured on the public instance, `Hosting/DeploymentStatus` went ×260 and ×257 to
  ×373 and ×376 across 2.7 h. Those reads are degrading NOW. A static count across two probes says
  the opposite — nothing has read that type since — and is the case where the entry may be a boot
  residue the registry has simply never been asked to clear.
- **An entry's ABSENCE is not a clean bill.** A degradation is recorded only when a READ degrades.
  A type nobody has opened on this replica appears in no list, however unregistered it is.
- **Repeated `/health` calls sample different replicas.** Four calls returned four different sets;
  that variation *is* the finding, not noise. The per-replica form with no guesswork is the
  control instance's `Sample` action, but it truncates each health body — the untruncated bodies
  come from calling the public `/health` several times.

## The blast radius — and the consumer it does NOT reach

**Everything that reads a node's content through `MeshNodeStreamCache` and then asks for the CLR
type is affected**: a view renders empty, `Content is X` / `as X` yields null, and a reactive wait
on a typed shape never completes. There is no exception and no log line naming a cause beyond the
degradation warning itself.

🚨 **It does NOT reach a consumer that uses `ContentAs<T>` with a statically-known `T`, and
getting that wrong is easy.** `ContentAs<T>` deserialises to the type the CALLER names; it never
consults the mesh-wide registry, which is exactly why it is the mandated accessor. So a lane that
reads a record this way is immune to this defect even on a replica that cannot type the same node
for a view.

That distinction was worth an issue comment to retract. `SelfUpdateRouting` answering *"no
`Hosting/Deployment` record … lists 0"*
([Plugins#2178](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2178), a 24-hour CD stall)
reads exactly like this defect and is NOT it, twice over: its lookup folds over INDEX ROWS
(`MatchRecord` on path/id, no content), and `DeploymentContent` is a framework type shipped in the
image, reached by reference. That zero was core#4958 — a zero with two causes, "none exist" and "I
could not see them", treated as one — and the lane now answers `RecordVerdict.Unseen` at Error
instead of concluding absence.

The lesson generalises: **a silent empty is not evidence of this defect.** Before attributing one
here, check whether the consumer names its type statically; if it does, look elsewhere.

🚨 **A restart does not fix it and neither does a recycle.** A fresh activation re-reads, but what
it *finds* is the same shared store and the same skip: `AlreadyBaked`, never activated, never
registered. Rolling forward "fixes" it only by accident — a new framework identity makes the
prebuilts stale, which forces a local compile, and a local compile activates the hub and registers
the type. That is why the remedy looked like a build-artifact remedy and why it kept coming back.
See [Stale State Until Recycle](../StaleStateUntilRecycle) for the general rule this is an
instance of, and for why a second recycle proves nothing the first did not.

## Closing it — and the fix that must NOT be shipped

🚨 **The obvious fix is unsafe, and this is the most useful thing on this page.** The tempting
move is to call the existing `ContentTypeRegistration.ProbeRegister` from the degrade seam: the
place that already *reports* the gap would *close* it, on demand, once per type, at zero boot cost.
That was built and it works — and it must not ship, because of what obtaining the configuration
costs.

Getting a dynamic type's hub configuration means `IMeshNodeHubFactory.ResolveHubConfiguration` →
`NodeTypeEnrichmentHelpers.EnrichWithNodeType`, and that path is **not a read**:

- it is handed the `IMeshNodeCompilationService` and **triggers the compilation chain** when the
  type's sources are ahead of its build;
- it **writes the shared NodeType record** — the stale-`Ok`-with-null-assembly self-heal flips the
  record to `Pending` to force a recompile;
- it **arms the rebind watcher** for that address.

A NodeType record is ONE row the whole deployment shares. Letting a read seam take that path means
every non-owning replica that happens to read a node independently compiles and re-stamps the
record — which is precisely the mid-roll cross-stamp `NodeTypeLiveRecordCensus` was built to
DETECT as a fault, and the hazard behind the leaving-hub and per-replica-compile rules. **That
argument is a reading of the code, and it is the whole case.**

🚨 **It was nearly propped up with a measurement that does not hold, which is worth recording.**
With the seam wired, `ANodeTypesSourcesWaitForItsBundleTest` failed *"Expected AdoptedVerified …
but found Compiled"* while clean `main` ran 861/861 green — the exact shape the hazard predicts, and
it read as proof. It is not: the same test failed again on a tree with the seam REMOVED and the
same 862-test assembly as `main`, so the diff could not reach it. One green control run is not a
control. The test is intermittent, the attribution is withdrawn, and the failure is filed on its
own — an assertion that an ADOPTED type is never driven through a COMPILE, failing intermittently,
is either a test-isolation defect or the very race this page is about, and guessing which is how a
wrong root cause gets published.

**So registration belongs with the component already sanctioned to activate dynamic types on this
replica: the pre-warmer.** Its `AlreadyBaked` branch is where the skip happens, it runs once per
process, it is sequenced after bundle seeding, and it already holds the bake's verdict that the
type has usable bytes here.

## The registration-only pass

The shape approved for the fix (policy
[`dynamic-content-type-registration-pass`](../PolicyNotProse)) is a **paced, registration-only pass
over the already-baked dynamic types, after the bake settles, off the readiness path.** It is
`DynamicContentTypeRegistrar`, run once per process by `DynamicContentTypeRegistrationHostedService`,
which `AddDynamicTypePreWarming` registers beside the pre-warmer.

**When.** After `PreWarmCompletion` settles — any settlement: completed, faulted or not applicable
all mean the compile queue has drained and the adopted bundles have landed. So no probe meets an
adopted-but-not-yet-loadable bundle. Nothing gates on the pass: a replica serves while it runs.

**What, per dynamic type**, in path order — except that the types a read on this replica has
already degraded go first (`DynamicContentTypeRegistrar.OrderForRegistration`): their readers are
waiting on exactly this registration, every other type is registered for a read that may never come.

| step | what it reads | what it skips on, named |
|---|---|---|
| already resolvable here? | `IMeshContentTypeRegistry.TryResolveByNodeType` | `AlreadyRegistered` — an instance activated here first |
| a usable build for this framework? | the record alone (`HasUsableBuild` — no store probe) | `NotBaked` — first access compiles it, as before |
| the bytes in this process's store | `IAssemblyStore.TryGetAssemblyPath(path, LastCompiledVersion)` | `BytesMissing` |
| are they the published build? | `ServedBuildIdentity.Mismatch(LatestAssemblyMvid, the file's MVID)` | `StaleBytes` — registering would bind a type family the activations will not use |
| load the configuration | `GetConfigurationsFromExistingAssembly` (reflection over the existing file) | `Faulted` — the recorded build did not load |
| build it once | `ContentTypeRegistration.ProbeRegister` — a transient probe, nothing started | `Faulted` when the probe's configuration threw; `DeclaresNoContentType` only when the build ran and registered nothing |

That is the enrichment hot path with every write removed. **No compile is driven, no NodeType record
is written** (no stale-`Ok` self-heal, no `Pending` flip) and no rebind watcher is armed. That is why
it may run on every replica, where a read seam must not.

**Pacing.** Each assembly load is blocking file I/O plus reflection, so it runs through the
`FileSystem` `IIoPool`. The types run one at a time (`Concat`), with a pause between two types that
each did real work: `PreWarm:RegistrationBetweenTypes`, default 200 ms. Skips pause for nothing. The
~13.5 s of assembly opening #1660 removed from boot is paid here instead, in the background, spread
over the pass. `PreWarm:RegisterContentTypes=false` turns the pass off.

**What it says.** One Information line per pass with the count per outcome and the elapsed time. One
Warning names every type that stayed unregistered (`BytesMissing`, `StaleBytes`, `Faulted`) and why:
those are the types whose content can still render empty on this replica.

**Pinned by** `ABakedTypeRegistersWithoutAnInstanceTest` (MeshWeaver.Hosting.Test). The batch bake
compiles a type with a content type **without activating a hub**, which is the `AlreadyBaked` replica
state, and the control asserts the registry does not know it. The pass then registers it from the
existing bytes and leaves the record's build stamp unchanged. A type with no build is skipped and not
compiled. With the probe call removed, the first case fails (`DeclaresNoContentType`).

🚨 **A probe that throws is `Faulted`, never `DeclaresNoContentType`.** The probe used to build through
`GetHostedHub`, which answers a configuration that throws with a null hub and no exception, and the
probe also caught anything that did escape, at Debug. Either way the pass then asked the registry,
found nothing, and filed the type under `DeclaresNoContentType`. That is the one outcome the Warning
does not name, so its content stayed untyped on this replica and no production line said why. The
probe now builds through `TryGetHostedHub` and returns the fault to its caller. The pass files it as
`Faulted`, and the static sweep names it at Error. Pinned by
`AFaultedRegistrationProbeIsNotADeclarationTest`, which fails on the old probe with
`found DeclaresNoContentType`.

## The boot registration window

The pass cannot start before the bake barrier, and the readers do not wait for it. One boot, timed
end to end (memex-cloud pod `884964bb7-6gv59`, 2026-10-09, read through governed `Logs` actions):

| instant (UTC) | event |
|---|---|
| 19:48:37.3 | 132 of the boot's 134 "stayed an untyped JsonElement" lines: a `Posts` query and the standing watches on `Ops/Status/*` and `Ops/Watch/fleet-watch` |
| 19:48:37.8 | `DynamicTypePreWarmer: starting background warm-up` — half a second *after* those reads |
| 19:50:21 | warm-up complete: `compiled=0 alreadyBaked=414` |
| 19:52:50 | the registration-only pass is done |

So the burst had nothing to do with compiling, and registering each type "as the pre-warmer finds it
already built" cannot remove it either: the reads come before the pre-warmer starts. What the line
asserted at the read — *"consumers will fail"* — was not yet decidable. The pass registered those
types minutes later. A `GetMeshNodeStream` reader then re-typed by itself (the late re-type), but a
**query** reader did not: a query has no late re-type, so a result set read in the window stayed
untyped until something changed it. Deferring the warning (below) only re-timed the verdict; the
read itself is fixed in the section "A read never waits for the pass" below.

**The window.** `DynamicContentTypeRegistrationHostedService` opens it when it is constructed. The
host resolves every hosted service before it starts any, so the window is open before the first
boot reader. While it is open, the two read seams in `MeshNodeStreamCache` still **record** every
degraded read in `ContentDegradationRegistry`, so `/health`'s `content-types` names it exactly as
before. They do not log the warning. When the pass ends (completed, faulted, or switched off),
`ContentDegradationRegistry.SettleDeferredWarnings` closes the window and returns every recorded type
that is **still** untyped. The service writes one warning per such type, with the same wording, the
same `MeshNodeContentDegradedException` marker, and the count and window of the reads it stands for.
A type the pass registered is never warned, because its readers were cured. A type it did not
register is warned once, at the moment that is known. Nothing is dropped and nothing is doubled: a
seam records a read and learns whether its warning is deferred in one step
(`RecordDeferringWarning`), under the same gate as the settle's close-and-snapshot. A read that races
the close is therefore either in the snapshot or warned by the seam, never both and never neither.
The settled line names the seam that read the path it names (`ContentDegradation.LastSeam`), and the
first seam beside it.

A host that does not run the pass never opens the window, and every read there warns at the read, as
before. That includes every test host, so the CI untyped-content gate is unaffected.

**Pinned by** `BootRegistrationWindowDefersUntypedWarningsTest` (MeshWeaver.Hosting.Test). Its
negative control is the same read with no window, which warns at the read.

**Acceptance on a portal:** after the roll that carries this, the boot-window count of
`|~ "(?i)stayed an untyped JsonElement"` per replica drops to the types the pass could not register,
which are the ones its own Warning names. The pre-change count was ≈130 per boot on memex-cloud and
the same shape on memex.

## A read never waits for the pass

The window above stops the warning from claiming a verdict too early. It does not change what the
reader gets. The readers in the burst are the process's **own** boot work: a `Posts` query, the
standing watches on `Ops/Status/*`, the install records (`*/_Install/*`) read by the default-install
pass. They run before `ApplicationStarted`, so holding readiness back cannot help them: readiness
gates traffic, not hosted services. The pass, for its part, cannot move ahead of the bake barrier
without becoming a 414-type boot cost again (#1660). So the read seams register the ONE type they
are reading, on demand.

**`ContentTypeOnDemandRegistration`** (MeshWeaver.Hosting, registered by `AddDynamicTypePreWarming`).
When `MeshNodeStreamCache.GetStream(path, options)` or `GetQuery(id, options, …)` meets content that
stays untyped and names a NodeType, the emission **waits** for `EnsureRegistered(nodeType)` and is
typed after it:

1. read the NodeType's record from the STORE, the authority (never a query, whose stale or incomplete snapshot would be cached as a verdict; as system: infrastructure, not a user read; bounded by
   `RecordReadBudget`, the same 30 s budget as the pass's enumeration);
2. run `DynamicContentTypeRegistrar.RegisterType`, which is the pass's own route for one type:
   `AlreadyRegistered` / `NotBaked` from the record, the identity-checked bytes from the store
   (`ShippedBuildRefetch` when this replica lacks them), `GetConfigurationsFromExistingAssembly`,
   one transient `ProbeRegister`.

**No compile and no record write.** That is why it may run on a read where the enrichment path
must not (the section "Closing it" above still holds word for word).
A type with no usable build answers `NotBaked` at once and the read degrades exactly as before:
compiling stays the first activation's job.

**Why the barrier is not needed here.** The pass waited for the barrier so that no probe would meet
an adopted-but-not-yet-loadable file and trip the loader's bad-image delete. Store writes are now
atomic (temp file + rename, MeshWeaver#1387), and the probe loads only bytes whose MVID **is** the
record's published build. Anything else is `StaleBytes` or `BytesMissing`, and nothing is loaded.

**Bounded.** One attempt per type per process, shared by every concurrent reader through an instance
`PromiseCache`. The load and the probe run on the `FileSystem` IO pool. A verdict is kept for the
process; the other two routes (activation, the pass) still register a type this one could not.
Only `Faulted` is released, because it is a failure of this replica rather than a verdict about the
type, so the next read asks again. Order is preserved (`Concat`): an emission never overtakes one
that is waiting, and content that types cleanly is answered at once. Each attempt logs one line
(Information, or Warning when it faulted) with its outcome and elapsed time.

A host without the service (every test host that does not add it) keeps the old behaviour: the seam
degrades, and `GetMeshNodeStream`'s late re-type waits for a registration. The deferred warning above
stays as the diagnostic for whatever is still untyped after this route and the pass.

**Pinned by** `AReadAtBootIsTypedNotUntypedTest` (MeshWeaver.Hosting.Test). A type is baked without
activating a hub, and its instance is written straight to the store, so nothing registers it as a
side effect. The negative control is the same read without the service (current `main` before this
change): it answers an untyped `JsonElement`. With the service, the real cache's query and the
stream seam answer typed on the **first** emission, and the record's build stamp has not moved. A
type with no build answers `NotBaked` and compiles nothing. With the seam's wiring removed, the query
case fails with *"Did not expect value to be of type JsonElement"*.

**Acceptance on a portal:** after the roll that carries this, the boot-window count of
`|~ "(?i)stayed an untyped JsonElement"` per replica is about 0. What remains is the types this route
could not register, which its own Warning lines name, plus the deferred settle lines for the same
types. Each boot also logs one `ContentTypeOnDemandRegistration: <type> → Registered` line per type a
boot reader needed.

## `StaleBytes` on every boot: a record that names a build the store does not hold

After the two fixes above, the `memex` deployment still logged this boot after boot:

> `DynamicContentTypeRegistration: 15 baked dynamic NodeType(s) could NOT be registered on this replica — their content stays untyped here until an instance activates here`

Each of the fifteen was `StaleBytes`: the record's `LatestAssemblyMvid` was not the MVID of the file
the store held under the record's version. What the governed `Logs` reads established
(`Ops/Actions/w14c-memex-*` on the control instance, 2026-10-10):

| reading | value |
|---|---|
| boots that logged the line | 13, across five consecutive generations, in a 240 min window |
| the types | the same fifteen, with the same pair of MVIDs each, in two lines read in full: 16:53Z and 20:43Z, four generations apart |
| rebuilt in between? | no: an unchanged pair means neither the record nor the store's file moved in those four hours |
| effect on a reader | `content for nodeType … was NEVER resolvable on this replica — 2 read(s) degraded` for one of them |

So it is not a roll race and not a previous image's bake. It is a **standing record**: version, path
and MVID are one reference, and these records carry a version and a path that resolve to build N
with the MVID of a build N+1. That is what the first-write-wins store produced when a type was
recompiled, or a bundle adopted, at an unchanged node version: the store kept N's file and handed N's path back, and the
caller stamped the MVID of the bytes it had in hand
([Stale State Until Recycle](../StaleStateUntilRecycle), `BundleUpdateTest`). The store is
content-addressed now, so no new record gets into this state.

**Why nothing healed the old ones.** Three components asked three different questions about the
same record:

| component | question | answer for such a record |
|---|---|---|
| activation (bind-time check) | are these the bytes the record names? | no — recompile, per instance, and only if an instance activates |
| the registration-only pass, and the on-demand route | are these the bytes the record names? | no — `StaleBytes`, nothing registered |
| the boot sweep's bake probe (`NodeTypeBakeStatus.ProbeOne`) | does the store hold *a* file under `(path, LastCompiledVersion)`? | yes — `Baked` |

The sweep is the only pass that rebuilds without an instance, and it takes its work list from the
probe. A type with few instances, read through queries and never activated, therefore stayed
untypeable on every replica, on every boot, under a bake report that counted it baked.

**The fix is at the probe.** When a record claims a build for the live framework and states an MVID,
the probe resolves by identity (`IAssemblyStore.TryGetBuildPath` with the record's content path and
MVID) and compares the MVID of the file it got. A different build under the record's version is
`BytesMissing`, with a detail that names both MVIDs. The sweep then does what it does for any store
miss: re-fetch the shipped build if a source has exactly that MVID, else rebuild on the owner. The
rebuild stores its bytes and stamps version, path and MVID from that one upload, so the record is
coherent, the next boot's probe reads `Baked`, and the pass registers the type. Two boundaries are
kept on purpose:

- A record that names **another framework** keeps the key-only question. "Bytes win over the record"
  is about a live-framework build sitting under a record whose write-back lagged; that build is by
  construction not the one such a record names.
- An MVID that **cannot be read** (a store that hands out no local file) is not a mismatch.
- The per-type store read (the lookup and the MVID read) is blocking file I/O on a network share. The
  in-process callers run it through the mesh's `FileSystem` I/O pool
  (`NodeTypeBakeStatus.ProbeThrough`), the pool the registration pass reads the same files through,
  or `IoPool.Unbounded` on a mesh with no pool registry. The public `Probe` keeps its signature and
  reads inline, for a caller with no mesh.
- An entry whose foreign bytes sit **at the record's own content path** is **never a regression
  baseline** (`NodeTypeBakeEntry.RecordNamesABuildTheStoreLacks`, read by `IsRegressionBaselineFor`).
  That is the residue. A record whose own file is *gone*, with a sibling of the version answering
  for it, named a working build and lost it: it is an ordinary store miss and keeps its baseline
  (`AForeignSiblingIsNotAlwaysTheLegacyResidueTest`). The build the record names was available to no replica, so a
  rebuild that fails takes nothing away. It is reported and stamped like any failed compile, and it
  does not refuse the new replica's readiness. Without this, one long-incoherent record whose source
  no longer compiles would stall the first roll that looked at it.

The pass and the read route stay non-compiling and non-writing. Nothing was added to them.

**Pinned by** `RecordNamesABuildTheStoreLacksTest` (MeshWeaver.Updates.Test). It compiles a type,
stamps its record with an MVID no file carries, and asserts the pass's `StaleBytes` (the symptom),
the probe's `BytesMissing`, the sweep's rebuild, the coherent record after it, and that the pass no
longer answers `StaleBytes`. Its control is the same probe on the untouched record: `Baked`. The pooled
read is pinned by `BakeProbeReadsTheStoreThroughThePoolTest`.

**Acceptance on a portal:** on the first boot that carries this, one
`DynamicTypePreWarmer: N NodeType(s) claim a usable build … Rebuilding:` Warning names the fifteen
with both MVIDs. On every boot after it, the `could NOT be registered` Warning no longer names them.
A type that is named on a *second* boot was re-broken by something live, and that is a new finding.

## A registration can end

The mesh-wide registry holds types from collectible load contexts, and a context is unloaded when
its hub is disposed (`MeshDataSource` → `ICompilationCacheService.UnloadNodeContexts`) or a newer
build supersedes it. `AssemblyLoadContext.Unload()` only **starts** an unload: `Unloading` fires
while the generation is fully loaded, and it stays loaded for as long as anything holds one of its
objects. The registry used to drop the generation's entries at that instant.

Measured on an outgoing memex-cloud pod (`6dc5db759b-786n6`, booted 19:51Z, 2026-10-10,
`Ops/Actions/w14c-cloud-786n6-*` on the control instance):

| instant (UTC) | event |
|---|---|
| 20:34:49 | another replica of its generation stops |
| 20:34:53 – 20:36:03 | fifteen `GetStream: Content for Ops/Status/… stayed an untyped JsonElement` lines, three rounds over five nodes |
| 20:36:10 | `Application is shutting down...` on this pod |
| 20:36:11 | `giving up on owner Hosting/DeploymentStatus — 4 DISTINCT owner activations have refused this stream`, and `content for nodeType Hosting/DeploymentStatus was NEVER resolvable on this replica — 15 read(s) degraded` |

The degraded-read count is exactly the fifteen lines, so the pod typed every earlier read of that
type, for 45 minutes. It lost the type before its own host began to stop, while the type's owner
address was being re-activated and torn down again. No successor registered on that pod. The
on-demand route could not put the type back either: its verdict is kept for the process, and the
kept verdict said `Registered`.

**The registry demotes instead of dropping** (`MeshContentTypeRegistry`). On `Unloading`, a
generation's entries leave the strong maps and enter a weak shadow, the same shape `TypeRegistry`
has had since MeshWeaver#1169. A demoted type answers for as long as something else keeps its
generation alive, roots nothing, never answers for a contested discriminator, and is displaced by
the first successor registration. Once the generation is collected the entry is dead and the lookup
says "unknown", as before. Registration and eviction share one short
lock, so an unload cannot begin between a type's strong inserts and its `Unloading` subscription. A
type that registers after its context began unloading goes to the shadow directly: the event fires
once, so a strong entry would never be evicted. The eviction runs wherever the unload is initiated,
which can be the finalizer thread after the registry itself became unreachable, so it reads nothing
finalizable. The "already unloading" marker is a long weak reference in an immutable list, not a
`ConditionalWeakTable`, whose container can be finalized first. A lookup that finds the type alive hands out a strong reference, so a demoted type
that keeps being read stays loaded until a successor registers: at most one superseded generation
per name.

**Pinned by** `ContentTypeRegistryReleasesAnUnloadedGenerationTest` (MeshWeaver.Compiler.Pipeline.Test:
a type whose context began unloading still resolves while an object of it is held; a successor
displaces it; a late registration is never rooted; a context that is finalized instead of unloaded
does not fault the finalizer; a collected generation is released, the pre-existing case) and
`AReadAfterTheTypesHubLeftIsStillTypedTest` (MeshWeaver.Graph.Test: a baked type is registered
through the read route, its contexts are unloaded the way a hub's disposal unloads them, and the
next read is typed).

**Acceptance on a portal:** during a roll, the outgoing replicas log no
`GetStream: Content for Ops/Status/… stayed an untyped JsonElement` line between the first new
replica's boot and their own shutdown line.

## What this does not establish

- **The fifteen records themselves were not read.** The MCP connection to the control instance's
  own mesh was down while this was written, so the record fields (`lastCompiledVersion`,
  `latestAssemblyPath`, `lastCompileSucceededAt`) are inferred from the log line's two MVIDs staying
  the same across four generations. The store's own Information lines (`Cached assembly at …`,
  `no file … carries the published MVID`) are not shipped to the log store, so their absence from a
  `Logs` read says nothing. That they date from the first-write-wins store is the
  explanation that fits every reading; their stamp dates would confirm it.
- **What tears the hubs down on an outgoing replica** was not traced past the owner-refusal line. The
  registry change does not depend on it.
- **A registration that ended because its generation was COLLECTED is not re-established by a
  read.** `ContentTypeOnDemandRegistration` keeps its verdict for the process, so a kept
  `Registered` is replayed over a registry that no longer answers. Releasing a lapsed verdict was
  written and withdrawn: a test mesh could not be brought to collect the generation, so the state
  could not be reproduced and the change could not be shown to fix it.
- **The pass is shipped; production verification is not.** What is established is the mechanism,
  the measurements that separate it from a declined bundle, that the cheap fix is unsafe, and — in
  a test mesh — that the pass registers a baked type without compiling or writing.
- **No production verification of the pass.** The acceptance measurement for any fix is positive, not an
  absence: on a replica reporting a type under `content-types`, that entry disappears from the next
  `/health` probe while `bake-report` is unchanged, and the type's record still reads
  `buildProvenance: AdoptedVerified` afterwards — a fix that heals by compiling has re-stamped the
  shared record and is the unsafe shape wearing a green tick.
- **The residual population, after the pass, is the pass's own Warning line**: the types whose
  bytes are missing, stale or unloadable on this replica. Before the pass it was unbounded — on the
  control instance 229 of 230 types were adopted and only the 2 that had been read were named.
- **The time the pass takes on a live replica** was not measured. The ~13.5 s figure is #1660's
  boot measurement of opening every assembly; the pass adds its pacing on top of that.
- **Whether a type also fails to register after a genuine local compile** was not tested; every
  case measured was an adoption.

## See also

- [NodeType Compilation](../NodeTypeCompilation) — how an in-mesh type becomes an assembly.
- [Module Build Architecture](../ModuleBuildArchitecture) — the bake, the share, and why a second
  replica finds the first one's work.
- [A Content Verdict Is Per Node](../AContentVerdictIsPerNode) — the neighbouring rule about what
  a per-node verdict can and cannot be read to cover.
- [A Census That Counts Must Name](../ACensusThatCountsMustName) — why a count without a
  denominator is not a measurement.
- [Operating From The Portal](../OperatingFromThePortal) — where `/health`, `Sample` and the
  per-replica readings come from.
