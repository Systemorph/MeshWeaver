using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// Thrown when a <b>satellite</b> MeshNode (Activity, Thread, Comment, Notification, …) is created
/// or posted at a <b>top-level / ownerless</b> path — a bare <c>_Activity/{id}</c> / <c>_Thread/{id}</c>
/// (empty owner segment before the satellite folder), or — for an Activity specifically — an empty
/// <c>MainNode</c>.
///
/// <para><b>Why this is fatal.</b> A satellite lives at <c>{ownerPath}/_Segment/{id}</c> and is
/// served / addressed through its owning node's partition (the first path segment). When the owner
/// is empty there is no partition / per-node hub to route to, so every poster
/// (<c>SubmitCodeRequest</c>, a thread submission, an activity status flip) and every subscriber
/// (the GUI progress panels, the activity stream readers, the chat view) gets a
/// <c>[ROUTE] NotFound</c> from the RoutingGrain. A re-subscriber then hammers that path hundreds of
/// times → CPU pegs and the hub wedges (the atioz <c>_Activity/import-*</c> / <c>_Activity/compile-*</c>
/// resubscribe storm; the voice-bridge bare-<c>_Thread</c> anchor). The defect is the <i>creation</i>,
/// so we fail fast AT THE SOURCE — loudly, with a named exception — instead of letting a phantom
/// escape and surface downstream as an opaque routing storm.</para>
///
/// <para><b>Controlled startup (design note).</b> Platform-startup activities (seed import,
/// first-build compile, indexing) must hang off a <i>real</i> owning node, never the root mesh
/// hub. The canonical anchor is a node in the <b>Admin partition</b> — e.g. a node that records
/// the installed platform version — so a boot-time activity lives at
/// <c>Admin/{versionNode}/_Activity/{id}</c> (served by the Admin partition's hub), not at a bare
/// <c>_Activity/{id}</c>. This guard is the backstop: any startup path that tries to anchor a
/// satellite top-level throws here at create time rather than storming the router after boot.</para>
///
/// <para>The historical type name (<c>OwnerlessActivityException</c>) is retained from Phase 1 when
/// the guard covered <c>_Activity</c> only; the predicate now covers ALL owner-requiring satellites.
/// The exception message always names the offending segment so the failure is unambiguous.</para>
/// </summary>
public sealed class OwnerlessActivityException : InvalidOperationException
{
    /// <summary>Creates the exception with a message naming the offending top-level segment.</summary>
    /// <param name="message">Human-readable description of which satellite segment was anchored ownerless.</param>
    public OwnerlessActivityException(string message) : base(message) { }
}

/// <summary>
/// Structural invariant for <b>satellite</b> MeshNodes: a satellite instance MUST be anchored at
/// <c>{ownerPath}/_Segment/{id}</c> under a real owning node, never at a top-level / ownerless
/// path. Pure + synchronous so it is unit-testable and can fail fast at every satellite
/// create / post boundary (the <c>CreateNodeRequest</c> handler, <see cref="MeshNode"/>-building
/// helpers, the markdown kernel views).
///
/// <para>The set of owner-requiring satellite segments is derived from
/// <see cref="SatelliteTableMapping.Defaults"/> — the same single source the storage router uses —
/// EXCLUDING <c>_Access</c>: a root-scope <c>AccessAssignment</c> legitimately lives at the top
/// level (<c>_Access/{id}</c>, scope <c>""</c>, <c>MainNode=""</c>) and a partition-root grant at
/// <c>{partition}/_Access/{id}</c> legitimately carries <c>MainNode=""</c>
/// (see <c>GlobalAdminSeed</c> / <c>TestUsers.PublicAdminAccess</c> / <c>GrantPlatformAdmin</c>), so
/// <c>_Access</c> is never owner-required.</para>
/// </summary>
public static class ActivityNodeGuard
{
    /// <summary>The framework path segment that holds activity satellites.</summary>
    public const string ActivitySegment = "_Activity";

    /// <summary>
    /// The <c>_Access</c> satellite segment — deliberately EXEMPT from the ownerless check: a
    /// root-scope access grant lives at <c>_Access/{id}</c> (the global fallback scope) and a
    /// partition-root grant legitimately uses <c>MainNode=""</c>. Rejecting it here would lock the
    /// platform out of its admins (the first-user <c>GrantPlatformAdmin</c> write).
    /// </summary>
    private const string AccessSegment = "_Access";

    /// <summary>
    /// Underscore-prefixed satellite segments that REQUIRE a real owning node before them, derived
    /// from <see cref="SatelliteTableMapping.Defaults"/> minus <see cref="AccessSegment"/>. A
    /// <c>static readonly</c> immutable set computed once from a constant lookup and never written at
    /// runtime — the allowed kind of static (a constant), not a mutable cache.
    /// </summary>
    private static readonly ImmutableHashSet<string> OwnerRequiringSatelliteSegments =
        SatelliteTableMapping.Defaults
            .Select(m => m.Segment)
            .Where(s => s.Length > 1 && s[0] == '_'
                        && !string.Equals(s, AccessSegment, StringComparison.Ordinal))
            .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="node"/> is a satellite instance node (it lives directly inside an
    /// owner-requiring satellite folder — <c>_Activity</c>, <c>_Thread</c>, <c>_Comment</c>,
    /// <c>_Notification</c>, <c>_UserActivity</c>, …) that is anchored at a top-level / ownerless path
    /// — i.e. the namespace has no owning segment before the satellite folder, or (for an
    /// <c>_Activity</c> specifically) its <see cref="MeshNode.MainNode"/> is empty.
    /// <paramref name="reason"/> carries a precise, hunt-friendly message that names the offending
    /// segment.
    ///
    /// <para>Returns <c>false</c> for everything that is NOT an owner-requiring satellite instance —
    /// including the satellite NodeType DEFINITION nodes (<c>new MeshNode("Activity")</c>,
    /// namespace null), any non-satellite node, and any <c>_Access</c> node (top-level grants are
    /// legitimate) — so the guard only ever rejects the genuine defect shape.</para>
    /// </summary>
    public static bool IsOwnerless(MeshNode? node, out string reason)
    {
        reason = string.Empty;
        if (node is null)
            return false;

        // Identify a satellite INSTANCE by its placement: the last namespace segment is one of the
        // owner-requiring satellite folders, i.e. the node sits directly under it. This is
        // content-agnostic (it does not need the NodeType) and never matches a satellite
        // type-DEFINITION node (namespace null), a non-satellite node, or an _Access node.
        var nsSegments = (node.Namespace ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (nsSegments.Length == 0)
            return false;

        var satelliteSegment = nsSegments[^1];
        if (!OwnerRequiringSatelliteSegments.Contains(satelliteSegment))
            return false;

        // Owner = the namespace with the trailing satellite segment removed.
        //   "_Thread"            -> ""           (ownerless — the bug)
        //   "Doc/_Thread"        -> "Doc"        (owned — fine)
        //   "Doc/Sub/_Activity"  -> "Doc/Sub"    (owned — fine)
        var owner = string.Join('/', nsSegments[..^1]);
        if (string.IsNullOrEmpty(owner))
        {
            reason =
                $"Satellite '{node.Path}' is anchored at a top-level / ownerless path: the namespace " +
                $"'{node.Namespace}' has no owning node before '{satelliteSegment}'. Satellites MUST live at " +
                $"'{{ownerPath}}/{satelliteSegment}/{{id}}' under a real owning node (a Space, NodeType, User " +
                $"partition, or an Admin-partition version node for startup activities) — never at a bare " +
                $"'{satelliteSegment}'. There is no partition / per-node hub to route to, so every " +
                $"poster/subscriber NotFound-storms the router.";
            return true;
        }

        // Activity-specific secondary check: an empty MainNode. Preserves the Phase-1 Activity
        // behaviour. NOT generalised to other satellites: a partition-root _Access grant
        // legitimately carries MainNode="" — and other satellites route through their partition
        // (first segment), which the owner-segment check above already guarantees is present.
        if (string.Equals(satelliteSegment, ActivitySegment, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(node.MainNode))
        {
            reason =
                $"Activity '{node.Path}' has an empty MainNode. Set MainNode to the owning node path " +
                $"('{owner}') so access delegates to it and the activity is routable — an empty MainNode is " +
                $"the ownerless shape that NotFound-storms the router.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fail-fast: throws <see cref="OwnerlessActivityException"/> when <paramref name="node"/> is a
    /// top-level / ownerless satellite (see <see cref="IsOwnerless"/>). Call this at every place that
    /// BUILDS a satellite node before handing it to the mesh, so the defect surfaces at the source
    /// with a stack trace instead of as a downstream routing storm.
    /// </summary>
    public static void EnsureOwned(MeshNode node)
    {
        if (IsOwnerless(node, out var reason))
            throw new OwnerlessActivityException(reason);
    }
}
