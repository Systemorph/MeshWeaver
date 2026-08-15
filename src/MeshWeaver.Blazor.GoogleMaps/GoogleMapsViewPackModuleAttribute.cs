using MeshWeaver.Maps;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Blazor.GoogleMaps.GoogleMapsViewPackModule]

namespace MeshWeaver.Blazor.GoogleMaps;

/// <summary>
/// Module registration for the Google Maps provider of <see cref="MapControl"/>. Loading this DLL
/// via <c>Modules:Assemblies</c> applies the hub-side view registration
/// (<see cref="BlazorGoogleMapsExtensions.AddGoogleMaps"/>) and binds
/// <see cref="GoogleMapsConfiguration"/> from the host's <c>GoogleMaps</c> configuration section
/// through the options pipeline — no compiled call from the portal. Dropping the module from the
/// list behaves like the old <c>Features:UiPacks:GoogleMaps</c> flag set to false. Other map
/// providers (OpenStreetMap, Apple MapKit) arrive as sibling modules registering their own view
/// for the same provider-neutral control.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GoogleMapsViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Blazor.GoogleMaps")
        {
            Name = "Google Maps view pack",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            // Options-pipeline binding, not services.Configure(section): no IConfiguration is in
            // reach at module-install time — the binder resolves the host's configuration when
            // the options are first read (same pattern as the Observability boot-pack).
            services.AddOptions<GoogleMapsConfiguration>().BindConfiguration("GoogleMaps");
            return services;
        }),
    ];

    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddGoogleMaps()];
}
