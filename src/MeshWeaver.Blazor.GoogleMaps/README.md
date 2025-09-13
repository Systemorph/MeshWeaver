# MeshWeaver.Blazor.GoogleMaps

Blazor Server implementation for MeshWeaver Google Maps integration.

## Overview

This package provides the Blazor Server rendering layer for MeshWeaver's Google Maps controls. It works in conjunction with `MeshWeaver.GoogleMaps` to deliver interactive map functionality within MeshWeaver applications.

## Quick Start

### 1. Installation

Install the NuGet package:

```bash
dotnet add package MeshWeaver.Blazor.GoogleMaps
```

### 2. Configuration

Add Google Maps to your MeshWeaver hub configuration:

```csharp
public static MessageHubConfiguration ConfigureHub(this MessageHubConfiguration config)
{
    return config.AddGoogleMaps();
}
```

Configure your Google Maps API key in `Program.cs`:

```csharp
builder.Services.Configure<GoogleMapsConfiguration>(options =>
{
    options.ApiKey = "your-google-maps-api-key";
});
```

### 3. Usage

Create a map control in your layout areas:

```csharp
public static UiControl CreateMap() =>
    new GoogleMapControl
    {
        Options = new MapOptions
        {
            Center = new LatLng(37.7749, -122.4194),
            Zoom = 12
        },
        Markers = new[]
        {
            new MapMarker
            {
                Position = new LatLng(37.7749, -122.4194),
                Title = "San Francisco",
                Id = "sf-marker"
            }
        }
    }.WithStyle("height: 400px;");
```

## Features

- **GoogleMapView**: Blazor component for rendering Google Maps
- **Interactive Markers**: Clickable markers with customizable icons and labels
- **Circle Overlays**: Customizable circular overlays with styling options
- **Click Events**: Integration with MeshWeaver's message hub for event handling
- **Dynamic Updates**: Real-time marker and circle updates
- **JavaScript Interop**: Efficient JavaScript module loading and interaction

## Requirements

- Google Maps JavaScript API key
- .NET 9.0
- Blazor Server
- MeshWeaver.GoogleMaps package

## Dependencies

- MeshWeaver.Blazor
- MeshWeaver.GoogleMaps
- Microsoft.AspNetCore.Components.Web

For complete documentation and examples, see the main [MeshWeaver.GoogleMaps README](../MeshWeaver.GoogleMaps/README.md).