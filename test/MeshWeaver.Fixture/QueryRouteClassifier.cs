using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Fixture;

/// <summary>How the Postgres router answers a query — the three outcomes it actually has.</summary>
public enum QueryRoute
{
    /// <summary>One concrete schema: the lowercased first segment, or a registered global satellite's schema.</summary>
    Pinned,

    /// <summary>A first segment the router refuses to route (an unregistered <c>_</c>-prefixed segment): no schema, no fan-out, no write can ever land there.</summary>
    Unroutable,

    /// <summary>No first segment, or a wildcard one: a <c>UNION ALL</c> over every partition schema.</summary>
    FanOut,
}

/// <summary>
/// The decision the Postgres planner takes on a query BEFORE it runs it — the one that, since
/// MeshWeaver.Plugins #1231, ends in an <c>UnanchoredQueryException</c> for a query that names no
/// partition and did not ask to span them (#3202).
/// </summary>
public enum PlannerVerdict
{
    /// <summary>The query names its partition — a concrete <c>path:</c>/<c>namespace:</c> first segment, a multi-path, or a wildcard namespace pattern.</summary>
    Anchored,

    /// <summary>The query carries <c>partitions:all</c> — the explicit, declared fan-out.</summary>
    DeclaredFanOut,

    /// <summary>The query text names no partition, but a registered <c>QueryRoutingRule</c> (a node-type pin such as <c>nodeType:User</c> → <c>Auth</c>) does.</summary>
    PinnedByRoutingRule,

    /// <summary>None of the above: the planner REFUSES the query at runtime.</summary>
    Refused,
}

/// <summary>
/// The ONE test-side reproduction of the Postgres router's routing and refusal rules, shared by
/// every census that classifies query shapes (<c>SecurityQueryShapesTest</c>, the sign-in guard,
/// the unanchored-query census) so two copies cannot drift apart — a hand-copied classifier that
/// "cannot drift because it is the single implementation" is a hypothesis three defects disproved
/// in one day.
///
/// <para><b>Routing</b> (<see cref="RouteOf"/>) mirrors <c>PostgreSqlPartitionedMeshQuery</c> /
/// <c>PostgreSqlPathRoutingAdapter</c>: the first segment of <c>path:</c> (which a single
/// <c>namespace:</c> also sets), lowercased, unless it is a wildcard (fan-out), empty (fan-out), or
/// <c>_</c>-prefixed (registered → its schema, otherwise unroutable).</para>
///
/// <para><b>Refusal</b> (<see cref="VerdictOf"/>) mirrors the planner's gate in
/// <c>PostgreSqlPartitionedMeshQuery.FanOutQuery</c>: a query is served iff
/// <c>IsSufficientlySpecified(parsed) || ResolvesByRoutingHint(parsed)</c>, where the first is
/// <c>CrossPartition || concrete first segment || Paths.Count &gt; 0 || a wildcard namespace
/// pattern</c> and the second is a non-empty <c>MeshConfiguration.ResolveRoutingHints(parsed).Partition</c>.
/// The planner's own copy is pinned by <c>PostgreSqlPartitionedMeshQueryMappingTests</c> in
/// MeshWeaver.Plugins; this mirror is pinned against the same corpus so the two agree.</para>
/// </summary>
public static class QueryRouteClassifier
{
    /// <summary>
    /// The global satellite namespaces the platform registers with an explicit schema
    /// (<c>DefaultPartitionProvider.CreateGlobalSatellitePartition</c>) — the registry the router's
    /// <c>ResolveGlobalSchema</c> consults for a <c>_</c>-prefixed first segment. Mirrored here so
    /// the shape tests stay pure parser tests; <c>SecurityQueryRootLegRegistryTest</c> asserts this
    /// mirror against the REAL registry of a running mesh, so it cannot drift silently.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> RegisteredGlobalSatellites =
        ImmutableDictionary<string, string>.Empty.Add(SecurityQueries.RootAccessNamespace, "system_access");

    /// <summary>The route a query takes, reproduced on the shared <see cref="QueryParser"/>.</summary>
    /// <param name="query">The query text.</param>
    /// <returns>The route kind and, when pinned, the schema it pins to.</returns>
    public static (QueryRoute Kind, string? Target) RouteOf(string query)
    {
        var parsed = new QueryParser().Parse(query);
        var fromPath = RouteOfSegment(parsed.Path);
        if (fromPath.Kind != QueryRoute.FanOut)
            return fromPath;
        foreach (var ns in parsed.ExtractNamespaces())
        {
            var fromNamespace = RouteOfSegment(ns);
            if (fromNamespace.Kind != QueryRoute.FanOut)
                return fromNamespace;
        }
        return (QueryRoute.FanOut, null);
    }

    private static (QueryRoute Kind, string? Target) RouteOfSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (QueryRoute.FanOut, null);
        var trimmed = path.Trim().Trim('/');
        if (trimmed.Length == 0)
            return (QueryRoute.FanOut, null);
        var slash = trimmed.IndexOf('/');
        var first = slash > 0 ? trimmed[..slash] : trimmed;
        if (first.Length == 0 || first == "*" || first.Contains('*'))
            return (QueryRoute.FanOut, null);
        if (first.StartsWith('_'))
            return RegisteredGlobalSatellites.TryGetValue(first, out var schema)
                ? (QueryRoute.Pinned, schema)
                : (QueryRoute.Unroutable, null);
        return (QueryRoute.Pinned, first.ToLowerInvariant());
    }

    /// <summary>
    /// The planner's <c>IsSufficientlySpecified</c>: whether the query text itself says where to
    /// look. Three ways, and a query needs exactly one — a concrete anchor, the explicit
    /// <c>partitions:all</c>, or a wildcard namespace pattern (which the parser keeps as a filter,
    /// leaving <see cref="ParsedQuery.Path"/> null — a Path-only check would refuse exactly the
    /// satellite browses that are the legitimate spanning reads).
    /// </summary>
    /// <param name="parsed">The parsed query.</param>
    /// <returns>True when the text names a partition or declares the fan-out.</returns>
    public static bool IsSufficientlySpecified(ParsedQuery parsed) =>
        parsed.CrossPartition
        || IsConcreteAnchor(parsed.Path)
        || parsed.Paths is { Count: > 0 }
        || parsed.ExtractNamespacePatterns().Count > 0;

    private static bool IsConcreteAnchor(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0)
            return false;
        var slash = trimmed.IndexOf('/');
        var first = slash < 0 ? trimmed : trimmed[..slash];
        return first.Length > 0 && first != "*";
    }

    /// <summary>
    /// The planner's decision for <paramref name="query"/> under <paramref name="configuration"/>'s
    /// routing rules — <see cref="PlannerVerdict.Refused"/> is what arrives at runtime as an
    /// <c>UnanchoredQueryException</c> faulting the caller's stream.
    /// </summary>
    /// <param name="query">The query text, exactly as the caller issues it.</param>
    /// <param name="configuration">The mesh configuration whose <c>QueryRoutingRules</c> the planner consults, or null for a planner with no rules.</param>
    /// <returns>The verdict.</returns>
    public static PlannerVerdict VerdictOf(string query, MeshConfiguration? configuration)
    {
        var parsed = string.IsNullOrEmpty(query) ? ParsedQuery.Empty : new QueryParser().Parse(query);
        if (parsed.CrossPartition)
            return PlannerVerdict.DeclaredFanOut;
        if (IsSufficientlySpecified(parsed))
            return PlannerVerdict.Anchored;
        if (configuration is not null
            && !string.IsNullOrEmpty(configuration.ResolveRoutingHints(parsed).Partition))
            return PlannerVerdict.PinnedByRoutingRule;
        return PlannerVerdict.Refused;
    }

    /// <summary>
    /// The shape <c>PostgreSqlCrossSchemaQueryProvider.DescribeQueryShape</c> puts on the
    /// <c>[CrossSchema] SLOW</c> line — <c>nodeType:{type|*} path:{path|-} scope:{Scope}</c> — so
    /// a census here and the Loki census speak the same vocabulary.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <returns>The shape.</returns>
    public static string Describe(string query)
    {
        var parsed = new QueryParser().Parse(query);
        return $"nodeType:{parsed.ExtractNodeType() ?? "*"}"
            + $" path:{(string.IsNullOrEmpty(parsed.Path) ? "-" : parsed.Path)}"
            + $" scope:{parsed.Scope}";
    }
}
