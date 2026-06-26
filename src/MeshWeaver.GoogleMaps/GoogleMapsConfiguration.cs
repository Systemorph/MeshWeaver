namespace MeshWeaver.GoogleMaps;

/// <summary>
/// Options for loading the Google Maps JavaScript API, typically bound from application configuration.
/// </summary>
public class GoogleMapsConfiguration
{
    /// <summary>API key used to authenticate requests to the Google Maps JavaScript API.</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Comma-separated list of optional Google Maps libraries to load (e.g. <c>places</c>, <c>marker</c>).</summary>
    public string? Libraries { get; set; } = "places,visualization,drawing,marker";
    /// <summary>Language code used to localize the map UI; falls back to the browser setting when null.</summary>
    public string? Language { get; set; }
    /// <summary>Region code biasing geocoding and map behavior to a country; none when null.</summary>
    public string? Region { get; set; }

}