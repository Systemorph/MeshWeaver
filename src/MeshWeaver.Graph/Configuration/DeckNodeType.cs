using System.Collections.Immutable;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The compiled residue of the retired built-in Deck node type. Decks are owned by the
/// Publish pack (<c>Publish/Deck</c>, a dynamic NodeType with in-mesh layout areas); V53
/// retyped every bare <c>Deck</c> instance mesh-wide (#1589). What stays compiled: this
/// type's <see cref="NodeType"/> const and suffix-aware <see cref="Matches"/> (persistence
/// parser + export gates) and the <see cref="DeckContent"/> record — the EXTERNAL, ordered
/// slide manifest that <c>DeckSlidesCache</c> and the export templates resolve.
/// </summary>
public static class DeckNodeType
{
    /// <summary>
    /// The NodeType value used to identify deck nodes.
    /// </summary>
    public const string NodeType = "Deck";

    /// <summary>
    /// True when <paramref name="nodeType"/> is a deck type: the built-in <c>Deck</c> OR any
    /// plugin-installed variant whose dynamic type identity ends in <c>/Deck</c> (a dynamic
    /// NodeType's identity is its install path; instances carry it verbatim). Every compiled
    /// gate that used to compare with <c>==</c> must use this instead — an equality gate makes
    /// a plugin deck silently lose its Export menu, its pixel-export picker and its manifest
    /// ordering, with no error anywhere. See <see cref="SlideNodeType.Matches"/>.
    /// </summary>
    public static bool Matches(string? nodeType) =>
        nodeType == NodeType
        || nodeType?.EndsWith("/" + NodeType, StringComparison.Ordinal) == true;
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
    /// The ordered list of slide (or page) <b>references</b> — this IS the deck's order,
    /// declared EXTERNALLY here rather than on each slide. A referenced slide may live
    /// <b>anywhere in the mesh</b>: presentation is fully decoupled from the slide nodes, so the
    /// SAME slide path may appear in many decks, in different orders and selections. Each entry is
    /// either a bare id (relative to the deck — <c>"intro"</c> → <c>"{deck}/intro"</c>, kept for
    /// backward compatibility) or an <b>absolute path</b> to a slide node anywhere (any entry
    /// containing a <c>/</c> is treated as an absolute path). Each is resolved to its node via
    /// <c>workspace.GetMeshNodeStream(path)</c>. The side-nav, prev/next/index/count, and the
    /// Present walk all follow this order. Default empty.
    /// </summary>
    public ImmutableList<string> Slides { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Optional GitHub-style mesh-node query selecting the deck's slides DYNAMICALLY, as a live
    /// (synced) set — used only when <see cref="Slides"/> is empty. The matched nodes are ordered
    /// by <c>MeshNode.Order</c> (nulls last, ties by path). When BOTH this and
    /// <see cref="Slides"/> are empty the deck defaults to <b>its own subtree</b>
    /// (<c>path:{deck} scope:descendants</c>), so a deck with slides as children just works with no
    /// manifest. An explicit <see cref="Slides"/> manifest always wins and is kept in its declared
    /// order (never re-sorted). Default null.
    /// </summary>
    public string? Query { get; init; }
}
