---
Name: Build Identity Admission
Category: Architecture
Description: A process must refuse a compiled assembly keyed to a framework build identity that is not its own — and compilationStatus must not read Ok for a type whose hubs cannot activate. The adoption half of the 2026-09-06 outage.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 4 6v6c0 4.5 3.2 8.3 8 9 4.8-.7 8-4.5 8-9V6z"/><path d="m9 12 2 2 4-4"/></svg>
---

> **"a NodeType's self-report is not proof"** — AGENTS.md, written before the day it was proved.

[Mesh Admission](/Doc/Architecture/MeshAdmission) is the **membership** half of the 2026-09-06
outage: a pod its readiness gate had refused kept running and kept publishing. This is the
**adoption** half — what a healthy process must refuse to *load*, and what it may honestly *report*
about a build it cannot load. The two compose; neither subsumes the other
([#3472](https://github.com/Systemorph/MeshWeaver/issues/3472)).

## What happened

`Crm/Offer` and `Crm/Opportunity` on `memex.systemorph.com` reported `compilationStatus: Ok`
throughout a two-and-a-half-hour window in which every one of their per-instance hubs was dead —
`Crm/Offer` timed out on activation, `Crm/Opportunity` answered *"Area not found"*. Their two
healthy siblings, compiled from the same sources, differed in exactly one field:

| type | `compiledFrameworkVersion` | behaviour |
|---|---|---|
| `Crm/Client`, `Crm/Mail` | `s2f227642d…` | renders |
| `Crm/Offer`, `Crm/Opportunity` | **`sc273ee39f…`** | dead |

Every deal page and every offer page of a client portal was down, and every instrument that a
deploy is gated on read green.

## Why `Ok` was a lie, stated precisely

🚨 **`CompilationStatus.Ok` is a claim SCOPED to `CompiledFrameworkVersion`, persisted as though it
were absolute.**

- *"The last compile succeeded"* is true, and process-independent.
- *"These bytes can be loaded"* is only ever answerable **relative to a process**.

The record has always carried both halves — the verdict and its scope — in two separate fields.
Every instrument read the first one. That is the entire defect, and it is why no amount of care at
the **writer** fixes it alone: the pod that wrote `Ok` could load what it had just built. It was
telling the truth about itself.

So the answer is split, and each half is necessary.

## Half one — the writer: the identity travels with the coordinates

`NodeTypeContractHandler.ResolvedStoreVersion` already argues the rule, for the store-key version:

> The path and the version are ONE reference, so they must come from ONE source. Taking the version
> from the response while the PATH fell back to `def` would pair the retained bytes with a key that
> was never theirs.

**The framework identity is the fourth member of that reference, and it was left out of it.**
`ApplyResolvedSuccess` decided the assembly path and the store key by "did the response carry a
reference", and decided `CompiledFrameworkVersion` by a different predicate (`freshCompile`). On the
HYDRATE path those disagree — and a hydrate only *succeeds* when the assembly store resolved bytes,
under a key whose framework tag is this process's own. The result:

```text
compilationStatus        Ok
latestAssemblyPath       <bytes the store just handed us>
compiledFrameworkVersion <somebody ELSE's identity>
```

A record that says `Ok`, names loadable bytes, and declares those bytes unloadable.
`HasUsableBuild` is then false **forever**; every per-instance activation takes the ABI-stale
recompile path; after `MaxRecompileAttempts` the instance binds the fallback configuration — and a
hub resolves its configuration exactly once, so that is *"Area not found"* for the grain's whole
lifetime. Worse, this handler **races** the activity write-back, so it re-imposes the foreign stamp
over each correct one and the type never converges.

The fix is one predicate for all four fields, extracted so they cannot drift:

```csharp
private static long? StoreVersionFromResponse(GetCompilationPathResponse response)
    => !string.IsNullOrEmpty(response.ContentPath)
       && long.TryParse(response.Version, out var v) && v > 0 ? v : null;

CompiledFrameworkVersion = freshCompile || ReferenceCameFromResponse(response)
    ? NodeTypeCompilationHelpers.FrameworkVersion
    : def.CompiledFrameworkVersion
```

🚨 **The retained branch still retains.** When the producer supplied no reference (a `memory://`
compile, a Null store, unreadable bytes) the coordinates are kept — and the identity is kept *with*
them. That is the one case where an ABI-staleness marker is real, and stamping a live identity over
retained coordinates would be the same record-describes-a-build-it-is-not-serving defect reached
the other way round.

## Half two — the reader: `NodeTypeBuildIdentity`

Foreign-`Ok` records still **arrive**, and no writer-side rule can stop them:

- a peer replica on another image ([#3395](https://github.com/Systemorph/MeshWeaver/issues/3395)'s
  ping-pong — the incident's own mechanism);
- a node repo that **commits** a record (MeshWeaver.Plugins ships `Store/Catalog` with a July
  framework hash);
- a prebuilt bundle.

`NodeTypeBuildIdentity` (`src/MeshWeaver.Compiler.Pipeline/NodeTypeBuildIdentity.cs`) is one pure
predicate with two consumers — deliberately the shape `MeshPublicationGate` uses, and for the same
reason: the defect is a **divergence**, so the refusal and the reported status are derived from one
comparison rather than being two tables that can drift.

| call | answers |
|---|---|
| `RefusalReason(def[, live])` | why this process may not load the build the record names, or `null` |
| `ReportedStatus(def[, live])` | the status this process may honestly report |

`ReportedStatus` folds the pair: a successful build with a recorded assembly whose identity is not
this process's reports **`CompilationStatus.Foreign`**; everything else passes through untouched.

### Why a fourth state rather than reusing one

Each candidate carries a different remedy, and all three are wrong:

| state | says | why not |
|---|---|---|
| `Error` | *correct the code* | the code is fine — conflating the two is [#641](https://github.com/Systemorph/MeshWeaver/issues/641)'s defect in the other direction |
| `Unavailable` | *retry or wait* | the state is fully determined, and waiting is precisely what does not help |
| `Ok` | *it is fine* | the lie |

`Foreign` says **"recompile here"**, which is the remedy — and which the compile watcher already
drives on its own.

### 🚨 It is DERIVED, and it is never persisted

Nothing writes `Foreign`. Writing a reader-relative verdict into a shared record is exactly how
#3395's ping-pong was made — two replicas, two identities, each correctly overwriting the other's
answer forever. **The record keeps saying what the compiler did; only the report is scoped.** That
is also what makes this compose with `MeshPublicationGate` instead of duplicating it: that gate
decides *whether* this process may publish; this one adds nothing to publish.

## Where it is enforced

`NodeTypeCompilationHelpers.HasUsableBuild` has carried the framework equality since
[#464](https://github.com/Systemorph/MeshWeaver/issues/464) — on **one** of the seven paths that
load a NodeType's assembly. The other six gated on the two assembly-coordinate fields being
populated, or on `CompilationStatus == Ok` alone:

| site | was gated on |
|---|---|
| `NodeTypeEnrichmentHelpers` — pinned release | `RequestedReleasePath` set. `NodeTypeRelease.FrameworkVersion` exists and nothing read it |
| `NodeTypeContractHandler` — pinned release | the same, answering `GetCompilationPathRequest` |

> 🚨 **Both pinned-release sites decide from the release's `Artifacts`, never from
> `NodeTypeRelease.FrameworkVersion`.** That field is the framework's ASSEMBLY VERSION string
> (`3.0.0.0`) and its own doc says it has never gated adoption; the identity that does lives on each
> `ReleaseArtifact.FrameworkIdentity`. The first cut of #3472 compared the two, so from
> `3.0.0-ci.7939` every pin to a historical release was refused on every mesh — *"built against
> framework 3.0.0.0 and this process is 1deb…"*, #1696's producer/gate disagreement one door over. It
> was caught by MeshWeaver.Plugins' moved suite (`CodeEditRecompileTest.NodeType_RequestedReleasePath_PinsToHistoricalRelease`)
> on the 7991 pin bump; this repository had no test that pins a release, and now has one
> (`PinnedReleaseAdmissionTest`). The decision is `NodeTypeBuildIdentity.PinnedReleaseRefusal`: an
> artifact for this identity and a runnable architecture → admitted; artifacts present, none for this
> identity → refused, naming what the release offers; no artifact link at all (a release written
> before links existed) → admitted **unverified**, logged — an absence is not a verdict (#890).
| `NodeTypeContractHandler` — published-release hydrate | `LatestReleasePath` + coordinates |
| `MeshDataSource.HandleNodeTypeSchemaRequest` | coordinates |
| `NodeTypeDataModelAreas.ResolveInstanceHubConfig` | coordinates |
| `MeshOperations.ResolveHubConfigForSchema` | `CompilationStatus == Ok` |
| `CellSurfaceAssemblyProvider` | `CompilationStatus == Ok` — and it takes a lifetime **lease** |

🚨 **What stood between those and a foreign load was an eight-character substring inside one store
implementation's glob.** `FileSystemAssemblyStore.TryGetAssemblyPath` looks up
`v{version}-{FrameworkTag}-*.dll`, so a foreign-identity record already misses there — which is why
the six were never observed to break. That is a property nothing asserted, in one of two
implementations; the other, `BlobAssemblyStore`, ships in a module this repository cannot compile.
An implicit guard is not a guard.

**Refusing is not "the type is dead".** Every site takes exactly the branch a **store miss** already
takes — which is a local compile, a fallback configuration, or a no-answer — so behaviour on a
tag-carrying store is unchanged and the operator gains a line naming **both** identities. Naming
only one is how the bake gate's *"regressed on this image"* sent three investigations to the wrong
repository on the same night.

## Where the honest status is reported

| surface | before | now |
|---|---|---|
| `get_diagnostics` (`FormatDiagnosticsFromDef`) | `status: "Ok"` | `status: "Foreign"`, with both identities in `error` |
| compile-progress overlay | `Ok` ⇒ **redirect** to the page that cannot render, which bounces back | holds, and says why — localized (`ui.compileForeignFramework`) |
| NodeType overview progress line | ✓ **Compiled**, printing the foreign hash beside the green tick as decoration | ⚠ built for another platform build |

## 🚨 What this does NOT reach — and a sweep whose two backends disagree

**`search 'nodeType:NodeType compilationStatus:Error'` — AGENTS.md's stated pre-deploy sweep — does
not see a foreign build, and separately, its selector does not mean the same thing on both query
backends.** Two findings, kept here because a deploy is gated on this instrument.

**1. The sweep never returns the value.** `MeshOperations`' search envelope projects
`Path/Name/NodeType/Version/LastModified` only; `compilationStatus` is a WHERE clause and nothing
more. To read a type's status you must `get` the node or call `get_diagnostics`. So even where the
filter works, the sweep answers *which* types are broken, never *what* any type's status is — and
`Foreign` is invisible to it either way, because `Foreign` is derived by the reader and the filter
runs in the store.

**2. 🚨 The two query providers disagree about which selectors exist, and nothing compares
them** ([#3511](https://github.com/Systemorph/MeshWeaver/issues/3511)). This is the finding; "the
sweep is broken" would be the wrong summary and was the wrong filing.

| backend | `compilationStatus:Error` | `content.compilationStatus:Error` |
|---|---|---|
| in-memory / FileSystem (`QueryEvaluator`), **before #3511** | **matches nothing** — resolves to `null` | reaches the field |
| in-memory / FileSystem, **after #3511** | reaches the field | reaches the field |
| Postgres (`PostgreSqlSqlGenerator`, out of repo) | **discriminates** — measured on a live mesh: 5 for `Error`, 195 for `Ok`, disjoint | the same 5 |

`QueryEvaluator` used to resolve a selector by reflection against the object it was handed — a
`MeshNode` — which has no `compilationStatus` property and had no `Content` fallback, so the
comparison was `null == "Error"`: false for every node, and a mesh full of broken types answered
`count: 0`. **The evaluator now has the fallback**, resolving a selector the way SQL always did:
the node's own field first, else the content field of the same name. So the two providers answer
this query alike, and `SweepSelectorReachesTheCompileStatusTest` plus the shared
`SelectorResolutionCorpus` pin both of them against one list. The full account — the ordering
decision, what it changes on a running portal, and the selectors on which the two providers still
disagree — is [Query Provider Parity](/Doc/Architecture/QueryProviderParity).

**The consequence, while it lasted, was not "the sweep lies" but "the sweep means different things
in different places".** On the portal a deploy is actually gated on, the bare form discriminated.
On a dev Monolith, a disposable CI mesh, or any FileSystem-backed host, the same query was green by
construction — the CI-gate-that-skips-on-missing-input shape: *it never ran* and *it passed* paint
the same colour. A rehearsal of the sweep on a local mesh therefore proved nothing about the sweep.

🚨 **The instruction stays `content.compilationStatus:Error` even so.** The fix ships with a
platform build; the sweep is run against whatever image a portal is *already* running, and the bare
form answers correctly there only on Postgres. The dotted form is the established production idiom
(`nodeType:User content.email:…`), it reaches the field on every backend and every deployed image,
and it says where the field lives. And the instrument that is genuinely reader-relative — the one
that answers the question this page is about — is `get_diagnostics`, which now answers `Foreign`.

## What this does not fix

- **It does not stop two images sharing one mesh.** That is
  [Mesh Admission](/Doc/Architecture/MeshAdmission) (membership) and
  [#3479](https://github.com/Systemorph/MeshWeaver/issues/3479) (roll selection).
- **It does not bind a record to bytes it does not name.** When the framework moved and the store
  holds bytes under the live tag, `DecideStaleBuildAction` still answers `Skip` — nothing here has
  hashed those bytes, and restamping on them would assert validity for a build the lane never
  examined. That boundary is unchanged and deliberate; see `ResolveStaleBuildAction`. It is also
  why a record can sit at `Ok` with a foreign identity **indefinitely**, which is what makes half
  two necessary rather than cosmetic.
- **It does not make a foreign record heal without an activation.** The instance-activation
  self-heal is still the path that corrects it.

## Related

[Mesh Admission](/Doc/Architecture/MeshAdmission) ·
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) ·
[Module Set Convergence](/Doc/Architecture/ModuleSetConvergence) ·
[Modules](/Doc/Architecture/Modules) ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals)
