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

## Exit codes — and why nothing may throw out of `Main`

| code | meaning |
|---|---|
| `0` | green |
| `1` | the gate ran and something failed (or an allow entry went stale) |
| `2` | bad usage / bad configuration — an unknown argument, an option with no value, an `--allow` path that does not exist or does not parse |
| `70` | an unanticipated failure; the whole exception is printed above it |

🚨 **An exception must never escape `Main` (#1741).** Every consumer runs this binary as a
container's PID 1 (`docker run … --entrypoint /app/mw-plugin-test`). There, an unhandled exception
does not end the process — the runtime prints the trace and calls `abort()`, whose SIGABRT the
kernel **discards** for a PID-namespace init with the default disposition (`SIGNAL_UNKILLABLE`);
`abort()` falls through to its trap instruction, the runtime's SIGTRAP handler returns to the
instruction that trapped, and the main thread re-traps forever. Measured 2026-08-17: two containers
"Up" 36 and 57 minutes at ~100% CPU each, whose entire output was one `FileNotFoundException`
printed in their first second. On CI that reads as a **hang**, burning the job's whole timeout and
reporting nothing about the bad argument that caused it.

So every failure becomes a message plus an exit code, `Program.cs` carries a top-level guard for the
ones nobody anticipated, and the workflows pass **`docker run --init`** — which covers what a
`catch` cannot reach (a stack overflow, an OOM abort, an unhandled throw on a background thread).
`StartupFailureProcessTest` pins all of this on the real process, with a bounded wait: an unbounded
one would reproduce the bug instead of catching it.

## `--allow` — a MISSING ratchet is a configuration error, not an empty one

`--allow <file>` names the known-debt ratchet. A path that does not exist is refused (exit `2`) with
one actionable line rather than read as an empty list. Substituting an empty list would be the
*stricter* verdict — with no entries every failure is a new failure, so it could never turn a red run
green — but it would make the gate's own configuration unverifiable: `known-debt allowlist: 0
entr(ies)` would then mean either "the ratchet is empty" or "the gate never found the ratchet you
passed", and nothing in the run could tell those apart. That is the shape the node-repo CI policy
exists to forbid: *a gate that cannot read its input must not look like a gate that passed.*

An empty ratchet has two honest spellings — an **empty file** at that path, or **omitting `--allow`**
(which the reusable `node-repo-gate.yml` already does when a repo configures no allow file).
