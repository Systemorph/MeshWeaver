# MeshWeaver.Connection.SignalR

## Overview
MeshWeaver.Connection.SignalR enables external connections to the MeshWeaver data mesh using SignalR. This library provides the client-side implementation for connecting message hubs over SignalR transport.

## Features
- Connectivity for SignalR
- Connection management
- Authentication and authorization
- Transport protocols
- Connection pooling
- Retry and fallback mechanisms
- Diagnostics and monitoring

## Usage

### Basic Connection
```csharp
// Create a message hub with SignalR connection
var client = serviceProvider.CreateMessageHub(
    new SignalRAddress(),
    config => config.UseSignalRClient(SignalRUrl)
);
```

### Request-Response Pattern
```csharp
// Get target hub address
var targetAddress = Host.Services.GetRequiredService<IMessageHub>().Address;

// Send request and await response
var response = await client.AwaitResponse(
    new PingRequest(),
    o => o.WithTarget(targetAddress)
);
```

## Configuration
The SignalR client can be configured during hub creation:
```csharp
config => config
    .UseSignalRClient(SignalRUrl)
    // Additional SignalR options can be configured here
```

## Connection Types
- REST API connections
- Real-time messaging
- Database connections
- Service integration points
- External system interfaces

## Integration
- Works with [MeshWeaver.Mesh.Contract](../MeshWeaver.Mesh.Contract/README.md) for mesh addressing
- Integrates with [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) for message routing

## Related Projects
- [MeshWeaver.Messaging](../MeshWeaver.Messaging/README.md) - Messaging infrastructure
- [MeshWeaver.Data](../MeshWeaver.Data/README.md) - Data persistence

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about external mesh connections.
