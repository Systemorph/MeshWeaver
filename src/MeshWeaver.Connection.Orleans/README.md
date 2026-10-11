# MeshWeaver.Connection.Orleans

## Overview
MeshWeaver.Connection.Orleans provides client connectivity to Orleans-hosted mesh networks. It enables applications to connect to and communicate with MeshWeaver hubs that are distributed across Orleans clusters.

## Usage
From an ASP.NET Core application:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add Azure Table storage for Orleans clustering
builder.AddKeyedAzureTableClient("orleans-clustering");

// Configure Orleans mesh client
builder.UseOrleansMeshClient()
    .ConfigureWebPortal()
    .ConfigureServices(services => 
        services.AddAzureBlobArticles()
    );

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Seamless connection to Orleans-hosted mesh networks
- Transparent message routing through Orleans clusters
- Support for Azure Table storage clustering
- Integration with ASP.NET Core dependency injection
- Compatible with other MeshWeaver services

## Integration
- Works with [MeshWeaver.Hosting.Orleans](../MeshWeaver.Hosting.Orleans/README.md)
- Integrates with [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/overview) client system
- Supports Azure and other cloud providers for clustering

## Placement expiry
A delivery whose message outlives Orleans' response timeout while its target grain is still being placed is cancelled with `OperationCanceledException: Message expired before placement could complete for grain ...` (issue #5731; seen once on memex for an ephemeral `_Activity/compile-*` hub). The message reached no activation, so the sender is told a transient failure (`ErrorType.ShuttingDown`) on the first attempt and the delivery is not re-sent: `RoutingGrain.IsPlacementExpired` (in `MeshWeaver.Hosting.Orleans`) is the classifier and `PlacementExpiryClassificationTest` pins Orleans' wording. Whether the stall itself is general placement latency during cluster membership changes is not evidenced; watch log fingerprint `bb128e1afa6c6449` for recurrence.

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about Orleans clients
- [Main MeshWeaver Documentation](../../Readme.md) - More about mesh connectivity options
