---
Name: MeshWeaver 3.0.0
Category: Release Notes
Description: Release notes for MeshWeaver 3.0.0 — LSP-style language services, live Roslyn diagnostics, and pre-flight code checking for NodeType authoring.
Icon: Rocket
---

# MeshWeaver 3.0.0

## What's New

### LSP-style Language Services for NodeType Authoring

NodeType authoring gets a full-stack language intelligence upgrade in 3.0. Roslyn-backed in-process language services now run over every NodeType's live `CSharpCompilation`, delivering speculative pre-flight diagnostics, hover (QuickInfo), and code completions — before a single line hits the compiler.

The implementation is unified: `IMeshLanguageService` (declared in `MeshWeaver.Mesh.Contract`, implemented in `MeshWeaver.Graph`) drives all three surfaces below from a single backend.

#### Coder Agent — Pre-flight LSP Plugin

A new `Lsp` plugin exposes four tools to any agent that opts in via `plugins: - Lsp`:

| Tool | Purpose |
|---|---|
| `LspCheckNode` | Full-substitution pre-flight check — rebuilds the whole source set with one file substituted |
| `LspDiagnosticsForNode` | Returns current Roslyn diagnostics for a node |
| `LspHoverForNode` | QuickInfo hover for a symbol at a given position |
| `LspCompletionsForNode` | Completion suggestions at a given position |

The Coder agent uses `LspCheckNode` as a pre-flight loop before every non-trivial source `Patch`, eliminating the `Patch → Compile → Recycle → fix` round-trips that dominated CI failures.

> **Full-substitution semantics.** `LspCheckNode` rebuilds the entire NodeType source set with the proposed file substituted in, so it catches cross-file breakage — for example, renaming a type in file A that file B still references. Single-file isolation would miss this.

#### External MCP Clients

The same four tools (`LspCheckNode`, `LspDiagnosticsForNode`, `LspHoverForNode`, `LspCompletionsForNode`) are exposed on `McpMeshPlugin`, making them available to Claude Code and any other MCP-compatible client.

#### Monaco Editor in the Portal

The **Edit** view on any `Code` MeshNode under a NodeType's `Source/` subtree now shows live Roslyn squiggles. The wiring is debounced (~300 ms) and uses the existing Blazor JS interop over SignalR — no new transport, no `monaco-languageclient`. Opt-in is per-`CodeEditorControl` via the new `LanguageServer` property.

#### NuGet Reference Support

> **`#r "nuget:..."` in pre-flight checks.** `LspCheckNode` strips `#r` directives and resolves any new packages through the same `INuGetAssemblyResolver` the production compile uses. The proposed source is therefore checked against exactly the references it will see at build time — no surprises after commit.

---

**See also:** [Language Services](xref:Architecture/LanguageServices) · [Coder workflow](xref:Agent/Coder)
