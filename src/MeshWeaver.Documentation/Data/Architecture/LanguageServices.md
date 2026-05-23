---
nodeType: Markdown
name: Language Services (LSP-style)
category: Architecture
description: Roslyn-backed in-process language services over a NodeType's live CSharpCompilation — speculative pre-flight diagnostics, hover, completions. Consumed by the Coder agent (Lsp plugin), external MCP clients (McpMeshPlugin), and Monaco live squiggles in the portal Edit view.
icon: /static/NodeTypeIcons/code.svg
---

# Language Services

MeshWeaver exposes Roslyn's language services (diagnostics, hover, completions) over
each NodeType's live `CSharpCompilation`. Three consumers share one in-process backend:

1. The **Coder agent** uses the `Lsp` plugin for **pre-flight diagnostics** before it
   commits a source-file `Patch` — replacing the blind `Patch → Compile → Recycle → fix`
   cycle that previously dominated CI failures.
2. **External MCP clients** (Claude Code, etc.) call the `Lsp*` tools on `McpMeshPlugin`.
3. The **Monaco editor in the portal** shows live squiggles for any `Code` MeshNode
   under a NodeType's `Source/` subtree.

Behind all three: one `IMeshLanguageService` interface, one in-process implementation
that wraps Roslyn's `CompletionService`, `QuickInfoService`, and per-NodeType
`AdhocWorkspace` over the cached `CSharpCompilation` produced by
`MeshNodeCompilationService`.

## Architecture

```
   ┌──────────────────────────────────────────────────────┐
   │       IMeshLanguageService (Mesh.Contract)           │
   │   GetDiagnostics / GetHover / GetCompletions /       │
   │   CheckSpeculative                                   │
   │   — all return IObservable<T>                        │
   └────────────────────────┬─────────────────────────────┘
                            │
                            ▼
   ┌──────────────────────────────────────────────────────┐
   │       MeshNodeLanguageService (Graph)                │
   │   • Per-NodeType AdhocWorkspace, cached by source-   │
   │     versions hash (rebuilt when any source changes). │
   │   • One Document per user source (FilePath = MeshNode│
   │     path) + one __skeleton__ document carrying the   │
   │     generated MeshNodeProviderAttribute boilerplate. │
   │   • Roslyn CompletionService / QuickInfoService /    │
   │     CSharpCompilation.GetDiagnostics queries.        │
   └──┬─────────────────────────────────────────────────┬─┘
      │                                                 │
      │ shared CompilationInputs (per-file syntax       │
      │ trees, MetadataReferences, skeleton source)     │
      │                                                 │
      ▼                                                 ▼
   ┌─────────────────────────────┐   ┌─────────────────────────────┐
   │  MeshNodeCompilationService │   │  SpeculativeCompilation     │
   │  .GetCompilationInputsAsync │   │  .GetDiagnosticsAsync       │
   │  (NodeType source set,      │   │  (substitute one file with  │
   │   @@-includes resolved,     │   │   proposedCode, strip & re- │
   │   NuGet refs resolved,      │   │   resolve #r, rebuild, get  │
   │   skeleton generated)       │   │   diagnostics)              │
   └─────────────────────────────┘   └─────────────────────────────┘
```

**Why a parallel `GetCompilationInputsAsync` distinct from the emit path?** Language
services need per-file syntax trees (so positions map back to what the user is editing),
while the emit path concatenates all sources into one tree to produce a single
assembly. The parallel pipeline keeps the production compile untouched and lets the
language service consume a per-file shape without behavioural risk.

## The MCP / agent surface

Four tools are exposed via `McpMeshPlugin` (external MCP clients) and via the `Lsp`
agent plugin (mounted only on agents that opt in — Coder declares
`plugins: - Lsp`). Method names match across both surfaces so the prompt can
reference one set of names.

| Tool | Purpose | Returns |
|---|---|---|
| `LspCheckNode` | Speculative pre-flight against the NodeType's current source set with one file substituted. Reuses the cached `MetadataReference` set, strips `#r "nuget:..."` directives, resolves any new packages. | `{ok, diagnostics: [...]}` |
| `LspDiagnosticsForNode` | Diagnostics from the NodeType's current cached compilation (no substitution, no compile). Faster than re-`Compile` for "what does Roslyn currently think of this?" | `{ok, diagnostics: [...]}` |
| `LspHoverForNode` | QuickInfo (signature + XML doc summary) at a position, rendered as markdown. | `{markdown}` or `{}` |
| `LspCompletionsForNode` | Code completions at a position, mapped from Roslyn's `WellKnownTags` to LSP-style kinds. | `{items: [{label, kind, insertText, detail?, sortText?}]}` |

Positions are **0-based line/character** (LSP convention). Monaco's 1-based
coordinates are converted at the JS bridge.

`{ok: true}` means there are no `Error`-severity diagnostics — warnings alone don't
fail the check, mirroring how a real `Compile` succeeds with warnings.

## The Coder pre-flight loop

Before any non-trivial source change:

1. Read current source (`Get` if not already in context).
2. `LspCheckNode({nodeTypePath, sourcePath, proposedCode})`.
   - `{ok: true, diagnostics: []}` → safe; persist via `Patch`.
   - `{ok: false, diagnostics: [errors...]}` → fix in head, re-call.
3. Once clean, `Patch` / `Update`.
4. `Compile` + `GetDiagnostics` for the real emit.

**Full-substitution semantics**: `LspCheckNode` rebuilds the whole NodeType source
set with the one proposed file substituted in. This catches the dominant Coder
failure mode where editing one file breaks a sibling (rename a type in A, B's
reference still points at the old name). Single-file isolation would miss this.

**`#r` support**: the proposed source can include `#r "nuget:PackageId, Version"`
directives — the speculative compile strips them and resolves the packages through
the same `INuGetAssemblyResolver` the production compile uses. New diagnostics
reflect what `Compile` will see.

Full Coder workflow is in [Coder.md](xref:Agent/Coder).

## Monaco live diagnostics

The portal's **Edit** view on any `Code` MeshNode under a NodeType's `Source/`
subtree gets live Roslyn squiggles. The wiring:

1. `CodeLayoutAreas.Edit` derives the NodeType path from `host.Hub.Address` (the
   Code node's path) and sets `CodeEditorControl.LanguageServer = new
   CodeEditorLanguageServerConfig(nodeTypePath, sourcePath)`. Standalone Code
   nodes (not under `/Source/`) and non-C# languages skip the opt-in.
2. `CodeEditorView.razor` resolves `IMeshLanguageService` from DI and passes a
   `DiagnosticsCallback = text => languageService.CheckSpeculative(nodeTypePath,
   sourcePath, text)` to `MonacoEditorView`. The `DiagnosticSourcePath` filter
   keeps squiggles scoped to this file (sibling-file diagnostics from the
   full-substitution compile are suppressed).
3. `MonacoEditorView.razor` exposes a `RequestDiagnostics` JSInvokable. The JS
   module debounces `onDidChangeModelContent` (~300ms) and pings .NET with the
   current text.
4. The callback returns `IObservable<DiagnosticInfo[]>`; the first emission is
   pushed to JS via `pushDiagnostics`. JS converts to Monaco `IMarkerData`
   (0→1 coords, severity 0..3 → 1,2,4,8) and calls
   `monaco.editor.setModelMarkers(model, 'meshweaver-lsp', markers)`.

The pattern mirrors the existing reactive completion provider — no new transport,
no monaco-languageclient, no LSP-over-WebSocket. Blazor's existing JS interop
over SignalR carries everything.

## Cache semantics

`MeshNodeLanguageService` caches one `AdhocWorkspace` per NodeType, keyed by the
source-versions snapshot (`{sourcePath → MeshNode.LastModified.Ticks}`). When any
source file changes, the snapshot diverges and the workspace is rebuilt. The
in-process `MeshNodeCompilationService._references` field (TPA + a few well-known
additions) is static and shared across all NodeTypes, so reference resolution
cost is paid once per process.

Speculative compiles do **not** cache — every `LspCheckNode` call rebuilds the
substituted compilation. Cost is dominated by parse + bind + diagnose (~200-500ms
for typical NodeTypes). No emit, no DLL on disk.

## Reactive contract

🚨 **Every `IMeshLanguageService` method returns `IObservable<T>`.** The Roslyn
`*Async` APIs are wrapped at the seam via `Observable.FromAsync(ct => svc.GetXxxAsync(...))`
— same pattern `MeshNodeCompilationService.CompileCore` uses. MCP / agent tool
surfaces bridge to `Task<string>` via `.FirstAsync().ToTask()` (the sanctioned
exception for external-protocol adapters — see [AsynchronousCalls](xref:Architecture/AsynchronousCalls)).
No `await` anywhere in hub-reachable code.

## What's deferred

- **Stage 2: repo-level LSP** for the MeshWeaver framework itself (50+ projects,
  on-disk `.slnx`). Originally planned as a separate CLI host spawning
  `Microsoft.CodeAnalysis.LanguageServer` for Claude Code's consumption.
  Skipped for now — the in-portal surface (Stage 1) covers the immediate Coder
  workflow.
- **Monaco hover + completions**. `MonacoEditorView` only consumes the
  `DiagnosticsCallback` today. Hover and completion providers would be additional
  JSInvokables + Monaco `registerHoverProvider` / `registerCompletionItemProvider`
  calls — but they need overlay support in `IMeshLanguageService` (in-flight
  editor text rather than saved text) before they're useful, which is a separate
  API extension.
- **Cross-NodeType go-to-definition** (jump from a NodeType source into framework
  symbols). Would require unifying the in-portal compilation with a framework
  workspace; not feasible without significant scaffolding.
- **Auto-wired NodeType-hub diagnostics view** (a layout area that surfaces
  `LspDiagnosticsForNode` results live on the NodeType itself). Planned follow-up.

## Where things live

| Concern | File |
|---|---|
| Interface + DTOs | `src/MeshWeaver.Mesh.Contract/Services/IMeshLanguageService.cs` |
| In-process implementation | `src/MeshWeaver.Graph/Configuration/MeshNodeLanguageService.cs` |
| Speculative compile + `#r` handling | `src/MeshWeaver.Graph/Configuration/SpeculativeCompilation.cs` |
| `CompilationInputs` (shared with emit path) | `src/MeshWeaver.Graph/Configuration/CompilationInputs.cs` |
| `GetCompilationInputsAsync` (the per-file pipeline) | `src/MeshWeaver.Graph/Configuration/MeshNodeCompilationService.cs` |
| DI registration | `src/MeshWeaver.Graph/Configuration/GraphConfigurationExtensions.cs` (`AddGraph`) |
| Agent plugin | `src/MeshWeaver.AI/Plugins/LspPlugin.cs` |
| MCP tools | `src/MeshWeaver.Blazor.AI/McpMeshPlugin.cs` (the `Lsp*` methods) |
| Monaco wiring | `src/MeshWeaver.Blazor/Components/Monaco/MonacoEditorView.razor[.js]`, `CodeEditorView.razor` |
| Editor opt-in | `src/MeshWeaver.Layout/CodeEditorControl.cs` (`LanguageServer` property) |
| Edit-view opt-in | `src/MeshWeaver.Graph/CodeLayoutAreas.cs` (`Edit` area) |
| Integration tests | `test/MeshWeaver.Hosting.Monolith.Test/MeshNodeLanguageServiceTest.cs` |
