# MeshWeaver.Compiler

The MeshWeaver NodeType compile toolchain, factored out of `MeshWeaver.Graph` (issue #1707) so
the framework build identity's full-MVID rule pins a small, low-churn assembly.

Everything that shapes the GENERATED INPUT of a dynamic NodeType compile lives here:

- **Skeleton generation** — `DynamicMeshNodeAttributeGenerator` emits the
  `MeshNodeProviderAttribute` subclass wrapping the NodeType's `configuration` lambda.
- **Source-query resolution** — `CodeQueryResolver` expands a NodeType's `Sources`/`Tests`
  queries; `NodeCompileShaping` folds query results, filters executables, resolves `@@` includes
  (through a caller-supplied node reader), and combines sources into the compile unit.
- **Reference assembly** — the process-wide TPA baseline plus installed-module composition
  (`CompileReferences`).
- **Generator pipeline** — `#r "nuget:"` shaping and Roslyn source-generator discovery/execution.
- **Emit** — staged, verified, atomic disk emit (`EmitPipeline`), the emit canary, and the
  in-memory emit.
- **Identity** — `FrameworkBuildIdentity` / `FrameworkIdentity`: the one framework build identity
  every compiled NodeType release is pinned to, and the assembly-store filename tag
  (`FileSystemAssemblyStore`).

The mesh-actor half of the pipeline — source discovery against the live mesh, access
impersonation, IoPool scheduling, compile status write-backs — stays in `MeshWeaver.Graph`
(`MeshNodeCompilationService`, `NodeTypeCompilationHelpers`) and orchestrates this toolchain.

Everything in this assembly lives in the `MeshWeaver.Compiler` namespace (unified in #1712 —
swept clean: no in-mesh source and no live-portal content references the moved types by their old
`MeshWeaver.Graph.Configuration` names, so no forwards are carried). The framework identity's
full-MVID rule covers this assembly plus its computed MeshWeaver dependency closure — the
toolchain calls into what it links, so a body-only change in a closure member must recompile
content too.
