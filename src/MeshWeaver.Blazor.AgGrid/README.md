# MeshWeaver.Blazor.AgGrid

## Overview
MeshWeaver.Blazor.AgGrid provides Blazor UI components and services for AgGrid functionality within the MeshWeaver ecosystem. This library offers specialized UI rendering and interactions for Blazor applications.

The components in this library implement the `GridControl` class from the MeshWeaver.GridModel project using AG Grid technology, providing rich data grid visualization capabilities with the standard MeshWeaver UI behavior and styling.

## Features
- Blazor implementation of the `GridControl` class using AG Grid
- Advanced data grid functionality with sorting, filtering, and pagination

## Usage
```csharp
// In your Program.cs or Startup.cs
builder
       .AddAgGrid();

```

## Components
- Specialized UI elements for AgGrid
- Interactive data visualization
- User input controls
- Layout components

## Installation
The MeshWeaver.Blazor.AgGrid package can be added to your project using the `AddAgGrid()` extension method when configuring your application:

```csharp
// In Program.cs
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

// Configure MeshWeaver portal services
builder.ConfigureWebPortalServices();
builder.ConfigureWebPortal()
       .AddAgGrid();

var app = builder.Build();
app.StartPortalApplication();
```

## Integration with MeshWeaver
- Seamless integration with MeshWeaver.Layout
- Implementation of the GridControl model from MeshWeaver.GridModel
- Data binding with MeshWeaver.Data sources
- Event handling with MeshWeaver messaging

## Related Projects
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Core Blazor components
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Layout system integration
- [MeshWeaver.GridModel](../MeshWeaver.GridModel/README.md) - Grid model definitions

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
