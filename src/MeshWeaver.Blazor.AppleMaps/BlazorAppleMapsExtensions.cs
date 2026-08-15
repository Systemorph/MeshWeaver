using MeshWeaver.Maps;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.AppleMaps
{
    /// <summary>
    /// Extension methods for registering the Apple MapKit renderer of <c>MapControl</c> with a
    /// message hub.
    /// </summary>
    public static class BlazorAppleMapsExtensions
    {
        /// <summary>
        /// Registers the map control types and the Apple MapKit Blazor view renderer on the hub
        /// configuration. View maps are first-match-wins, so a deployment activates exactly one
        /// map provider module.
        /// </summary>
        /// <param name="configuration">The message hub configuration to extend.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static MessageHubConfiguration AddAppleMaps(this MessageHubConfiguration configuration)
        {
            return configuration
                // The control + the content/config records serialised inside it (Options, markers,
                // circles) — the reflection sweep in AddLayoutTypes only covers the
                // MeshWeaver.Layout assembly.
                .WithTypes(
                    typeof(MapControl),
                    typeof(MapOptions),
                    typeof(LatLng),
                    typeof(MapMarker),
                    typeof(MapCircle))
                .AddViews(registry => registry.WithView<MapControl, AppleMapView>());
        }
    }
}
