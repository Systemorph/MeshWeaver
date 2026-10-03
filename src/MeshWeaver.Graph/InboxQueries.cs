using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Resolves the inbox's legs for one viewer — the read side of "everyone brings their own food".
///
/// <para>This class holds the BUILT-IN legs and the template substitution. It deliberately does NOT
/// hold a list of providers: a provider contributes its leg as data, and
/// <see cref="Resolve(System.Collections.Generic.IEnumerable{InboxQueryLeg}, string)"/> takes
/// whatever legs it is given. Teaching this file about each provider is the design's failure mode.</para>
/// </summary>
public static class InboxQueries
{
    /// <summary>The token a stored leg carries where the viewer's partition belongs.</summary>
    public const string ViewerToken = "{viewer}";

    /// <summary>
    /// The projection every inbox leg must carry.
    ///
    /// <para>🚨 <b>A leg without <c>select:</c> returns whole nodes, CONTENT INCLUDED.</b> The inbox
    /// renders one line per item — a path, a name, an icon, a timestamp — so fetching every
    /// document body, every markdown page and every activity log to display a row is pure waste,
    /// paid on every render, for every viewer, on a surface that is open all day. Name the fields.</para>
    /// </summary>
    public const string RowProjection = "select:path,name,nodeType,icon,lastModified";

    /// <summary>
    /// The terminal <c>ActivityStatus</c> values, as query terms. 🚨 <c>Running</c> is the enum
    /// DEFAULT, and the serializer omits defaults from stored JSON — so a positive
    /// <c>content.status:Running</c> matches nothing that is actually running, and a negated
    /// <c>-content.status:Running</c> never excludes it (the trap <c>OutboundEmailWatchQueryTest</c>
    /// pins). The two bands are therefore split on the explicitly STAMPED terminal states: Running
    /// negates each of them, Recent matches any of them.
    /// </summary>
    internal static readonly ImmutableArray<string> TerminalActivityStatuses =
        [nameof(ActivityStatus.Succeeded), nameof(ActivityStatus.Warning), nameof(ActivityStatus.Failed), nameof(ActivityStatus.Cancelled)];

    /// <summary>
    /// The legs core itself brings. Packages add their own; these are not a registry of what may exist.
    ///
    /// <para>Every one is anchored on <see cref="ViewerToken"/>, so each resolves to exactly one
    /// partition.</para>
    /// </summary>
    public static ImmutableArray<InboxQueryLeg> BuiltIn { get; } =
    [
        new()
        {
            Source = "Notifications",
            Band = InboxBand.NeedsYou,
            Order = 10,
            Query = $"namespace:{ViewerToken}/{NotificationService.SatelliteSegment} "
                    + $"nodeType:{NotificationNodeType.NodeType} sort:CreatedAt-desc {RowProjection}",
        },
        new()
        {
            Source = "Activities",
            Band = InboxBand.Running,
            Order = 10,
            // Not terminal — every stamped terminal state negated (Running itself is omitted).
            Query = $"namespace:{ViewerToken}/_Activity nodeType:Activity "
                    + string.Join(" ", TerminalActivityStatuses.Select(s => $"-content.status:{s}"))
                    + $" sort:LastModified-desc {RowProjection}",
        },
        new()
        {
            Source = "Activities",
            Band = InboxBand.Recent,
            Order = 20,
            // Terminal — any stamped terminal state.
            Query = $"namespace:{ViewerToken}/_Activity nodeType:Activity "
                    + $"content.status:{string.Join("|", TerminalActivityStatuses)}"
                    + $" sort:LastModified-desc {RowProjection}",
        },
    ];

    /// <summary>
    /// Substitutes the viewer into every enabled leg, ordered by band then <see cref="InboxQueryLeg.Order"/>.
    /// </summary>
    /// <param name="legs">The contributed legs — built-in plus whatever providers brought.</param>
    /// <param name="viewer">The viewer's partition.</param>
    /// <returns>The resolved legs, ready to run.</returns>
    /// <exception cref="ArgumentException">
    /// When <paramref name="viewer"/> is blank. 🚨 A blank viewer would resolve
    /// <c>namespace:{viewer}/_Notification</c> to <c>namespace:/_Notification</c>, which names no
    /// partition and is therefore the cross-schema fan-out this whole shape exists to prevent —
    /// so it fails loudly here rather than producing a query that "works".
    /// </exception>
    public static ImmutableArray<InboxQueryLeg> Resolve(IEnumerable<InboxQueryLeg> legs, string viewer)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (string.IsNullOrWhiteSpace(viewer))
            throw new ArgumentException(
                "An inbox leg must be anchored to a viewer's partition; a blank viewer would fan out "
                + "across every partition schema.", nameof(viewer));

        return
        [
            .. legs.Where(leg => leg.Enabled && !string.IsNullOrWhiteSpace(leg.Query))
                .Select(leg => leg with { Query = leg.Query.Replace(ViewerToken, viewer, StringComparison.Ordinal) })
                .OrderBy(leg => leg.Band, StringComparer.Ordinal)
                .ThenBy(leg => leg.Order)
                .ThenBy(leg => leg.Source, StringComparer.Ordinal)
        ];
    }
}
