using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// What an OPEN secured query re-decides its rows on (MeshWeaver#7365). A live listing is admitted
/// row by row when it runs, and before this its rows were decided again only when a write landed
/// UNDER the query's own scope. A grant or a revocation lands elsewhere: on an ancestor's
/// <c>_Access</c>, in a group membership, in a role. So a reader whose access was revoked kept the
/// rows they held, and kept receiving any row a write under the scope happened to re-admit before the
/// permission fold had taken the revocation in. A newly granted reader saw nothing until they queried
/// again.
///
/// <para>A query provider merges these triggers into its live re-query, past its own path filters,
/// exactly like a change-feed gap (<see cref="ChangeFeedGap.ToRequeryTrigger"/>). A trigger is ONLY a
/// trigger: the re-query is the sole source of rows, and it re-runs the provider's own permission
/// filter on every row. The in-memory executor runs the read-validator chain; the Postgres executor
/// runs its SQL permission projection. The provider then diffs the result against what it holds. A
/// revoked row leaves as <see cref="QueryChangeType.Removed"/>, a newly readable one arrives as
/// <see cref="QueryChangeType.Added"/>, and a trigger that changed nothing for this reader emits
/// nothing.</para>
///
/// <para>The signals are the same ones an open node-stream cache read follows its standing Read on
/// (MeshWeaver#7504, <c>MeshNodeStreamCache.FollowStandingRead</c>). They are signals, never a timer:</para>
/// <list type="bullet">
///   <item><description><b>A committed snapshot of a permission-fold input</b> that can reach the
///     query: a global one, or one anchored to the partition of an anchor, to the partition of a held
///     row's <see cref="MeshNode.MainNode"/>, or to <c>Admin</c>. These are announced after the
///     two-phase commit ENDS, so the re-query reads the new snapshot.</description></item>
///   <item><description><b>An access commit on the invalidation feed</b> that can move a row's Read
///     (<see cref="DeclaredPointRead.AffectsRead(MeshChangeEvent, Func{string, bool})"/>). These cover a
///     grant, policy or sync config on any scope above or below an anchor, any group membership, and
///     any role. This is how a backend that projects permissions itself (Postgres) learns of a grant
///     the moment it commits, and how a fold that is not open yet is covered.</description></item>
///   <item><description><b>Every declared gap in that feed</b>, because the commit a gap swallowed may
///     have been a grant.</description></item>
/// </list>
///
/// <para>🚨 <b>Bounded and fail-closed is the PROVIDER's half.</b> The re-check is the provider's own
/// row filter, so it must refuse a row whose verdict cannot be established within the
/// permission-establishment budget. A row is withheld, never shown, on a fault, a timeout or a
/// validator that answers nothing. A fault in these signals is the query's fault: the provider ends
/// the query, because a listing that can no longer follow access must not go on serving rows.</para>
/// </summary>
public static class SecuredQueryAccessFollow
{
    /// <summary>The entity a re-check trigger carries, so a log can tell it from a data change. Never read as a row.</summary>
    /// <param name="Reason">Which signal fired.</param>
    public sealed record AccessRecheck(string Reason);

    /// <summary>
    /// The re-check triggers for an open secured query that <paramref name="viewer"/> holds over
    /// <paramref name="anchors"/>. They never emit for System, for an absent viewer, or where the mesh
    /// keeps no permission fold and no invalidation feed. Those reads follow no grant.
    /// </summary>
    /// <param name="services">The mesh's service provider. When it is null, nothing is followed.</param>
    /// <param name="viewer">The identity the query's rows are filtered for.</param>
    /// <param name="anchors">The paths the query reads under. A base path or a namespace counts; a
    /// <c>*</c> segment cuts the path at that segment, and <c>""</c> is the whole mesh.</param>
    /// <param name="heldAnchors">Read at each signal: further paths the held rows' Read is decided on,
    /// such as their <see cref="MeshNode.MainNode"/>. May be null.</param>
    /// <returns>A trigger per relevant signal, shaped for the provider's live re-query pipeline.</returns>
    public static IObservable<DataChangeNotification> RecheckTriggers(
        IServiceProvider? services,
        string? viewer,
        IReadOnlyCollection<string> anchors,
        Func<IEnumerable<string>>? heldAnchors = null)
    {
        if (services is null
            || string.IsNullOrEmpty(viewer)
            || string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal))
            return Observable.Never<DataChangeNotification>();

        return Observable.Defer(() =>
        {
            var fixedAnchors = anchors.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            IEnumerable<string> CurrentAnchors()
                => heldAnchors is null ? fixedAnchors : fixedAnchors.Concat(heldAnchors().Select(Normalize));

            // Resolved at SUBSCRIBE time: the providers are root singletons built while the mesh is
            // still being wired, and the cache's constructor takes the mesh hub.
            var foldCommits = services.GetService<IMeshNodeStreamCache>() is MeshNodeStreamCache cache
                ? cache.FoldInputCommits
                    .Where(partition => partition is null || FoldReaches(partition, CurrentAnchors()))
                    .Select(partition => Trigger($"permission-fold input committed ({partition ?? "global"})"))
                : Observable.Never<DataChangeNotification>();
            var feed = services.GetService<IMeshInvalidationFeed>();
            var accessCommits = feed is null
                ? Observable.Never<DataChangeNotification>()
                // The filter runs as an Rx operator, so a fault in it reaches the query as an error
                // instead of being thrown into the feed's delivery callback.
                : Observable.Create<MeshChangeEvent>(observer => feed.Subscribe(observer.OnNext))
                    .Where(change => DeclaredPointRead.AffectsRead(change, scope => ScopeReaches(scope, CurrentAnchors())))
                    .Select(change => Trigger($"access commit {change.Kind} {change.Path}"));
            var gaps = feed is null
                ? Observable.Never<DataChangeNotification>()
                : feed.Gaps.Select(gap => Trigger($"invalidation-feed gap ({gap.Source})"));
            return Observable.Merge(foldCommits, accessCommits, gaps);
        });
    }

    /// <summary>
    /// Whether an access input placed on <paramref name="scope"/> can move a Read decision about a row
    /// under one of <paramref name="anchors"/>. The root and <c>Admin</c> reach everything. An ancestor
    /// or self of an anchor reaches every row under it. A scope below an anchor reaches the rows at and
    /// under that scope.
    /// </summary>
    internal static bool ScopeReaches(string scope, IEnumerable<string> anchors)
    {
        var s = Normalize(scope);
        if (s.Length == 0 || string.Equals(s, PermissionEvaluator.AdminScope, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var anchor in anchors)
        {
            if (anchor.Length == 0
                || string.Equals(anchor, s, StringComparison.OrdinalIgnoreCase)
                || anchor.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(anchor + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Whether a fold input anchored to <paramref name="partition"/> can move a row under one of <paramref name="anchors"/>.</summary>
    internal static bool FoldReaches(string partition, IEnumerable<string> anchors)
    {
        if (string.Equals(partition, PermissionEvaluator.AdminScope, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var anchor in anchors)
        {
            if (anchor.Length == 0 || string.Equals(PartitionOf(anchor), partition, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static DataChangeNotification Trigger(string reason)
        => new(string.Empty, DataChangeKind.Updated, new AccessRecheck(reason), DateTimeOffset.UtcNow);

    private static string PartitionOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    // A namespace pattern's wildcard segment and everything after it widen the anchor to the path
    // before it: `acme/*/Doc` reads under `acme`.
    private static string Normalize(string? path)
    {
        var segments = (path ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = segments.TakeWhile(segment => !segment.Contains('*'));
        return string.Join('/', kept);
    }
}
