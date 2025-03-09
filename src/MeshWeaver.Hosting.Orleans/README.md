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

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about the Orleans virtual actor model
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
