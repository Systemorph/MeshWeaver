# MeshWeaver.Kernel

## Overview
MeshWeaver.Kernel provides the API for interacting with .NET Interactive kernels in the MeshWeaver mesh. It defines the core message types and events for kernel communication.

## Message Types

### Events and Commands
```csharp
// Event envelope for kernel responses
public record KernelEventEnvelope(string Envelope);

// Command envelope for kernel execution
public record KernelCommandEnvelope(string Command)
{
    public string IFrameUrl { get; init; }
    public string ViewId { get; init; } = Guid.NewGuid().AsString();
}

// Code submission request
public record SubmitCodeRequest(string Code)
{
    public string IFrameUrl { get; init; }
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

// Event subscription management
public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
```

## Usage

### Submitting Code
There are two ways to handle code execution output:

#### 1. Using Area ID (for internal usage)
Used when output should update a specific area in the mesh, typically for interactive components like markdown editors:
```csharp
// Output will update an area with the specified ID
var request = new SubmitCodeRequest("Console.WriteLine(\"Hello\");")
{
    Id = "specific-area-id"  // Updates area with this ID
};

client.Post(request, o => o.WithTarget(new KernelAddress()));
```

#### 2. Using IFrameUrl (for Polyglot Notebooks)
Used when output should be rendered in a notebook iframe:
```csharp
// Output will be rendered in a notebook iframe
var request = new SubmitCodeRequest("Console.WriteLine(\"Hello\");")
{
    IFrameUrl = "http://localhost/area"  // Renders in notebook iframe
};

client.Post(request, o => o.WithTarget(new KernelAddress()));
```

### Managing Event Subscriptions
```csharp
// Subscribe to kernel events
client.Post(new SubscribeKernelEventsRequest(), 
    o => o.WithTarget(new KernelAddress()));

// Unsubscribe from kernel events
client.Post(new UnsubscribeKernelEventsRequest(), 
    o => o.WithTarget(new KernelAddress()));
```

## Integration
- Used by [MeshWeaver.Kernel.Hub](../MeshWeaver.Kernel.Hub/README.md)
- Supports .NET Interactive kernel communication
- Enables mesh-based code execution

## See Also
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver architecture 