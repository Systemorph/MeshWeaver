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
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="290" y="20" width="180" height="64" rx="10" fill="#1e88e5"/>
  <text x="380" y="47" text-anchor="middle" fill="#fff" font-weight="bold">IMeshLanguageService</text>
  <text x="380" y="66" text-anchor="middle" fill="#fff" font-size="11">MeshWeaver.Graph</text>
  <rect x="80" y="18" width="160" height="44" rx="10" fill="#2a2a2a" stroke="currentColor" stroke-opacity=".25"/>
  <text x="160" y="36" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="11">NodeType</text>
  <text x="160" y="52" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="11">CSharpCompilation</text>
  <line x1="240" y1="40" x2="288" y2="47" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="170" width="180" height="110" rx="10" fill="#43a047"/>
  <text x="150" y="196" text-anchor="middle" fill="#fff" font-weight="bold">Coder Agent</text>
  <text x="150" y="214" text-anchor="middle" fill="#fff" font-size="11">Lsp Plugin</text>
  <text x="150" y="234" text-anchor="middle" fill="#dcedc8" font-size="11">LspCheckNode (pre-flight)</text>
  <text x="150" y="250" text-anchor="middle" fill="#dcedc8" font-size="11">LspDiagnosticsForNode</text>
  <text x="150" y="266" text-anchor="middle" fill="#dcedc8" font-size="11">Hover · Completions</text>
  <rect x="290" y="170" width="180" height="110" rx="10" fill="#8e24aa"/>
  <text x="380" y="196" text-anchor="middle" fill="#fff" font-weight="bold">MCP Clients</text>
  <text x="380" y="214" text-anchor="middle" fill="#fff" font-size="11">McpMeshPlugin</text>
  <text x="380" y="234" text-anchor="middle" fill="#e1bee7" font-size="11">LspCheckNode</text>
  <text x="380" y="250" text-anchor="middle" fill="#e1bee7" font-size="11">LspDiagnosticsForNode</text>
  <text x="380" y="266" text-anchor="middle" fill="#e1bee7" font-size="11">Hover · Completions</text>
  <rect x="520" y="170" width="180" height="110" rx="10" fill="#f57c00"/>
  <text x="610" y="196" text-anchor="middle" fill="#fff" font-weight="bold">Monaco Editor</text>
  <text x="610" y="214" text-anchor="middle" fill="#fff" font-size="11">Code MeshNode</text>
  <text x="610" y="234" text-anchor="middle" fill="#fff3e0" font-size="11">Live Roslyn squiggles</text>
  <text x="610" y="250" text-anchor="middle" fill="#fff3e0" font-size="11">Debounced (~300 ms)</text>
  <text x="610" y="266" text-anchor="middle" fill="#fff3e0" font-size="11">Blazor JS / SignalR</text>
  <line x1="320" y1="84" x2="200" y2="170" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="84" x2="380" y2="170" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="440" y1="84" x2="560" y2="170" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>
*`IMeshLanguageService` is the single Roslyn-backed backend powering the Coder agent LSP plugin, external MCP clients, and the Monaco editor in the portal.*

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

**See also:** [Language Services](/Doc/Architecture/LanguageServices) · [Coder workflow](/Agent/Coder)
