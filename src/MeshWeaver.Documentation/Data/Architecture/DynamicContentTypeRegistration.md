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
`Hosting/Deployment`, `Hosting/DeploymentStatus`, `Store/Subscription`, `PG3Reporting/Fund` — is
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

| instrument | memex.systemorph.com (core `746b4e48`) | memex.meshweaver.cloud (core `026442ff`) |
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
- **A GROWING count is positive evidence, and it is the reading to take.** The ×count only rises
  when a read degrades, so comparing two probes turns the weakest part of this instrument into its
  strongest: measured on memex.meshweaver.cloud, `Hosting/DeploymentStatus` went ×260 and ×257 to
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

**What, per dynamic type**, in path order:

| step | what it reads | what it skips on, named |
|---|---|---|
| already resolvable here? | `IMeshContentTypeRegistry.TryResolveByNodeType` | `AlreadyRegistered` — an instance activated here first |
| a usable build for this framework? | the record alone (`HasUsableBuild` — no store probe) | `NotBaked` — first access compiles it, as before |
| the bytes in this process's store | `IAssemblyStore.TryGetAssemblyPath(path, LastCompiledVersion)` | `BytesMissing` |
| are they the published build? | `ServedBuildIdentity.Mismatch(LatestAssemblyMvid, the file's MVID)` | `StaleBytes` — registering would bind a type family the activations will not use |
| load the configuration | `GetConfigurationsFromExistingAssembly` (reflection over the existing file) | `Faulted` — the recorded build did not load |
| build it once | `ContentTypeRegistration.ProbeRegister` — a transient probe, nothing started | `DeclaresNoContentType` when the build registered nothing |

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

## What this does not establish

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
