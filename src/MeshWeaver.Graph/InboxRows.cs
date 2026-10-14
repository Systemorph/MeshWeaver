using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>The catalog key of a provider band's display name, when the provider gave one.</summary>
    public string? BandLabelKey { get; init; }

    /// <summary>Whether every row this leg brings is unread (<see cref="InboxQueryLeg.Unread"/>).</summary>
    public bool Unread { get; init; }

    /// <summary>Whether the source is shown before the viewer touches its switch.</summary>
    public bool ShownByDefault { get; init; } = true;

    /// <summary>The source's symbol (<see cref="InboxQueryLeg.SourceIcon"/>), or empty.</summary>
    public string SourceIcon { get; init; } = string.Empty;

    /// <summary>The content property the rows are timed by (<see cref="InboxQueryLeg.TimeField"/>), or null for <c>LastModified</c>.</summary>
    public string? TimeField { get; init; }

    /// <summary>The side-menu category (<see cref="InboxQueryLeg.Category"/>, else the source).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The catalog key of the category's display name, when the provider gave one.</summary>
    public string? CategoryLabelKey { get; init; }
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

    /// <summary>The source's symbol, shown before the title.</summary>
    public string SourceIcon { get; init; } = string.Empty;

    /// <summary>Whether the item is unread — the row is shown bold.</summary>
    public bool Unread { get; init; }

    /// <summary>The side-menu category the row is filed under (<see cref="InboxSection.Category"/>).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// The row's CSS class — <see cref="InboxRows.UnreadClass"/> for an unread item, else empty. The
    /// grid applies it per row (<c>DataGridControl.WithRowClassProperty</c>).
    /// </summary>
    public string RowClass { get; init; } = string.Empty;

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
/// The orderings the inbox offers — a CLOSED set, owned by the page: the select lists exactly
/// <see cref="All"/>, and no package adds one. String constants only because the chosen one is
/// STORED in the page's data section and bound to the select. The default is <see cref="Newest"/> —
/// everything by date, as a mail client lists it; a stored value outside the set (one a later
/// version retired) reads as that default.
/// </summary>
public static class InboxOrder
{
    /// <summary>What needs the viewer first, then what is running, then what finished — newest first within each.</summary>
    public const string Importance = "Importance";

    /// <summary>Newest change first, whatever the band. The default.</summary>
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

    /// <summary>An <see cref="InboxOrder"/> constant — no longer read: the list is always newest first.</summary>
    public string Order { get; init; } = InboxOrder.Newest;

    /// <summary>The side-menu category picked (<see cref="InboxRow.Category"/>), or empty for all.</summary>
    public string Category { get; init; } = string.Empty;

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

    /// <summary>The row property that carries the source's symbol.</summary>
    public const string SourceIconProperty = "sourceIcon";

    /// <summary>The row property that carries the row's CSS class.</summary>
    public const string RowClassProperty = "rowClass";

    /// <summary>The CSS class of an unread row — bold, as a mail client shows new mail.</summary>
    public const string UnreadClass = "inbox-unread";

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
        => string.Join('\n', new[] { filter.Search, filter.Order, filter.Category }
            .Concat(filter.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}")));

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
        => Of(node, section, zone, blockedReason, band, rank, sourceLabel, culture, options: null);

    /// <summary>
    /// <see cref="Of(MeshNode, InboxSection, string?, string?, string?, int, string?, CultureInfo?)"/>
    /// with the hub's serializer options, which a timed row's content conversion needs
    /// (<see cref="TimeOf"/>). A separate overload — never an added optional parameter, which would
    /// replace the released signature that compiled modules bind.
    /// </summary>
    /// <param name="node">The node the leg returned.</param>
    /// <param name="section">The leg that returned it.</param>
    /// <param name="zone">The viewer's time zone.</param>
    /// <param name="blockedReason">Why the row cannot be selected, or null when it can.</param>
    /// <param name="band">The band's title as the viewer reads it.</param>
    /// <param name="rank">The band's rank.</param>
    /// <param name="sourceLabel">The source's display name.</param>
    /// <param name="culture">The viewer's culture.</param>
    /// <param name="options">The hub's serializer options; Web defaults when null.</param>
    public static InboxRow Of(MeshNode node, InboxSection section, string? zone, string? blockedReason,
        string? band, int rank, string? sourceLabel, CultureInfo? culture, JsonSerializerOptions? options)
    {
        // ONE read of the row's time: a timed row's content is converted to find it, and that cost is
        // paid per row on every render.
        var at = TimeOf(node, section.TimeField, options);
        return new()
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
            When = DisplayTimeExtensions.ToDisplayTime(at, zone)
                .ToString("g", culture ?? CultureInfo.InvariantCulture),
            Stamp = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            BlockedReason = blockedReason ?? string.Empty,
            SourceIcon = section.SourceIcon,
            Category = section.Category.Length > 0 ? section.Category : section.Source,
            Unread = section.Unread,
            RowClass = section.Unread ? UnreadClass : string.Empty,
        };
    }

    private static readonly JsonSerializerOptions ContentOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// When <paramref name="node"/> happened as its source times it: the content property
    /// <paramref name="timeField"/> (a stored UTC instant), read in whatever shape the content arrived
    /// — else, or when that field is missing or unreadable, the node's <c>LastModified</c>. Pure.
    /// </summary>
    /// <param name="node">The node a leg returned.</param>
    /// <param name="timeField">The leg's <see cref="InboxQueryLeg.TimeField"/>, or null.</param>
    /// <param name="options">The hub's serializer options — typed content may need a converter only
    /// the mesh registers; Web defaults when omitted.</param>
    public static DateTimeOffset TimeOf(MeshNode node, string? timeField, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(timeField))
            return node.LastModified;
        JsonElement? element;
        try
        {
            element = node.Content switch
            {
                null => null,
                JsonElement je => je,
                System.Text.Json.Nodes.JsonNode jn => JsonSerializer.SerializeToElement(jn, options ?? ContentOptions),
                var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options ?? ContentOptions),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // Unreadable content is the documented fallback, never a fault of the whole inbox.
            return node.LastModified;
        }
        if (element is not { ValueKind: JsonValueKind.Object } content)
            return node.LastModified;
        foreach (var property in content.EnumerateObject())
            if (string.Equals(property.Name, timeField, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return at;
        return node.LastModified;
    }

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
    /// <param name="culture">
    /// Not read. The list is always newest first, so no alphabetical order depends on the viewer's
    /// language any more; the parameter stays only so released callers keep compiling.
    /// </param>
    public static ImmutableList<InboxRow> Apply(IEnumerable<InboxRow> rows, InboxFilter filter, CultureInfo? culture = null)
        => Apply(rows, filter, filter.Category);

    /// <summary>
    /// The rows the viewer asked for, ALWAYS newest first (policy <c>inbox-newest-first</c>; the order
    /// is not a choice, <see cref="InboxFilter.Order"/> is ignored).
    /// With a <paramref name="category"/> picked in the side menu, every row filed under it is shown,
    /// whatever the source switches say (a source off by default is reached by its category); with
    /// none, the switches apply. Date bounds in the search narrow both. Pure.
    /// </summary>
    /// <param name="rows">The merged rows.</param>
    /// <param name="filter">The search and the source switches.</param>
    /// <param name="category">The side-menu category, or null/empty for all.</param>
    /// <remarks>
    /// Internal on purpose (issue #6740): public beside the culture overload, a call passing a literal
    /// <c>null</c> third argument would be ambiguous (CS0121). Public callers name the category on
    /// <see cref="InboxFilter.Category"/> and call the culture overload.
    /// </remarks>
    internal static ImmutableList<InboxRow> Apply(IEnumerable<InboxRow> rows, InboxFilter filter, string? category)
    {
        var (_, before, after) = SplitDates(filter.Search);
        var shown = string.IsNullOrEmpty(category)
            ? rows.Where(row => !filter.Sources.TryGetValue(SourceKey(row.SourceId), out var on) || on)
            : rows.Where(row => string.Equals(row.Category, category, StringComparison.Ordinal));
        return shown.Where(row => InRange(row.Stamp, before, after))
            .OrderByDescending(r => r.Stamp, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToImmutableList();
    }

    /// <summary>
    /// The side menu's counts: how many rows each category holds, and how many of them are unread —
    /// read off the merged rows BEFORE the switches and the category apply, so a category's count is
    /// never filtered away with its rows. Pure.
    /// </summary>
    public static ImmutableDictionary<string, (int Total, int Unread)> CategoryCounts(IEnumerable<InboxRow> rows)
        => rows.GroupBy(r => r.Category, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => (g.Count(), g.Count(r => r.Unread)), StringComparer.Ordinal);

    /// <summary>
    /// The date bounds a search carries — <c>before:2026-10-01</c> (strictly before that day) and
    /// <c>after:2026-10-01</c> (that day or later), dates as <c>yyyy-MM-dd</c> in UTC — and the rest of
    /// the text, which goes to the sources as before. A bound that is not a date stays in the text,
    /// so a typo narrows nothing silently. With a bound, a viewer can list everything before a day,
    /// tick all and put it away in one Delete. Pure.
    /// </summary>
    /// <param name="search">What the viewer typed.</param>
    public static (string Text, DateTimeOffset? Before, DateTimeOffset? After) SplitDates(string? search)
    {
        var tokens = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
        // The LAST bound of each kind wins, as a later word overrides an earlier one.
        var before = tokens.Select(t => Bound(t, "before:")).LastOrDefault(b => b is not null);
        var after = tokens.Select(t => Bound(t, "after:")).LastOrDefault(a => a is not null);
        var rest = tokens.Where(t => Bound(t, "before:") is null && Bound(t, "after:") is null);
        return (string.Join(' ', rest), before, after);

        static DateTimeOffset? Bound(string token, string prefix)
            => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && DateTime.TryParseExact(token[prefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                ? new DateTimeOffset(day, TimeSpan.Zero)
                : null;
    }

    /// <summary>Whether a row's stamp lies in [<paramref name="after"/>, <paramref name="before"/>). Pure.</summary>
    internal static bool InRange(string stamp, DateTimeOffset? before, DateTimeOffset? after)
    {
        if (before is null && after is null)
            return true;
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return false;
        return (before is not { } b || at < b) && (after is not { } a || at >= a);
    }

    /// <summary>
    /// How many rows of the obligation band the source switches hide — counted BEFORE the switches
    /// apply, so the count is never filtered away with the rows.
    /// </summary>
    /// <param name="rows">The merged rows.</param>
    /// <param name="filter">What the viewer asked for.</param>
    /// <param name="needsYouRank">The rank of the "Needs you" band, or null when no leg feeds it.</param>
    public static int HiddenObligations(IEnumerable<InboxRow> rows, InboxFilter filter, int? needsYouRank)
        => needsYouRank is not { } rank
            ? 0
            : rows.Count(row => row.Rank == rank
                                && filter.Sources.TryGetValue(SourceKey(row.SourceId), out var on) && !on);

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
