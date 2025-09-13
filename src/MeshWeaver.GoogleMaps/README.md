# MeshWeaver Google Maps Integration

This library provides Google Maps integration for MeshWeaver applications, offering framework-agnostic map controls with Blazor Server rendering capabilities.

## Projects

### MeshWeaver.GoogleMaps
Core library containing the framework-agnostic Google Maps controls and data models.

### MeshWeaver.Blazor.GoogleMaps
Blazor Server implementation providing the rendering layer for Google Maps integration.

## Features

- **Interactive Maps**: Display Google Maps with customizable options
- **Markers**: Add clickable markers with titles, labels, and custom icons
- **Circles**: Display circular overlays with customizable styling
- **Click Events**: Handle marker and circle click events through MeshWeaver's message hub
- **Responsive Design**: Maps automatically adapt to container dimensions
- **Dynamic Updates**: Real-time marker and circle updates without page refresh

## Quick Start

### 1. Configuration

Add Google Maps to your MeshWeaver hub configuration:

```csharp
public static MessageHubConfiguration ConfigureHub(this MessageHubConfiguration config)
{
    return config.AddGoogleMaps();
}
```

Configure your Google Maps API key:

```csharp
services.Configure<GoogleMapsConfiguration>(options =>
{
    options.ApiKey = "your-google-maps-api-key";
    options.Libraries = "places,visualization,drawing,marker"; // Optional
    options.Language = "en"; // Optional
    options.Region = "US"; // Optional
});
```

### 2. Creating a Map Control

```csharp
public static UiControl CreateMap() =>
    new GoogleMapControl
    {
        Options = new MapOptions
        {
            Center = new LatLng(37.7749, -122.4194), // San Francisco
            Zoom = 12,
            MapTypeId = "roadmap"
        },
        Markers = new[]
        {
            new MapMarker
            {
                Position = new LatLng(37.7749, -122.4194),
                Title = "San Francisco",
                Label = "SF",
                Id = "sf-marker"
            }
        },
        Circles = new[]
        {
            new MapCircle
            {
                Center = new LatLng(37.7749, -122.4194),
                Radius = 5000, // 5km radius
                FillColor = "#FF0000",
                FillOpacity = 0.35,
                Id = "sf-circle"
            }
        }
    }.WithStyle("height: 400px; width: 100%;");
```

## API Reference

### GoogleMapControl

Main control for rendering Google Maps.

**Properties:**
- `Options`: Map configuration options
- `Markers`: Collection of map markers
- `Circles`: Collection of map circles

### MapOptions

Configuration options for the map display.

**Properties:**
- `Center`: Map center coordinates (LatLng)
- `Zoom`: Zoom level (default: 15)
- `MapTypeId`: Map type - "roadmap", "satellite", "hybrid", "terrain" (default: "roadmap")
- `DisableDefaultUI`: Hide all default UI controls (default: false)
- `ZoomControl`: Show zoom controls (default: true)
- `MapTypeControl`: Show map type selector (default: true)
- `ScaleControl`: Show scale control (default: false)
- `StreetViewControl`: Show street view control (default: true)
- `RotateControl`: Show rotation control (default: false)
- `FullscreenControl`: Show fullscreen control (default: false)

### MapMarker

Represents a clickable point on the map.

**Properties:**
- `Position`: Marker coordinates (LatLng)
- `Title`: Hover tooltip text
- `Label`: Single character marker label
- `Draggable`: Allow marker dragging (default: false)
- `Icon`: Custom marker icon URL
- `Id`: Unique identifier for click events
- `Data`: Additional data payload

### MapCircle

Represents a circular overlay on the map.

**Properties:**
- `Center`: Circle center coordinates (LatLng)
- `Radius`: Circle radius in meters (default: 1000)
- `FillColor`: Interior fill color (default: "#FF0000")
- `FillOpacity`: Interior fill opacity (default: 0.35)
- `StrokeColor`: Border color (default: "#FF0000")
- `StrokeOpacity`: Border opacity (default: 0.8)
- `StrokeWeight`: Border width in pixels (default: 2)
- `Id`: Unique identifier for click events
- `Data`: Additional data payload

### LatLng

Represents geographical coordinates.

**Constructor:**
- `LatLng(double Lat, double Lng)`

## Event Handling

The GoogleMapView automatically handles marker and circle clicks, posting `ClickedEvent` messages through MeshWeaver's message hub:

```csharp
public class MyHandler
{
    public IMessageDelivery HandleMapClick(MessageHub hub, IMessageDelivery<ClickedEvent> delivery)
    {
        var clickedId = delivery.Message.Payload; // Marker or circle ID
        // Handle the click event
        return delivery.Processed();
    }
}
```

Register the handler in your hub configuration:

```csharp
config.AddHandler<ClickedEvent>(HandleMapClick)
```

## GoogleMapsConfiguration

Configuration options for the Google Maps API integration.

**Properties:**
- `ApiKey`: Your Google Maps JavaScript API key (required)
- `Libraries`: Comma-separated list of additional libraries to load (optional)
- `Language`: Language for map labels and controls (optional)
- `Region`: Localization region (optional)

## Requirements

- **Google Maps JavaScript API Key**: Required for map functionality
- **Internet Connection**: Maps require external API access
- **.NET 9.0**: Target framework
- **Blazor Server**: For rendering implementation

## Dependencies

### MeshWeaver.GoogleMaps
- MeshWeaver.Layout

### MeshWeaver.Blazor.GoogleMaps
- MeshWeaver.Blazor
- MeshWeaver.GoogleMaps
- Microsoft.AspNetCore.Components.Web

## Integration Example

```csharp
public static class MyLayoutAreas
{
    public static void AddMapAreas(this LayoutConfiguration config)
    {
        config.AddLayoutArea("LocationMap", LocationMap);
    }

    public static UiControl LocationMap(LayoutAreaHost host, RenderingContext ctx)
    {
        var locations = GetLocations(); // Your data source

        return new GoogleMapControl
        {
            Options = new MapOptions
            {
                Center = new LatLng(locations.First().Latitude, locations.First().Longitude),
                Zoom = 10
            },
            Markers = locations.Select(loc => new MapMarker
            {
                Position = new LatLng(loc.Latitude, loc.Longitude),
                Title = loc.Name,
                Id = loc.Id.ToString()
            }).ToArray()
        }.WithStyle("height: 500px;");
    }
}
```

## Styling

The map container automatically adapts to its parent's dimensions. Use CSS styling through the `WithStyle()` method:

```csharp
var map = new GoogleMapControl { /* options */ }
    .WithStyle("height: 400px; width: 100%; border: 1px solid #ccc;");
```

## Performance Notes

- Maps are rendered asynchronously after initial page load
- Marker and circle updates are batched and delayed to prevent excessive API calls
- JavaScript module loading is optimized with dynamic imports
- Component implements proper cleanup to prevent memory leaks