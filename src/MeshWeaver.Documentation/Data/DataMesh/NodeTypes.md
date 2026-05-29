---
Name: Node Types
Category: Documentation
Description: Everything about designing, compiling, referencing packages from, and testing node types.
---

A **node type** is itself a `MeshNode` — its *own* `nodeType` is the literal `"NodeType"` and its `Content` is a `NodeTypeDefinition`. Its shape is defined by a C# record and its behavior (layouts, data sources, initial data) is declared by configuration code. Because a type is just another node, you browse it, query for it (`nodeType:NodeType`), version it, grant access on it, and open it in the navigator like any other node — and a data node's `nodeType` field is the **path** to the type node that gives it its shape (the Settings → Metadata view links straight to it). Node types are compiled on demand from `Source/*.cs` files; you never need to redeploy the portal to add or change one.

This chapter pulls together everything a node-type author needs:

- **[Creating Node Types](CreatingNodeTypes)** — the walkthrough: `Source/` folder layout, the content record, reference data, the NodeType JSON, CSV data, layout areas, and child types. Start here.
- **[NodeType Configuration Reference](NodeTypeConfiguration)** — JSON schema reference for the NodeType definition itself.
- **[NodeType Compilation & Releases](../Architecture/NodeTypeCompilation)** — the runtime lifecycle: how a compile is triggered, how to watch progress, how to cancel, where releases live, how to pin an instance to a fixed release, and the verify-before-skip rules (including framework-version freezing) that decide when a NodeType is recompiled.
- **[NuGet Packages](NodeTypeWithNuGet)** — add `#r "nuget:..."` at the top of any `Source/*.cs` to pull in third-party libraries (Math.NET, Markdig, …). No redeploy, no .NET SDK on the container.
- **[Testing Node Types](NodeTypes/Testing)** — how to stand up a `MonolithMeshTestBase` test project against a samples directory, render layout areas against a real client, and assert on the streaming response.

The chapter assumes you are comfortable with [Data Modeling](DataModeling), [Unified Path](UnifiedPath), and [CRUD](CRUD).
