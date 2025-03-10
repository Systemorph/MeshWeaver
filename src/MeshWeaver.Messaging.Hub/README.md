# MeshWeaver.Messaging.Hub

## Overview
MeshWeaver.Messaging.Hub implements the core modular components of the MeshWeaver ecosystem - Message Hubs. Based on the actor model pattern, each hub is an independent unit that processes messages sequentially and communicates with other hubs through message passing.

## Key Concepts

### Message Hubs
- Each hub has a unique address for identification
- Hubs process one message at a time (actor model)
- Each hub has its own dependency injection container
- Hubs can be instantiated dynamically
- Support for hierarchical hosting of hubs

### Features
- Message routing between hubs
- Request-response pattern support
- Hierarchical hub hosting
- Configurable message handlers
- Built-in dependency injection
- Asynchronous message processing

## Usage Examples

### Basic Request-Response Pattern
```csharp
// Define message types
record SayHelloRequest : IRequest<HelloEvent>;
record HelloEvent;

// Configure message handler
configuration.WithHandler<SayHelloRequest>((hub, request) =>
{
    hub.Post(new HelloEvent(), options => options.ResponseFor(request));
    return request.Processed();
});

// Send request and await response
var response = await host.AwaitResponse(
    new SayHelloRequest(),
    o => o.WithTarget(new HostAddress())
);
```

### Hierarchical Hub Hosting
```csharp
// Create a sub-hub with its own address
var subHub = client.ServiceProvider.CreateMessageHub(
    new NewAddress(),
    conf => conf.WithTypes(typeof(Ping), typeof(Pong))
);

// Send message from sub-hub to host
var response = await subHub.AwaitResponse(
    new Ping(), 
    o => o.WithTarget(new HostAddress())
);
```

## Configuration
Message hubs can be configured with custom handlers, message types, and processing options:

```csharp
var configuration = new MessageHubConfiguration()
    .WithTypes(typeof(Ping), typeof(Pong))
    .WithHandler<Ping>((hub, request) => {
        // Handle message
    });
```

## Integration
- Seamless communication between MeshWeaver components
- Support for distributed systems
- Easy integration with dependency injection
- Scalable message processing

## Related Projects
- [MeshWeaver.Messaging.Contract](../MeshWeaver.Messaging.Contract/README.md) - Message contracts and interfaces
- Other MeshWeaver components

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project architecture.
