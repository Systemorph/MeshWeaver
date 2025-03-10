# MeshWeaver.Mesh.Contract

## Overview
MeshWeaver.Mesh.Contract defines the foundational interfaces and types for operating a distributed data mesh. This library provides the contract definitions for data storage, streaming, and mesh topology management, enabling seamless integration of diverse data sources and processing nodes.

## Core Concepts

### Addressing System
```csharp
// Base address type for mesh nodes
public record Address(string Type, string Id);

// Application and UI addresses
public record ApplicationAddress(string Id) : Address("app", Id);

// Computation addresses
public record KernelAddress(string Id = null) : Address("kernel", Id ?? Guid.NewGuid().AsString());

// Content addresses
public record PortalAddress(string Id = null) : Address("portal", Id ?? Guid.NewGuid().AsString());
```

Each address type serves a specific purpose in the mesh:
- `ApplicationAddress`: Identifies application instances
- `SignalRAddress`: Addresses real-time communication endpoints
- `KernelAddress`: Points to computation kernels
- `PortalAddress`: References portal instances

Note that many address types support automatic ID generation using GUIDs when no explicit ID is provided.

### Creating Domain-Specific Addresses
The addressing system is extensible, allowing creation of purpose-specific addresses for different data domains. For example:

```csharp
// Time series data domain
public record PricingAddress(string Id) : Address("pricing", Id);

// Document storage domain
public record TransactionalDataAddress(string Id) : Address("transactionaldata", Id);

```

When creating domain-specific addresses:
1. Choose a meaningful type identifier that represents your domain
2. Decide if automatic ID generation is needed for your use case
3. Consider adding domain-specific validation or formatting rules
4. Document the address format and usage patterns

## Best Practices
1. Use appropriate address types for different mesh resources
2. Implement proper error handling for distributed operations
3. Consider data locality when designing mesh topology
4. Use schemas to ensure data consistency
5. Implement proper retry mechanisms for distributed operations
6. Monitor and manage resource lifecycle

## Integration
- Works with [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) for mesh communication
- Provides foundation for [MeshWeaver.Mesh.Core](../MeshWeaver.Mesh.Core/README.md)
- Enables custom provider implementations

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall mesh architecture.
