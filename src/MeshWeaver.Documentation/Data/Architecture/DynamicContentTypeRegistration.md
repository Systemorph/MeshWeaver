---
Name: Dynamic Content Type Registration
Category: Architecture
Description: A dynamic NodeType's content CLR type registers only as a side effect of one of its instances activating in THIS process — so a type whose few instances live on another replica is untypeable here, with a usable assembly, a clean bake and a clean census. The mechanism, the measurements that separate it from a declined bundle, and why the obvious on-demand fix must not ship. NOT yet closed.
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
3. **Nothing at boot closes the gap.** `DynamicTypePreWarmer`'s adopt-only pass *asks, never
   builds*, and it is the default path for every deployment. Where the compiling sweep does run, a
   type the shared assembly store already holds is reported `AlreadyBaked` and — in the warmer's
   own words — *"never activated"*. That is the normal case for **every replica after the first**,
   and for every replica of a roll onto an identity that is already baked.

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
  its type registers. Only the **×count** is cumulative since boot.
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
DETECT as a fault, and the hazard behind the leaving-hub and per-replica-compile rules. Measured:
with the seam wired that way, `ANodeTypesSourcesWaitForItsBundleTest` failed with *"Expected
AdoptedVerified … but found Compiled"* — a held, correctly-adopted type driven into a compile by
nothing but a read.

**So registration belongs with the component already sanctioned to activate dynamic types on this
replica: the pre-warmer.** Its `AlreadyBaked` branch is where the skip happens, it runs once per
process, it is sequenced after bundle seeding, and it already holds the bake's verdict that the
type has usable bytes here. The shape that fits the two original objections is a **registration-only
pass over the `AlreadyBaked` types, as a paced background trickle AFTER the sweep settles** — off
the readiness path, so the per-NodeType boot cost the content bake removed stays removed, and after
the bake has settled, so no probe meets an adopted-but-not-yet-loadable bundle. That pass covers
every adopted type rather than only the ones somebody read, which also closes the blind spot below.

The cost is real and is a judgement call, not a detail: it is the ~13.5 s of assembly opening that
#1660 removed from boot, moved to a background trickle. That trade wants the maintainer's decision,
which is why this page describes the shape rather than shipping it.

## What this does not establish

- **No fix is shipped and neither issue is closed.** What is established is the mechanism, the
  measurements that separate it from a declined bundle, and that the cheap fix is unsafe.
- **No production verification.** The acceptance measurement for any fix is positive, not an
  absence: on a replica reporting a type under `content-types`, that entry disappears from the next
  `/health` probe while `bake-report` is unchanged, and the type's record still reads
  `buildProvenance: AdoptedVerified` afterwards — a fix that heals by compiling has re-stamped the
  shared record and is the unsafe shape wearing a green tick.
- **The residual population is not bounded.** Every dynamic NodeType this replica adopted rather
  than compiled is unregistered here until something reads one; the measurement can name only the
  ones that HAVE been read. On the control instance that is 229 of 230 types adopted, and 2 named.
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
