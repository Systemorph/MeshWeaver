namespace MeshWeaver.Blazor.AppleMaps;

/// <summary>
/// Options for MapKit JS, bound from the <c>AppleMaps</c> configuration section by the pack's
/// module attribute.
/// </summary>
public class AppleMapsConfiguration
{
    /// <summary>
    /// The MapKit JS authorization token (a JWT minted from an Apple Developer MapKit key).
    /// Unset ⇒ the view renders an explanatory placeholder instead of a map — never a broken
    /// script load.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
