using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

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
    /// How many rows the inbox asks ONE leg for. Every leg sorts newest first, so this is the newest
    /// <see cref="LegLimit"/> items of each source — what a person works through; anything older is
    /// found with the search box, which goes to the query engine.
    ///
    /// <para>🚨 Without a bound the page folds every historical row of every source into memory and
    /// into the client's payload, on a surface that is open all day. The bound is the page's, not
    /// the provider's: a leg that names its own <c>limit:</c> keeps it.</para>
    /// </summary>
    public const int LegLimit = 100;

    /// <summary>
    /// How many rows <paramref name="query"/> is read for: the provider's own limit when it names a
    /// POSITIVE one no larger than <see cref="LegLimit"/>, else <see cref="LegLimit"/>. Read through
    /// the query parser, never by spotting a <c>limit:</c> token — <c>limit:all</c> and a
    /// non-positive limit are UNBOUNDED to the parser, and a larger one would lift the page's cap.
    /// </summary>
    /// <param name="query">A resolved leg query.</param>
    public static int EffectiveLimit(string query)
        => new QueryParser().Parse(query).Limit is > 0 and var own && own <= LegLimit ? own : LegLimit;

    /// <summary>
    /// <paramref name="query"/> as the page runs it: bounded to <see cref="EffectiveLimit"/> rows and
    /// projected onto <see cref="RowProjection"/>. Whatever a provider wrote, the page reads only
    /// what a row shows — a contributed leg with no <c>select:</c>, or a wider one, would otherwise
    /// fetch whole thread and message bodies to render one line each. A term already right is left as
    /// it is; otherwise the page's is appended, and the parser takes the LAST one it reads.
    /// </summary>
    /// <param name="query">A resolved leg query.</param>
    public static string Bounded(string query)
    {
        var parsed = new QueryParser().Parse(query);
        var limit = EffectiveLimit(query);
        var projected = parsed.Select is { } select && select.Order(StringComparer.Ordinal)
            .SequenceEqual(RowFields.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        return query
               + (parsed.Limit == limit ? string.Empty : $" limit:{limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
               + (projected ? string.Empty : " " + RowProjection);
    }

    /// <summary>The fields a row reads — <see cref="RowProjection"/> as a list.</summary>
    internal static ImmutableArray<string> RowFields { get; } = [.. RowProjection["select:".Length..].Split(',')];

    /// <summary>
    /// Whether a RESOLVED leg reads exactly one partition, and that partition is the
    /// <paramref name="viewer"/>'s: its first term is a <c>namespace:</c> or <c>path:</c> whose first
    /// segment is the viewer, literally — no wildcard, no alternation — it is the query's ONLY
    /// <c>namespace:</c>/<c>path:</c> term, and the path the PARSER resolves (and any path list) lies
    /// in the viewer's partition with no <c>partitions:all</c>. A leg that fails is never run (<see cref="Resolve"/> drops it): an
    /// unanchored query is a fan-out over every partition schema, on every render of every inbox.
    /// </summary>
    /// <param name="resolvedQuery">The leg's query with the viewer substituted.</param>
    /// <param name="viewer">The viewer's partition.</param>
    public static bool IsAnchored(string resolvedQuery, string viewer)
    {
        if (AnchorPattern.Match(resolvedQuery) is not { Success: true } anchor
            || !string.Equals(anchor.Groups[1].Value, viewer, StringComparison.OrdinalIgnoreCase)
            // ONE structural anchor: the parser lets a later namespace:/path: replace the first,
            // so a leg that names two could pass on its first and run on its second.
            || resolvedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(IsStructural) != 1)
            return false;
        // And what the PARSER will run — not only what the text says first.
        var parsed = new QueryParser().Parse(resolvedQuery);
        return !parsed.CrossPartition
               && parsed.Path is { } path && InPartition(path, viewer)
               && (parsed.Paths is null || parsed.Paths.All(p => InPartition(p, viewer)));
    }

    private static bool IsStructural(string term)
    {
        var bare = term.TrimStart('-', '!');
        return bare.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase)
               || bare.StartsWith("path:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool InPartition(string path, string viewer)
    {
        var trimmed = path.TrimStart('/');
        return trimmed.Equals(viewer, StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith(viewer + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex AnchorPattern =
        new(@"^(?:namespace|path):([A-Za-z0-9_.\-]+)(?=/|\s|$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The enabled legs <see cref="Resolve"/> refuses to run because they are not anchored on
    /// <paramref name="viewer"/>'s partition — for the page to LOG, so a provider's mistake is loud
    /// rather than a silently missing source.
    /// </summary>
    /// <param name="legs">The contributed legs.</param>
    /// <param name="viewer">The viewer's partition.</param>
    public static ImmutableArray<InboxQueryLeg> Refused(IEnumerable<InboxQueryLeg> legs, string viewer)
        => [.. Substituted(legs, viewer).Where(leg => !IsAnchored(leg.Query, viewer))];

    private static IEnumerable<InboxQueryLeg> Substituted(IEnumerable<InboxQueryLeg> legs, string viewer)
        => legs.Where(leg => leg.Enabled && !string.IsNullOrWhiteSpace(leg.Query))
            .Select(leg => leg with { Query = leg.Query.Replace(ViewerToken, viewer, StringComparison.Ordinal) });

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
            SourceLabelKey = "inbox.source.Notifications",
            Band = InboxBand.NeedsYou,
            Order = 10,
            // Not put away. `Dismissed` defaults to false and is therefore OMITTED from stored
            // JSON, so the leg negates the stamped `true` — a positive match on `false` would
            // select nothing (the same trap as the activity legs below).
            Query = $"namespace:{ViewerToken}/{NotificationService.SatelliteSegment} "
                    + $"nodeType:{NotificationNodeType.NodeType} -content.dismissed:true "
                    + $"sort:CreatedAt-desc {RowProjection}",
        },
        new()
        {
            Source = "Mail",
            SourceLabelKey = "inbox.source.Mail",
            Band = InboxBand.NeedsYou,
            Order = 20,
            // The viewer's own stored mail, inbound and not archived. Both defaults (Inbound, New)
            // are omitted from stored JSON, so the leg negates the two stamped values it excludes.
            Query = $"namespace:{ViewerToken}/{EmailNodeType.UserEmailSegment} "
                    + $"nodeType:{EmailNodeType.NodeType} -content.status:{nameof(EmailStatus.Archived)} "
                    + $"-content.direction:{nameof(EmailDirection.Outbound)} "
                    + $"sort:LastModified-desc {RowProjection}",
        },
        new()
        {
            Source = "Activities",
            SourceLabelKey = "inbox.source.Activities",
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
            SourceLabelKey = "inbox.source.Activities",
            Band = InboxBand.Recent,
            Order = 20,
            // Terminal — any stamped terminal state.
            Query = $"namespace:{ViewerToken}/_Activity nodeType:Activity "
                    + $"content.status:{string.Join("|", TerminalActivityStatuses)}"
                    + $" sort:LastModified-desc {RowProjection}",
        },
    ];

    /// <summary>
    /// Every leg on this mesh: core's own plus each one a package registered as a service
    /// (<c>services.AddSingleton(new InboxQueryLeg { … })</c>). The inbox runs what it is handed —
    /// it carries no list of the packages it knows.
    /// </summary>
    /// <param name="services">The hub's service provider.</param>
    public static ImmutableArray<InboxQueryLeg> All(IServiceProvider services)
        => [.. BuiltIn, .. services.GetServices<InboxQueryLeg>()];

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
            // 🚨 A leg that does not read exactly the viewer's own partition is never run.
            .. Substituted(legs, viewer)
                .Where(leg => IsAnchored(leg.Query, viewer))
                .OrderBy(leg => leg.Band, StringComparer.Ordinal)
                .ThenBy(leg => leg.Order)
                .ThenBy(leg => leg.Source, StringComparer.Ordinal)
        ];
    }
}
