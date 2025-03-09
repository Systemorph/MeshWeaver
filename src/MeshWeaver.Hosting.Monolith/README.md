# MeshWeaver.Hosting.Monolith

## Overview
MeshWeaver.Hosting.Monolith provides a simple, single-process hosting model for MeshWeaver. All mesh components run in a single portal, making it ideal for development, testing, or small-scale deployments.

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure portal services
builder.ConfigureWebPortalServices();
builder.AddServiceDefaults();

// Configure MeshWeaver with monolithic hosting
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseMonolithMesh()
        .ConfigureServices(services => services.AddArticles())
);

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Single-process deployment
- Simplified configuration
- Direct in-memory messaging
- Integrated service provider
- Built-in SignalR support for external connections

## Integration
- Built on [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Compatible with SignalR external connections
- Supports all mesh message types and patterns

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about hosting options.
