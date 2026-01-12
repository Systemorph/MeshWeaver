---
Name: NodeType Configuration
Category: Documentation
Description: How to create and configure NodeTypes
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/NodeTypeConfiguration/icon.svg
---

# NodeType Configuration Guide

NodeTypes define the schema and behavior for nodes in MeshWeaver. This guide explains how to create and configure NodeTypes.

## What is a NodeType?

A NodeType is a template that defines:
- **Content schema**: The data structure for instances
- **Hub configuration**: How instances behave and render
- **Display properties**: Icon, description, display order

## NodeType Structure

A NodeType is itself a node with `nodeType: "NodeType"`. It consists of:

### Node Properties
```json
{
  "id": "Organization",
  "namespace": "",
  "name": "Organization",
  "nodeType": "NodeType",
  "description": "An organization containing projects",
  "icon": "Building",
  "displayOrder": 10,
  "isPersistent": true
}
```

### NodeTypeDefinition Content
```json
{
  "$type": "NodeTypeDefinition",
  "id": "Organization",
  "namespace": "",
  "displayName": "Organization",
  "icon": "Building",
  "description": "An organization containing projects",
  "displayOrder": 10,
  "configuration": "config => config.WithContentType<Organization>().AddDefaultViews()"
}
```

## Configuration String

The `configuration` property is a C# expression that configures the hub for instances:

### WithContentType<T>()
Specifies the content record type:
```csharp
config.WithContentType<Organization>()
```

### AddDefaultViews()
Adds standard views (Details, Catalog, Thumbnail, Metadata, Settings):
```csharp
config.WithContentType<Organization>().AddDefaultViews()
```

### AddDefaultViews()
Same as AddMeshNodeViews but chainable:
```csharp
config.WithContentType<Organization>().AddDefaultViews()
```

### MapContentCollection()
Maps a property to a content collection (files, images):
```csharp
config.WithContentType<Organization>()
  .AddDefaultViews()
  .MapContentCollection("logos", "storage", $"logos/{config.Address.Segments.Last()}")
```

## Content Record

Define the content type in a Code/dataModel.json file:

```csharp
using System.ComponentModel.DataAnnotations;

public record Organization
{
    [Key]
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Icon { get; init; } = "Building";
}
```

## Catalog Behavior

The catalog for a NodeType automatically displays instances of that type within the current namespace. The query is built dynamically using:
- `namespace:{current}` - searches within the current namespace
- `nodeType:{nodeTypePath}` - filters to instances of this NodeType

No manual `childrenQuery` configuration is needed.

## Namespace Hierarchy

### Root Namespace
NodeTypes in the root namespace (empty namespace) define global types:
- `Organization` at path `Organization` with `namespace: ""`
- Instances like `Systemorph` also have `namespace: ""`

### Nested Namespace
NodeTypes can be scoped to a namespace:
- `Systemorph/Project` at path `Systemorph/Project` with `namespace: "Systemorph"`
- Instances like `Systemorph/Marketing` have `namespace: "Systemorph"`

## Views

### Default Views
- **Details**: Main content view
- **Catalog**: List of children/instances
- **Thumbnail**: Card view for grids
- **Metadata**: Technical properties
- **Settings**: NodeType definitions in this namespace

### Custom Views
Add custom views in the configuration:
```csharp
config
  .WithContentType<Story>()
  .AddDefaultViews()
  .AddLayout(layout => layout
    .WithView("Timeline", StoryViews.Timeline))
```

## Example: Creating a Story NodeType

### 1. Create Story.json
```json
{
  "id": "Story",
  "namespace": "Systemorph/Marketing",
  "name": "Story",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "id": "Story",
    "namespace": "Systemorph/Marketing",
    "displayName": "Story",
    "configuration": "config => config.WithContentType<Story>().AddDefaultViews()"
  }
}
```

### 2. Create Story/Code/dataModel.json
```json
{
  "code": "public record Story { [Key] public string Id { get; init; } public string Title { get; init; } public string? Markdown { get; init; } }"
}
```

### 3. Create Instances
```json
{
  "id": "ClaimsProcessing",
  "namespace": "Systemorph/Marketing",
  "name": "Claims Processing",
  "nodeType": "Systemorph/Marketing/Story",
  "content": {
    "$type": "Story",
    "id": "ClaimsProcessing",
    "title": "Claims Processing Pipeline",
    "markdown": "# Overview\n..."
  }
}
```

## Best Practices

1. **Use meaningful namespaces**: Group related types together
2. **Set display order**: Control catalog sorting with displayOrder
3. **Choose appropriate icons**: Use Fluent UI icons
4. **Write clear descriptions**: Help users understand the type's purpose
