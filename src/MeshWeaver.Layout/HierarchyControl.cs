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

    /// <summary>The preferred one among alternatives (the favourite quote option): marked with a star.</summary>
    public const string Favourite = "favourite";
}

/// <summary>
/// What a <see cref="HierarchyNode"/> IS — an OPEN vocabulary the renderers style distinctly (a
/// proportional treaty and an excess-of-loss layer must not look alike). Unknown kinds draw plainly.
/// </summary>
public static class HierarchyNodeKind
{
    /// <summary>A whole the others belong to: a submission, a programme, a book.</summary>
    public const string Group = "group";

    /// <summary>One of several mutually exclusive alternatives (a quote option).</summary>
    public const string Option = "option";

    /// <summary>A proportional cover: quota share or surplus (ceded %, commission).</summary>
    public const string Proportional = "proportional";

    /// <summary>A non-proportional cover: an excess-of-loss layer (limit xs attachment, rate on line).</summary>
    public const string ExcessOfLoss = "excess-of-loss";

    /// <summary>What a cover applies to: a section, a segment, a line of business.</summary>
    public const string Section = "section";
}

/// <summary>
/// How a node lays out its CHILDREN — an open vocabulary; unknown values lay out as <see cref="Row"/>.
/// </summary>
public static class HierarchyArrangement
{
    /// <summary>Side by side under the parent — alternatives, parts (the default).</summary>
    public const string Row = "row";

    /// <summary>
    /// One above the other under the parent, in input order top-down — a TOWER of layers. Give the
    /// highest layer first. Keeps a deal of five options with six layers each readable: five columns,
    /// not thirty boxes in a row.
    /// </summary>
    public const string Stack = "stack";
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
    string? Emphasis = null)
{
    /// <summary>What the node is — a <see cref="HierarchyNodeKind"/> constant; styles the box. Optional.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// The label of the edge to the parent — the RELATION, e.g. "inures to", "on retention of",
    /// "option". Already localized. Optional.
    /// </summary>
    public string? Relation { get; init; }

    /// <summary>How this node's children are laid out — a <see cref="HierarchyArrangement"/> constant. Optional.</summary>
    public string? Arrange { get; init; }

    /// <summary>Short status chips on the box (a verdict, "bound", "declined"). Already localized. Optional.</summary>
    public IReadOnlyList<string>? Badges { get; init; }

    /// <summary>Small figures on the box (our line, our premium), each a label and a formatted value. Optional.</summary>
    public IReadOnlyList<KpiItem>? Figures { get; init; }

    /// <summary>Whether this node lays out its children as a <see cref="HierarchyArrangement.Stack"/>.</summary>
    public bool StacksChildren => string.Equals(Arrange, HierarchyArrangement.Stack, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this node is of <paramref name="kind"/> (case-insensitive).</summary>
    /// <param name="kind">A <see cref="HierarchyNodeKind"/> constant.</param>
    /// <returns>True when <see cref="Kind"/> is <paramref name="kind"/>.</returns>
    public bool IsKind(string kind) => string.Equals(Kind ?? string.Empty, kind, StringComparison.OrdinalIgnoreCase);
}

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
/// A top-down hierarchy of boxes — each box a title, a subtitle, optional badges and small figures,
/// joined to its parent by a connector that may carry the RELATION ("inures to"), the current or
/// favourite node emphasised, every other box optionally a link, and each box styled by its
/// <see cref="HierarchyNode.Kind"/>. The structure of a deal at any depth — the submission → its
/// mutually exclusive options → each option's layers (stacked as a tower) → what each layer covers —
/// and chains of covers (a quota share whose retention is protected by XL layers).
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
    ///   <item>A cycle (a → b → a) has no root to hang from: its member that comes first in input
    ///     order becomes a root — nodes that merely hang BELOW the cycle keep their parent — so every
    ///     node is drawn exactly once.</item>
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
        // Whatever is left has no root above it: it is ON a cycle, or hangs below one. Walk up the
        // parent links from the first unplaced node until a node repeats — that loop is the cycle —
        // and break it at the cycle member that comes FIRST in input order, so a descendant listed
        // before its cycle keeps its parent instead of being promoted to a root.
        var order = unique.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var byId = unique.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var orphan in unique)
        {
            if (placed.Contains(orphan.Id))
                continue;
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cursor = orphan;
            while (seen.Add(cursor.Id))
            {
                path.Add(cursor.Id);
                cursor = byId[cursor.Parent!];
            }
            var cycle = path.Skip(path.IndexOf(cursor.Id));
            roots.Add(Build(byId[cycle.MinBy(id => order[id])!], 0));
        }

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
