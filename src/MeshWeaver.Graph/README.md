# MeshWeaver.Graph

Core graph node type system for MeshWeaver. Registers all built-in node types (Markdown, Code, Agent, User, Group, Role, etc.), provides layout areas for viewing and editing nodes, and supports dynamic node type compilation from C# scripts.

## Features

- `AddGraph()` registers 20+ built-in node types: Markdown, Code, Agent, User, VUser, Group, Role, AccessAssignment, GroupMembership, Notification, Approval, Activity, and more
- Layout areas for node views: Overview, Create, Delete, Edit, Settings, Import/Export, Version, Comments, Activity, Access Control
- `MeshHubBuilder` for fluent hub configuration with data type registration and navigation
- Dynamic node type compilation via `MeshNodeCompilationService` using Roslyn C# scripting
- Node type registry with query routing rules for partition and satellite table resolution
- Autocomplete providers for mesh node paths and unified `@` references
- Embedded node type icons served as content collections

## Usage

```csharp
// Register all built-in graph node types
builder.AddGraph();

// Configure a mesh hub with graph navigation
configuration.ConfigureMeshHub()
    .WithDataType<MyCustomType>()
    .WithMeshNavigation()
    .Build();
```

## Dependencies

- `MeshWeaver.Data` -- CRUD operations and data source configuration
- `MeshWeaver.Layout` -- framework-agnostic UI control abstractions
- `MeshWeaver.Messaging.Hub` -- message routing and handler registration
- `MeshWeaver.Markdown` -- markdown content parsing
- `MeshWeaver.Kernel` / `MeshWeaver.Kernel.Hub` -- C# script execution
- `Microsoft.CodeAnalysis.CSharp.Scripting` -- Roslyn for dynamic type compilation
