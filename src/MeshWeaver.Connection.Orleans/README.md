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

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about Orleans clients
- [Main MeshWeaver Documentation](../../Readme.md) - More about mesh connectivity options
