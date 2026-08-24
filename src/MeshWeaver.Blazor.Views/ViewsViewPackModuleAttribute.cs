using System.Runtime.CompilerServices;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

// Same test-visibility set as the base pack these views moved out of.
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith.Test")]

[assembly: MeshWeaver.Blazor.Views.ViewsViewPackModule]

namespace MeshWeaver.Blazor.Views;

/// <summary>
/// Module registration for the default-views pack. Loading this DLL via
/// <c>Modules:Assemblies</c> applies the pack's hub-side view registrations
/// (<see cref="ViewsExtensions.AddDefaultViews"/>) with no compiled call from the portal —
/// the same lane as the EntityViews/Analysis/Radzen/GoogleMaps packs. Dropping the module leaves
/// EVERY standard control to the escaped-HTML fallback slot, which is why the portals declare the
/// DLL under <c>Modules:Required</c>: a rollout that lost the pack must STALL on readiness, not
/// complete into a portal that renders raw control JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ViewsViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddDefaultViews()];
}
