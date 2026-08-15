using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Blazor.AppleMaps.AppleMapsViewPackModule]

namespace MeshWeaver.Blazor.AppleMaps;

/// <summary>
/// Module registration for the Apple MapKit provider of <c>MapControl</c>. Listing this DLL
/// under <c>Modules:Assemblies</c> registers the MapKit JS renderer and binds
/// <see cref="AppleMapsConfiguration"/> from the host's <c>AppleMaps</c> configuration section
/// through the options pipeline (a MapKit JS token is required for the map to actually render).
/// View maps are first-match-wins, so a deployment lists exactly ONE map provider module.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AppleMapsViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Blazor.AppleMaps")
        {
            Name = "Apple MapKit view pack",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            // Options-pipeline binding, not services.Configure(section): no IConfiguration is in
            // reach at module-install time — the binder resolves the host's configuration when
            // the options are first read.
            services.AddOptions<AppleMapsConfiguration>().BindConfiguration("AppleMaps");
            return services;
        }),
    ];

    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddAppleMaps()];
}
