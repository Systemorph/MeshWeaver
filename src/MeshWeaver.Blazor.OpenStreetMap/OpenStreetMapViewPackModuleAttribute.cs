using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Blazor.OpenStreetMap.OpenStreetMapViewPackModule]

namespace MeshWeaver.Blazor.OpenStreetMap;

/// <summary>
/// Module registration for the OpenStreetMap provider of <c>MapControl</c>. Listing this DLL
/// under <c>Modules:Assemblies</c> registers the Leaflet/OSM renderer — no API key, no further
/// configuration. View maps are first-match-wins, so a deployment lists exactly ONE map provider
/// module (this one, Google Maps, or Apple MapKit); the control and every layout area using it
/// stay identical across providers.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OpenStreetMapViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddOpenStreetMap()];
}
