# MeshWeaver.Blazor.ChartJs

## Overview
MeshWeaver.Blazor.ChartJs provides Blazor UI components and services for ChartJs functionality within the MeshWeaver ecosystem. This library offers specialized UI rendering and interactions for Blazor applications.

The components in this library implement the `ChartControl` class from the MeshWeaver.Charting project using Chart.js technology, providing rich data visualization capabilities with the standard MeshWeaver UI behavior and styling.

## Features
- Blazor implementation of the `ChartControl` class using Chart.js
- Advanced chart visualization with various chart types and customization options

## Usage
```csharp
// In your Program.cs or Startup.cs
builder.ConfigureWebPortal()
       .AddChartJs();

```

## Components
- Various chart types (bar, line, pie, etc.)
- Interactive data visualization
- Responsive chart layouts

## Installation
The MeshWeaver.Blazor.ChartJs package can be added to your project using the `AddChartJs()` extension method when configuring your application:

```csharp
// In Program.cs
using MeshWeaver.Portal.Shared.Web;
using MeshWeaver.Blazor.ChartJs;

var builder = WebApplication.CreateBuilder(args);

// Configure MeshWeaver portal services
builder.ConfigureWebPortalServices();
builder.ConfigureWebPortal()
       .AddChartJs();

var app = builder.Build();
app.StartPortalApplication();
```

## Integration with MeshWeaver
- Seamless integration with MeshWeaver.Layout
- Implementation of the ChartControl model from MeshWeaver.Charting
- Data binding with MeshWeaver.Data sources
- Event handling with MeshWeaver messaging

## Related Projects
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Core Blazor components
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Layout system integration
- [MeshWeaver.Charting](../MeshWeaver.Charting/README.md) - Chart model definitions

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
s