---
Name: Node Types
Category: Documentation
Description: Everything about designing, compiling, referencing packages from, and testing node types.
---

A **node type** is a piece of data living in the mesh whose shape is defined by a C# record and whose behavior (layouts, data sources, initial data) is declared by configuration code. Node types are compiled on demand from `_Source/*.cs` files; you never need to redeploy the portal to add or change one.

This chapter pulls together everything a node-type author needs:

- **[Creating Node Types](CreatingNodeTypes)** — the walkthrough: `_Source/` folder layout, the content record, reference data, the NodeType JSON, CSV data, layout areas, and child types. Start here.
- **[NodeType Configuration Reference](NodeTypeConfiguration)** — JSON schema reference for the NodeType definition itself.
- **[NuGet Packages](NodeTypeWithNuGet)** — add `#r "nuget:..."` at the top of any `_Source/*.cs` to pull in third-party libraries (Math.NET, Markdig, …). No redeploy, no .NET SDK on the container.
- **[Testing Node Types](NodeTypes/Testing)** — how to stand up a `MonolithMeshTestBase` test project against a samples directory, render layout areas against a real client, and assert on the streaming response.

The chapter assumes you are comfortable with [Data Modeling](DataModeling), [Unified Path](UnifiedPath), and [CRUD](CRUD).
