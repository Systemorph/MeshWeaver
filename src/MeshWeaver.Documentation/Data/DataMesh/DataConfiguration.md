---
Name: Data Configuration Guide
Category: Documentation
Description: How to configure data sources, initialization, and hub-to-hub synchronization
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

This guide explains how to configure data in message hubs, including data sources with initialization and hub-to-hub data synchronization.

# Overview

MeshWeaver provides flexible data configuration patterns:
- **AddSource**: Configure local data sources with optional initialization
- **AddHubSource**: Synchronize data from parent or related hubs
- **AddPartitionedHubSource**: Aggregate data from multiple child hubs by partition
- **WithVirtualDataSource**: Load data from reactive streams (mesh queries, node content, etc.)
- **WithInitialData**: Seed data sources with predefined records

# Data Model Relationships

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

# Data Flow Architecture

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

# Configuring Data Sources

## AddSource with WithInitialData

Use `AddSource` to configure local data sources. The `WithInitialData` method seeds the source with predefined records.

### Example: Status Data Model

First, define the data model in your `_Source/Status.cs` file:

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

### Configuration in NodeType

Add the data configuration to your NodeType's configuration string:

```csharp
config => config
    .WithContentType<Project>()
    .AddData(data => data
        .AddSource(source => source
            .WithType<Status>(t => t.WithInitialData(Status.All))))
    .AddLayout(layout => layout.AddDefaultLayoutAreas())
```

# Hub-to-Hub Data Synchronization

## AddHubSource

Use `AddHubSource` to synchronize data from a parent or related hub. This is useful when child hubs need access to reference data owned by a parent.

### Address Derivation

When a Todo hub needs to access Status data from its parent Project hub, compute the parent address:

```csharp
// Todo instance at: ACME/ProductLaunch/Todo/AnalystBriefings
// Parent Project at: ACME/ProductLaunch
// Formula: Remove last 2 segments (Todo collection + instance id)

new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray())
```

### Configuration Example

```csharp
config => config
    .WithContentType<Todo>()
    .AddData(data => data
        .AddHubSource(
            new Address(config.Address.Segments.Take(config.Address.Segments.Length - 2).ToArray()),
            source => source.WithType<Status>()))
    ```

## Synchronization Flow

This example shows a scenario where the Todo hub is dormant (not in memory). When a message arrives, the hub is woken up and initialized before processing the request.

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

When a `DataChangeRequest` arrives at the Todo hub, changes are persisted to storage and synced out to subscribers.

# Best Practices

1. **Use data models instead of enums**: Data models provide richer metadata (descriptions, display order) and can be extended without recompilation

2. **Initialize reference data at the source**: Use `WithInitialData` on the hub that owns the data, then sync to dependent hubs

3. **Derive addresses dynamically**: Use `config.Address.Segments` to compute relative addresses between hubs

4. **Keep data models synchronized**: When using `AddHubSource`, ensure the data model definition exists in both hubs

> **Note**: Future versions of MeshWeaver will support shared data model assemblies to avoid duplicating model definitions across hubs.

# Complete Example

## Project Hub Configuration

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

## Todo Hub Configuration

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

This configuration enables the Todo hub to access Status reference data from its parent Project hub, ensuring consistent status options across the hierarchy.

# Partitioned Hub Data Source

## AddPartitionedHubSource

Use `AddPartitionedHubSource` to aggregate data from multiple child hubs into a parent hub. Each child hub owns its own data; the parent reads from them as live streams. This is the data mesh pattern for cross-domain data composition.

### When to Use

- A group/parent hub needs to consolidate data from multiple domain hubs
- Each domain hub independently owns and manages its data
- The consolidated view should update reactively when any domain's data changes

### Configuration Example

```csharp
config => config
    .AddData(data => data
        .AddPartitionedHubSource<Address>(
            c => c.WithType<DataCube>(
                    row => (Address)("Namespace/" + row.Partition + "/Analysis"))
                .InitializingPartitions(
                    (Address)"Namespace/PartitionA/Analysis",
                    (Address)"Namespace/PartitionB/Analysis")))
```

The `WithType<T>` lambda maps each data row to the hub address that owns it. `InitializingPartitions` lists the addresses to subscribe to on startup.

### Data Flow

```mermaid
graph LR
    A[Hub A<br/>owns data] -->|stream| P[Parent Hub<br/>PartitionedHubDataSource]
    B[Hub B<br/>owns data] -->|stream| P
    C[Hub C<br/>owns data] -->|stream| P
    P --> V[Consolidated View]

    classDef domain fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef parent fill:#e6f4ea,stroke:#34a853,color:#333
    class A,B,C domain
    class P,V parent
```

Adding a new partition is a single address — no new ETL pipeline, no data copying.

# Virtual Data Sources

## WithVirtualDataSource

Use `WithVirtualDataSource` to load data from reactive `IObservable` streams — e.g., mesh node queries, computed data, or embedded node content.

### Loading from Mesh Node Queries

```csharp
config => config
    .AddData(data => data
        .WithVirtualDataSource("ReferenceData", vs => vs
            .WithVirtualType<ExchangeRate>(workspace =>
            {
                var meshQuery = workspace.Hub.ServiceProvider
                    .GetRequiredService<IMeshService>();
                return meshQuery.ObserveQuery<MeshNode>(
                    MeshQueryRequest.FromQuery("nodeType:ExchangeRate"));
            })))
```

### Loading from Embedded Node Content

When data is stored as structured arrays inside a MeshNode's `Content` field, load it by querying the node and extracting the embedded data:

```csharp
config => config
    .WithContentType<AnalysisContent>()
    .AddData(data => data
        .WithVirtualDataSource("LocalData", vs => vs
            .WithVirtualType<DataCube>(workspace =>
                LoadDataFromContent(workspace))))
```

This pattern is deployment-mode independent — the same code works whether the MeshNode is stored on the filesystem, in PostgreSQL, or in Cosmos DB.

### When to Use Which Pattern

| Pattern | Use When |
|---------|----------|
| **Individual MeshNodes** | Each row is an independently editable entity (users, products, orders) |
| **Embedded content array** | Data is loaded as a unit, read-heavy, updated as a batch (datacubes, time series) |
| **External files (CSV, etc.)** | Legacy integration only — not recommended for new development (filesystem-dependent) |
