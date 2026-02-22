using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for AccessAssignment nodes.
/// Uses standard editor views driven by attributes on the AccessAssignment model.
/// Only registers the Delete view explicitly.
/// </summary>
public static class AccessAssignmentLayoutAreas
{
    /// <summary>
    /// Adds the AccessAssignment views to the hub's layout.
    /// Standard Overview/Edit are handled by the default MeshNode editor via attributes.
    /// </summary>
    public static MessageHubConfiguration AddAccessAssignmentViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));
}
