using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Blazor.EntityViews.EntityViewsViewPackModule]

namespace MeshWeaver.Blazor.EntityViews;

/// <summary>
/// Module registration for the EntityViews view pack. Loading this DLL via
/// <c>Modules:Assemblies</c> applies the pack's hub-side view registrations
/// (<see cref="EntityViewsExtensions.AddEntityViews"/>) with no compiled call from the portal —
/// the same lane as the Analysis/Radzen/GoogleMaps packs. Dropping the module leaves the entity
/// form/edit controls to the escaped-HTML fallback slot, which is why the portals declare the
/// DLL under <c>Modules:Required</c>: a rollout that lost the pack must STALL on readiness, not
/// complete into a portal whose edit forms went blank.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class EntityViewsViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddEntityViews()];
}
