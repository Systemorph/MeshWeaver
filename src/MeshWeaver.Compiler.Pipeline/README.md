# MeshWeaver.Compiler.Pipeline

The **mesh-actor half** of NodeType compilation, factored out of `MeshWeaver.Graph`. It drives the
pure toolchain in `MeshWeaver.Compiler`:

- **The compile actor** — `MeshNodeCompilationService`, `NodeTypeCompilationHelpers`,
  `NodeTypeCompilationActivity`: source discovery against the live mesh, access impersonation,
  IoPool scheduling, compile status write-backs.
- **The assembly cache** — `CompilationCacheService`, `CompilationCacheOptions`,
  `AssemblyCacheRetention`, `CompilationLock`, `CompileThread`.
- **Prebuilt adoption** — `PrebuiltAssemblySeeder`, `NodeTypeAdoptionRegistry`, `NodeTypeBakeStatus`.
- **Failure containment** — `NodeTypeCompileParkRegistry`, `NodeTypeParkedException`,
  `NodeTypeUnparkPostDeletionHandler`, `OverlayHealBudget`.
- **Ordering** — `NodeTypeDependencyGraph`, `RecompileClosure`.
- **Build state at the read seam** — `NodeTypeBuildState`, `NodeTypeCompileState`.
- **Scripting and language services** — `ScriptCompilationService`, `ScriptCodeGenerator`,
  `MeshNodeLanguageService`, `CompletionMemoryStore`, `CompletionUsageIndex`,
  `CellSurfaceAssemblyProvider`.

## 🚨 Why this is not simply part of `MeshWeaver.Compiler`

`MeshWeaver.Compiler` is a **full-MVID toolchain root**
(`FrameworkBuildIdentity.ToolchainRoots`): a body-only change anywhere inside it — or inside
anything it references — re-bakes **every NodeType on every mesh**, with no API change required.
Issue #1707 factored the toolchain out of `MeshWeaver.Graph` precisely so that rule pins a small,
low-churn assembly.

This pipeline is the highest-churn code in the platform. Folding it into `MeshWeaver.Compiler`
would have tripled that assembly (5,542 → 18,891 lines) and made every pipeline commit a
fleet-wide re-bake — and it measurably widened the full-MVID closure
(`FullMvidClosure_IsExactly_TheKnownSet` went red). So the pipeline sits **above** the toolchain and
is surface-hashed, exactly as it was while it lived in `MeshWeaver.Graph`. The full-MVID closure is
unchanged by the split.

Namespaces are unchanged (`MeshWeaver.Graph`, `MeshWeaver.Graph.Configuration`): the assembly moved,
the namespace did not.
