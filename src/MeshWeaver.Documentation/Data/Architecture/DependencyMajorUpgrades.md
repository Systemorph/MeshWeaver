---
Name: Dependency Major Upgrades
Category: Architecture
Description: How to cross a dependency's MAJOR version boundary here — the four things a green -warnaserror build cannot see, the differential method that replaces them, and the ledger of boundaries we have actually crossed.
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
For a major bump it is close to worthless, for four independent reasons:

| What a green build cannot see | Why |
|---|---|
| **In-mesh source** | Every `.cs` stored in a mesh node — NodeType `Source/*.cs`, Scripts, layout areas — compiles at RUNTIME in the portal. A NodeType's `configuration` lambda is C# inside a JSON string, invisible even to `grep --include='*.cs'`. |
| **Authored CONTENT** | A parser bump changes how *data already in the tree* is read. Nothing compiles that data, so nothing fails when it starts parsing differently. |
| **The satellite repositories** | `MeshWeaver.Plugins` has **no `Directory.Packages.props` of its own**. Its `src/Directory.Packages.props` IMPORTS core's, at the commit named by `MW_PLATFORM_REF`. A one-line edit here therefore chooses what *another repository* compiles against — and core CI is green by construction, because nothing here references what broke. |
| **Behaviour behind an unchanged signature** | The shape no surface detector can see. The method below attacks it with a corpus and with the library's own compatibility baseline, not with a diff. |

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

### 3. Diff the real public API, don't read the release notes alone

Release notes are a summary written by someone who was not thinking about this codebase — and they
can be actively misleading. Rx 7.0.0, 7.0.0-rc.1 and 7.0.0-preview.20 ship **byte-identical**
release bodies, an auto-generated cumulative changelog reaching back to 2021; most items people cite
as "new in Rx 7" actually shipped in 6.1.0.

So download both `.nupkg`s, extract the assembly for the TFM we actually resolve, and enumerate the
public surface of each with `MetadataLoadContext`. Sort, normalise away the assembly-qualified
version stamps (they otherwise dominate the diff), and `comm` the two lists.

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
to the test binary (check the copied DLL's version), because a props edit that did not take reads
identically to one that did.

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
| API removals | **ZERO** public or protected members, once non-public entries are excluded (step 3). Additions are 16 `ObservableEx.CombineLatest`/`Zip` overloads for 9–16 sources, on a type nothing here references. |
| Surface we rely on | `System.Reactive.Linq.Observable` is byte-identical at **656 members**; every `Subject`/`ReplaySubject`/`BehaviorSubject`/`AsyncSubject`, every `System.Reactive.Disposables` type and every scheduler are member-identical. |
| Upstream's own verdict | `EnablePackageValidation` against a **6.1.0** baseline; **none** of its 57 suppressions is a behavioural or core-API break — all are `ref`-vs-`lib` UI omissions and TFM shape. |
| In-mesh sweep | **28 of 620** in-mesh artefacts reference Rx (27 of 90 `.cs` node sources, 1 of 530 node `.json` — `AnalyticsCatalog.json`). Since the public removal set is empty, there is nothing for any of them to have used. |
| Suites | 4,780 tests, **0 failures**: Graph 1536 · Compiler.Pipeline 841 · Hosting 608 · Data 508 · Layout 484 · Messaging.Hub 409 · Documentation 394. |

**The defect this bump actually fixed was not a version being old.** `Microsoft.Reactive.Testing` was
already pinned at **7.0.0** while `System.Reactive` read **6.1.0**. The testing package depends on
`System.Reactive >= 7.0.0`, and with `CentralPackageTransitivePinningEnabled` unset the higher
version wins — but only in the projects that reference it. Measured before the change: all 19 `src/`
projects resolved Rx **6.1.0**, and the eight test projects referencing the testing package resolved
Rx **7.0.0**.

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
packages. (The nine pre-6.x facades — `System.Reactive.Core`, `.Linq`, `.Interfaces`,
`.PlatformServices`, `.Providers`, `.Experimental`, `.Compatibility`, `.Runtime.Remoting`,
`.Windows.Threading` — are frozen at 6.1.0; no 7.0.0 of them exists. We reference none.)

🚨 **The package now ships a Roslyn analyzer**, and on a `-warnaserror` build that is the thing most
likely to bite: `System.Reactive.Analyzers.dll` emits **RXNET0001–RXNET0004** at Warning, which
`-warnaserror` promotes to errors. All four come from `AddUiFrameworkPackageAnalyzer` and fire only
on the Windows UI types above. This codebase uses none of them — zero hits across both repositories,
against a positive control of **1,453** `.cs` files that *do* contain `IObservable` (606 here, 847 in
MeshWeaver.Plugins) — which is why the build stays clean. A satellite that ever grows a WPF or
WinForms surface will meet these as build errors.

**Behaviour: no change found.** The four things that would be correctness changes here — scheduler
defaults, `ObserveOn`/`SubscribeOn` semantics, error-propagation timing, and completion/disposal
ordering — are unchanged, and that matters because this codebase's action block is an error boundary
and a disposal fence, so a shift in when Rx signals disposal would be a correctness change rather
than a performance note. Every item in the Rx 7.0 milestone is packaging, analyzers, tooling, docs,
or the annotation below. Two things often *attributed* to Rx 7 in fact shipped in 6.1.0 and were
already in force here: the `RefCount` fixes and the opt-in `ResetExceptionDispatchState`. And ADR
0004, which would change when `OnError` becomes a thrown exception, is still **Proposed** — that is,
not implemented.

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
