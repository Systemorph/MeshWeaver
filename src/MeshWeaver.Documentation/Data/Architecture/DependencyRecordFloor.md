---
Name: The Dependency Record Floor
Category: Architecture
Description: A compiled NodeType's dependency record states a FLOOR over each module it binds — "I need at least X" — not the module's MVID. The decision (#3934), why an MVID pin could never converge, what the floor deliberately does not relax, and the outage that measured it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 21h18"/><path d="M6 21V9l6-5 6 5v12"/><path d="M3 13h18"/></svg>
---

# The Dependency Record Floor

**Platform owner's decision, 2026-09-10 ([#3934](https://github.com/Systemorph/MeshWeaver/issues/3934)),
in four clauses:**

1. **There is no incompatibility.** Two builds of one module assembly link fine — `AssemblyVersion`
   is synchronised fleet-wide, so `Assembly.LoadFrom` returns the already-loaded copy and ignores
   the second path. Nothing throws.
2. **It is a floor, not a pin.** A record says *"I need at least X"* and accepts anything at or
   above.
3. **It must never recompile.** A moved MVID is not a reason to rebuild a NodeType at runtime.
4. **It must not crash.** If a floor genuinely is not met, that must be visible — never a silent
   fallback that drops the type's views.

This page is the authority on how a module entry of a
[dependency record](../NodeTypeCompilation) is written and compared. Where another page describes a
module entry as an exact build, it is describing the mechanism this replaced.

## What the record carried before, and why it could not converge

`CompiledDependencies.CreateIdResolver` resolved each referenced assembly to a *surface id*:

| lane | id | compared |
|---|---|---|
| platform assembly | `ref:<surface-hash>` | exactly — but the hash moves only on a **surface** change, so a rebuild with an unchanged API adopts |
| installed module | `mvid:<guid>` | exactly — and the MVID moves on **every compilation**, by construction |

That asymmetry is the whole defect. A platform rebuild of identical source compares equal; a
*module* rebuild of identical source compares unequal, and every NodeType binding that module is
declined at adoption.

**It is not fixable by making the producers agree.** Roslyn's deterministic MVID also hashes the
**absolute source paths**, and neither core nor MeshWeaver.Plugins sets `DeterministicSourcePaths`,
`PathMap` or `ContinuousIntegrationBuild`. Measured 2026-09-10: the same commit, the same
properties, and the same emitted `InformationalVersion`, compiled at `$GITHUB_WORKSPACE` and at
`/repo` inside the image, produced `dace9bf7-…` and `202e6e4f-…`. So byte-equality is a property of
the *lane*, not of the source — see
[Module-Owned Siblings Ride](../ModuleOwnedSiblingsRide), which now records that its earlier
"agreement is cheaper than exclusivity" remedy treats the symptom.

### The outage it produced

Measured on `memex.meshweaver.cloud`, 2026-09-10. One pod held
`MeshWeaver.Markdown.Collaboration` in 15 copies and **three builds** — the three independent
compilations a MeshWeaver.Plugins publication is composed from. The bake host loaded whichever copy
arrived first and stamped *that* MVID into every record; the pods loaded a different one.

* `PrebuiltAssemblySeeder` declined 22 of 272 entries at that identity, across 3 module names, on
  **one** entry each: `mvid:798f92a0…` recorded, `mvid:825581836a…` live, with all eight `ref:`
  surfaces and `!toolchain` agreeing. For the `socialmedia` bundle it was **all six** declines.
* A declined bundle means the content compiles **in-portal**, which sets
  `latestAssemblyCollection: "local"` — documented by `FileSystemAssemblyStore` as *"the bytes live
  in the local filesystem cache only; cross-silo readers must recompile"*. memex runs 2 replicas, so
  the type was dark on whichever pod lacked the bytes.
* Each replica then declined the other's stamp, recompiled, and stamped its own —
  `v2031 → v2050 → v2053`, two `compiledModulesHash` values alternating with
  `currentSourceFingerprint` constant, two forced re-activations recompiling **2 seconds apart**
  with identical inputs.
* Net effect: `/Posts/SavThankYou` returned **HTTP 200 rendering nothing**. Every view
  `SocialMedia/Post` declares — `Preview`, `Write`, `PostCard` — was absent for the whole day, while
  the NodeType's own record read `compilationStatus: Ok` throughout. An instance `recycle` was
  measured not to fix it.

## The floor

A module now resolves **`min:<version>`**, where the version is the module assembly's
`AssemblyInformationalVersion` with the `+<sha>` build metadata stripped —
`InstalledModuleAssembly.Version`, the one reader, which both the portal's resolver
(`NodeTypeCompilationHelpers.DependencyIdResolverOf`) and the bake host's (`mw-plugin-test`'s
`BakeHost`) go through so a floor is never compared against a value nobody else computes.

**A live id satisfies a stamped one when it is at or above it**, ordered by
`NuGetVersionComparer` — the repo's one SemVer fold, used verbatim rather than re-implemented
(`MeshWeaver.Compiler` now references `MeshWeaver.Plugin.Packaging` for it). String order is wrong
here in a way that silently picks an old build: as text `3.0.0-ci.900` sorts above
`3.0.0-ci.3758`.

```text
recorded          live              verdict
min:3.0.0         min:3.0.0         Satisfied      ← the SavThankYou case: same version, moved MVID
min:3.0.0-ci.900  min:3.0.0-ci.3758 Satisfied      ← numeric, not textual
min:1.6.0         min:1.5.0         FloorNotMet    ← below; land a newer module
min:3.0.0         mvid:798f92a0…    NotChecked     ← incomparable; declines
mvid:798f92a0…    min:3.0.0         NotChecked     ← a legacy record, once; declines
min:3.0.0         absent            Drifted        ← the module is not here at all
```

**A module that states no version keeps the exact `mvid:` pin.** An environment that cannot read a
version has no floor to offer, and inconclusive stays on the rebuild side — the same rule the
content key follows. The three-argument `CreateIdResolver` overload is exactly the four-argument one
with a version resolver that answers nothing.

### Where the version comes from, and what that makes the floor mean today

Core's `Directory.Build.props` pins `InformationalVersion` to `$(PlatformVersion)` under `CIRun`
(plus the SDK's `+<SourceRevisionId>`), and MeshWeaver.Plugins carries the same pin
(Plugins#1604 — `$(AssemblyVersion)`, or the platform's version string where it is readable). So a
module built anywhere in one platform line carries **one** ordered value, e.g. `3.0.0`, whichever
lane compiled it. `NuGetVersionComparer` treats `3.0.0.0` and `3.0.0` as equal, so the two shapes
the Plugins props can emit agree.

That is exactly what clause 1 asks for: every build of a line satisfies every record of that line,
and the floor still orders across lines and across a module that versions independently of the
platform.

## What the floor deliberately does NOT relax

The relaxation lives in exactly one function, `CompiledDependencies.Satisfies`, and it **refuses to
compare across schemes**. Everything below is ordinal equality, exactly as before:

* **`!toolchain`** — the toolchain PROXY ("the toolchain moved, so the bytes MIGHT be stale") still
  invalidates every record on a toolchain change. A module floor met with room to spare licenses
  nothing about it.
* **`!input`** — the direct observation ("the input that produced these bytes hashed to X") is
  still exact. A regenerated input that hashes differently is not excusable by any version.
* **`ref:` platform surfaces** — still exact, and a `min:` never compares to a `ref:`. That
  refusal is what keeps [#3175](../ModuleBuildArchitecture)'s one-producer rule intact: a module the
  platform host *also* ships resolves `ref:` on a portal and `min:` in the bake, which now reports
  *NotChecked* rather than quietly matching.
* **`absent`** — a build that binds something this environment does not have is a drift with a
  definite answer, and stays one.

**The demotion the content key licenses survives the floor.** `LiveContentKeyOf` folds a
**satisfied** entry at its *recorded* value rather than its live one — the key's assembly half asks
"does every entry still hold", and under floor semantics holding is satisfaction. Folding the live
value would make a module rebuilt *above* the floor move the key, reintroducing the same
invalidation one layer down. It cannot widen anything: a non-satisfying entry folds its live value,
so the key differs and nothing is demoted, and the comparison re-checks every entry independently
afterwards. See [The Toolchain Re-evaluation Lane](../ToolchainReevaluationLane).

## Clause 4: an unmet floor is visible, and never a silent default

Two changes, both about a verdict that could not previously be read.

**The comparison answers WHICH outcome.** `CompiledDependencies.Validate` returns a
`DependencyRecordOutcome` — `Satisfied` / `Drifted` / `FloorNotMet` / `NotChecked` — modelled on
`NodeDiagnosticsOutcome` ([Language Services](../LanguageServices), #3912/#3916) rather than
inventing a second way to say *"I could not check"*. `FindMismatch` is a **projection** of it
(`outcome.Problem`), so the string form and the status form cannot disagree. `IsSatisfied` is false
for `NotChecked` too, so a caller reading only the flag still cannot mistake "no answer" for
"compatible". Every reader — the prebuilt seeder's decline, `HasUsableBuild`, its stale twin, the
bake probe's `ClassifyDetailed`, the stale-build kickoff — goes through that one comparison, so the
floor is honoured and named everywhere or nowhere.

**A recorded build whose bytes this process cannot resolve now overlays a diagnosis.** That branch
used to bind the **default configuration**: the instance activated, served the generic areas, and
every area the type declares was simply missing — which is precisely what a reader saw at
`/Posts/SavThankYou`, with nothing on the page, in the node, or in a recycle to name the cause. It
now takes the same `AssemblyUnavailable` overlay the pinned-release branch beside it already used,
with the same version-gated self-heal, and keeps the fail-fast contract: the overlay sets an
`UnhandledMessageNack`, so a typed request the missing assembly would have handled gets a terminal
`DeliveryFailure` naming the NodeType instead of being silently ignored.

## Controls

`DependencyRecordFloorTest` runs both directions, because a test that only proves the new
acceptance would also pass for "adopt everything":

* the falsification arm — the old `mvid:` pin **declined** the moved-build case, asserted verbatim,
  so the acceptance test says something about what changed;
* a record **below** its floor still declines, with the exact sentence
  `'X' needs at least 1.6.0 — this environment has 1.5.0, below the recorded floor`;
* `!toolchain`, `!input`, `ref:` and `absent` each have their own arm proving they did not move;
* `LiveContentKeyOf` reproduces the stamped key when a module moved **above** its floor and does not
  when it moved **below** it;
* every adopt-time reader (`HasUsableBuild`, `HasStaleFrameworkBuild`, `Classify`,
  `ClassifyDetailed`) is exercised on both sides of one floor.

## What this does not close

**The producer still composes a publication from more than one compilation.** The floor makes a
consumer stop caring, which is the point — but the sealed-set assertions
([Module-Owned Siblings Ride](../ModuleOwnedSiblingsRide) → "The producer asserts it") still refuse a
set carrying one assembly name at two builds, and should: a torn publication is a real defect even
when nothing declines it downstream. Merging MeshWeaver.Plugins' `floor` and `rest` `module-pack`
calls into one workspace remains the remedy at source.

**The registry does not yet answer "the latest version compatible with this platform".** The
resolution model recorded on #3934 inverts today's direction — a consumer asks and the registry
supplies, rather than a producer stamping an identity a consumer must match. That is an
architectural change to how every instance obtains modules and is not implemented here; the floor is
the consumer half that stops a divergent producer from being fatal in the meantime.

**A version string still decides nothing about whether a module LOADS.**
[Module Adoption Policy](../ModuleAdoptionPolicy) rule R2 is unchanged: loadability is measured by
the link probe and the load, never declared. The floor is not a `minMeshVersion`-style claim an
author writes — it is the version of the module the producer actually compiled against, recorded
automatically, and it governs only whether a *prebuilt NodeType assembly* is reused.

See also: [Module-Owned Siblings Ride](../ModuleOwnedSiblingsRide) ·
[Module Adoption Policy](../ModuleAdoptionPolicy) ·
[NodeType Compilation](../NodeTypeCompilation) ·
[The Toolchain Re-evaluation Lane](../ToolchainReevaluationLane) ·
[Controls That Cannot Fail](../ControlsThatCannotFail)
