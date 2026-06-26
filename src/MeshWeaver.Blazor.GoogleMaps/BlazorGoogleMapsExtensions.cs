using MeshWeaver.GoogleMaps;
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
        /// Registers the <c>GoogleMapControl</c> type and its Blazor view renderer on the hub configuration.
        /// </summary>
        /// <param name="configuration">The message hub configuration to extend.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static MessageHubConfiguration AddGoogleMaps(this MessageHubConfiguration configuration)
        {
            return configuration
                .WithTypes(typeof(GoogleMapControl))
                .AddViews(registry => registry.WithView<GoogleMapControl, GoogleMapView>());
        }
    }
}