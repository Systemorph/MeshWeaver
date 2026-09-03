using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>
/// Declares a query as MESH-WIDE — one whose answer genuinely lives in every partition, so the
/// storage layer may UNION every partition schema for it (#3202).
///
/// <para>🚨 <b>Fan-out is opt-in, not the fallback.</b> Since MeshWeaver.Plugins #1231 the Postgres
/// planner REFUSES a query that names no partition (no concrete <c>path:</c>/<c>namespace:</c>
/// first segment, no registered node-type pin) and did not ask to span them — because a silent
/// cross-schema UNION over 199 schemas takes heavyweight locks on 500+ relations and stalls every
/// other query on the database (memex-cloud, 2026-09-02: 7 786 such fan-outs in 30 minutes). The
/// refusal arrives at RUNTIME, as an <c>UnanchoredQueryException</c> faulting the caller's stream;
/// nothing compiles differently and no test fails — which is how the sign-in path broke for every
/// user on every image built after that change (#3202).</para>
///
/// <para>So a caller that reads across the whole mesh says so HERE, by construction, instead of
/// relying on a fallback that no longer exists. The three legitimate reasons, and every call site
/// carries one of them in its comment:</para>
/// <list type="bullet">
///   <item><b>A catalog whose instances live wherever their owner lives</b> — every
///     <c>NodeType</c> definition, every <c>UiContribution</c>, every <c>Space</c> root, every
///     <c>{Space}/_GitSync</c> config. There is no partition to anchor to because the set of
///     partitions IS the answer.</item>
///   <item><b>A record keyed by a value, not a location</b> — a <c>MeshWeaverInstance</c> looked
///     up by id lives under whichever user registered it. The durable fix is an index in a pinned
///     partition; until then the read declares its cost.</item>
///   <item><b>A process-wide watch</b> — the outbound-mail sender, the event-subscription runner.
///     One live subscription per process, re-run on relevant changes.</item>
/// </list>
///
/// <para>🚨 <b>What this is NOT for.</b> A read whose subject provably lives in ONE partition —
/// a user's own records, a partition's grants, a node by path — must be ANCHORED, never declared
/// mesh-wide: the sign-in role fold (#3202) reads three pinned homes, not the whole mesh. And the
/// security fold's own globals (<c>Role</c>, <c>GroupMembership</c>, gated types) go through
/// <c>SecurityQueries.Global</c>, which stamps completeness as well — see
/// <c>Doc/Architecture/UnanchoredSecurityReads</c>.</para>
/// </summary>
public static class MeshWideQuery
{
    /// <summary>
    /// <paramref name="query"/> with <see cref="ParsedQuery.CrossPartitionQualifier"/> appended
    /// (idempotent — a query the parser already reads as <see cref="ParsedQuery.CrossPartition"/> is
    /// returned trimmed and unchanged; the decision is the parser's, not a substring's, so a value
    /// that merely contains the text does not count and a token the parser sees always does).
    /// </summary>
    /// <param name="query">The query text, which names no partition on purpose.</param>
    /// <returns>The same query, declared mesh-wide.</returns>
    public static string Declare(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var trimmed = query.Trim();
        return new QueryParser().Parse(trimmed).CrossPartition
            ? trimmed
            : $"{trimmed} {ParsedQuery.CrossPartitionQualifier}";
    }

    /// <summary>Every instance of <paramref name="nodeType"/> in the mesh — the catalog shape.</summary>
    /// <param name="nodeType">The node type whose instances live in every partition.</param>
    /// <returns><c>nodeType:{nodeType} partitions:all</c>.</returns>
    public static string OfType(string nodeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        return Declare($"nodeType:{nodeType}");
    }
}
