---
Name: Node Types
Category: Documentation
Description: Design, compile, test, and extend node types — the building blocks that give mesh data its shape and behavior.
---

A **node type** is a first-class citizen of the mesh: it is itself a `MeshNode` whose `nodeType` field is the literal `"NodeType"` and whose `Content` is a `NodeTypeDefinition`. This means you can browse it, query for it (`nodeType:NodeType`), version it, grant access on it, and open it in the navigator just like any other node.

> A data node's `nodeType` field is the **path** to the type node that defines its shape. The Settings → Metadata panel links straight to it.

Node types are compiled on demand from `Source/*.cs` files. You never redeploy the portal to add or change one — the runtime compiles, caches, and versions the assembly for you.

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

### [Creating Node Types](CreatingNodeTypes)

The full walkthrough: `Source/` folder layout, the content record, reference data, the NodeType JSON, CSV data, layout areas, and child types. **Start here.**

### [NodeType Configuration Reference](NodeTypeConfiguration)

JSON schema reference for the `NodeTypeDefinition` object: every field, its type, and when to use it.

### [NodeType Compilation & Releases](../Architecture/NodeTypeCompilation)

The runtime lifecycle — how a compile is triggered, how to watch progress, how to cancel, where releases live, and how to pin an instance to a fixed release. Covers the verify-before-skip rules (including framework-version freezing) that decide when a recompile is required.

### [NuGet Packages](NodeTypeWithNuGet)

Pull in third-party libraries (Math.NET, Markdig, and more) by dropping a `#r "nuget:..."` directive at the top of any source file.

### [Testing Node Types](NodeTypes/Testing)

Stand up a `MonolithMeshTestBase` test project against a samples directory, render layout areas against a real client, and assert on the streaming response.

---

## Prerequisites

This chapter assumes familiarity with [Data Modeling](DataModeling), [Unified Path](UnifiedPath), and [CRUD](CRUD).

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
