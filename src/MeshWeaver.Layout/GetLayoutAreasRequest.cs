using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Gets the list of layout areas available in the layout context.
/// </summary>
public record GetLayoutAreasRequest : IRequest<LayoutAreasResponse>;

/// <summary>
/// Returns the list of layout areas with their definitions.
/// </summary>
/// <param name="Areas">List of layout area definitions available</param>
public record LayoutAreasResponse(IEnumerable<LayoutAreaDefinition> Areas);

/// <summary>
/// Reference for listing available layout areas in a hub.
/// Used by the "layoutAreas" UnifiedPath handler.
/// Returns LayoutAreaDefinition list from the layout system.
/// </summary>
public record LayoutAreasReference() : WorkspaceReference<object>
{
    /// <summary>Returns the fixed string <c>"layoutAreas"</c> that identifies this reference in hrefs.</summary>
    public override string ToString() => "layoutAreas";
}

