namespace MeshWeaver.GoogleMaps;

public class GoogleMapsConfiguration
{
    public string ApiKey { get; set; } = string.Empty;
    public string? Libraries { get; set; } = "places,visualization,drawing,marker";
    public string? Language { get; set; }
    public string? Region { get; set; }

}