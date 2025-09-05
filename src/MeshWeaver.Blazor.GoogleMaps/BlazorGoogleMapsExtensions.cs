using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.GoogleMaps
{
    public static class BlazorGoogleMapsExtensions
    {
        public static MessageHubConfiguration AddGoogleMaps(this MessageHubConfiguration configuration)
        {
            return configuration
                .WithTypes(typeof(GoogleMapControl))
                .AddViews(registry => registry.WithView<GoogleMapControl, GoogleMapView>());
        }
    }
}