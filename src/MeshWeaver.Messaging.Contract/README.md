# MeshWeaver.Messaging.Contract

## Overview
MeshWeaver.Messaging.Contract defines the core interfaces, types, and abstractions that power the MeshWeaver messaging system. This library provides the contract definitions that enable type-safe message passing between hubs and standardized message handling patterns.

## Core Concepts

### Addresses
```csharp
// Unique identifiers for message hubs
public record Address(string Type, string Id);
public record HostAddress() : Address("host", "main");
```

### Message Contracts

#### Request-Response Pattern
```csharp
// Base interface for request messages
public interface IRequest<TResponse>
{
    // Marker interface for requests expecting TResponse
}

// Message delivery interface
public interface IMessageDelivery<TMessage>
{
    TMessage Message { get; }
}
```

### Message Hub Interface
```csharp
public interface IMessageHub
{
    // Send a message and await response
    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(
        IRequest<TResponse> request,
        Action<MessageOptions> configureOptions,
        CancellationToken cancellationToken = default
    );

    // Post a message without waiting for response
    void Post<TMessage>(
        TMessage message,
        Action<MessageOptions> configureOptions
    );
}
```

### Message Options
```csharp
public class MessageOptions
{
    // Configure message as response to a request
    public void ResponseFor<T>(T request);
    
    // Set target hub address
    public void WithTarget(Address address);
}
```

## Configuration

### Hub Configuration
```csharp
public class MessageHubConfiguration
{
    // Register message types
    public MessageHubConfiguration WithTypes(params Type[] types);
    
    // Register message handler
    public MessageHubConfiguration WithHandler<TRequest>(
        Func<IMessageHub, TRequest, Task> handler
    );
}
```

## Usage Patterns

### Defining Messages
```csharp
// Request message
record PingRequest : IRequest<PongResponse>;

// Response message
record PongResponse;
```

### Message Handler Registration
```csharp
configuration.WithHandler<PingRequest>((hub, request) =>
{
    // Handle the request
    hub.Post(new PongResponse(), opt => opt.ResponseFor(request));
    return request.Processed();
});
```

## Best Practices
1. Always define request-response pairs using the `IRequest<TResponse>` interface
2. Use strongly-typed messages for type safety
3. Keep message contracts simple and serializable
4. Use meaningful addresses for hub identification
5. Configure message timeouts for reliability
6. Handle message failures gracefully

## Integration
- Used by [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) for message processing
- Provides contract definitions for all MeshWeaver messaging components
- Enables custom message hub implementations

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall messaging architecture.
