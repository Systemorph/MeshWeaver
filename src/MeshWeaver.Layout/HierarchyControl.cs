using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// How a <see cref="HierarchyNode"/> is set off from the others — an OPEN vocabulary of string
/// constants, never an enum (policy <c>open-vocabulary-string-constants</c>): a renderer that meets
/// a value it does not know draws the node plainly.
/// </summary>
public static class HierarchyEmphasis
{
    /// <summary>No emphasis — an ordinary box.</summary>
    public const string None = "";

    /// <summary>
    /// The node the page is ABOUT: outlined in the accent colour and never a link (it would only
    /// navigate to the page already open).
    /// </summary>
    public const string Current = "current";

    /// <summary>A heading node — the whole the others belong to (a deal, a programme): filled in the accent colour.</summary>
    public const string Strong = "strong";

    /// <summary>A node of secondary weight (a dimension, a reference): drawn subdued.</summary>
    public const string Muted = "muted";
}

/// <summary>
/// One box of a <see cref="HierarchyControl"/>, given FLAT with a reference to its parent — the
/// shape a projection naturally produces, and one that survives the wire without recursion.
/// </summary>
/// <param name="Id">The node's identifier, unique within the hierarchy.</param>
/// <param name="Parent">The parent's <see cref="Id"/>; null or blank for a root.</param>
/// <param name="Title">The box's first line (already localized).</param>
/// <param name="Subtitle">The box's second line — terms, a kind, a figure. Optional.</param>
/// <param name="Href">Navigation target for the box; null renders it unlinked.</param>
/// <param name="Emphasis">A <see cref="HierarchyEmphasis"/> constant; defaults to none.</param>
public record HierarchyNode(
    string Id,
    string? Parent,
    string Title,
    string? Subtitle = null,
    string? Href = null,
    string? Emphasis = null);

/// <summary>
/// One node placed in the tree — the pure structure <see cref="HierarchyControl.Layout"/> hands a view.
/// </summary>
/// <param name="Node">The node.</param>
/// <param name="Depth">0 for a root, 1 for its children, …</param>
/// <param name="Children">The node's children in input order.</param>
public record HierarchyTreeNode(HierarchyNode Node, int Depth, ImmutableList<HierarchyTreeNode> Children);

/// <summary>
/// The hierarchy resolved into trees.
/// </summary>
/// <param name="Roots">The top-level nodes in input order.</param>
/// <param name="Depth">The number of levels — 1 for a flat list of roots.</param>
public record HierarchyLayout(ImmutableList<HierarchyTreeNode> Roots, int Depth);

/// <summary>
/// A top-down hierarchy of boxes — each box a title and a subtitle, joined to its parent by an elbow
/// connector, the current node emphasised and every other box optionally a link. The structure of a
/// deal (the deal → its options → what each one covers), an organisation, a programme.
///
/// <para>The nodes are given FLAT (<see cref="HierarchyNode.Parent"/> names the parent), and the tree
/// is resolved by the pure <see cref="Layout"/>, which is pinned by unit tests: unknown parents become
/// roots, duplicates and cycles are broken deterministically, so a renderer never recurses forever and
/// never drops a node silently.</para>
///
/// <para>Composable like any other leaf control — drop it into a <c>Controls.Stack</c> or a
/// <c>Controls.LayoutGrid</c> cell.</para>
/// </summary>
/// <param name="Nodes">The nodes, or a data-binding reference resolving to them.</param>
public record HierarchyControl(object Nodes)
    : UiControl<HierarchyControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Returns a copy drawing <paramref name="nodes"/> instead.</summary>
    /// <param name="nodes">The nodes, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="HierarchyControl"/> with the specified nodes.</returns>
    public HierarchyControl WithNodes(object nodes) => this with { Nodes = nodes };

    /// <summary>
    /// Resolves the flat <paramref name="nodes"/> into trees. Pure — the hierarchy's whole structure,
    /// kept out of the views.
    /// <list type="bullet">
    ///   <item>A node whose parent is blank, itself, or not among the nodes is a ROOT.</item>
    ///   <item>Children keep their input order.</item>
    ///   <item>A repeated <see cref="HierarchyNode.Id"/> keeps its FIRST occurrence; later ones are dropped.</item>
    ///   <item>A cycle (a → b → a) has no root to hang from: its first node in input order becomes a
    ///     root, so every node is drawn exactly once.</item>
    /// </list>
    /// Returns <c>null</c> when there are no nodes; a view says so in a sentence.
    /// </summary>
    /// <param name="nodes">The nodes to place.</param>
    /// <returns>The resolved trees, or <c>null</c> when there is nothing to draw.</returns>
    public static HierarchyLayout? Layout(IEnumerable<HierarchyNode>? nodes)
    {
        var unique = new List<HierarchyNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // A node off the wire may carry a null id; it is still a box to draw, under the empty id.
        foreach (var node in (nodes ?? []).Where(n => n is not null).Select(n => n.Id is null ? n with { Id = string.Empty } : n))
            if (ids.Add(node.Id))
                unique.Add(node);
        if (unique.Count == 0)
            return null;

        bool IsRoot(HierarchyNode n) =>
            string.IsNullOrWhiteSpace(n.Parent) || n.Parent == n.Id || !ids.Contains(n.Parent);

        var children = unique
            .Where(n => !IsRoot(n))
            .GroupBy(n => n.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;

        HierarchyTreeNode Build(HierarchyNode node, int level)
        {
            placed.Add(node.Id);
            depth = Math.Max(depth, level + 1);
            var kids = children.TryGetValue(node.Id, out var list)
                ? list.Where(c => !placed.Contains(c.Id)).ToList()
                : [];
            // Mark before descending, so a sibling cannot be re-entered through a cycle below.
            foreach (var kid in kids)
                placed.Add(kid.Id);
            return new HierarchyTreeNode(node, level, kids.Select(k => Build(k, level + 1)).ToImmutableList());
        }

        var roots = ImmutableList.CreateBuilder<HierarchyTreeNode>();
        foreach (var root in unique.Where(IsRoot))
            roots.Add(Build(root, 0));
        // Whatever is left hangs only from itself: a cycle. Break it at its first node in input order.
        foreach (var orphan in unique)
            if (!placed.Contains(orphan.Id))
                roots.Add(Build(orphan, 0));

        return new HierarchyLayout(roots.ToImmutable(), depth);
    }

    /// <summary>
    /// Whether <paramref name="node"/> is drawn as a link: it has an href and it is not the
    /// <see cref="HierarchyEmphasis.Current"/> node (which would only open the page already open).
    /// Returns the navigable href (see <see cref="TowerControl.NavigableHref"/>), or null.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The href to navigate to, or <c>null</c> for an unlinked box.</returns>
    public static string? LinkOf(HierarchyNode node) =>
        IsEmphasis(node, HierarchyEmphasis.Current) ? null : TowerControl.NavigableHref(node.Href);

    /// <summary>Whether <paramref name="node"/> carries <paramref name="emphasis"/> (case-insensitive).</summary>
    /// <param name="node">The node.</param>
    /// <param name="emphasis">A <see cref="HierarchyEmphasis"/> constant.</param>
    /// <returns>True when the node's emphasis is <paramref name="emphasis"/>.</returns>
    public static bool IsEmphasis(HierarchyNode node, string emphasis) =>
        string.Equals(node.Emphasis ?? string.Empty, emphasis, StringComparison.OrdinalIgnoreCase);
}
