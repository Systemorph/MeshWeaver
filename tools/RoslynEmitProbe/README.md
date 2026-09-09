# Native standalone Roslyn emit diagnostic

The manual **Flake repro (manual)** workflow accepts `diagnostic: standalone-emit`. Its dedicated
job runs directly on GitHub's `ubuntu-24.04` X64 hosted runner, without a container or services.
It builds with SDK **10.0.401** and requires runtime **10.0.12** and Roslyn CSharp **5.9.0**.
The earlier failing CI installed those versions but did not record its actual testhost runtime;
the probe records its own loaded runtime, compiler MVID, architecture and processor count.

The fixed driver `.github/scripts/run-emit-probe.py` runs three fresh processes without retries:

| Arm | Iterations | Workers | Expected result |
|---|---:|---:|---|
| Default runtime environment | 1,000 | 1 | 4,000 successful emits |
| `DOTNET_TieredPGO=0` | 1,000 | 1 | 4,000 successful emits |
| Invalid nested source | 2 | 1 | Four nested diagnostic failures and four successful flat emits; process exit 1 |

Each process has a five-minute deadline. Failure of one arm does not suppress the remaining arms.
The driver refuses a missing/failed build, unsupported host, inherited JIT tuning, or missing CI
runner assertions. Host checks cannot universally detect emulation: the native premise comes
from the workflow's hosted-runner configuration, not an environment variable alone.

Artifacts under `artifacts/roslyn-emit-probe/` include each raw log and `verdict.json`: exact exit
codes, per-leg counts, environment records, source/project/DLL SHA256 and classifications.
An expected negative-control exit is accepted only after all four legs and totals reconcile.
Missing summaries are incomplete, signals are runtime crashes, and contradictory evidence is a
harness mismatch. Compiler diagnostics, emit exceptions and wrong output shapes are separate
observations. None automatically establishes CI issue #890; a clean run is only a bounded
non-reproduction. The workflow changes no production configuration or credentials.

## Provenance and earlier result

`Program.cs` and `EmitProbe.csproj` are byte-identical to the preserved probe in local Plugins
commit `e9be16b85724204bb4ce0da81677d3666afc5291`, under `scripts/diagnostics/emit-probe/`:

- Program SHA256: `6bcd1ea1e605b5d30ec00831be239e268f934af8f93d058b89a597f9542a4687`
- Project SHA256: `a231e383d83e4c7990ecc1d02196a84f9ec6d73b49bf03a001bb5e7caf2c2aa5`

Nested and flat source strings and compilation options originate in core
`174f5ab7711e1a107c0c047dfb0dd9ba3bc8b91c`, `src/MeshWeaver.Compiler/EmitPipeline.cs`.
Each compilation parses fresh source; shared legs reuse a minimal CoreLib reference, while
pristine legs use fresh CoreLib bytes. This is not CI's full framework/module reference set.
PE metadata checks three types/two nested types/six generic parameters versus one flat type;
emitted assemblies are never loaded, and no mesh, DI scope or collectible ALC is involved.

On September 9, the same source ran under Linux X64 emulation on an ARM Colima host. Both
sequential valid-source arms emitted **4,000/4,000**, with no failures (144.122s default,
144.640s PGO off). The invalid-source control produced four expected diagnostics and four flat
emits. The portable DLL was built with the host's SDK 10.0.400, then ran in the SDK 10.0.401
container; this differs from the new native build. Preliminary emulated build/reporting crashes
are preserved in that commit and are not the Roslyn NRE. These results establish neither a native
failure rate nor a lifecycle cause, and do not justify changing production JIT settings.

Platform documentation: `Doc/Architecture/StandaloneRoslynEmitDiagnostic`.
