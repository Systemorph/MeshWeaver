---
NodeType: Markdown
Name: Standalone Roslyn Emit Diagnostic
Category: Architecture
Abstract: "A bounded native control experiment for nested Roslyn emit failures, with exact provenance and fail-closed evidence handling."
---

# Standalone Roslyn emit diagnostic

The manual **Flake repro (manual)** workflow's `standalone-emit` choice asks whether a nested emit
failure can occur without MeshWeaver, hub disposal, DI scopes or collectible assembly loading.
It runs `tools/RoslynEmitProbe` directly on a native GitHub-hosted Ubuntu 24.04 X64 runner.
It does not deploy, change production JIT flags, grant access or introduce credentials.

## Fixed comparison

The SDK is pinned to **10.0.401**, the runtime to **10.0.12** with roll-forward disabled, and
Microsoft.CodeAnalysis.CSharp to **5.9.0**. Plugins failure run `34402293577`, job
`102636899891`, records installation of those toolchain versions; its actual testhost runtime
and loaded compiler MVID were not recorded. The diagnostic records those identities directly.

Three fresh processes run without retries: 1,000 sequential iterations with the default runtime
environment, 1,000 with `DOTNET_TieredPGO=0`, and two iterations with deliberately invalid nested
source. Every iteration tries nested and flat source against both shared and pristine CoreLib
references. Valid arms require 4,000 successful emits each. The invalid-source control requires
four nested compiler-diagnostic failures and four successful flat emits, with exit code 1.

Each process is bounded to five minutes. Both comparison arms and the negative control remain
scheduled even if an earlier process fails. The driver requires a successful build from this run,
checks runtime/compiler/architecture and arm settings, reconciles every leg's counts, and saves
raw logs plus `verdict.json` under `artifacts/roslyn-emit-probe/`. Source, project and DLL hashes
identify the exact experiment. OS/architecture checks alone cannot exclude all emulation; the
workflow supplies the additional native premise by selecting the hosted runner without a container.

## Interpret the evidence

| Observation | Meaning |
|---|---|
| Missing build or unsupported host | Not run; no inference about Roslyn |
| Deadline or absent completed summary | Incomplete; no successful control result |
| Process terminated by signal | Runtime crash; inspect separately from managed emit exceptions |
| Identity, count or exit-code contradiction | Harness mismatch; the experiment did not establish its premise |
| Expected negative diagnostics with flat controls passing | The negative control worked |
| Valid-source diagnostic, exception or wrong output shape | Observed failure of that kind; compare site and controls before attributing it to #890 |
| All valid emits succeed | No reproduction in this bounded experiment; not proof of a fix or a lifecycle cause |

No nonzero exit alone establishes the original compiler defect. Repeated emits within one
process are not independent CI suite runs. The shared reference leg is intentionally CoreLib-only,
not CI's full framework/module closure. Successful bytes are inspected through PE metadata and
never loaded: the nested control must contain three types, two nested types and six total CLR
generic parameters; the flat control must contain one non-generic top-level type.

## Provenance and prior emulated controls

The exact Program and project files come from local Plugins commit
`e9be16b85724204bb4ce0da81677d3666afc5291`, `scripts/diagnostics/emit-probe/`.
Their SHA256 values and the operational recipe are in `tools/RoslynEmitProbe/README.md`.
The nested/flat sources and compilation options were copied from core
`174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c`'s `EmitPipeline` canaries.

The September 9 Linux X64 runs under emulation on ARM Colima produced 4,000/4,000 successful
emits in each sequential arm, default and PGO off. Their negative control produced four expected
nested diagnostics and four successful flat emits. That portable DLL was built with SDK
10.0.400 on the ARM host, then executed in the X64 SDK 10.0.401 container. Preliminary emulated
build and JSON-reporting crashes are preserved separately and are not classified as the CI NRE.
The native comparison addresses that environment limitation; it does not turn the earlier clean
results into evidence that disposal causes the failure.

## Native observation: run 34414443069

The single native run on September 9, 2026 UTC completed successfully on commit
`210ed8ea5312a504bb6edbf8fa7cdb06137a225c`. Artifact `10128517549` records Ubuntu
24.04.4 X64, four processors, .NET 10.0.12 and Roslyn
`5.9.0-1.26357.3+35d9211b841e7613c1d2f8f5af6d628ace696c4c`.
Roslyn MVID `9d28c907-337b-44f2-8746-85bb8e79479b` and CoreLib MVID
`473d0ad3-441b-4fee-a00f-67c03eb1e2b3` match the earlier emulated controls.

Default settings emitted 4,000/4,000 successfully in 18.360 seconds; PGO off emitted
4,000/4,000 in 17.856 seconds. Both exited zero. The negative control recorded four
nested CS1001/CS1513 failures and four successful flat emits, exiting one as expected.
These are driver wall times for one process per arm, not a performance benchmark.
Raw output and the reconciled verdict are preserved in
`tools/RoslynEmitProbe/results/34414443069/`.

The native environment did not reproduce the defect. This removes emulation as a
limitation of this particular control observation; it establishes neither a fix nor
a disposal cause. The experiment still excludes CI's full reference closure and prior
mesh/test process activity. The next useful discriminator would preserve those inputs
and process history, rather than repeat these clean controls or change production JIT
settings. No production change or second diagnostic run was performed.

## Exact failing reference closure: unavailable

The follow-up inspected all 26 artifact inventory entries for Plugins run `34402293577`.
The failing shard's artifact `10125123788` contains 696 files: 683 logs and 13 TRX files,
with no DLL, runtimeconfig, deps file, dump or reference manifest. The two plausible
workspace-build archives were inspected too: `10124339542` contains 113 DLLs in 165 files;
`10124174588` contains 43 DLLs in 78 files. Neither contains the Monolith test build or
an ordered reference manifest. Their `.closure.txt` files list module project names only.
They are outputs of separate module-build jobs, not the
failing test process's reference capture. Inventory and inspected archive hashes are in
`tools/RoslynEmitProbe/results/34402293577-closure-audit/`.

The shard log records its own Release MSBuild graph from Plugins merge `e601973` and
core `174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c`, followed by `dotnet test --no-build`.
It does not consume those workspace-build archives. An image digest is present in its
environment, but the test host runs the graph built on the runner; extracting a portal
image or rebuilding the commits cannot establish its exact runtime-selected references.
No image pull, reference reconstruction or second probe was therefore performed.

At that exact core revision, `CompileReferences.cs` lines 46–55 and 69–82 compose ordered
TPA file references and missing known framework additions; lines 102–115 append installed
module assembly locations with path deduplication. `MeshNodeCompilationService.cs`
lines 181–185 cache that list per service, and lines 1909–1916 append resolved NuGet
assembly paths. These are file-backed references. Generator output adds syntax trees,
not in-memory reference assemblies; the fresh image-backed CoreLib belongs only to the
pristine canary. The capture gap is runtime selection, ordering and exact bytes, not an
established dependence on dynamically emitted input assemblies.

### Smallest next capture, before another replay

Use a CI-only, opt-in, once-per-process capture at the original emit exception boundary
(`EmitPipeline.cs` lines 187–196), without replacing the original exception or adding retries.
For the unchanged canary probe, the required payload is the **ordered
`faulted.References` list and referenced bytes**, not all workload source or the entire
test output directory. Each entry needs ordinal, reference/metadata kind, path/display,
aliases, EmbedInteropTypes, assembly identity, MVID, size and SHA256; retained bytes should
be content-addressed. Record missing/unreadable entries and a completion marker: a partial
capture must never be replayed as the exact closure.

Record the actual compiler and CoreLib identities, runtime, architecture, processor count,
JIT flags, process/test identity, original exception/site and existing canary outcomes with
that capture. Preserve the already pinned canary source/options and native runner for the
comparison. Full workload replay additionally needs final generated syntax trees and
parse/compilation/emit options, but that is a separate, larger experiment.

A file reread after the failure may differ from metadata Roslyn already mapped. Compare
captured-file identity against the reference's held metadata identity and explicitly record
the capture method; do not claim mapped-byte equivalence merely from a path or matching
MVID. If immutability of the files cannot be established, retain their bytes when references
are created and record that instrumentation change. Even an exact byte closure reconstructed
in a new process does not preserve reference-object caches, JIT history or heap state.
This is a capture specification only: no compiler instrumentation, new permissions,
production changes, timeout expansion or additional CI execution was introduced.
