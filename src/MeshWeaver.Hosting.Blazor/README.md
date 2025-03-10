# MeshWeaver.Hosting.Blazor

## Overview
MeshWeaver.Hosting.Blazor provides extensions for hosting Blazor Server applications within the MeshWeaver mesh. It enables real-time UI updates through SignalR and integrates with the mesh's message routing system.

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Blazor hosting
builder.ConfigureWebPortalServices();

// Configure MeshWeaver with Blazor support
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .UseBlazorServer()
        .ConfigureServices(services => 
        {
            services.AddRazorPages();
            services.AddServerSideBlazor();
        })
);

var app = builder.Build();

// Configure the Blazor pipeline
app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.StartPortalApplication();
```

## Features
- Blazor Server integration
- Real-time UI updates via SignalR
- Mesh message routing in Blazor components
- Component state management
- Integrated authentication

## Integration
- Built on [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Works with both monolithic and Orleans hosting
- Supports standard Blazor Server features

## See Also
- [Blazor Server Documentation](https://learn.microsoft.com/aspnet/core/blazor) - Learn more about Blazor Server
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
