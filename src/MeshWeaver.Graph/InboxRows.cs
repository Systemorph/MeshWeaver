using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>One leg of the page, resolved for its owner: which band it feeds, who brought it, and its query.</summary>
/// <param name="Band">The band the leg feeds.</param>
/// <param name="Source">Who brought the leg — the row's source marker.</param>
/// <param name="Order">The leg's order within its band.</param>
/// <param name="Query">The resolved query, anchored on the owner.</param>
public sealed record InboxSection(string Band, string Source, int Order, string Query);

/// <summary>
/// One row of the inbox, whatever brought it — the grid's row record, so its camelCase property
/// names are the column properties.
/// </summary>
public sealed record InboxRow
{
    /// <summary>The item's node path — the row key, and where Open goes.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>What the row says: the node's name, else its id.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Who brought the row — the leg's <see cref="InboxQueryLeg.Source"/>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The item's node type — what picks its <see cref="IInboxItemHandler"/>.</summary>
    public string NodeType { get; init; } = string.Empty;

    /// <summary>When the item last changed, in the VIEWER's time zone.</summary>
    public string When { get; init; } = string.Empty;

    /// <summary>The same instant as a sortable UTC stamp — the row order, never shown.</summary>
    public string Stamp { get; init; } = string.Empty;

    /// <summary>
    /// Why the row cannot be selected, or empty when it can — the grid renders a disabled checkbox
    /// with this as its tooltip.
    /// </summary>
    public string BlockedReason { get; init; } = string.Empty;
}

/// <summary>What Delete put away — what Undo brings back.</summary>
/// <param name="Path">The item's node path.</param>
/// <param name="NodeType">Its node type, so Undo finds the same handler.</param>
public sealed record InboxUndoItem(string Path, string NodeType);

/// <summary>The rows of the inbox, as pure functions of the nodes the legs returned.</summary>
public static class InboxRows
{
    /// <summary>The row property that keys a row (camelCase, as serialized).</summary>
    public const string KeyProperty = "path";

    /// <summary>The row property that carries why a row is not selectable.</summary>
    public const string BlockedReasonProperty = "blockedReason";

    /// <summary>
    /// The selection data id of <paramref name="band"/>'s grid. A provider may name its own band, so
    /// anything that is not a letter or digit is dropped — a client write into a data id containing
    /// <c>/</c> does not reach the owner (Doc/GUI/DataGrid → "Row selection").
    /// </summary>
    /// <param name="band">The band.</param>
    public static string SelectionId(string band) => "inboxSelection" + Slug(band);

    /// <summary>The rows data id of <paramref name="band"/>'s grid.</summary>
    /// <param name="band">The band.</param>
    public static string GridId(string band) => "inboxRows" + Slug(band);

    /// <summary>The synced-query id of one leg — stable per leg, never composed per call.</summary>
    /// <param name="section">The leg.</param>
    public static string QueryId(InboxSection section)
        => $"Inbox.{Slug(section.Band)}.{Slug(section.Source)}.{section.Order.ToString(CultureInfo.InvariantCulture)}";

    private static string Slug(string value) => new([.. value.Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// The row for <paramref name="node"/>, brought by <paramref name="section"/>.
    /// </summary>
    /// <param name="node">The node the leg returned (projected — content is not read).</param>
    /// <param name="section">The leg that returned it.</param>
    /// <param name="zone">The viewer's time zone, resolved once on the render turn.</param>
    /// <param name="blockedReason">Why the row cannot be selected, or null when it can.</param>
    public static InboxRow Of(MeshNode node, InboxSection section, string? zone, string? blockedReason)
        => new()
        {
            Path = node.Path,
            Title = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
            Source = section.Source,
            NodeType = node.NodeType ?? string.Empty,
            // A stored instant, shown where the viewer is — never the container's UTC.
            When = DisplayTimeExtensions.ToDisplayTime(node.LastModified, zone)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Stamp = node.LastModified.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            BlockedReason = blockedReason ?? string.Empty,
        };

    /// <summary>
    /// One band's rows from its legs' results: each path once (the first leg that brought it wins),
    /// newest first.
    /// </summary>
    /// <param name="legs">The rows each leg produced, in leg order.</param>
    public static ImmutableList<InboxRow> Merge(IEnumerable<IEnumerable<InboxRow>> legs)
        => legs.SelectMany(rows => rows)
            .GroupBy(row => row.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(row => row.Stamp, StringComparer.Ordinal)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ToImmutableList();

    /// <summary>
    /// The rows a bulk act may touch: the selected keys re-checked against the rows as they stand
    /// now — a selection is the viewer's claim about what they saw, never a grant.
    /// </summary>
    /// <param name="rows">The band's current rows.</param>
    /// <param name="selected">The keys the client reported.</param>
    public static ImmutableList<InboxRow> Targets(IEnumerable<InboxRow> rows, IEnumerable<string> selected)
        => MeshWeaver.Layout.DataGrid.DataGridRowSelection.Prune(rows, selected, r => r.Path, r => r.BlockedReason);
}
