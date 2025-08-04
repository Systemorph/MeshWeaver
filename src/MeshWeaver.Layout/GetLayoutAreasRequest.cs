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

