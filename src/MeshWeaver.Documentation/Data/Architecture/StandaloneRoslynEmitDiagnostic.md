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
