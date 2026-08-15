using MeshWeaver.Maps;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.GoogleMaps
{
    /// <summary>
    /// Extension methods for registering the Google Maps control and its Blazor view with a message hub.
    /// </summary>
    public static class BlazorGoogleMapsExtensions
    {
        /// <summary>
        /// Registers the <c>MapControl</c> type and its Blazor view renderer on the hub configuration.
        /// </summary>
        /// <param name="configuration">The message hub configuration to extend.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static MessageHubConfiguration AddGoogleMaps(this MessageHubConfiguration configuration)
        {
            return configuration
                // The control + the content/config records serialised inside it (Options, markers, circles)
                // — the reflection sweep in AddLayoutTypes only covers the MeshWeaver.Layout assembly.
                .WithTypes(
                    typeof(MapControl),
                    typeof(MeshWeaver.Maps.MapOptions),
                    typeof(MeshWeaver.Maps.LatLng),
                    typeof(MeshWeaver.Maps.MapMarker),
                    typeof(MeshWeaver.Maps.MapCircle))
                .AddViews(registry => registry.WithView<MapControl, GoogleMapView>());
        }
    }
}