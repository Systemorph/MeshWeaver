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
public sealed record InboxSection(string Band, string Source, int Order, string Query)
{
    /// <summary>The catalog key of the source's display name, when the provider gave one.</summary>
    public string? SourceLabelKey { get; init; }
}

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

    /// <summary>Who brought the row, as the viewer reads it — the source's display name.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Who brought the row, as an identifier — the leg's <see cref="InboxQueryLeg.Source"/>. What
    /// the source's switch keys on, never shown. (The by-source order follows the display name.)
    /// </summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>The item's node type — what picks its <see cref="IInboxItemHandler"/>.</summary>
    public string NodeType { get; init; } = string.Empty;

    /// <summary>The band the row belongs to, as the viewer reads it ("Needs you", "Running", …).</summary>
    public string Band { get; init; } = string.Empty;

    /// <summary>The band's rank — what needs the viewer first. The importance order, never shown.</summary>
    public int Rank { get; init; }

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

/// <summary>
/// The orderings the inbox offers. An open vocabulary of string constants: the chosen one is STORED
/// in the page's data section and bound to the order select.
/// </summary>
public static class InboxOrder
{
    /// <summary>What needs the viewer first, then what is running, then what finished — newest first within each. The default.</summary>
    public const string Importance = "Importance";

    /// <summary>Newest change first, whatever the band.</summary>
    public const string Newest = "Newest";

    /// <summary>Oldest change first.</summary>
    public const string Oldest = "Oldest";

    /// <summary>By title, A to Z.</summary>
    public const string Title = "Title";

    /// <summary>By the source's display name, then newest first.</summary>
    public const string Source = "Source";

    /// <summary>Every ordering, in the order the select lists them.</summary>
    public static ImmutableArray<string> All { get; } = [Importance, Newest, Oldest, Title, Source];
}

/// <summary>
/// What the viewer asked the inbox to show — the page's ONE piece of view state, held in the layout
/// area's data section and bound, field by field, to the controls above the grid: the search box,
/// the order select and one switch per source. The page never keeps a copy of it.
/// </summary>
public sealed record InboxFilter
{
    /// <summary>The search text. It is handed to the mesh query engine with every leg — not matched against the rows already loaded.</summary>
    public string Search { get; init; } = string.Empty;

    /// <summary>An <see cref="InboxOrder"/> constant.</summary>
    public string Order { get; init; } = InboxOrder.Importance;

    /// <summary>
    /// Which sources are shown, by <see cref="InboxRows.SourceKey"/>. A source with no entry is
    /// shown: a leg a package brings later must not arrive switched off.
    /// </summary>
    public ImmutableDictionary<string, bool> Sources { get; init; } = ImmutableDictionary<string, bool>.Empty;
}

/// <summary>The rows of the inbox, as pure functions of the nodes the legs returned.</summary>
public static class InboxRows
{
    /// <summary>The row property that keys a row (camelCase, as serialized).</summary>
    public const string KeyProperty = "path";

    /// <summary>The row property that carries why a row is not selectable.</summary>
    public const string BlockedReasonProperty = "blockedReason";

    /// <summary>The data id of the grid's selection. No <c>/</c>: a client write into such an id does not reach the owner.</summary>
    public const string SelectionId = "inboxSelection";

    /// <summary>The data id of the grid's rows.</summary>
    public const string GridId = "inboxRows";

    /// <summary>The data id of the <see cref="InboxFilter"/> the controls above the grid are bound to.</summary>
    public const string FilterId = "inboxFilter";

    /// <summary>
    /// The key of <paramref name="source"/> in <see cref="InboxFilter.Sources"/> — and so the last
    /// segment of the pointer its switch is bound to. Lower-case letters and digits only: a source is
    /// named by its provider, a pointer segment must not contain <c>/</c>, and a serializer that
    /// re-cases dictionary keys must not move it.
    /// </summary>
    /// <param name="source">The leg's source.</param>
    public static string SourceKey(string source)
        // The id itself, hex-encoded: one key per id BY CONSTRUCTION. A readable slug is not unique
        // ("Ops / Approvals" and "Ops Approvals" read the same) and a hash suffix only makes a
        // collision rarer — two sources sharing a key would share one switch.
        => "s" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(source)).ToLowerInvariant();

    /// <summary>
    /// <paramref name="filter"/> by VALUE — what the page compares to tell a change the viewer made
    /// from a re-emission of the same state. The record's own equality cannot: its dictionary is
    /// compared by reference, and every read of the data section deserializes a new one.
    /// </summary>
    /// <param name="filter">The filter.</param>
    public static string Fingerprint(InboxFilter filter)
        => string.Join('\n', new[] { filter.Search, filter.Order }
            .Concat(filter.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}")));

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
    /// <param name="band">The band's title as the viewer reads it; the band id when omitted.</param>
    /// <param name="rank">The band's rank in the importance order.</param>
    /// <param name="sourceLabel">The source's display name; its id when omitted.</param>
    /// <param name="culture">The viewer's culture, resolved once on the render turn; invariant when omitted.</param>
    public static InboxRow Of(MeshNode node, InboxSection section, string? zone, string? blockedReason,
        string? band = null, int rank = 0, string? sourceLabel = null, CultureInfo? culture = null)
        => new()
        {
            Path = node.Path,
            Title = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
            Source = sourceLabel ?? section.Source,
            SourceId = section.Source,
            NodeType = node.NodeType ?? string.Empty,
            Band = band ?? section.Band,
            Rank = rank,
            // A stored instant, shown where the viewer is and the way the viewer writes a date —
            // never the container's UTC, never one fixed pattern for every language.
            When = DisplayTimeExtensions.ToDisplayTime(node.LastModified, zone)
                .ToString("g", culture ?? CultureInfo.InvariantCulture),
            Stamp = node.LastModified.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            BlockedReason = blockedReason ?? string.Empty,
        };

    /// <summary>
    /// Every leg's rows as one list: each path once — the first leg that brought it wins, and legs
    /// arrive in band order, so an item two bands select is listed under the more urgent one.
    /// </summary>
    /// <param name="legs">The rows each leg produced, in leg order.</param>
    public static ImmutableList<InboxRow> Merge(IEnumerable<IEnumerable<InboxRow>> legs)
        => legs.SelectMany(rows => rows)
            .GroupBy(row => row.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableList();

    /// <summary>
    /// The rows the viewer asked for: the sources switched on, in the chosen order. The search is
    /// NOT applied here — it went to the query engine with each leg.
    /// </summary>
    /// <param name="rows">The merged rows.</param>
    /// <param name="filter">What the viewer asked for.</param>
    public static ImmutableList<InboxRow> Apply(IEnumerable<InboxRow> rows, InboxFilter filter)
    {
        var shown = rows.Where(row => !filter.Sources.TryGetValue(SourceKey(row.SourceId), out var on) || on);
        var ordered = filter.Order switch
        {
            InboxOrder.Newest => shown.OrderByDescending(r => r.Stamp, StringComparer.Ordinal),
            InboxOrder.Oldest => shown.OrderBy(r => r.Stamp, StringComparer.Ordinal),
            InboxOrder.Title => shown.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Stamp, StringComparer.Ordinal),
            // By the name the viewer READS, so the list is alphabetical in their language; the id
            // only separates two sources that happen to share a display name.
            InboxOrder.Source => shown.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.SourceId, StringComparer.Ordinal)
                .ThenByDescending(r => r.Stamp, StringComparer.Ordinal),
            // Importance — and anything unrecognised: an unknown stored order must never hide what
            // needs the viewer behind what merely happened last.
            _ => shown.OrderBy(r => r.Rank).ThenByDescending(r => r.Stamp, StringComparer.Ordinal),
        };
        return ordered.ThenBy(r => r.Path, StringComparer.Ordinal).ToImmutableList();
    }

    /// <summary>
    /// The rows a bulk act may touch: the selected keys re-checked against the rows as they stand
    /// now — a selection is the viewer's claim about what they saw, never a grant — and confined to
    /// the OWNER's partition, so a search that reached further can never turn into a write there.
    /// </summary>
    /// <param name="rows">The grid's current rows.</param>
    /// <param name="selected">The keys the client reported.</param>
    /// <param name="owner">The inbox's owner.</param>
    public static ImmutableList<InboxRow> Targets(IEnumerable<InboxRow> rows, IEnumerable<string> selected, string owner)
        => MeshWeaver.Layout.DataGrid.DataGridRowSelection.Prune(
            rows.Where(row => row.Path.StartsWith(owner + "/", StringComparison.Ordinal)),
            selected, r => r.Path, r => r.BlockedReason);
}
