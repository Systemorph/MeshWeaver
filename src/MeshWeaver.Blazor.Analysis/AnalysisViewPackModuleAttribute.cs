using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Blazor.Analysis.AnalysisViewPackModule]

namespace MeshWeaver.Blazor.Analysis;

/// <summary>
/// Module registration for the Analysis view pack. Loading this DLL via <c>Modules:Assemblies</c>
/// applies the pack's hub-side view registrations
/// (<see cref="AnalysisViewPackExtensions.AddAnalysisViews"/>) with no compiled call from the
/// portal. Dropping the module from the list behaves like the old <c>Features:UiPacks:Analysis</c>
/// flag set to false: the pack's controls fall back to the fallback slot.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AnalysisViewPackModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [config => config.AddAnalysisViews()];
}
