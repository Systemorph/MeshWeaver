# MeshWeaver.Hierarchies

MeshWeaver.Hierarchies is a library for defining and working with hierarchical dimensions in data structures. It provides a robust framework for managing parent-child relationships, level-based operations, and hierarchical data organization.

## Overview

The library enables you to:
- Define hierarchical dimensions with parent-child relationships
- Query ancestors and descendants at different levels
- Work with multiple hierarchical dimensions simultaneously
- Integrate with MeshWeaver's data source system

## Key Features

- **Hierarchical Dimension Support**: Define dimensions with multi-level hierarchies
- **Level-Based Operations**: Access elements at specific levels in the hierarchy
- **Parent-Child Relationships**: Navigate up and down the hierarchy tree
- **Multiple Dimension Support**: Work with multiple hierarchical dimensions in parallel
- **Integration with Data Sources**: Seamlessly integrate with MeshWeaver's data source system

## Usage Examples

### Defining a Hierarchical Dimension

```csharp
public record MyHierarchicalDimension : IHierarchicalDimension
{
    [Key]
    public string SystemName { get; init; }
    public string DisplayName { get; init; }
    public object Parent { get; init; }

    public static MyHierarchicalDimension[] Data =
    {
        new() { SystemName = "Root1", DisplayName = "Root 1", Parent = null },
        new() { SystemName = "Child1", DisplayName = "Child 1", Parent = "Root1" },
        new() { SystemName = "Child11", DisplayName = "Child 1.1", Parent = "Child1" }
    };
}
```

### Configuring with Message Hub

```csharp
services.AddMessageHub(hub => hub
    .AddData(data => data
        .AddSource(source => source
            .WithType<MyHierarchicalDimension>(type => 
                type.WithInitialData(MyHierarchicalDimension.Data))
        )
    )
);
```

### Working with Hierarchies

```csharp
// Get hierarchy information
var hierarchy = dimensionCache.GetHierarchy<MyHierarchicalDimension>();

// Get parent of an element
var parent = dimensionCache.Parent<MyHierarchicalDimension>("Child11");

// Get ancestor at specific level
var ancestor = dimensionCache.AncestorAtLevel<MyHierarchicalDimension>("Child11", 0); // Gets Root1

// Check level in hierarchy
var level = dimensionCache.GetHierarchy<MyHierarchicalDimension>("Child11").Level; // Returns 2
```

### Using Multiple Dimensions

You can work with multiple hierarchical dimensions simultaneously:

```csharp
public class MyDataModel
{
    [Dimension(typeof(DimensionA))]
    public string DimA { get; init; }

    [Dimension(typeof(DimensionB))]
    public string DimB { get; init; }

    public double Value { get; init; }
}
```

## Integration with Data Sources

The hierarchical dimensions integrate seamlessly with MeshWeaver's data source system:

```csharp
public record ValueWithHierarchicalDimension
{
    [NotVisible]
    [Dimension(typeof(MyHierarchicalDimension), nameof(Dim))]
    public string Dim { get; init; }

    public double Value { get; init; }
}
```

## Features

### Level-Based Operations
- Get elements at specific levels
- Navigate up and down the hierarchy
- Query ancestor-descendant relationships

### Parent-Child Management
- Automatic parent-child relationship tracking
- Efficient hierarchy traversal
- Support for multiple root nodes

### Data Integration
- Seamless integration with data sources
- Support for dimension annotations
- Automatic hierarchy maintenance

## Best Practices

1. Always define clear parent-child relationships
2. Use meaningful SystemName and DisplayName values
3. Ensure hierarchy levels are properly structured
4. Consider performance implications for deep hierarchies
5. Validate hierarchy integrity during initialization

## API Reference

### Key Interfaces

- `IHierarchicalDimension`: Base interface for hierarchical dimensions
- `DimensionCache`: Main class for working with hierarchies
- `IHierarchy`: Interface for hierarchy operations

### Common Operations

- `GetHierarchy<T>()`: Get hierarchy information for a dimension
- `Parent<T>(string id)`: Get parent of an element
- `AncestorAtLevel<T>(string id, int level)`: Get ancestor at specific level
- `Get<T>(string id)`: Get dimension element by ID
