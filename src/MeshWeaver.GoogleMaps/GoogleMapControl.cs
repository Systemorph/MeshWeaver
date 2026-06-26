using MeshWeaver.Layout;

namespace MeshWeaver.GoogleMaps;

/// <summary>
/// A layout-area control that renders an interactive Google Map, optionally overlaid with markers and circles.
/// </summary>
public record GoogleMapControl() : UiControl<GoogleMapControl>("MeshWeaver.GoogleMaps", "1.0.0")
{

    /// <summary>Map configuration such as center, zoom level and which UI controls are shown.</summary>
    public MapOptions? Options { get; set; }
    /// <summary>Markers to display on the map.</summary>
    public IEnumerable<MapMarker>? Markers { get; set; }
    /// <summary>Circles to display on the map.</summary>
    public IEnumerable<MapCircle>? Circles { get; set; }

}

/// <summary>
/// Configuration controlling the map's initial view and which built-in UI controls are shown.
/// </summary>
public record MapOptions
{
    /// <summary>Geographic point the map is centered on.</summary>
    public LatLng? Center { get; init; }
    /// <summary>Initial zoom level; higher values zoom in closer.</summary>
    public int Zoom { get; init; } = 15;
    /// <summary>Base map type, e.g. <c>roadmap</c>, <c>satellite</c>, <c>hybrid</c> or <c>terrain</c>.</summary>
    public string? MapTypeId { get; init; } = "roadmap";
    /// <summary>Hides all default map UI controls when <c>true</c>.</summary>
    public bool DisableDefaultUI { get; init; } = false;
    /// <summary>Whether the zoom control is shown.</summary>
    public bool ZoomControl { get; init; } = true;
    /// <summary>Whether the map-type (roadmap/satellite/...) control is shown.</summary>
    public bool MapTypeControl { get; init; } = true;
    /// <summary>Whether the scale control is shown.</summary>
    public bool ScaleControl { get; init; } = false;
    /// <summary>Whether the Street View pegman control is shown.</summary>
    public bool StreetViewControl { get; init; } = true;
    /// <summary>Whether the rotate control is shown.</summary>
    public bool RotateControl { get; init; } = false;
    /// <summary>Whether the fullscreen control is shown.</summary>
    public bool FullscreenControl { get; init; } = false;
}

/// <summary>
/// A geographic coordinate expressed as latitude and longitude in decimal degrees.
/// </summary>
/// <param name="Lat">Latitude in decimal degrees.</param>
/// <param name="Lng">Longitude in decimal degrees.</param>
public record LatLng(double Lat, double Lng);

/// <summary>
/// A marker pin placed on the map at a given position.
/// </summary>
public record MapMarker
{
    /// <summary>Geographic position of the marker.</summary>
    public LatLng Position { get; init; } = new(0, 0);
    /// <summary>Tooltip text shown when hovering over the marker.</summary>
    public string? Title { get; init; }
    /// <summary>Short label rendered on the marker.</summary>
    public string? Label { get; init; }
    /// <summary>Whether the user can drag the marker to a new position.</summary>
    public bool Draggable { get; init; } = false;
    /// <summary>URL of a custom icon image for the marker.</summary>
    public string? Icon { get; init; }
    /// <summary>Identifier reported back in click events for this marker.</summary>
    public string? Id { get; init; }
    /// <summary>Arbitrary application data associated with the marker.</summary>
    public object? Data { get; init; }
}

/// <summary>
/// A circular overlay drawn on the map, centered on a point with a radius in meters.
/// </summary>
public record MapCircle
{
    /// <summary>Geographic center of the circle.</summary>
    public LatLng Center { get; init; } = new(0, 0);
    /// <summary>Radius of the circle in meters.</summary>
    public double Radius { get; init; } = 1000;
    /// <summary>Fill color of the circle as a CSS/hex color string.</summary>
    public string? FillColor { get; init; } = "#FF0000";
    /// <summary>Fill opacity between 0 (transparent) and 1 (opaque).</summary>
    public double FillOpacity { get; init; } = 0.35;
    /// <summary>Stroke (border) color as a CSS/hex color string.</summary>
    public string? StrokeColor { get; init; } = "#FF0000";
    /// <summary>Stroke (border) opacity between 0 and 1.</summary>
    public double StrokeOpacity { get; init; } = 0.8;
    /// <summary>Stroke (border) width in pixels.</summary>
    public int StrokeWeight { get; init; } = 2;
    /// <summary>Identifier reported back in click events for this circle.</summary>
    public string? Id { get; init; }
    /// <summary>Arbitrary application data associated with the circle.</summary>
    public object? Data { get; init; }
}


