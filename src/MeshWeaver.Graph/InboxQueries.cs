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
    public static string Bounded(string query) => Bounded(query, timeField: null);

    /// <summary>
    /// <see cref="Bounded(string)"/> for a leg timed by a content property
    /// (<see cref="InboxQueryLeg.TimeField"/>): its projection keeps <c>content</c>, where the row
    /// reads its time (<see cref="TimedRowProjection"/>).
    /// </summary>
    /// <param name="query">The leg's resolved query.</param>
    /// <param name="timeField">The leg's time field, or null for <see cref="RowProjection"/>.</param>
    public static string Bounded(string query, string? timeField)
    {
        var parsed = new QueryParser().Parse(query);
        var limit = EffectiveLimit(query);
        var (projection, fields) = string.IsNullOrWhiteSpace(timeField)
            ? (RowProjection, RowFields)
            : (TimedRowProjection, TimedRowFields);
        var projected = parsed.Select is { } select && select.Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        return query
               + (parsed.Limit == limit ? string.Empty : $" limit:{limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
               + (projected ? string.Empty : " " + projection);
    }

    /// <summary>
    /// The projection of a leg timed by a content property: <see cref="RowProjection"/> plus
    /// <c>content</c>, where the row reads its time.
    /// </summary>
    public const string TimedRowProjection = RowProjection + ",content";

    /// <summary>The fields a timed row reads — <see cref="TimedRowProjection"/> as a list.</summary>
    internal static ImmutableArray<string> TimedRowFields { get; } = [.. TimedRowProjection["select:".Length..].Split(',')];

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
        var terms = resolvedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The FIRST term is the anchor, on the viewer's own partition — compared to the viewer's id
        // itself, never to a character class: partition keys may be any Unicode letters and digits
        // (PartitionDefinition.IsValidPartitionSegment), and "müller" must get an inbox.
        if (terms.Length == 0 || Anchor(terms[0]) is not { } anchor || !InPartition(anchor, viewer)
            // ONE structural anchor: the parser lets a later namespace:/path: replace the first,
            // so a leg that names two could pass on its first and run on its second.
            || terms.Count(IsStructural) != 1)
            return false;
        // And what the PARSER will run — not only what the text says first.
        var parsed = new QueryParser().Parse(resolvedQuery);
        return !parsed.CrossPartition
               && parsed.Path is { } path && InPartition(path, viewer)
               && (parsed.Paths is null || parsed.Paths.All(p => InPartition(p, viewer)));
    }

    private static string? Anchor(string term)
        => term.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase) ? term["namespace:".Length..]
            : term.StartsWith("path:", StringComparison.OrdinalIgnoreCase) ? term["path:".Length..]
            : null;

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
        // Notifications — OFF by default: the feed is long and mostly informational, so it stays
        // one switch away (the bell still shows it). Two legs: unread (bold) and read. `IsRead` and
        // `Dismissed` default to false and are OMITTED from stored JSON, so unread negates the
        // stamped `true` and read matches it — a positive match on `false` would select nothing.
        Notifications(unread: true),
        Notifications(unread: false),
        // Approval requests — their own source, shown by default (the notification feed is not).
        Approvals(unread: true),
        Approvals(unread: false),
        // The viewer's own stored mail, inbound and not archived. Both defaults (Inbound, New) are
        // omitted from stored JSON, so the legs negate the stamped values they exclude: unread is
        // "not Read", read is "Read".
        Mail(unread: true),
        Mail(unread: false),
        // Policy inbox-activities-need-someone: only the runs MARKED for the inbox
        // (ActivityLog.ShowInInbox — background runs never are). Still going: Running. FAILED: it
        // needs you, and is dealt with on its own page. A run that finished well is never listed.
        new()
        {
            Source = "Activities",
            SourceLabelKey = "inbox.source.Activities",
            SourceIcon = ActivitiesIcon,
            Band = InboxBand.Running,
            Order = 10,
            // Running itself is the omitted default, so every stamped terminal state is negated.
            Query = $"namespace:{ViewerToken}/_Activity nodeType:Activity content.showInInbox:true "
                    + string.Join(" ", TerminalActivityStatuses.Select(s => $"-content.status:{s}"))
                    + $" sort:LastModified-desc {RowProjection}",
        },
        new()
        {
            Source = "Activities",
            SourceLabelKey = "inbox.source.Activities",
            SourceIcon = ActivitiesIcon,
            Band = InboxBand.NeedsYou,
            Order = 50,
            Query = $"namespace:{ViewerToken}/_Activity nodeType:Activity content.showInInbox:true "
                    + $"content.status:{nameof(ActivityStatus.Failed)}"
                    + $" sort:LastModified-desc {RowProjection}",
        },
    ];

    private const string NotificationsIcon = "🔔";
    private const string MailIcon = "✉️";
    private const string ActivitiesIcon = "⚙️";

    private static InboxQueryLeg Notifications(bool unread) => new()
    {
        Source = "Notifications",
        SourceLabelKey = "inbox.source.Notifications",
        SourceIcon = NotificationsIcon,
        ShownByDefault = false,
        Unread = unread,
        Band = InboxBand.NeedsYou,
        Order = unread ? 10 : 11,
        // Timed by when it ARRIVED: reading a notification flips isRead, and that write must not
        // move it ahead of newer ones.
        TimeField = "createdAt",
        // Every notification that is NOT an approval request: those are the Approvals source.
        Query = $"namespace:{ViewerToken}/{NotificationService.SatelliteSegment} "
                + $"nodeType:{NotificationNodeType.NodeType} -content.dismissed:true "
                + $"content.notificationType:{string.Join("|", StampedNotificationTypes)} "
                + (unread ? "-content.isRead:true " : "content.isRead:true ")
                + $"sort:CreatedAt-desc {TimedRowProjection}",
    };

    /// <summary>
    /// The approval requests addressed to the viewer — the "approval required" notifications every
    /// approval flow sends its approver (document approvals, instance actions, …), not put away.
    /// Shown by default: they need the viewer. 🚨 <see cref="NotificationType.ApprovalRequired"/> is
    /// the enum DEFAULT and is omitted from stored JSON, so the leg NEGATES every stamped type
    /// (<see cref="StampedNotificationTypes"/>) rather than matching it.
    /// </summary>
    private static InboxQueryLeg Approvals(bool unread) => new()
    {
        Source = ApprovalsSource,
        SourceLabelKey = "inbox.source.Approvals",
        SourceIcon = ApprovalsIcon,
        Unread = unread,
        Band = InboxBand.NeedsYou,
        Order = unread ? 5 : 6,
        // Timed by when the request ARRIVED, like every notification: reading it must not reorder.
        TimeField = "createdAt",
        Query = $"namespace:{ViewerToken}/{NotificationService.SatelliteSegment} "
                + $"nodeType:{NotificationNodeType.NodeType} -content.dismissed:true "
                + string.Concat(StampedNotificationTypes.Select(t => $"-content.notificationType:{t} "))
                + (unread ? "-content.isRead:true " : "content.isRead:true ")
                + $"sort:CreatedAt-desc {TimedRowProjection}",
    };

    /// <summary>The source id of the approval requests.</summary>
    public const string ApprovalsSource = "Approvals";

    private const string ApprovalsIcon = "✅";

    /// <summary>
    /// Every <see cref="NotificationType"/> other than the default
    /// <see cref="NotificationType.ApprovalRequired"/> — the values that ARE stamped in stored JSON.
    /// 🚨 A type appended to the enum must be added here, or it would be listed as an approval;
    /// <c>InboxSelectAndDeleteTest.EveryStampedNotificationType_IsClassified</c> fails until it is.
    /// </summary>
    // A computed property, not an initialized one: BuiltIn (declared above) reads it while the type
    // initializes, before a later static initializer would have run.
    internal static ImmutableArray<string> StampedNotificationTypes =>
    [
        nameof(NotificationType.ApprovalGiven),
        nameof(NotificationType.ApprovalRejected),
        nameof(NotificationType.General),
        nameof(NotificationType.AccessGranted),
        nameof(NotificationType.ChatReady),
        nameof(NotificationType.System),
    ];

    private static InboxQueryLeg Mail(bool unread) => new()
    {
        Source = "Mail",
        SourceLabelKey = "inbox.source.Mail",
        SourceIcon = MailIcon,
        Unread = unread,
        Band = InboxBand.NeedsYou,
        Order = unread ? 20 : 21,
        // Timed by when it was RECEIVED: marking a mail read is a write, and it must not move the
        // mail ahead of newer ones. The bound still keeps the newest-touched (LastModified).
        TimeField = "receivedAt",
        Query = $"namespace:{ViewerToken}/{EmailNodeType.UserEmailSegment} "
                + $"nodeType:{EmailNodeType.NodeType} -content.status:{nameof(EmailStatus.Archived)} "
                + $"-content.direction:{nameof(EmailDirection.Outbound)} "
                + (unread ? $"-content.status:{nameof(EmailStatus.Read)} " : $"content.status:{nameof(EmailStatus.Read)} ")
                + $"sort:LastModified-desc {TimedRowProjection}",
    };

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
