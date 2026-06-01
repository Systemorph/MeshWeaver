---
Name: Data Configuration Guide
Category: Documentation
Description: Configure local data sources with seeded reference data and synchronize data across related hubs in the mesh.
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

MeshWeaver gives every hub its own data layer — locally owned collections, initial seed data, and live synchronization from related hubs. This guide walks through the three core configuration primitives and shows how they compose into a working cross-hub data pipeline.

## Core Primitives

| API | Purpose |
|-----|---------|
| `AddSource` | Register a local data collection within a hub |
| `WithInitialData` | Seed a collection with predefined records at startup |
| `AddHubSource` | Pull a live-synchronized copy of a collection from another hub |

---

## Data Model Relationships

```mermaid
classDiagram
    class Project {
        +string Id
        +string Name
        +string Description
        +ProjectStatus Status
    }
    class Todo {
        +string Id
        +string Title
        +TodoStatus Status
    }
    class Status {
        +string Id
        +string Name
        +string Description
        +int Order
        +static All
    }
    Project --> Status : owns
    Todo --> Status : syncs from Project
```

The `Status` type lives in the Project hub and flows into the Todo hub via `AddHubSource`. Neither hub has to duplicate business logic; the Todo hub simply declares a dependency on the parent's collection.

---

## Data Flow Overview

```mermaid
graph LR
    subgraph "Project Hub"
        PS[Status Data]
    end
    subgraph "Todo Hub"
        TS[Status Data]
    end
    PS -->|AddHubSource| TS
```

---

## Configuring a Local Source

### Define the data model

Place your data model in `Source/Status.cs`. Using a record rather than an enum gives you richer metadata — descriptions, display order, and future extensibility without recompilation.

```csharp
public record Status
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int Order { get; init; }

    public static readonly Status Planning = new()
    {
        Id = "Planning", Name = "Planning",
        Description = "Project is in planning phase", Order = 1
    };

    public static readonly Status Active = new()
    {
        Id = "Active", Name = "Active",
        Description = "Project is actively being worked on", Order = 2
    };

    // ... additional status values

    public static IEnumerable<Status> All => new[]
    {
        Planning, Active, OnHold, Completed, Cancelled
    };
}
```

### Wire up the source in the NodeType configuration

```csharp
config => config
    .WithContentType<Project>()
    .AddData(data => data
        .AddSource(source => source
            .WithType<Status>(t => t.WithInitialData(Status.All))))
    .AddLayout(layout => layout.AddDefaultLayoutAreas())
```

`WithInitialData` seeds the collection on first activation. Subsequent starts do not re-insert records that already exist in persistence.

---

## Synchronizing Data from a Parent Hub

### When to use `AddHubSource`

Use `AddHubSource` when a child hub needs read access to reference data owned by a parent. The child hub stays up-to-date automatically — no polling, no duplicated ownership.

### Deriving the parent address

Hub addresses are hierarchical path segments. A Todo instance lives at `ACME/ProductLaunch/Todo/AnalystBriefings`; its owning Project hub is two segments up.

```csharp
// Todo instance at: ACME/ProductLaunch/Todo/AnalystBriefings
// Parent Project at: ACME/ProductLaunch
// Formula: remove the last 2 segments (collection name + instance id)

new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray())
```

### Configuration

```csharp
config => config
    .WithContentType<Todo>()
    .AddData(data => data
        .AddHubSource(
            new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray()),
            source => source.WithType<Status>()))
```

---

## Initialization and Synchronization Sequence

When a message reaches a dormant Todo hub, the framework wakes it and completes full initialization — including the cross-hub Status sync — before the request is handled.

```mermaid
sequenceDiagram
    participant Client
    participant Todo as Todo Hub
    participant Storage as Persistence
    participant Project as Project Hub

    Note over Todo: Hub is dormant
    Client->>Todo: GetDataRequest
    rect rgb(240, 248, 255)
        Note over Todo: Initialization starts
        Todo->>Storage: Load Todo data
        Storage-->>Todo: Return Todo records
        Todo->>Project: SubscribeRequest for Status
        Project-->>Todo: DataChangedEvent with Statuses
        Note over Todo: Initialization complete
    end
    Todo-->>Client: Return data
```

After initialization, any `DataChangeRequest` that arrives at the Todo hub is persisted to storage and fanned out to all subscribers.

---

## Live Example

The cell below renders a summary of the configuration patterns covered on this page — a quick reference you can keep open alongside your code.

```csharp --render DataConfigSummary --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("### Data Configuration Quick Reference"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "| Pattern | API | When to use |\n" +
        "|---------|-----|-------------|\n" +
        "| Local collection | `AddSource(...)` | Hub owns the data |\n" +
        "| Seed on startup | `.WithInitialData(records)` | Reference / lookup data |\n" +
        "| Cross-hub sync | `AddHubSource(address, ...)` | Child needs parent's data |\n"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        $"*Rendered at {DateTime.Now:HH:mm:ss}*"))
```

---

## Best Practices

> **Use data models instead of enums.** Records provide descriptions, display order, and localization hooks. They can be extended at runtime without a recompile.

> **Initialize reference data at the owner.** Call `WithInitialData` on the hub that owns the type, then let dependent hubs pull via `AddHubSource`. Avoid seeding the same data in multiple places.

> **Derive addresses dynamically.** Use `config.Address.Segments` to compute relative addresses rather than hardcoding path strings. This makes NodeType configurations portable across namespaces.

> **Keep shared model definitions aligned.** When using `AddHubSource`, both hubs must reference the same `Status` type (same assembly or an identical record definition). A future MeshWeaver release will support shared data model assemblies to eliminate this duplication.

---

## Complete NodeType JSON

### Project Hub

```json
{
  "id": "Project",
  "namespace": "ACME",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config.WithContentType<Project>().AddData(data => data.AddSource(source => source.WithType<Status>(t => t.WithInitialData(Status.All)))).AddLayout(layout => layout.AddDefaultLayoutAreas())"
  }
}
```

### Todo Hub

```json
{
  "id": "Todo",
  "namespace": "ACME/Project",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config.WithContentType<Todo>().AddData(data => data.AddHubSource(new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray()), source => source.WithType<Status>())).AddDefaultLayoutAreas()"
  }
}
```

With this configuration the Todo hub accesses live Status reference data from its parent Project hub, ensuring consistent status options across the entire hierarchy.
