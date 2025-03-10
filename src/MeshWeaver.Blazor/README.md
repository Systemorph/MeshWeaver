# MeshWeaver.Blazor

## Overview
MeshWeaver.Blazor provides Blazor UI components and services for building web applications using the MeshWeaver framework. This library enables rich, interactive user interfaces that integrate seamlessly with MeshWeaver's data processing capabilities.

The components in this library are Blazor implementations of the `UiControl` classes defined in the MeshWeaver.Layout project, providing consistent UI behavior and styling across different frontend technologies.

## Features
- Reusable Blazor components that implement the `UiControl` class hierarchy
- State management utilities
- Integration with MeshWeaver data services
- Responsive layouts and design patterns

## Usage
```csharp
// In your Program.cs or Startup.cs
builder.AddBlazor();


## Component Library
- Data visualization components
- Form controls
- Navigation and layout components
- Modal and notification systems

## Installation
The MeshWeaver.Blazor package can be added to your project using the `AddBlazor()` extension method when configuring your application:

```csharp
// In Program.cs
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

// Configure MeshWeaver portal services with Blazor components
builder.ConfigureWebPortalServices();
builder.ConfigureWebPortal()
       .AddBlazor();

var app = builder.Build();
app.StartPortalApplication();
```

## See Also
- [MeshWeaver.Blazor.AgGrid](../MeshWeaver.Blazor.AgGrid/README.md)
- [MeshWeaver.Blazor.ChartJs](../MeshWeaver.Blazor.ChartJs/README.md)
- [Main MeshWeaver documentation](../../Readme.md) 