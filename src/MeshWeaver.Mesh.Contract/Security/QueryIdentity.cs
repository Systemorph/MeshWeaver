using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// What a secured read means when <see cref="MeshQueryRequest.UserId"/> is not set and no ambient
/// identity can be recovered — the ONE thing the old query surface could not express, and the
/// reason a lost viewer was indistinguishable from a deliberate public listing.
///
/// <para>Both of those situations produce the same wire-level read (<c>Anonymous</c>), so before
/// this enum existed they produced the same OUTCOME too: an empty result set that reads as
/// "the record does not exist". Five user-visible bugs in a single day (MeshWeaver.Plugins #360,
/// #406, #415, and two in #417) were that one confusion — a "run from your own copy" redirect that
/// had never once fired, a game-history grid telling its owner there were no recorded games while
/// six sat in storage. Naming the intent is what makes the two cases tell themselves apart.</para>
/// </summary>
public enum QueryIdentityFallback
{
    /// <summary>
    /// Default. Resolve the viewer from the request, else the ambient access context; if neither
    /// yields one, evaluate as <see cref="WellKnownUsers.Anonymous"/> — <b>and say so</b>. The
    /// result is never widened, but the read is no longer silent: the boundary logs a warning
    /// naming the query, because "the caller forgot to stamp a viewer" is far more likely than
    /// "the caller meant Anonymous" and the two used to look identical.
    /// </summary>
    Anonymous = 0,

    /// <summary>
    /// This read IS a mesh-wide public listing — <see cref="WellKnownUsers.Anonymous"/> is the
    /// intended viewer, not a fallback. No ambient lookup, no diagnostic.
    ///
    /// <para>🚨 Use this deliberately, not to silence a warning. A catalog of published courses is
    /// the real case: stamping the visitor there would fold each viewer's own private copies into
    /// the public list as duplicate cards (MeshWeaver.Plugins #415). If the read is supposed to
    /// show the CALLER their own content, this is the wrong answer — stamp the viewer instead.</para>
    /// </summary>
    PublicListing,

    /// <summary>
    /// This read is meaningless without a real viewer — a user's own private space, their drafts,
    /// their installed copies. When no identity can be resolved, fail closed with
    /// <see cref="QueryIdentityUnresolvedException"/> instead of quietly answering with the
    /// Anonymous view, which for these reads always looks like "nothing is there".
    ///
    /// <para>Reach for this whenever an empty result would be reported to a human as absence.
    /// A thrown exception is diagnosable; an empty list is not.</para>
    /// </summary>
    Fail,
}

/// <summary>
/// Where the viewer of a secured read came from. Purely diagnostic — the access decision is the
/// same whatever the source — but it is the distinction that makes a lost identity visible.
/// </summary>
public enum QueryIdentitySource
{
    /// <summary>The request carried an explicit <see cref="MeshQueryRequest.UserId"/>.</summary>
    Request,

    /// <summary>No <c>UserId</c> on the request; recovered from the caller's ambient access context.</summary>
    Ambient,

    /// <summary>
    /// The request declared <see cref="QueryIdentityFallback.PublicListing"/> — Anonymous by intent.
    /// </summary>
    PublicListing,

    /// <summary>
    /// 🚨 Nothing named a viewer and nothing was ambient. The read degrades to
    /// <see cref="WellKnownUsers.Anonymous"/>, which for a user-scoped path means "returns nothing"
    /// — the failure mode that reads as absence. Always worth a warning; with
    /// <see cref="QueryIdentityFallback.Fail"/> it is an error instead.
    /// </summary>
    Unresolved,
}

/// <summary>
/// The resolved viewer of one secured read: who the row-level-security predicate evaluates for,
/// and how that was decided.
/// </summary>
/// <param name="UserId">
/// The effective viewer id — a real user, <see cref="WellKnownUsers.Anonymous"/>, or
/// <see cref="WellKnownUsers.System"/>.
/// </param>
/// <param name="Source">How <paramref name="UserId"/> was arrived at.</param>
public readonly record struct QueryIdentity(string UserId, QueryIdentitySource Source)
{
    /// <summary>True for the infrastructure identity that bypasses row-level security.</summary>
    public bool IsSystem => string.Equals(UserId, WellKnownUsers.System, StringComparison.Ordinal);

    /// <summary>True when the read evaluates as the unauthenticated visitor.</summary>
    public bool IsAnonymous => string.Equals(UserId, WellKnownUsers.Anonymous, StringComparison.Ordinal);

    /// <summary>
    /// 🚨 The viewer fell back to Anonymous because nothing named one — the shape that turns a
    /// user's own content into "no records found". Callers surface this; they never silently
    /// accept it.
    /// </summary>
    public bool IsUnresolved => Source == QueryIdentitySource.Unresolved;

    /// <summary>
    /// The value the storage layer's RLS predicate takes: the empty string for
    /// <see cref="WellKnownUsers.System"/> (meaning "apply no user filter"), the viewer id
    /// otherwise. The native providers encoded this inconsistently before the resolver existed —
    /// two spelled System as <c>""</c>, two as the literal, and one did not special-case it at all.
    /// </summary>
    public string RlsUserId => IsSystem ? "" : UserId;
}

/// <summary>
/// Thrown when a read that declared <see cref="QueryIdentityFallback.Fail"/> could not resolve a
/// viewer. Deliberately loud: the alternative is an empty result the caller will report to a human
/// as "you have nothing here".
/// </summary>
public class QueryIdentityUnresolvedException : InvalidOperationException
{
    /// <summary>The query whose viewer could not be resolved.</summary>
    public string Query { get; }

    /// <summary>Creates the exception for <paramref name="query"/>.</summary>
    /// <param name="query">The query string whose viewer could not be resolved.</param>
    public QueryIdentityUnresolvedException(string query)
        : base(QueryIdentityResolver.DescribeUnresolved(query, forError: true))
        => Query = query;
}

/// <summary>
/// 🚨 THE single place a secured read decides whose eyes it runs behind.
///
/// <para><b>Why it is one place.</b> This rule used to be copy-pasted into five providers
/// (<c>StorageAdapterMeshQueryProvider</c>, <c>PostgreSqlMeshQuery</c>,
/// <c>PostgreSqlPartitionedMeshQuery</c>, <c>SnowflakeMeshQuery</c>,
/// <c>SnowflakePartitionedMeshQuery</c>) and the copies had DRIFTED: an explicit
/// <c>UserId = ""</c> meant "evaluate as Anonymous" in the first and "go look at the ambient
/// context" in the other four, so the same request answered differently depending on which
/// storage backend served it. Both spellings of the System bypass were in circulation too.</para>
///
/// <para><b>Where it runs.</b> <c>MeshService.Query</c> resolves at the CALL BOUNDARY, where the
/// caller's ambient context is still reliably theirs, and stamps the result on the request — but
/// ONLY when it actually resolved one. It must never pin the Anonymous FALLBACK, because the
/// boundary is not the last word: a caller whose ambient context is empty at call time can still
/// have one at SUBSCRIBE time (the plugin installer constructs its queries outside the
/// <c>ImpersonateAsSystem</c> scope it subscribes them in — pinning there installed 0 nodes).
/// The provider therefore resolves again, through <see cref="ResolveAndReport"/>, and that is
/// where the unresolved-viewer diagnostic fires: the point at which the answer is final.</para>
/// </summary>
public static class QueryIdentityResolver
{
    /// <summary>
    /// Resolves the viewer for <paramref name="request"/>. Never throws and never widens: the
    /// worst case is <see cref="WellKnownUsers.Anonymous"/>, reported as
    /// <see cref="QueryIdentitySource.Unresolved"/> so the caller can surface it.
    ///
    /// <para>Order:</para>
    /// <list type="number">
    ///   <item><c>UserId == </c><see cref="WellKnownUsers.System"/> → the infrastructure identity
    ///     (row-level security is bypassed).</item>
    ///   <item><c>UserId</c> non-empty → that viewer. This is also how a deliberate public listing
    ///     spells itself when it stamps <see cref="WellKnownUsers.Anonymous"/> outright.</item>
    ///   <item><c>UserId == ""</c> → explicitly Anonymous. The empty string has always been the
    ///     "I mean the anonymous visitor" marker on the pedestrian provider; the native providers
    ///     used to ignore it and consult the ambient context instead. It now means the same thing
    ///     everywhere.</item>
    ///   <item><see cref="MeshQueryRequest.IdentityFallback"/> is
    ///     <see cref="QueryIdentityFallback.PublicListing"/> → Anonymous by intent, no ambient
    ///     lookup.</item>
    ///   <item><c>UserId</c> unset (null) → <paramref name="ambientUserId"/> when there is one.</item>
    ///   <item>Otherwise → Anonymous, flagged <see cref="QueryIdentitySource.Unresolved"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="request">The read whose viewer is being decided.</param>
    /// <param name="ambientUserId">
    /// The caller's ambient identity — <c>accessService.Context?.ObjectId ??
    /// accessService.CircuitContext?.ObjectId</c>. Passed in rather than resolved here so this
    /// stays a pure function of its inputs, and so the caller reads the ambient context at the
    /// moment it is still correct.
    /// </param>
    /// <returns>The resolved viewer and how it was decided.</returns>
    public static QueryIdentity Resolve(MeshQueryRequest request, string? ambientUserId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = request.UserId;

        if (string.Equals(requested, WellKnownUsers.System, StringComparison.Ordinal))
            return new QueryIdentity(WellKnownUsers.System, QueryIdentitySource.Request);

        if (!string.IsNullOrEmpty(requested))
            return new QueryIdentity(requested, QueryIdentitySource.Request);

        // Empty string (as opposed to null) is the long-standing "evaluate as the anonymous
        // visitor" marker — see the pedestrian provider's original contract. Honour it uniformly
        // so a test or caller that opts into the anonymous view gets it from every backend.
        if (requested is not null)
            return new QueryIdentity(WellKnownUsers.Anonymous, QueryIdentitySource.Request);

        if (request.IdentityFallback == QueryIdentityFallback.PublicListing)
            return new QueryIdentity(WellKnownUsers.Anonymous, QueryIdentitySource.PublicListing);

        if (!string.IsNullOrEmpty(ambientUserId))
            return new QueryIdentity(ambientUserId, QueryIdentitySource.Ambient);

        return new QueryIdentity(WellKnownUsers.Anonymous, QueryIdentitySource.Unresolved);
    }

    /// <summary>
    /// Resolves the viewer AND reports it when nothing named one — the shape every storage provider
    /// uses, so the diagnostic fires exactly once, at the point the answer becomes final.
    ///
    /// <para>🚨 The diagnostic lives HERE rather than at the <c>MeshService</c> boundary because the
    /// boundary is not the last word: a read whose ambient context is empty at CALL time can still
    /// pick one up at SUBSCRIBE time (the plugin installer constructs its queries outside the
    /// <c>ImpersonateAsSystem</c> scope it subscribes them in). Warning at the boundary would cry
    /// wolf for every one of those, and — far worse — <i>pinning</i> Anonymous there would break
    /// them outright. The provider is where the viewer is finally decided, so it is where "nobody
    /// decided" is finally true.</para>
    /// </summary>
    /// <param name="request">The read whose viewer is being decided.</param>
    /// <param name="ambientUserId">The caller's ambient identity, or null.</param>
    /// <param name="logger">Diagnostic sink; may be null.</param>
    /// <returns>The resolved viewer.</returns>
    /// <exception cref="QueryIdentityUnresolvedException">
    /// The read declared <see cref="QueryIdentityFallback.Fail"/> and no viewer could be resolved.
    /// </exception>
    public static QueryIdentity ResolveAndReport(
        MeshQueryRequest request, string? ambientUserId, ILogger? logger)
    {
        var identity = Resolve(request, ambientUserId);
        if (!identity.IsUnresolved)
            return identity;
        identity.EnsureResolved(request);
        // Only a read aimed INTO a named partition is worth warning about — an unscoped read
        // answering as Anonymous returns the mesh's public subset, which is a sensible answer and
        // the shape a genuine mesh-wide catalog has. See TargetsNamedPartition.
        if (TargetsNamedPartition(request))
            logger?.LogWarning("{Diagnostic}", DescribeUnresolved(request.Query, forError: false));
        return identity;
    }

    /// <summary>
    /// Fails closed when <paramref name="identity"/> is unresolved and <paramref name="request"/>
    /// declared <see cref="QueryIdentityFallback.Fail"/>. A no-op otherwise, so it is safe to call
    /// on every read.
    /// </summary>
    /// <param name="identity">The resolved viewer.</param>
    /// <param name="request">The read it was resolved for.</param>
    /// <exception cref="QueryIdentityUnresolvedException">
    /// The read requires a real viewer and none could be resolved.
    /// </exception>
    public static QueryIdentity EnsureResolved(this QueryIdentity identity, MeshQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (identity.IsUnresolved && request.IdentityFallback == QueryIdentityFallback.Fail)
            throw new QueryIdentityUnresolvedException(request.Query);
        return identity;
    }

    /// <summary>
    /// True when the query aims at ONE concrete partition — a <c>path:</c> or <c>namespace:</c>
    /// whose first segment is a real name rather than a wildcard.
    ///
    /// <para>This is the discriminator that keeps the unresolved-viewer warning honest. An
    /// UNSCOPED read that evaluates as Anonymous returns the mesh's public subset, which is a
    /// sensible answer and the shape a genuine catalog has. A read aimed INTO a named partition
    /// that evaluates as Anonymous returns nothing — and "nothing" is what the caller reports to a
    /// human as "you have no records". Every one of the five known instances was the scoped
    /// shape.</para>
    /// </summary>
    /// <param name="request">The read to classify.</param>
    /// <returns>True when the read targets a named partition.</returns>
    public static bool TargetsNamedPartition(MeshQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var query in request.EffectiveQueries)
        {
            if (string.IsNullOrWhiteSpace(query))
                continue;
            // Unguarded, exactly like every other Parse call site (MeshQuery.NamespacesForRequest
            // parses the same string a moment later on the normal path). Swallowing here would only
            // move an authoring error's stack trace, not prevent it.
            var parsed = new QueryParser().Parse(query);
            if (IsNamedSegment(FirstSegment(parsed.Path)))
                return true;
            foreach (var ns in parsed.ExtractNamespaces())
                if (IsNamedSegment(FirstSegment(ns)))
                    return true;
        }
        return !string.IsNullOrEmpty(FirstSegment(request.DefaultPath))
               && IsNamedSegment(FirstSegment(request.DefaultPath));
    }

    private static string? FirstSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    private static bool IsNamedSegment(string? segment)
        => !string.IsNullOrEmpty(segment) && !segment.Contains('*') && !segment.Contains('?');

    /// <summary>
    /// The one wording for "this read has no viewer", shared by the warning and the exception so a
    /// log line and a stack trace say the same thing and point at the same two remedies.
    /// </summary>
    /// <param name="query">The query whose viewer could not be resolved.</param>
    /// <param name="forError">True for the fail-closed phrasing, false for the fell-back phrasing.</param>
    /// <returns>The diagnostic sentence.</returns>
    public static string DescribeUnresolved(string? query, bool forError)
    {
        var q = string.IsNullOrWhiteSpace(query) ? "(empty query)" : query;
        var head = forError
            ? $"Query '{q}' requires a viewer and none could be resolved"
            : $"Query '{q}' ran with NO resolvable viewer and fell back to '{WellKnownUsers.Anonymous}'";
        return head
            + " — neither the request's UserId nor the caller's ambient AccessContext named one. "
            + "Results are the ANONYMOUS view, so anything the caller owns privately reads as ABSENT "
            + "rather than as denied. Fix it at the call site: stamp the viewer "
            + "(MeshQueryRequest.FromQuery(q, userId) / .ForViewer(userId)) when the read is about a "
            + "specific user; declare .AsPublicListing() when Anonymous genuinely IS the intended "
            + "viewer (a mesh-wide catalog); declare .RequireViewer() to fail closed instead of "
            + "answering as Anonymous. See Doc/Architecture/QueryIdentity.";
    }
}
