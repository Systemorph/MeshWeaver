using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Slide node types in the graph.
/// A Slide is one page of a presentation deck: its <see cref="SlideContent"/> carries
/// the slide body (markdown — raw HTML/SVG passes through the markdown pipeline, so
/// authors have full visual freedom), optional speaker notes, and an optional CSS
/// background for the stage.
/// <para>
/// A <b>deck</b> is not a node type of its own: any parent node whose children are
/// Slide nodes is a deck, and the slides play in <see cref="MeshNode.Order"/>
/// (lower first, null last). The Slide views resolve prev/next from the sibling
/// Slide nodes of the same parent — see <see cref="SlideLayoutAreas"/>.
/// </para>
/// </summary>
public static class SlideNodeType
{
    /// <summary>
    /// The NodeType value used to identify slide nodes.
    /// </summary>
    public const string NodeType = "Slide";

    /// <summary>
    /// Registers the built-in "Slide" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddSlideType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Slide node type.
    /// This provides HubConfiguration for nodes with nodeType="Slide".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Slide",
        Icon = "/static/NodeTypeIcons/presentation.svg",
        HubConfiguration = config => config
            .AddSlideViews()
            .AddMeshDataSource(s => s.WithContentType<SlideContent>())
    };
}

/// <summary>
/// The content of a Slide MeshNode — one page of a presentation deck.
/// Immutable; every mutation goes through
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
/// </summary>
public record SlideContent
{
    /// <summary>
    /// The slide body as markdown. Raw HTML and SVG pass through the markdown
    /// pipeline unchanged, so a slide can be anything from a bullet list to a
    /// full-bleed illustration.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Speaker notes (markdown). Shown only in the Notes view, never on the stage.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// CSS background for the slide stage (e.g. a gradient like
    /// <c>linear-gradient(135deg, #667eea 0%, #764ba2 100%)</c>).
    /// When null, the stage uses the theme-aware default gradient.
    /// </summary>
    public string? Background { get; init; }

    /// <summary>
    /// Reserved for a future slide-transition effect. Default null (no transition).
    /// </summary>
    public string? Transition { get; init; }
}
