# MeshWeaver.Hosting.Orleans

## Overview
MeshWeaver.Hosting.Orleans provides a distributed hosting model for MeshWeaver using Microsoft Orleans. Each message hub is represented as a virtual actor (grain) in the Orleans cluster, enabling automatic distribution, scalability, and fault tolerance.

## How It Works
- Each message hub is mapped to an Orleans grain
- Messages are dispatched through Orleans silos
- Grains are automatically distributed across the cluster
- Virtual actor model ensures hubs are always addressable
- Orleans handles activation/deactivation and placement of hubs

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Orleans cluster
builder.Host.UseOrleans(orleans =>
{
    orleans.UseLocalhostClustering();
    // Configure other Orleans options
});

// Configure MeshWeaver with Orleans hosting
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseOrleansMesh()
        .ConfigureServices(services => services.AddArticles())
);

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Distributed message processing
- Automatic scalability through Orleans clustering
- Fault tolerance and automatic recovery
- Virtual actor model for message hubs
- Transparent hub activation/deactivation
- Location transparency for message routing

## Benefits
- **Scalability**: Automatically scales across multiple servers
- **Reliability**: Built-in fault tolerance through Orleans
- **Persistence**: Optional state persistence for hubs
- **Distribution**: Automatic workload distribution
- **Recovery**: Automatic failure recovery

## Integration
- Built on [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Uses [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/overview) for distribution
- Compatible with all mesh message patterns

## Silo departure and cancellation timeouts (#6392)

`SiloStopTimeline` (registered by `AddOrleansMeshServices`) logs one Information line per silo lifecycle stage as the silo stops, so a departing silo's own log shows where its stop spent its time. How to read those lines next to peers' `GrainCallCancellationManager` 30 s timeouts (fingerprint `97b80f6a91dbdb74`), and what is and is not established about the cause, is the doc page `Doc/Architecture/ReadingASiloStop` (`src/MeshWeaver.Documentation/Data/Architecture/ReadingASiloStop.md`).

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about the Orleans virtual actor model
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
