---
Name: MeshWeaver 3.0.0
Category: Release Notes
Description: Release notes for MeshWeaver 3.0.0
Icon: Rocket
---

# MeshWeaver 3.0.0

## Highlights

### LSP-style language services for NodeType authoring

Roslyn-backed in-process language services are now available over every
NodeType's live `CSharpCompilation` — speculative pre-flight diagnostics,
hover (QuickInfo), and code completions. Three surfaces share one backend
(`IMeshLanguageService` in `MeshWeaver.Mesh.Contract`, in-process implementation
in `MeshWeaver.Graph`):

- **Coder agent** — a new `Lsp` plugin (mounted only on agents that opt in via
  `plugins: - Lsp`) exposes `LspCheckNode`, `LspDiagnosticsForNode`,
  `LspHoverForNode`, `LspCompletionsForNode`. Coder uses this as a pre-flight
  loop before every non-trivial source `Patch`, eliminating the
  `Patch → Compile → Recycle → fix` cycles that dominated CI failures.
- **External MCP clients** (Claude Code, etc.) — the same four tools are
  exposed on `McpMeshPlugin`.
- **Monaco editor in the portal** — the **Edit** view on any `Code` MeshNode
  under a NodeType's `Source/` subtree now shows live Roslyn squiggles. The
  wiring is debounced (~300ms) and uses the existing Blazor JS interop over
  SignalR — no new transport, no `monaco-languageclient`. Opt-in is
  per-`CodeEditorControl` via a new `LanguageServer` property.

**`#r "nuget:..."` support**: the speculative pre-flight strips `#r` directives
and resolves any new packages through the same `INuGetAssemblyResolver` the
production compile uses, so the proposed source is checked against the same
references it'll see at build time.

**Full-substitution semantics**: `LspCheckNode` rebuilds the whole NodeType
source set with the one proposed file substituted in — catches cross-file
breakage (rename a type in A, B still references the old name) that single-file
isolation would miss.

Full reference: [Language Services](xref:Architecture/LanguageServices) ·
Coder workflow: [Coder.md](xref:Agent/Coder).
