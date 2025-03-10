# MeshWeaver.Domain

MeshWeaver.Domain defines the core data types and entities that serve as the foundation for the MeshWeaver data ecosystem. This library provides the domain model definitions that are used by the Data module for data management and processing.

## Overview

The library provides:
- Core domain entity definitions
- Data type contracts
- Base classes for extensible data types
- Validation rules and constraints

## Usage

Domain types are used throughout the MeshWeaver ecosystem, particularly in the Data module. Here's a basic example:

```csharp
// Define a domain entity
public record LineOfBusiness(string SystemName, string DisplayName);

// Use in Data module for reference data
services.AddData(data =>
    data.AddSource(ds =>
        ds.WithType<LineOfBusiness>(t =>
            t.WithInitialData(initialData)
        )
    )
);
```

## Integration

### With Data Module
The Domain types are automatically integrated when using the Data module:

```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddData()  // Automatically registers domain types
    )
);
```

For more detailed examples of how these domain types are used in practice, refer to:
- [MeshWeaver.Data](../MeshWeaver.Data/README.md) - Core data processing
- [MeshWeaver.Import](../MeshWeaver.Import/README.md) - Data import functionality

## Related Projects

- MeshWeaver.Data - Uses domain types for data management
- MeshWeaver.Import - Uses domain types for data import
- MeshWeaver.Hierarchies - Uses domain types for hierarchical structures
