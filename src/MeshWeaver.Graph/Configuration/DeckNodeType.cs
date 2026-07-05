using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Deck node types in the graph.
/// A <b>Deck</b> is a presentation (or a course sequence) whose slide/page ORDER is
/// declared EXTERNALLY, on the deck node itself, in <see cref="DeckContent.Slides"/> —
/// an ordered list of child references. The individual <see cref="SlideNodeType">Slide</see>
/// nodes stay pure content: they carry no order of their own, so re-sequencing a deck is
/// a single edit to the deck's manifest, never a sweep across every slide.
/// <para>
/// The Deck's Overview renders a hidable side-nav built from that manifest plus a stage
/// with a "Present" entry point (see <see cref="DeckLayoutAreas"/>). When a Slide's parent
/// is a Deck, the Slide views resolve prev/next/index/count from the deck's manifest instead
/// of the sibling <see cref="MeshNode.Order"/> fallback — see <see cref="SlideLayoutAreas"/>.
/// </para>
/// </summary>
public static class DeckNodeType
{
    /// <summary>
    /// The NodeType value used to identify deck nodes.
    /// </summary>
    public const string NodeType = "Deck";

    /// <summary>
    /// Inline deck/presentation-screen glyph (SVG data URI). Kept inline so the node
    /// type needs no static-asset round-trip; it renders directly as an <c>&lt;img src&gt;</c>.
    /// </summary>
    private const string DeckIcon =
        "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%2024%2024'%20fill='%234f6bed'%3E"
        + "%3Cpath%20d='M3%204h18a1%201%200%200%201%201%201v10a1%201%200%200%201-1%201h-7v2h3v2H7v-2h3v-2H3a1%201%200%200%201-1-1V5a1%201%200%200%201%201-1zm2%203v6h14V7H5z'/%3E"
        + "%3C/svg%3E";

    /// <summary>
    /// Registers the built-in "Deck" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddDeckType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Deck node type.
    /// This provides HubConfiguration for nodes with nodeType="Deck": the deck views,
    /// the <see cref="DeckContent"/> data source, and the create-a-Slide menu affordance.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Deck",
        Icon = DeckIcon,
        HubConfiguration = config => config
            .AddDeckViews()
            .AddMeshDataSource(s => s.WithContentType<DeckContent>())
            // Creating a child from a Deck offers Slide — the deck's natural content.
            .AddCreatableTypes(SlideNodeType.NodeType)
    };
}

/// <summary>
/// The content of a Deck MeshNode — a presentation / course sequence.
/// Immutable; every mutation goes through
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
/// </summary>
public record DeckContent
{
    /// <summary>Optional display title for the deck's welcome stage. Falls back to the node name.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional markdown intro rendered on the deck's welcome stage (the Overview's
    /// right pane). Falls back to a default welcome message when empty.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The ordered list of child slide (or page) references — this IS the deck's order,
    /// declared EXTERNALLY here rather than on each slide. Each entry is either the child's
    /// id (relative to the deck, e.g. <c>"intro"</c>) or its full path; both resolve to a
    /// child node under the deck. The side-nav, prev/next, and Present walk follow this order.
    /// Default empty.
    /// </summary>
    public ImmutableList<string> Slides { get; init; } = ImmutableList<string>.Empty;
}
