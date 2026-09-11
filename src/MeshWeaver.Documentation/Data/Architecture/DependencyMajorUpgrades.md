---
Name: Dependency Major Upgrades
Category: Architecture
Description: How to cross a dependency's MAJOR version boundary here — the five things a green -warnaserror build cannot see, the differential method that replaces them, and the ledger of boundaries we have actually crossed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="m15.5 5.5-3.5 3-3.5-3"/><path d="M5 12h14"/><path d="M7 12v4a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2v-4"/><circle cx="12" cy="21" r="1"/></svg>
---

# Dependency Major Upgrades

The standing directive is to run the **latest** version of every library, at minimum current within
the minor line. A **major** boundary is the exception that needs a decision and a method, because it
is the one place where the compiler stops being a witness.

This page is the method and the ledger. [Dependency Licensing](../DependencyLicensing) covers which
licences may be adopted at all; this page covers how to cross a boundary once the licence is clear.

## Why a major is different here

A clean `dotnet build -c Release -warnaserror` is the normal standard of proof in this repository.
For a major bump it is close to worthless, for five independent reasons:

| What a green build cannot see | Why |
|---|---|
| **In-mesh source** | Every `.cs` stored in a mesh node — NodeType `Source/*.cs`, Scripts, layout areas — compiles at RUNTIME in the portal. A NodeType's `configuration` lambda is C# inside a JSON string, invisible even to `grep --include='*.cs'`. |
| **Authored CONTENT** | A parser bump changes how *data already in the tree* is read. Nothing compiles that data, so nothing fails when it starts parsing differently. |
| **The satellite repositories** | `MeshWeaver.Plugins` has **no `Directory.Packages.props` of its own**. Its `src/Directory.Packages.props` IMPORTS core's, at the commit named by `MW_PLATFORM_REF`. A one-line edit here therefore chooses what *another repository* compiles against — and core CI is green by construction, because nothing here references what broke. |
| **Behaviour behind an unchanged signature** | The shape no surface detector can see. The method below attacks it with a corpus and with the library's own compatibility baseline, not with a diff. |
| **Rules an ANALYZER would have enforced** | A new major can ship analyzers — and your build may never load them (folder convention, `ExcludeAssets`, `PrivateAssets`). Then the build is green about nothing: the rules were never evaluated, and the green reads exactly like a pass. Rx 7 is a live instance; see step 6 and the ledger. |

🚨 The `Satellite package pins (removal declared)` gate does **not** cover this. It fires on a
`PackageVersion` being **removed**, not bumped — a bump keeps the `Include` and so never trips it.
The satellite reads a bump only when somebody moves its pin, on another day, in another pull
request.

## The method

Six steps, in order. Each produces a positive artefact; none of them can pass by returning nothing.

### 1. Measure the premise against the live registry

Never adopt a target version asserted by an earlier session, an issue, or a memory. Ask nuget.org,
and **gate the read on HTTP status AND byte count** — a failed request's empty body greps to zero
and reads exactly like "no such version".

```bash
curl -sS -o idx.json -w "HTTP=%{http_code} BYTES=%{size_download}\n" \
  https://api.nuget.org/v3-flatcontainer/<package-id-lowercased>/index.json
```

### 2. Read the licence at the NEW major, not at the old one

A licence can change **at** a major boundary — that is exactly how `JsonPatch.Net` 5.0.0 moved
json-everything's published binaries to a maintenance fee, which is why the Aspire family is pinned.
Compare the `<license>` element of both nuspecs; a change is a **stop-and-report**, never a call to
make alone.

```bash
curl -sS -w "HTTP=%{http_code} BYTES=%{size_download}\n" -o new.nuspec \
  https://api.nuget.org/v3-flatcontainer/<id>/<new>/<id>.nuspec
```

### 3. Diff the real public API, don't read the release notes alone

Release notes are a summary written by someone who was not thinking about this codebase — and they
can be actively misleading. Rx 7.0.0, 7.0.0-rc.1 and 7.0.0-preview.20 ship **byte-identical**
release bodies, an auto-generated cumulative changelog reaching back to 2021; most items people cite
as "new in Rx 7" actually shipped in 6.1.0.

So download both `.nupkg`s, extract the assembly for the TFM we actually resolve, and enumerate the
public surface of each with `MetadataLoadContext`. Sort, normalise away the assembly-qualified
version stamps (they otherwise dominate the diff), and `comm` the two lists.

What matters is the **removal** set — additions cannot break a consumer. Then check that set against
what this repository and the satellites actually call.

🚨 **Filter on accessor visibility, or the tool will invent removals.** `Type.GetMembers` with
`BindingFlags.NonPublic` returns non-public properties too, and a naive dump lists them. Doing
exactly that on Rx produced one phantom removal — `CurrentThreadScheduler.Time`, a property with no
public or protected accessor, which no consumer could ever have called — out of 138 such non-public
entries. Excluding them, the true removal count was zero. A removal you cannot call is not a removal.

### 4. Ask the library whether IT thinks it broke

Cheaper and stronger than inference, and routinely skipped. A package that sets
`EnablePackageValidation` with a `PackageValidationBaselineVersion` carries a machine-generated list
of every difference against that baseline, with a suppression entry for each one the maintainers
accepted. Read the suppressions: if none of them is a behavioural or core-API break, that is the
maintainers' own statement that the release is a packaging release.

🚨 **Read them per-TFM, and never summarise the list in aggregate.** Each entry carries a `Left` and
a `Right` naming the two *assets* compared, and a suppression list mixes baseline-vs-new comparisons
with cross-TFM ones inside the same package. Rx's 57 read as "all UI omissions" in aggregate, but six
of them name core types (`ThreadPoolScheduler`, `NotificationKind`) — and those six turn out to
compare `netstandard2.0` against `uap10.0.18362`, TFMs this repo never resolves. The question is
never "is the list clean", it is **"does any entry name the asset pair I actually resolve"** — which
`project.assets.json` tells you exactly (`compile` and `runtime` per package, per target).

Also check the ADRs: a change that *would* alter semantics but is still `Status: Proposed` has not
shipped, however much the release notes discuss it.

### 5. Run the change against the CORPUS, not only the suite

This is the step that finds behaviour changes behind unchanged signatures, and it is the one most
often skipped. It applies whenever the library *processes artefacts we store* — a parser, a
serializer, a formatter.

Build the *same* probe program twice — identical source, two project files differing only in the
pinned version — configured **exactly** as production configures the library. Run both over every
artefact in the tree the library actually processes, and emit one stable, hashed line per artefact.
Then diff the two outputs. A zero-line diff over a few thousand real inputs is a far stronger
statement than any suite.

🚨 **Control the instrument before believing its zero.** A probe that cannot report a difference
reports zero for the wrong reason. Two checks, both cheap:

- **Negative control** — count distinct hashes. If the corpus collapses to a handful, the hash is
  not discriminating and the zero is vacuous.
- **Positive control** — mutate one cell of one output file and re-run the diff. It must fire.

The same discipline applies to a *sweep* for a removed symbol: state the denominator. "Zero hits"
means nothing without "…out of N files that contain the thing I was looking near".

### 6. Build and run the full suites, and never re-run a red

Build every touched project and its dependents `-c Release -warnaserror`, one project per
invocation, and read a literal `0 Error(s)` plus an elapsed time that makes sense — a two-second
"Build succeeded" is an up-to-date no-op, not a build. Confirm the new assembly actually landed next
to the test binary, because a props edit that did not take reads identically to one that did — and
prefer hashing it against the downloaded `.nupkg`'s asset to reading a version string, since the
hash also tells you *which* TFM asset you resolved.

🚨 **If the new major ships an ANALYZER, prove your build actually runs it before you read the green
as evidence.** A package can ship analyzers your compilation never loads — wrong folder convention,
`ExcludeAssets`, a `PrivateAssets` default — and then a clean `-warnaserror` build says nothing
whatever about the rules that analyzer enforces. `dotnet build <proj> -getItem:Analyzer` lists what
csc is actually handed; **demand a positive control**, because an empty list means "the instrument
is broken" just as readily as "no analyzer". If it turns out inert, say so and make the real
argument separately from source — do not let an unrelated green stand in for it. Rx 7 is exactly
this case (see the ledger), and the mistake is one-directional: it reads as reassurance.

Then run the **full** suites, not a filter. A major bump is exactly where a filtered run lies.

🚨 **Prefer the test executable to `dotnet test` for the long suites.** These are xUnit v3
executables; `dotnet test` drives them through the VSTest bridge, and on the longer suites here that
bridge has been seen to die with `SIGABRT` after ~70 s, print **no results at all**, and still exit
**0** — a textbook false pass. Running
`test/<Project>/bin/Release/net10.0/<Project>` directly prints an explicit
`=== TEST EXECUTION SUMMARY ===` with totals, which is the positive signal to demand.

And a red after a major bump is a real behavioural change until proven otherwise — that is the entire
point of the exercise, so it is investigated, never re-run.

## Ledger

### System.Reactive 6.1.0 → 7.0.0 — a packaging release, and a SPLIT FAMILY it closed

Measured 2026-09-11.

| Step | Result |
|---|---|
| Registry | `https://api.nuget.org/v3-flatcontainer/system.reactive/index.json` → HTTP **200**, **974 B**. Newest stable **7.0.0**; no 7.0.1 exists. |
| Licence | **MIT at both ends**, unchanged. Assembly identity holds too: public key token `94bc3704cddfc263` unchanged, only `AssemblyVersion` moves 6.1.0.0 → 7.0.0.0. |
| API removals | **ZERO** public or protected members, once non-public entries are excluded (step 3) — on *both* asset pairs this repo actually resolves, read straight off `project.assets.json`: compile `lib/net6.0` → `ref/net8.0` (0 removals) and runtime `lib/net6.0` → `lib/net8.0` (0 removals, 16 additions). The 16 are `ObservableEx.CombineLatest`/`Zip` overloads for 9–16 sources, on a type nothing here references. The probe excluded **138** non-public-accessor properties on each side — the exact population that manufactures phantom removals. |
| Surface we rely on | `System.Reactive.Linq.Observable` is byte-identical at **656 members**; every `Subject`/`ReplaySubject`/`BehaviorSubject`/`AsyncSubject`, every `System.Reactive.Disposables` type and every scheduler are member-identical. |
| Upstream's own verdict | `ApiCompatSuppressions.xml` at tag `rxnet-v7.0.0` (HTTP 200, 22,564 B) carries **57** entries: 51 `CP0001` (40 naming Windows-UI/WinRT types, 11 `AsyncInfoObservable`/`IEventPatternSource`), 5 `CP0008` and 1 `CP0002`. 🚨 The six non-`CP0001` ones DO name core types (`ThreadPoolScheduler`, `NotificationKind`), so "all are UI omissions" would be wrong — but every one of them is a **cross-TFM** comparison against `netstandard2.0` or `uap10.0.18362`, and **none involves the `net8.0` pair this repo resolves**. Both types, and the `CP0002` member, are present in `ref/net8.0` and `lib/net8.0` alike. |
| In-mesh sweep | **31 of 638** in-mesh artefacts reference Rx — 30 of 93 `.cs` node sources, and 1 of 545 node `.json` (`samples/Graph/Data/Northwind/AnalyticsCatalog.json`, Rx inside escaped source). None compiles in CI. Since the removal set on the resolved assets is empty, there is nothing for any of them to have used. |
| Suites | **15 suites, 6,909 tests, 0 errors, 0 failures** (2 skipped), run as the test executables: Graph 1545 · Memex.Portal.Shared 1291 · Compiler.Pipeline 841 · Hosting 619 · Data 508 · Layout 484 · Messaging.Hub 409 · Documentation 397 · PluginTester 362 · Hosting.Orleans 256 · ContainerImages 112 · ContentCollections 28 · Testing.Xunit 23 · Cli 19 · Deployment.Contract 15. |

**The defect this bump actually fixed was not a version being old.** `Microsoft.Reactive.Testing` was
already pinned at **7.0.0** while `System.Reactive` read **6.1.0**. The testing package depends on
`System.Reactive >= 7.0.0` (its nuspec: `<dependency id="System.Reactive" version="7.0.0" …/>`), and
with `CentralPackageTransitivePinningEnabled` unset — it appears in `Directory.Packages.props` only
in prose, never as a property — the higher version wins, **but only in the projects that reference
the testing package**. Exactly **8** test projects do. Everything else resolved 6.1.0. After the
change, all **52** Rx-resolving projects in the tree report `System.Reactive/7.0.0` and **none**
reports 6.1.0.

So the suite that gates the platform was exercising a different Rx **major** from the one the portals
shipped, and nothing anywhere was red. That is the one skew a test suite must never have, and no
control could see it: `PackageFamiliesMoveTogetherGuard` was keyed on a shared **prefix**, and
`System.Reactive` and `Microsoft.Reactive.Testing` share none. The guard now carries a second shape,
`NamedFamilies`, that lists a family's members explicitly — with its own negative control, because a
guard that has never been seen to fail is not a guard.

**Where the real change is, and why an API diff alone would miss it.** Rx 7 removed the Windows UI
surface — `DispatcherScheduler`, `ControlScheduler`, `CoreDispatcherScheduler`, `ControlObservable`,
`DispatcherObservable`, `WindowsObservable` and friends — from the **reference** assemblies while
keeping them in `lib`. That is *source*-breaking, not binary-breaking, and it is invisible to a diff
taken against `lib`. Those types now live in the separate `System.Reactive.Wpf`,
`System.Reactive.Windows.Forms`, `System.Reactive.WindowsRuntime` and `System.Reactive.Uwp`
packages.

🚨 **It is confined to the WINDOWS target frameworks, and that is why the diff above still reads
zero.** Those symbols only ever existed in the Windows-flavoured assets — measured by raw presence:
`DispatcherScheduler` is in 7.0.0's `lib/net472`, `lib/net8.0-windows10.0.19041` and
`lib/uap10.0.18362` and in *none* of its `ref/` assemblies, and it is in no `net6.0`/`net8.0` asset
of either version. This repo resolves the non-Windows `net8.0` pair and declares no `*-windows*` TFM,
so the source break cannot reach it. Read the removal count without that qualification and you would
conclude either that the diff was broken or that the break was ours. (The nine pre-6.x facades — `System.Reactive.Core`, `.Linq`, `.Interfaces`,
`.PlatformServices`, `.Providers`, `.Experimental`, `.Compatibility`, `.Runtime.Remoting`,
`.Windows.Threading` — are frozen at 6.1.0; no 7.0.0 of them exists. We reference none.)

🚨 **The package now ships a Roslyn analyzer — and it is INERT here, which is not the same as
passing.** Rx 7 adds `analyzers/dotnet/System.Reactive.Analyzers.dll` (6.1.0 ships none), emitting
**RXNET0001–RXNET0004** at Warning, which `-warnaserror` would promote to errors. It is the single
most likely thing to bite a warnings-as-errors build, so it was checked directly rather than
inferred from the build being green — and the check says csc never loads it. In
`project.assets.json` the analyzer appears **only** in the `libraries` file manifest, never in a
`targets` entry, and `dotnet build <proj> -getItem:Analyzer` on a project that references Rx returns
**zero** Rx analyzer entries while still returning the .NET analyzers, so the instrument
discriminates. The package places the assembly directly under `analyzers/dotnet/` with no language
subfolder, which is not where NuGet selects a C# analyzer from.

**So a clean `-warnaserror` build is NOT evidence that this codebase satisfies RXNET0001–0004.** The
evidence for that is separate and is a source sweep: the repository declares no Windows-flavoured
target framework at all (zero `*-windows*` TFMs in any `.csproj` or `.props`), and references none of
the Windows UI types the four rules fire on (0 hits), swept against a positive control of the **581**
`.cs` files under `src/` and `test/` that *do* contain `IObservable` — so the sweep is not looking at
an empty corpus. No pre-6.x Rx facade package is referenced either. Both facts are
needed; neither substitutes for the other. If upstream ever fixes the analyzer's path, the rules
become live for every consumer at once — and a satellite that grows a WPF or WinForms surface would
meet them as build errors.

**Behaviour: no change found.** The four things that would be correctness changes here — scheduler
defaults, `ObserveOn`/`SubscribeOn` semantics, error-propagation timing, and completion/disposal
ordering — are unchanged, and that matters because this codebase's action block is an error boundary
and a disposal fence, so a shift in when Rx signals disposal would be a correctness change rather
than a performance note. Every item in the Rx 7.0 milestone is packaging, analyzers, tooling, docs,
or the annotation below. Two things often *attributed* to Rx 7 in fact shipped in 6.1.0 and were
already in force here: the `RefCount` fixes and the opt-in `ResetExceptionDispatchState`. And ADR
0004 (`Rx.NET/Documentation/adr/0004-onerror-to-throw.md` at tag `rxnet-v7.0.0`, HTTP 200, 14,436 B),
which would change when `OnError` becomes a thrown exception, reads `Status: Proposed` — that is, not
implemented.

🚨 **All of that is still upstream's word about itself, so pair it with OUR OWN observation of the
property that matters.** `MeshWeaver.Messaging.Hub.Test` carries **40** `[Fact]`/`[Theory]` tests
across **31** files whose whole subject is disposal, teardown, quiescing, shutdown admission and
turn/backlog ordering — `RoutedDisposeRequest_Announces_Once_AndBeforeTheTeardownStarts`,
`GateOpenMustNotLetARunningTurnOvertakeTheBacklog`, `InitializationStopsAtTeardownStart`,
`ShutdownWindowAdmission`, the `DisposalStall*`/`DisposalRace*` family, and the rest. Every one
passes on Rx 7 (that suite: 409/409). That is the closest thing to a direct measurement of
"completion and disposal ordering did not shift" that this repository can make, and it is the check
to repeat on any future Rx boundary — a suite total alone would not have told you the ordering
tests were even in it.

The single compile-surface change is a nullability annotation:
`OfType<TResult>(this IObservable<object>)` became `IObservable<object?>`. It widens what is
accepted, so it cannot break a caller that compiled before.

**Target frameworks.** Rx 7 drops the `net6.0` asset and adds `ref/` assemblies (6.1.0 had none), so
the compile surface and the runtime surface are now genuinely different artefacts — which is exactly
why the UI removal above is invisible to a `lib`-only diff. `netstandard2.0` is still shipped, so a
net6/net7 consumer still resolves *something*; it is simply unsupported. We are on net10.0 and
resolve `net8.0`.

**One trap worth knowing, in the sibling product.** Ix.NET's `System.Linq.Async` v7 removed its LINQ
implementation from the reference assemblies in favour of .NET 10's own
`System.Linq.AsyncEnumerable`. A project still on `System.Linq.Async` **v6** that moves to .NET 10
gets ambiguity errors from two LINQ implementations. We pin `System.Interactive` 7.0.1, so this does
not reach us — but it is the shape to check if an Ix reference is ever added.

**What this bump changes for MeshWeaver.Plugins.** `MeshWeaver.Blazor`, `MeshWeaver.Blazor.Views`,
`MeshWeaver.InstanceSync` and `MeshWeaver.LogWatcher` reference `System.Reactive` versionless off
this repo's list, and three test projects reference `Microsoft.Reactive.Testing` the same way. They
move to Rx 7 when the satellite's `MW_PLATFORM_REF` next moves. Blazor Server is not a Windows-UI
framework in the analyzer's sense, so RXNET0001–0004 do not fire there either — verified by the same
sweep.
