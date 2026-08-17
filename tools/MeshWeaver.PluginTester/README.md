# MeshWeaver.Compiler.Cli (`mw-compiler` / `mw-plugin-test`)

The MeshWeaver content compiler CLI — the same binary the platform CI runs to gate and bake mesh
content (#1707). It stages content from a git checkout, compiles NodeTypes with the
`MeshWeaver.Compiler` toolchain (the identical code path the portals use at runtime), and — with
`--bake-output` — emits prebuilt-assembly bundles keyed by the framework build identity, so
portals adopt instead of recompiling.

Distributed two ways, same binary:

- **dotnet tool** — `dotnet tool install -g MeshWeaver.Compiler.Cli` (command: `mw-compiler`; use
  `--tool-path`/`--local` for CI-scoped installs), for satellite content repos' CI lanes: install
  the version matching your platform, run the bake, no platform-repo artifact download.
- **container image** — the `mw-plugin-test` image the plugin gates run.

Useful entry points:

```
mw-compiler compile <root> --output <dir>   # BAKE: build-step compile, NO mesh (#1763)
mw-compiler <checkout-root>                 # GATE: mesh run — render + Tests areas
mw-compiler <root> --bake-output <dir>      # legacy: the gate's mesh ALSO produces the bundles
mw-compiler --print-framework-identity      # one-line identity + provenance diagnostic
```

## `compile` — the bake, as a build step

`compile` is the compiler-driven bake (#1763). It reads the node files out of the checkout,
resolves each NodeType's `Sources`/`Tests` queries and `@@` includes against that in-memory node
set, compiles with the `MeshWeaver.Compiler` toolchain, emits **DLL + PDB**, and writes the same
`<package>.zip` bundles + `framework-mvid.txt` as before. **No `MeshBuilder`, no `AddGraph()`, no
content import, no hub.** Consumers cannot tell which producer wrote a bundle — and must not.

```
mw-compiler compile <checkout-root> --output <dir> [--source-sha <sha>]
```

Everything else in this binary is the **gate**, which legitimately stands up a mesh: rendering a
layout area and executing a `Tests` area are runtime behaviours; producing an assembly is not. The
gate's own `--bake-output` still works, so lanes can migrate one at a time.

🚨 The two bakes are held equivalent by `BakeEquivalenceTest`
(`test/MeshWeaver.PluginTester.Test`), which bakes one content set BOTH ways and compares the
resolved source sets, the per-type dependency records, the framework identity and the emitted
assemblies' surface. A baker that resolves sources differently fails NOTHING until a page renders
empty in production, so that test — not the speed — is the point. See
`Doc/Architecture/CiContentBake` → "BAKE is a build step; GATE is a mesh run that CONSUMES one".

The framework build identity resolves from the `meshweaver-surface.manifest` packed beside the
binaries — equal by construction with the portals' identity (see
`Doc/Architecture/CiContentBake`), which is what makes the bundles this tool produces adoptable.
