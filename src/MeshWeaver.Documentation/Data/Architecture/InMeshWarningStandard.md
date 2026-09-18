---
Name: The In-Mesh Warning Standard
Category: Architecture
Description: In-mesh C# was the only C# in the fleet held to no warning standard at all — the emit collected its warnings and the bake dropped them. The two shrink-only ratchets that fix that, why the runtime compile must stay lenient, the three diagnostic codes the platform was emitting into content it does not own, and how a repo adopts the baseline without going red.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>
---

# The In-Mesh Warning Standard

**Every `.cs` stored in a mesh node compiles at RUNTIME, never in CI — so no `-warnaserror` build
has ever seen it.** That is the premise of the whole "green CI does not mean the mesh compiles"
rule, and it had a second half nobody had closed: the bake that *does* compile in-mesh C# was not
looking at the warnings either.

The maintainer's report, 2026-09-16:

> baking, as executed in the plugins pipeline, is very verbose and does **NOT** surface warnings as
> errors ⇒ we have lots of missing xml comments

Both halves were true, and they had the same root.

## Where the warnings were going

`EmitPipeline.CreateParseOptions()` has **always** asked for `DocumentationMode.Diagnose`, so
`CS1591` ("missing XML comment for publicly visible type or member") has always been *produced*.
Three layers then discarded it, one after the other:

| Layer | What it did | Fixed by |
|---|---|---|
| `EmitPipeline` | read `emitResult.Diagnostics` only when `Success` was false | #3993 — a green compile now collects them |
| `NodeSetCompiler.Compile` | called the collecting emit and never read `artifact.Warnings` | this change |
| `TreeBake` / `CascadeBuild` | had nothing to read | this change |

So the runtime activity gained its warnings and the **build lane** — the one place that could hold
content to a standard — still saw none. The verdict was `compile=Ok` over source that would not
have built in `src/`.

## 🚨 The policy lives in the gate. The runtime compile stays lenient — always.

There is exactly ONE `CreateCompilationOptions()`, and `MeshNodeCompilationService` — the compile
every portal replica runs for every NodeType on every boot — shares it with the CI bake.

**Adding a `GeneralDiagnosticOption` there is the obvious fix and it is forbidden.** A NodeType that
cannot compile is PARKED; a parked NodeType makes the new replica's readiness gate refuse; a refused
readiness gate stalls the rollout; a stalled rollout means no instance actions at all. That is the
outage this fleet has already spent a night recovering from, and a missing doc comment must never be
able to cause it.

So the factory carries no escalation, `TheRuntimeCompileStaysLenientTest` fails if anyone adds one,
and the standard is a **gate policy** carried by the tester's `WarningBaseline`.

## Two ratchets, deliberately separate

The maintainer chose two over one combined gate, and the split is by diagnostic id:

- **`warnings`** — everything except `CS1591`. `CS0219` (an assigned-but-unused local), `CS1574` /
  `CS1584` (a `cref` that resolves to nothing), `CS1570` (badly formed XML in a doc comment),
  `CS1573` (a `<param>` tag naming a parameter that does not exist). These are latent bugs, or doc
  comments that EXIST and are WRONG — the exact breakage a cross-repo move causes.
- **`doc-comments`** — `CS1591` alone. Documentation debt: nothing is broken, the member simply is
  not described.

They are evaluated, reported and failed **independently**, so paying down (or carrying) doc debt is
never in the same verdict as a latent bug, and a missing doc comment can never be the reason an
unrelated change cannot merge.

> 🚨 Only `CS1591` is documentation debt. `CS1574` and `CS1573` look like doc diagnostics and are
> not — a `cref` pointing at a type that moved is a broken link in shipped API documentation, and it
> belongs with the bugs.

## The baseline, and why it can only shrink

One file per tree, passed as `--warning-baseline`. Lines are `<NodeType path> <diagnostic id>`;
`#` comments and blank lines are ignored.

```
ACME/Report      CS1591   # 12 public members with no doc comment
Northwind/Order  CS0219   # an unused local in OrderView.cs
```

The contract is the repo's usual one-way ratchet, and it bites in **both** directions:

| Observation | Verdict |
|---|---|
| a (type, code) pair NOT in the baseline | **NEW** — fails the bake |
| a pair in the baseline, observed again | known debt — reported, tolerated |
| a baseline pair whose type compiled CLEAN of that code | **STALE** — fails the bake until the line is deleted |
| a baseline pair whose type did NOT compile, or is not in this tree | unverifiable — warned, never failed |

That last row is the rule a ratchet gets wrong once and only once. A type that failed to compile
emitted **no warnings at all**, and reading that silence as "the debt is paid" would delete a real
record on a measurement nobody took — the same reason `GateVerdict` calls an allow entry whose scope
a narrowed run never reached *unverifiable* rather than stale. It is what makes the file correct on
a narrowed run and on a shard alike.

### Granularity: (type, code), not (type, code, member)

Per-member would make the baseline a transcript — 271 lines for `samples/Graph/Data` alone — and
every doc comment written would edit it. Per-TYPE would let a NodeType with one tolerated `CS1591`
acquire a `CS0219` in silence, which is the debt growing back under a green tick. `(type, code)` is
the coarsest key that still refuses a NEW KIND of defect anywhere, and the finest a reader can keep
in their head.

## 🚨 Three codes were fixed at the ROOT, not baselined

Before capturing anything, the inventory had to be read for **who owns each diagnostic**. Three of
the five codes were the PLATFORM emitting warnings into content it does not own, and baselining
those would have recorded the generator's debt under the content's name, in every repo of the fleet,
where no author could ever have paid it:

| Code | What was actually wrong | Fix |
|---|---|---|
| `CS0105` | `DynamicMeshNodeAttributeGenerator` concatenated every source file's `using` lines with no de-duplication — so an author who correctly writes `using MeshWeaver.Layout;` in each of three files, or once where the standard set already has it, earned *"the using directive … appeared previously"* | dedupe on emit, against each other AND against `StandardUsings`. The language-service path (`GenerateGlobalUsingsSource`) had always deduped — this also closes the #1802 drift |
| `CS1591` ×2 per type | the generated `…MeshNodeProviderAttribute` and its `Nodes` property are `public` and were undocumented. Two warnings on **every dynamic NodeType in the fleet**, fixable by nobody | generate the doc comments |
| `CS8632` | authored `string?` landing in a tree with no `#nullable` directive | `NullableContextOptions.Annotations` — the annotation context ONLY |

> 🚨 `Annotations`, never `Enable`. The annotation context makes `?` mean what the author wrote and
> turns on **no** nullable analysis, so it adds not one diagnostic and cannot make a compile that
> used to succeed fail. `Enable` would switch the whole CS86xx family on across every in-mesh source
> in the fleet at once — a content decision nobody has made, taken by a compiler option.

Measured on `samples/Graph/Data`, before → after those three fixes:

| | before | after |
|---|---|---|
| raw occurrences | 850 | **375** |
| distinct sites | 348 | **272** |
| types producing a warning | 27 of 27 | **18 of 27** |
| distinct codes | 5 | **2** |

What remains is genuine: `CS1591` on authored sample content (362 occurrences over 271 members in
17 types), and `CS1701` — *"assuming assembly reference 'System.Linq.Expressions, Version=8.0.0.0'
used by 'System.Reactive' matches identity '…Version=10.0.0.0'"*. That last one is the ONE class of
entry whose owner is the **reference set** rather than the content: `System.Reactive` is compiled
against .NET 8 and runs on .NET 10. No content author can fix it and no `#pragma` belongs in their
source, so the honest outcome is that the bake RECORDS it.

## 🚨 The parity list: what the in-mesh compile does NOT report

*Added 2026-09-17, on the maintainer's direction: "warn == error", everywhere, including the in-mesh
NodeType compile — so `plugin-gate-warnings.allow` goes to **zero**, not merely shrinks.*

Driving a baseline to zero means fixing what is broken. It also meant noticing that two of the
families in it were never the content's to fix, because **a raw `CSharpCompilation` applies no
`NoWarn` at all**. The premise of this whole standard is that in-mesh C# is held to the standard
`src/` C# is held to under `-warnaserror` — and it was being held to a *stricter* one, in exactly two
families. `CompileWarning.NotReported`, applied at `EmitPipeline.Collect`, is the in-mesh compile's
`NoWarn`:

| Family | Codes | Not reported for CORE's `src/` because | Entries this retired in MeshWeaver.Plugins |
|---|---|---|---|
| **Reference-set skew** | `CS1701`, `CS1702` | a project build's reference set is coherent — measured, not assumed (below) | **95** |
| **Doc COMPLETENESS** | `CS1591`, `CS1573`, `CS1712` | core's own `Directory.Build.props` `NoWarn`, mirrored in `MeshWeaver.Plugins/src/` and `MeshWeaver.SocialMedia/src/` | **115** |

> 🚨 **The `CS1701` half of that parity is a MEASUREMENT, not core's `NoWarn` — and mistaking one
> for the other is the trap.** The .NET SDK does ship the default
> (`Microsoft.NET.Sdk.CSharp.props`: `<NoWarn Condition=" '$(NoWarn)' == '' ">1701;1702</NoWarn>`),
> but the condition is `'$(NoWarn)' == ''` and core's `Directory.Build.props` **sets** `NoWarn`
> before it evaluates, so the default never applies here. Measured 2026-09-18, core's effective
> `$(NoWarn)`: `649;CA2255;NU5104;NU1510;CS1591;CS1573;CS1712;;NU1608` — no 1701, no 1702. What
> makes `src/` clean anyway is that MSBuild's reference resolution hands `csc` a **coherent** set:
> a 28-project `-c Release -warnaserror` build over CORE's Rx-referencing assemblies emits
> `0 Warning(s)` and zero `CS1701`. 🚨 That measurement is core's and the claim goes no wider —
> MeshWeaver.Plugins and the satellites were not built for it, and whether each reports `CS1701`
> turns on whether its own `Directory.Build.props` SETS `$(NoWarn)` (killing the SDK default) or
> appends to it. The in-mesh compile assembles its reference set by hand (the
> host's `TRUSTED_PLATFORM_ASSEMBLIES`) and is therefore shown a skew a project build never
> presents. The SDK line *does* govern `compile-check.py`'s synthesized projects, which carry no
> `Directory.Build.props` and append to `$(NoWarn)` rather than setting it — which is why
> `PARITY_NOWARN` there is `CS1591;CS1573;CS1712` and deliberately omits 1701/1702.

`CS1701` is *"assuming assembly reference 'System.Linq.Expressions, Version=8.0.0.0' used by
'System.Reactive' matches identity '…Version=10.0.0.0'"* — `System.Reactive` is built against .NET 8
and runs on .NET 10, so every NodeType that touches Rx earned one. It is a property of the REFERENCE
SET, not of the content: no author could fix it, no `#pragma` belonged in their source, and a
platform bump could add or remove 95 baseline lines with no content change at all. **This codebase
had already made exactly this call one lane over** — `ProjectFile` seeds `NoWarn = 1701;1702` for the
`build-project` verb, because omitting it *"turned five otherwise-clean projects red on warnings the
SDK does not report"*. The in-mesh compile was reporting a family core's own project build does not
— which is the measured statement; "the last compiler in the fleet still reporting them" is the
unmeasured one, and the satellites were never built to check it.

> 🚨 **This is a PARITY list, not an escape hatch.** A code that is NOT suppressed for `src/` must
> never be added to it; it gets fixed, or it is recorded as debt in a baseline. In particular the doc
> comments that EXIST and are WRONG stay reported and are now ERRORS — `CS1574`/`CS1584`/`CS0419` (a
> `cref` resolving to nothing, or to two things), `CS1570` (malformed XML), `CS1571`/`CS1572`/`CS1734`
> (a tag naming a parameter that is not there), `CS1587` (a doc comment on something that cannot
> carry one). `TheRuntimeCompileStaysLenientTest` pins both halves: each suppressed code is produced
> by the compiler and dropped by `Collect`, and each unsuppressed one still reaches the gate.

**Why at `Collect` and not through `WithSpecificDiagnosticOptions`.** The compilation options are
REFLECTED into `GeneratedInputIdentity.OptionsFingerprint`, so suppressing there would change the
content key of every NodeType in the fleet and force one global recompile — a rollout cost for a
reporting decision. `Collect` is the single point every consumer reads warnings through, so the
runtime activity log gets the same quietening for free.

### A baseline line naming a suppressed code is INERT, never STALE

Retiring a code must not be able to red a repo that has not trimmed its file yet. The moment
`CS1701` and `CS1591` stopped being reported, MeshWeaver.Plugins' 210 such lines would otherwise all
have become STALE in one platform roll — a hard bake failure, on an image whose timing that repo does
not control, with no pull request in flight having touched anything related. So `WarningBaseline.For`
skips them and `WarningBaseline.Inert` names them: the run prints one line saying how many there are
and which codes, and fails nothing. They tolerate nothing and can hide nothing, because nothing can
produce them.

> `WarningClass.DocComment` and the `doc-comments` ratchet are RETAINED and are now vacuous by
> policy — `CS1591` is its only member and `CS1591` is suppressed. Keeping the (generic) mechanism
> costs one log line and leaves the door open for a tree that chooses to enforce doc completeness;
> removing it would be a separate change across six repos' lane inputs.

### Core's two baselines are now EMPTY — and that is the strictest setting, not the weakest

*2026-09-18.* Trimming the inert lines is the second half of the parity change, and core's two
files were the last place in this repo still carrying any. Measured at `e8fce8130d`, before the
trim, with `MW_LOG_LEVEL=Information`:

| tree | compiled types | warning occurrences | INERT entries | `warnings` enforced against | `doc-comments` enforced against |
|---|---|---|---|---|---|
| `Doc` (`.github/doc-gate-warnings.allow`) | 4 of 4 | **0** | 7 | **0** | **0** |
| `samples/Graph/Data` (`.github/samples-gate-warnings.allow`) | 27 of 28 | **0** | 30 | **0** | **0** |

All **37** entries were `CS1591` or `CS1701`, so `WarningBaseline.For` had already excluded every
one of them: both ratchets were enforcing against **zero** entries *before* the trim, and nothing
either file said was being relied on by anything. What the trim changes is the log — the run no
longer prints an `INERT … Delete the lines.` line it printed on every bake — and the files now say
what is true.

🚨 **An empty baseline is not a disarmed one.** Omitting `--warning-baseline` is OBSERVE-ONLY; an
empty FILE tolerates nothing, so any in-mesh warning of any non-suppressed code fails the bake
immediately. `bake-then-gate.sh` still asserts both `ENFORCED` lines. Core's in-mesh C# is now at
**zero tolerated warnings over zero warnings REACHING THE RATCHETS** — the state
`plugin-gate-warnings.allow` reached from the other direction, by fixing 87 sites.

🚨 **"Reaching the ratchets" is not "produced", and the two numbers differ by exactly the parity
list.** Roslyn still emits `CS1591`/`CS1573`/`CS1712` and `CS1701`/`CS1702` on both trees;
`CompileWarning.NotReported` drops them in `EmitPipeline.Collect` — the single point every consumer
reads warnings through — so they never enter the `WarningInventory` the ratchets judge, and the
zeroes above are counts of REPORTED warnings. The paragraph below is that difference in numbers:
362 `CS1591` occurrences, emitted and unmeasured. `TheRuntimeCompileStaysLenientTest
.ACentrallySuppressedCode_IsNotReported` asserts both halves on one run — the compiler DID produce
the diagnostic, and `Collect` did NOT carry it — and
`ACodeThatIsNotSuppressed_StillReachesTheGate` is its control, so "nothing was reported" can never
quietly become "nothing is reported".

🚨 **And the emptiness is not a claim that the doc comments were written.** The 362 `CS1591`
occurrences over 271 members in 17 sample types are still undocumented; `CS1591` is simply no
longer measured. Writing them remains worth doing and would change no verdict — which is exactly
why the trimmed files say so in their own headers, rather than letting a future reader infer that
a count fell because debt was paid.

## Quietening the log

The second half of the report. Raw, the samples bake carries 375 warning occurrences; the runtime's
per-compile cap (`EmitPipeline.MaxReportedWarnings` = 50) would still let a 150-type bake write
7,500 lines. The fold is three-step:

1. **De-duplicate per compile** — already done by `EmitPipeline.Collect`.
2. **Fold across NodeTypes on `(id, message)`, NOT on the line.** A `Source/*.cs` pulled in by
   `shared=@Lib/Source` is concatenated into every consumer's generated tree, so ONE missing doc
   comment arrives as eight warnings at eight different line numbers. Folding on the line reports it
   eight times and tells the reader there are eight defects.
3. **Print the shape, not the transcript** — totals, one line per diagnostic id, then only the sites
   a reader has to ACT on (the ones behind a NEW pair).

A green enforced bake is four lines. This is the samples tree as it read BEFORE the parity list —
the shape a repo still carrying debt sees:

```
warnings: 375 raw occurrence(s) folded to 272 distinct site(s), over 18 of 27 compiled type(s)
warnings: by code — CS1591 362× / 271 site(s) / 17 type(s) · CS1701 13× / 1 site(s) / 13 type(s)
warnings: warnings ratchet — ENFORCED against 13 baseline entr(ies): 0 NEW, 0 stale, 13 known debt, 0 unverifiable
warnings: doc-comments ratchet — ENFORCED against 17 baseline entr(ies): 0 NEW, 0 stale, 17 known debt, 0 unverifiable
```

…and the same tree today, with both families retired and the baseline trimmed to nothing. The
`by code` line is gone because there is nothing to break down, and a zero is still PRINTED — "I
measured, and it was clean" and "I measured nothing" stay two different sentences:

```
warnings: 0 raw occurrence(s) folded to 0 distinct site(s), over 0 of 27 compiled type(s)
warnings: warnings ratchet — ENFORCED against 0 baseline entr(ies): 0 NEW, 0 stale, 0 known debt, 0 unverifiable
warnings: doc-comments ratchet — ENFORCED against 0 baseline entr(ies): 0 NEW, 0 stale, 0 known debt, 0 unverifiable
```

`MW_LOG_LEVEL=Information` restores every distinct site, exactly as it restores every other
held-back line — see `GateVerbosity`.

## Adopting it in a repo, without going red

**A gate that reds the whole fleet the moment it lands is a defect of its own.** So the flag is the
switch and the DEFAULT is honest rather than strict:

- **no `--warning-baseline`** ⇒ OBSERVE-ONLY. The bake still measures, still prints the folded
  inventory, and prints — by name — that it enforced nothing. A run that enforced nothing must never
  read like a run that found nothing; that is the same rule `GateAllowlist.MissingFileMessage`
  states from the other side.
- **a path that was GIVEN and is not there** ⇒ a hard refusal. An EMPTY baseline (nothing tolerated,
  every warning fails) is spelled as an empty FILE, so "no known debt" and "the bake never read the
  baseline" can never render the same.

The adoption path is three steps:

1. Run one bake with `MW_LOG_LEVEL=Information` and copy the `warnings: baseline …` lines.
2. Commit them as a file in the repo.
3. Name it on **both** lanes — `warning-baseline-file:` on `node-repo-gate.yml` (the PR lane) and on
   `node-repo-publish-bake.yml` (the merge lane). Naming only one holds a pull request and its own
   merge to different standards.

### …and in this repository it cannot silently switch off

Core's PR content gate passes `.github/doc-gate-warnings.allow` and
`.github/samples-gate-warnings.allow` through `bake-then-gate.sh`, which then **asserts both
`ENFORCED` lines in the bake log**. The tester's default is observe-only and exits 0, so a future
edit that dropped the argument would leave in-mesh C# with no standard at all while every run stayed
green. The postcondition makes that edit red instead — a gate that cannot fail is not a gate.

## The control

`InMeshWarningRatchetTest` runs the real bake over a two-NodeType fixture and pins, end to end:

- an EMPTY baseline over one deliberate `CS0219` and one deliberate `CS1591` → **RED**, each ratchet
  naming its own and printing the exact line to add;
- the same source with both pairs baselined → **GREEN**;
- a baseline entry whose type compiles clean → **RED as STALE**, and only on *its own* ratchet — the
  other stays green, which is what the two-ratchet split is for;
- an entry for a type this run never compiled → unverifiable, never stale;
- observe-only measures, enforces nothing, and says so;
- the whole report is four lines, not one per occurrence.

The negative control is `Ctrl/Clean`: a fully documented, warning-free NodeType in the SAME bake
must measure ZERO. Without it, "the ratchet fired" could equally mean "every type produces warnings
here", and the STALE case could not be written at all.

## Related

- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — what a dynamic NodeType compile is, and what parking one costs.
- [CI Content Bake](/Doc/Architecture/CiContentBake) — where the bake runs, and which lane publishes what.
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the build doctrine this log format follows ("warn/error + verdicts only").
- [Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries) — the other ratchet shape in this repo, and why an expired entry is red.
