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
mw-compiler <checkout-root>                 # the content gate: compile the repo's node trees
mw-compiler <root> --bake-output <dir>      # …and persist bundles + framework-mvid.txt
mw-compiler --print-framework-identity      # one-line identity + provenance diagnostic
```

The framework build identity resolves from the `meshweaver-surface.manifest` packed beside the
binaries — equal by construction with the portals' identity (see
`Doc/Architecture/CiContentBake`), which is what makes the bundles this tool produces adoptable.
