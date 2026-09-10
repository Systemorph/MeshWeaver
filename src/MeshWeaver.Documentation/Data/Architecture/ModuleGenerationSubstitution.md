---
Name: Module Generation Substitution
Category: Architecture
Description: Assembly.LoadFrom does not promise to load the path it is handed — it silently returns a copy the default load context already holds. What that did to the InstanceAction control plane, why nothing logged a word, and the three answers the loader now gives instead of two.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h9"/><path d="M4 12h16"/><path d="M4 17h9"/><path d="m16 4 4 3-4 3"/></svg>
---

# Module Generation Substitution

> **The one sentence.** `Assembly.LoadFrom(path)` does not promise to load `path`. When the default
> load context already holds an assembly of that identity, it returns **that** copy — same instance,
> its own `Location`, no exception and no diagnostic — so a loader that records the path it *asked
> for* is recording something nobody measured.

This page is the finding behind [MeshWeaver#3911](https://github.com/Systemorph/MeshWeaver/issues/3911),
where the `Hosting/InstanceAction` control plane on the control instance went silently dead after an
activation restart and no log line anywhere named a cause.

## What was measured

Two copies of one assembly, three cases, .NET 10 on macOS, 2026-09-10:

| The second `LoadFrom` is handed… | What happens |
|---|---|
| a copy carrying **different bytes** | throws `FileLoadException: Assembly with same name is already loaded` |
| a **byte-identical** copy at another path | **returns the first copy, silently** — same instance, `Location` = the *first* path |
| a copy of an assembly already loaded from `/app` | **returns the `/app` one, silently** |

The throwing case was already handled: the loader catches it, records the generation as never
loaded, and falls back to the previous one — with a `[MeshWeaver.Mesh.FallbackModule]` line on
stderr. The **silent** case was handled nowhere, because nothing compared the path that was asked
for with the path that arrived.

## Why this happens in production, not just in a test process

Four modules ship in the portal image as `MeshModuleClosure` **seeds** — `MeshWeaver.AI`,
`MeshWeaver.Blazor.Chat`, `MeshWeaver.Markdown.Collaboration` and the MCP server — under
`modules/<name>/`, so a self-registry install has *some* copy even with no registry to serve one.
The registry then lands the same module again as a generation, `modules/<name>@<id>/`. Two paths,
one assembly identity, and whichever a boot reaches first takes the name **for the whole process**.
A landed generation that is byte-identical to the seed (a re-publish, or a seed cut from the same
commit) is therefore substituted with nothing said.

## The blast radius, and why it renews itself

Every later reading of *"which generation is this process running"* is derived from the assembly the
loader kept, and inherited the wrong answer without a comparison anywhere:

- `InstalledModuleAssembly` — and therefore the in-mesh **compile reference set** and each
  NodeType's stamped `compiledDependencies`;
- `ModuleActivationStatus.LoadedModuleGenerations()` — the loaded-generation map;
- the **module-set adoption** record, which claims *this replica runs this set*.

The activation record names generation B; the process runs generation A; the derivation reads the
difference as an ordinary pending update and prints **"restart to activate"**. The next boot
resolves the same two paths the same way, so the prompt renews itself and no restart clears it —
which is what left the control instance recycling with a control plane that could not be operated,
and whose own restart is an `InstanceAction`.

🚨 **The silence was the defect, not the substitution.** One copy per name in the default load
context is a runtime constraint, not a bug. Not being able to *say which copy* is the bug.

## The control that could not fail

`ModuleLoadReport` was built for exactly this class of question (#2223: *say which copy of each
module pack is actually loaded*), and its `Describe` carried the claim:

> it reports the paths it is GIVEN — the same array that goes to `InstallAssemblies` — so the report
> and the load can never disagree by construction.

That makes the report agree with the **request**. It says nothing about the **load**, because the
report is computed from files *before* anything is loaded and is never reconciled with what came
back. It is the shape [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) calls
*a writer whose failure mode is a success line*: the line prints the generation that was requested,
whether or not that generation ever ran.

The loader is the only place that holds both halves, so that is where the comparison now lives —
`MeshBuilder.SubstitutedLocationOf`, one `Path.GetFullPath` comparison against
`Assembly.Location`. An assembly with no readable location answers "cannot see", never a finding.

## Three answers, never two

A substituted load is recorded as a `FallbackModule` carrying **`RunsAlreadyLoadedCopy`** — the same
record that already means *present, running, and behind*, so every surface that reads it (the boot
stderr line, the Warning re-log, `RequiredModuleStatus`, the package card, the activation report,
the adoption's running-generations map) names the state for free, and the report stops promising a
restart that cannot clear it.

The one place it must **not** be folded into the existing arms is the unloadable marker (#3650),
which answers *do these bytes load on this platform build*. This boot never asked:

| Verdict | Marker | Why the other two are wrong |
|---|---|---|
| **Unloadable** — measured, refused | written | — |
| **Loaded** — measured, ran | cleared | — |
| **Not measured** — never reached the loader | **untouched** | writing it makes the update reconcile permanently `SkipUnloadable` every rebuild of a version nobody executed; clearing it erases an earlier boot's real measurement |

`ModuleActivationBoot.MeasuredLoadability.HeadNotMeasured` is that third answer, and
`RecordMeasuredLoadability` neither writes nor clears on it — the same
*"I did not check" is distinct from "I checked and it was clean"* discipline as
`NodeDiagnosticsOutcome`.

## What is NOT claimed

- **Nothing here says those bytes are bad.** The head generation may be perfectly loadable; it
  simply never got a turn. The reason string says only what happened.
- **A restart is not promised, and not ruled out either.** Which of two paths a boot reaches first is
  not something a status surface may predict, so this is never "restart required" — the same rule
  the other fallback arms follow.
- **`compiledDependencies` divergence across a module MVID is a separate question.** A substituted
  load is byte-identical by construction (a different build throws instead), so it cannot by itself
  move a stamped MVID. #3911's report pairs an MVID with a generation directory; that pairing was
  not re-measurable after the pods rolled and is not asserted by this page.

## Where the code is

| Piece | File |
|---|---|
| The comparison | `MeshBuilder.SubstitutedLocationOf` (`src/MeshWeaver.Mesh.Contract/MeshBuilder.cs`) |
| Who holds a taken name, for the refusal message | `MeshBuilder.WhereTheNameIsAlreadyHeld` |
| The record arm | `FallbackModule.RunsAlreadyLoadedCopy` (`src/MeshWeaver.Mesh.Contract/ModuleInstallCandidate.cs`) |
| The third marker answer | `ModuleActivationBoot.MeasuredLoadability.HeadNotMeasured` (`src/MeshWeaver.PluginCatalog/ModuleActivation.cs`) |
| Repro through the real loader | `ConfiguredModuleActivationTest.WhenTheLoadContextAlreadyHoldsTheName_TheLoaderSaysWhichGenerationIsRunning` |
| The marker's pure control | `ModuleUnloadableMarkerTest.ABootThatNeverReachedTheHeadsBytes_NeitherWritesNorClearsItsMarker` |

See also: [Module Versioning](/Doc/Architecture/ModuleVersioning) ·
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) ·
[Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail)
