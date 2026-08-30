---
Name: The Build Process — compile and test as a dependency cascade
Category: Architecture
Description: How a node repo is built — per package, compile AND run tests, as a reactive cascade over the package dependency network, inside the container the platform build produced, with no mesh import and no core source checkout. The design, the invariants, the timings it reports, and what the mesh lane still owns.
Icon: Hammer
---

# The Build Process — compile and test as a dependency cascade

**Decision (maintainer, 2026-08-30).** A node repo is built by *our own* build runner and test
runner, not by `dotnet test` over xUnit projects that reference a core source checkout. The runner
is the platform's own container (`mw-plugin-test`, the image the platform BUILD produces), the
verb is `build`, and the lane that starts it is one small workflow.

```
mw-plugin-test build <repo-root> [<package>... | all] [--module <dll>]... [--out <dir>] \
    [--report <file>] [--max-parallel <n>] [--case-timeout <s>] [--no-tests] [--source-sha <sha>]
```

## The invariants

1. **Build means compile AND run tests.** They are one unit per package; there is no "it compiled"
   verdict without the tests, and no test run without the compile that produced its assembly.
2. **A cascade, not a schedule.** Every package has a result stream. A package *observes* the
   streams of the packages it `requires` and starts itself the moment the last one completes
   green. No level table, no topological list: the graph is the schedule, so packages with no
   edge between them build at the same time (bounded by `--max-parallel`), and a package with
   three dependencies starts the instant the slowest lands.
3. **On red we break; on green we continue.** A package whose dependency did not end green never
   starts: it completes at once as *blocked by \<dependency\>*, and that verdict cascades. A failure
   is reported exactly once, where it happened; everything above it reads "blocked by X", never a
   second, derived failure. A cycle is refused up front and named.
4. **One execution per package, however many dependents.** A package's stream is shared
   (`PublishLast`): the first subscriber starts the work, every later subscriber gets the same
   single result, and nothing is rebuilt because two packages both required it.
5. **No mesh, no import.** Sources are read from the git checkout on disk and composed into Roslyn
   compilations *exactly as the portal composes them* — the same skeleton, the same options, the
   image's `/app` as the reference set (`NodeSetCompiler`, the code path the portals run at
   runtime). Each package additionally compiles against the assemblies its dependency packages
   just emitted. Grains cannot carry a Roslyn workload; a build process can.
6. **Input is one or many packages, or `all`.** Naming packages selects them *and* their transitive
   requirements inside the repo, so a single package builds on freshly built dependencies rather
   than on nothing. `all` (the default) is the full rebuild — the platform-rebuild case, where
   every package must be re-proven against the new image.
7. **Timings are part of the result.** Every package carries when it became *ready* (its last
   dependency finished), when it *started* (a slot was free) and when it *finished*, plus the
   compile and test splits and per-type compile times. The report prints them, the **critical
   path** (the chain whose serial length is the wall-clock floor) and the parallel speed-up;
   `--report` writes it all as JSON, and the lane renders it into the job summary.
8. **Fails red, never skips.** The lane asserts its inputs first; an image that does not know the
   verb exits 2 and the step names the fix. Nobody reads a green that ran nothing.

## Tests without a mesh

The in-mesh test convention is `<Type>/Test/*.cs`: **static classes whose public static
parameterless methods throw on failure**, listed by a `Tests` layout area. The runner executes
those methods straight from the emitted assembly in a collectible load context, each case timed
and capped by `--case-timeout`. A case that takes a host (the layout-area aggregator, anything
needing a hub) is **counted and named as `needs-mesh`**, never dropped; the gate
(`mw-plugin-test <root> --seed <out>`) still runs those, seeded from the build's `--out` so
nothing is compiled twice.

## The parity flag

The portal reaches other packages' types by `shared=` *source inclusion*, never by referencing
their emitted assemblies. This build references them (the maintainer's instruction: use the
references the dependency packages produce). A type whose emitted assembly turns out to *bind* a
dependency package's assembly is therefore green here on grounds the portal does not have, and the
report marks it `binds-dependency-assembly` — visible in the table, not discovered as a
`CompileError` in production.

## What this replaces, and what it retires

- **`MeshWeaver.Fixture` and the two TestBase assemblies are the platform's own test support.**
  They live under `test/` and are never packed or published (they were a NuGet package only
  because they once lived under `src/`). A node repo does not reference them and does not need a
  `$(MeshWeaverRoot)` source checkout to test: the platform is the image, pinned by digest.
- **xUnit projects in node repos** are the migration's subject. The measured shapes (core:
  1,205 test files, 6,295 facts; 535 of those files mesh- or hub-based) convert mechanically once
  the runner offers one primitive — a per-class mesh factory replacing the `MonolithMeshTestBase`
  constructor — plus a log sink for `ITestOutputHelper`, loops for `[Theory]` rows and a classified
  skip. Substrate suites (Postgres via Testcontainers, Orleans TestingHost, `HtmlRenderer`) run in
  the runner *process*, never in a grain. Browser E2E stays a browser lane.

## Where the pieces are

| piece | where |
|---|---|
| the cascade (generic, unit-tested) | `tools/MeshWeaver.PluginTester/Cascade.cs` |
| the `build` verb | `tools/MeshWeaver.PluginTester/CascadeBuild.cs`, `Program.cs` |
| static test execution | `tools/MeshWeaver.PluginTester/StaticTestRunner.cs` |
| the compile composition it reuses | `MeshWeaver.Compiler` — `NodeSetCompiler`, `CompileReferences` |
| the lane (node repos) | `.github/workflows/build-cascade.yml` in each node repo |
| the tester's README | `tools/MeshWeaver.PluginTester/README.md` (`build`) |
