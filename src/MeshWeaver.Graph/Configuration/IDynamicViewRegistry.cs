using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registry for dynamically compiled views.
/// Views registered here are invoked by the DynamicViewRenderer during layout rendering.
/// </summary>
public interface IDynamicViewRegistry
{
    /// <summary>
    /// Registers a compiled view for the specified area.
    /// </summary>
    /// <param name="area">The area name</param>
    /// <param name="view">The compiled view delegate</param>
    /// <param name="areaDefinition">Optional area definition metadata</param>
    void RegisterView(string area, Func<LayoutAreaHost, RenderingContext, UiControl> view, LayoutAreaDefinition? areaDefinition = null);

    /// <summary>
    /// Registers only area definition metadata (no view function).
    /// </summary>
    /// <param name="area">The area name</param>
    /// <param name="areaDefinition">Area definition metadata</param>
    void RegisterAreaDefinition(string area, LayoutAreaDefinition areaDefinition);

    /// <summary>
    /// Gets a registered view by area name.
    /// </summary>
    /// <param name="area">The area name</param>
    /// <returns>The view delegate, or null if not found</returns>
    Func<LayoutAreaHost, RenderingContext, UiControl>? GetView(string area);

    /// <summary>
    /// Gets area definition by area name.
    /// </summary>
    /// <param name="area">The area name</param>
    /// <returns>The area definition, or null if not found</returns>
    LayoutAreaDefinition? GetAreaDefinition(string area);

    /// <summary>
    /// Gets all registered area names with views.
    /// </summary>
    IEnumerable<string> GetViewAreas();

    /// <summary>
    /// Gets all registered area definitions.
    /// </summary>
    IEnumerable<LayoutAreaDefinition> GetAreaDefinitions();

    /// <summary>
    /// Checks if a view is registered for the specified area.
    /// </summary>
    /// <param name="area">The area name</param>
    /// <returns>True if a view is registered</returns>
    bool HasView(string area);
}
