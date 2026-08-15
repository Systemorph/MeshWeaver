using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Blazor.Radzen.RadzenViewPackModule]

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// Module registration for the Radzen view pack. Loading this DLL via <c>Modules:Assemblies</c>
/// applies the pack's complete surface — the hub-side view registrations
/// (<see cref="RadzenViewPackExtensions.AddRadzenViews"/>) and the DI twin
/// (<see cref="RadzenServiceExtensions.AddRadzenServices"/>) — with no compiled call from the
/// portal. Dropping the module from the list behaves like the old <c>Features:UiPacks:Radzen</c>
/// flag set to false: the pack's controls fall back to the fallback slot.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RadzenViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Blazor.Radzen")
        {
            Name = "Radzen view pack",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddRadzenServices()),
    ];

    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddRadzenViews()];
}
