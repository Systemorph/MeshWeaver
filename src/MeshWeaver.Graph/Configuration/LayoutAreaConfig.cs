using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Defines a layout area configuration.
/// Stored in _config/layoutAreas/{id}.json
/// </summary>
public record LayoutAreaConfig
{
    /// <summary>
    /// Unique identifier for the layout area.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The area name for routing (e.g., "Details", "Thumbnail").
    /// </summary>
    public required string Area { get; init; }

    /// <summary>
    /// Display title for the area.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Group for organizing areas (e.g., "primary", "secondary").
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Display order within the group.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the area is invisible in navigation.
    /// </summary>
    public bool IsInvisible { get; init; }

    /// <summary>
    /// Inline C# source code for the view function.
    /// Expected signature: public static UiControl ViewName(LayoutAreaHost host, RenderingContext ctx)
    /// Example:
    /// <code>
    /// public static UiControl MyView(LayoutAreaHost host, RenderingContext ctx)
    /// {
    ///     return Controls.Html("&lt;h1&gt;Hello World&lt;/h1&gt;");
    /// }
    /// </code>
    /// </summary>
    public string? ViewSource { get; init; }

    /// <summary>
    /// The compiled view delegate. Not serialized - populated at runtime after compilation.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<LayoutAreaHost, RenderingContext, UiControl>? CompiledView { get; set; }
}
