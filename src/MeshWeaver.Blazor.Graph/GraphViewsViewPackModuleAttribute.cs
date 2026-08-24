using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Blazor.Graph.GraphViewsViewPackModule]

namespace MeshWeaver.Blazor.Graph;

/// <summary>
/// Module registration for the Graph node-view pack. Loading this DLL via
/// <c>Modules:Assemblies</c> applies the pack's hub-side view registrations
/// (<c>BlazorGraphExtensions.AddGraphViews</c>) with no compiled call from the portal — the same
/// lane as the Views/EntityViews/Analysis packs. Dropping the module leaves the node
/// editor/picker/card/collection surfaces to the escaped-HTML fallback slot, so the portals list
/// the DLL under <c>Modules:Assemblies</c> (the bits ship in the image via the
/// <c>modules/&lt;Name&gt;</c> lane).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GraphViewsViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddGraphViews()];
}
