---
Name: Node Types
Category: Documentation
Description: Design, compile, test, and extend node types — the building blocks that give mesh data its shape and behavior.
---

A **node type** is a first-class citizen of the mesh: it is itself a `MeshNode` whose `nodeType` field is the literal `"NodeType"` and whose `Content` is a `NodeTypeDefinition`. This means you can browse it, query for it (`nodeType:NodeType`), version it, grant access on it, and open it in the navigator just like any other node.

> A data node's `nodeType` field is the **path** to the type node that defines its shape. The Settings → Metadata panel links straight to it.

Node types are compiled on demand from `Source/*.cs` files. You never redeploy the portal to add or change one — the runtime compiles, caches, and versions the assembly for you.
<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="nt-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
    <marker id="nt-arrow-blue" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#1e88e5"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="300" rx="12" fill="#1a2030" opacity="0.6"/>
  <rect x="20" y="40" width="155" height="110" rx="10" fill="#1e2a3a" stroke="#5c6bc0" stroke-width="1.5"/>
  <rect x="20" y="40" width="155" height="34" rx="10" fill="#5c6bc0"/>
  <rect x="20" y="62" width="155" height="12" fill="#5c6bc0"/>
  <text x="97" y="62" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Source/*.cs</text>
  <text x="38" y="96" fill="#b0bec5" font-size="11">C# record  (content)</text>
  <text x="38" y="112" fill="#b0bec5" font-size="11">Layout areas</text>
  <text x="38" y="128" fill="#b0bec5" font-size="11">#r "nuget:…"</text>
  <rect x="20" y="180" width="155" height="80" rx="10" fill="#1e2a3a" stroke="#5c6bc0" stroke-width="1.5"/>
  <rect x="20" y="180" width="155" height="34" rx="10" fill="#5c6bc0"/>
  <rect x="20" y="202" width="155" height="12" fill="#5c6bc0"/>
  <text x="97" y="202" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">NodeType JSON</text>
  <text x="38" y="236" fill="#b0bec5" font-size="11">NodeTypeDefinition</text>
  <text x="38" y="252" fill="#b0bec5" font-size="11">name, icon, CSV data…</text>
  <line x1="97" y1="152" x2="97" y2="178" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#nt-arrow)"/>
  <rect x="250" y="100" width="160" height="100" rx="10" fill="#1b3a1e" stroke="#43a047" stroke-width="2"/>
  <rect x="250" y="100" width="160" height="34" rx="10" fill="#43a047"/>
  <rect x="250" y="122" width="160" height="12" fill="#43a047"/>
  <text x="330" y="122" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Runtime Compiler</text>
  <text x="268" y="158" fill="#b0bec5" font-size="11">Roslyn compile</text>
  <text x="268" y="174" fill="#b0bec5" font-size="11">cache + version</text>
  <text x="268" y="190" fill="#b0bec5" font-size="11">no redeploy needed</text>
  <line x1="176" y1="95" x2="248" y2="140" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="5,3" marker-end="url(#nt-arrow)"/>
  <line x1="176" y1="220" x2="248" y2="160" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="5,3" marker-end="url(#nt-arrow)"/>
  <rect x="480" y="40" width="260" height="60" rx="10" fill="#1e3050" stroke="#1e88e5" stroke-width="2"/>
  <rect x="480" y="40" width="260" height="34" rx="10" fill="#1e88e5"/>
  <rect x="480" y="62" width="260" height="12" fill="#1e88e5"/>
  <text x="610" y="62" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">NodeType MeshNode</text>
  <text x="498" y="90" fill="#b0bec5" font-size="11">nodeType: "NodeType"  ·  Content: NodeTypeDefinition</text>
  <line x1="412" y1="150" x2="478" y2="70" stroke="#1e88e5" stroke-width="2" marker-end="url(#nt-arrow-blue)"/>
  <rect x="480" y="130" width="120" height="50" rx="8" fill="#1e2540" stroke="#26a69a" stroke-width="1.5"/>
  <rect x="480" y="130" width="120" height="28" rx="8" fill="#26a69a"/>
  <rect x="480" y="146" width="120" height="12" fill="#26a69a"/>
  <text x="540" y="150" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Typed Content</text>
  <text x="495" y="173" fill="#b0bec5" font-size="11">C# record ↔ JSON</text>
  <rect x="480" y="200" width="120" height="50" rx="8" fill="#1e2540" stroke="#f57c00" stroke-width="1.5"/>
  <rect x="480" y="200" width="120" height="28" rx="8" fill="#f57c00"/>
  <rect x="480" y="216" width="120" height="12" fill="#f57c00"/>
  <text x="540" y="220" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Layout Areas</text>
  <text x="495" y="243" fill="#b0bec5" font-size="11">C# views in Blazor</text>
  <rect x="620" y="130" width="120" height="50" rx="8" fill="#1e2540" stroke="#8e24aa" stroke-width="1.5"/>
  <rect x="620" y="130" width="120" height="28" rx="8" fill="#8e24aa"/>
  <rect x="620" y="146" width="120" height="12" fill="#8e24aa"/>
  <text x="680" y="150" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Reference Data</text>
  <text x="635" y="173" fill="#b0bec5" font-size="11">CSV seed + NuGet</text>
  <rect x="620" y="200" width="120" height="50" rx="8" fill="#1e2540" stroke="#e53935" stroke-width="1.5"/>
  <rect x="620" y="200" width="120" height="28" rx="8" fill="#e53935"/>
  <rect x="620" y="216" width="120" height="12" fill="#e53935"/>
  <text x="680" y="220" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Data Nodes</text>
  <text x="635" y="243" fill="#b0bec5" font-size="11">nodeType: path/to/type</text>
  <line x1="610" y1="102" x2="540" y2="128" stroke="#90a4ae" stroke-width="1.2" marker-end="url(#nt-arrow)"/>
  <line x1="610" y1="102" x2="540" y2="198" stroke="#90a4ae" stroke-width="1.2" marker-end="url(#nt-arrow)"/>
  <line x1="610" y1="102" x2="680" y2="128" stroke="#90a4ae" stroke-width="1.2" marker-end="url(#nt-arrow)"/>
  <line x1="610" y1="102" x2="680" y2="198" stroke="#90a4ae" stroke-width="1.2" marker-end="url(#nt-arrow)"/>
</svg>
*A node type lives in the mesh as a `MeshNode`, compiled on demand from its `Source/*.cs` files — no redeploy required. It gives every data node a typed content record, C# layout areas, and optional reference data.*

---

## What a Node Type Gives You

| Capability | What it means |
|---|---|
| **Typed content** | A C# record you design; the mesh serializes it to JSON and back. |
| **Layout areas** | Razor-free views declared in C# — rendered in the browser via Blazor. |
| **Reference data** | CSV or inline data seeded into the graph at first creation. |
| **Child types** | Nested node types that live under a parent (e.g. line items under an invoice). |
| **NuGet packages** | `#r "nuget:..."` at the top of any `Source/*.cs` file — no redeploy, no SDK on the container. |

---

## Where to Start

The guides in this chapter are ordered by task. If you are new to node types, work through them in the order shown below.

### [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes)

The full walkthrough: `Source/` folder layout, the content record, reference data, the NodeType JSON, CSV data, layout areas, and child types. **Start here.**

### [NodeType Configuration Reference](/Doc/DataMesh/NodeTypeConfiguration)

JSON schema reference for the `NodeTypeDefinition` object: every field, its type, and when to use it.

### [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation)

The runtime lifecycle — how a compile is triggered, how to watch progress, how to cancel, where releases live, and how to pin an instance to a fixed release. Covers the verify-before-skip rules (including framework-version freezing) that decide when a recompile is required.

### [NuGet Packages](/Doc/DataMesh/NodeTypeWithNuGet)

Pull in third-party libraries (Math.NET, Markdig, and more) by dropping a `#r "nuget:..."` directive at the top of any source file.

### [Calling Python](/Doc/DataMesh/CallingPython)

Compute a layout area's content in an external `python3` process through the bounded Process I/O pool — reactive end to end, degrading gracefully when Python is absent.

### [A pandas node in Python](/Doc/DataMesh/PythonPandasNode)

Connect a Python process to the mesh as a participant, load a CSV file kept in mesh content into a live `pandas.DataFrame`, control that object over the mesh, and render its state back as a real `DataGridControl` — GUI in C#, backend in Python.

### [A standalone hub in Python](/Doc/DataMesh/PythonStandaloneHub)

Program a complete MeshWeaver hub in Python — its own address, message handlers, owned state — connect it to the mesh, load information from the mesh and save results back.

### [Fine-tuning an LLM on mesh content](/Doc/DataMesh/PythonFineTuning)

Distill the documentation into a training dataset kept in mesh content, fine-tune a language model on it in Python, and stream training progress live back onto a mesh node.

### [Testing Node Types](/Doc/DataMesh/NodeTypes/Testing)

Stand up a `MonolithMeshTestBase` test project against a samples directory, render layout areas against a real client, and assert on the streaming response.

---

## Prerequisites

This chapter assumes familiarity with [Data Modeling](/Doc/DataMesh/DataModeling), [Unified Path](/Doc/DataMesh/UnifiedPath), and [CRUD](/Doc/DataMesh/CRUD).

---

## Live Example — Node Type as a Mesh Node

The snippet below shows what a query for node types looks like. Because every node type is a plain `MeshNode`, the same query API works for types and data alike.

```csharp --render NodeTypeQueryDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "**Node types are queryable like any other node.**\n\n" +
        "Try: `nodeType:NodeType` in the search bar — every type in the mesh appears as a result.\n\n" +
        "Each result row has a `nodeType` of `NodeType` and a `Content` of `NodeTypeDefinition`."
    ))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        $"_This cell rendered at {DateTime.Now:HH:mm:ss}._"
    ))
```
