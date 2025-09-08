using MeshWeaver.Layout;

namespace MeshWeaver.GoogleMaps;

public record GoogleMapControl() : UiControl<GoogleMapControl>("MeshWeaver.GoogleMaps", "1.0.0")
{

    public MapOptions? Options { get; set; }
    public IEnumerable<MapMarker>? Markers { get; set; }
    public IEnumerable<MapCircle>? Circles { get; set; }

}

public record MapOptions
{
    public LatLng? Center { get; init; }
    public int Zoom { get; init; } = 15;
    public string? MapTypeId { get; init; } = "roadmap";
    public bool DisableDefaultUI { get; init; } = false;
    public bool ZoomControl { get; init; } = true;
    public bool MapTypeControl { get; init; } = true;
    public bool ScaleControl { get; init; } = false;
    public bool StreetViewControl { get; init; } = true;
    public bool RotateControl { get; init; } = false;
    public bool FullscreenControl { get; init; } = false;
}

public record LatLng(double Lat, double Lng);

public record MapMarker
{
    public LatLng Position { get; init; } = new(0, 0);
    public string? Title { get; init; }
    public string? Label { get; init; }
    public bool Draggable { get; init; } = false;
    public string? Icon { get; init; }
    public string? Id { get; init; }
    public object? Data { get; init; }
}

public record MapCircle
{
    public LatLng Center { get; init; } = new(0, 0);
    public double Radius { get; init; } = 1000;
    public string? FillColor { get; init; } = "#FF0000";
    public double FillOpacity { get; init; } = 0.35;
    public string? StrokeColor { get; init; } = "#FF0000";
    public double StrokeOpacity { get; init; } = 0.8;
    public int StrokeWeight { get; init; } = 2;
    public string? Id { get; init; }
    public object? Data { get; init; }
}


