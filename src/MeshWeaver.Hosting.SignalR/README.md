# MeshWeaver.Hosting.SignalR

## Overview
MeshWeaver.Hosting.SignalR provides hosting capabilities for SignalR-based mesh connections. It enables ASP.NET Core applications to host mesh nodes that can be accessed by external clients through SignalR.

## Host Configuration
```csharp
// In Program.cs or Startup.cs
public static void Configure(IApplicationBuilder app)
{
    // Map SignalR hubs for mesh communication
    app.MapMeshWeaverSignalRHubs();
}
```

## Features
- Application hosting for SignalR
- Configuration management
- Service discovery
- Environment setup
- Dependency resolution
- Lifecycle management
- Diagnostics and monitoring

## Usage Example
From `SignalRMeshTest.cs`:

```csharp
// Create and configure services
var services = CreateServiceCollection();
var serviceProvider = services.CreateMeshWeaverServiceProvider();

// Create a client hub that connects through SignalR
using var client = serviceProvider.CreateMessageHub(
    new SignalRAddress(),
    config => config.UseSignalRClient(SignalRUrl)
);

// Get the host's address and send a request
var address = Host.Services.GetRequiredService<IMessageHub>().Address;
var response = await client.AwaitResponse(
    new PingRequest(),
    o => o.WithTarget(address)
);
```

## Hosting Options
- Application environment configuration
- Service discovery settings
- Dependency injection setup
- Logging and monitoring
- Runtime settings

## Integration
- Works with [MeshWeaver.Connection.SignalR](../MeshWeaver.Connection.SignalR/README.md) for client connections
- Integrates with ASP.NET Core hosting
- Enables external mesh access through SignalR transport

## Related Projects
- [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md) - Core hosting functionality
- [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) - Messaging integration

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about mesh hosting options.
