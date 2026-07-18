using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Query provider that can execute a single query across multiple schemas using UNION ALL.
/// Implemented by PostgreSQL to avoid per-partition fan-out overhead.
/// </summary>
public interface ICrossSchemaQueryProvider
{
    /// <summary>
    /// Returns the list of searchable schema names (content partitions only).
    /// Excludes admin, portal, kernel, and rogue schemas.
    /// </summary>
    Task<IReadOnlyList<string>> GetSearchableSchemasAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the searchable-schemas registry from
    /// <c>information_schema.schemata</c>. Idempotent — a follow-up
    /// <see cref="GetSearchableSchemasAsync"/> returns the up-to-date list.
    /// Cheap (one SELECT + a batched INSERT) so fan-out callers can run it
    /// per-query to pick up partitions created mid-session without waiting
    /// for a pg_notify cycle. Throttled: a call within the implementation's
    /// TTL window is a no-op (the query hot path calls this per fan-out).
    /// </summary>
    Task SyncSearchableSchemasAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the searchable-schemas registry, bypassing the throttle window when
    /// <paramref name="force"/> is <c>true</c> (single-flight is still honoured). Used by the
    /// ONE-TIME boot self-heal that asserts a served catalog partition's schema is registered
    /// right after import (see <c>StaticRepoImporter</c> · #354) — NEVER on the query hot path,
    /// where <paramref name="force"/> stays <c>false</c> and the throttle protects the pool.
    /// </summary>
    Task SyncSearchableSchemasAsync(bool force, CancellationToken ct = default);

    /// <summary>
    /// Subset of <see cref="GetSearchableSchemasAsync"/> filtered to schemas
    /// that actually contain <paramref name="tableName"/>. Required before
    /// fanning out a UNION over a satellite table — older partitions and
    /// static-mesh schemas only ship <c>mesh_nodes</c>, so unfiltered fan-out
    /// hits <c>42P01 relation does not exist</c> on the satellite branches.
    /// </summary>
    Task<IReadOnlyList<string>> GetSchemasWithTableAsync(
        string tableName, CancellationToken ct = default);

    /// <summary>
    /// Top-level autocomplete from the <c>public.top_level_index</c> materialized view —
    /// the partition-root nodes (<c>namespace=''</c>, one per partition) whose name/id match
    /// <paramref name="prefix"/>, scored in Postgres (exact &gt; name-prefix &gt; id-prefix &gt;
    /// substring) and access-filtered via <c>public.partition_access</c> for
    /// <paramref name="userId"/> (<c>null</c> = system, sees all). Reads ONE small indexed
    /// relation — NEVER a cross-schema fan-out — so it can't drain the connection pool.
    /// Returns empty if the matview isn't present yet (DB not migrated).
    /// </summary>
    Task<IReadOnlyCollection<QueryResult>> AutocompleteTopLevelAsync(
        string prefix, string? userId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Queries nodes across multiple schemas in a single SQL UNION ALL query.
    /// </summary>
    IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Queries a satellite table across multiple schemas in a single SQL UNION ALL query.
    /// Used for satellite node types (Thread, Activity, etc.) that live in dedicated tables.
    /// </summary>
    IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        string? userId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same fan-out shape as the table-name overload, but with an
    /// <paramref name="activityUserId"/> hook for <c>source:activity</c> and
    /// <c>source:accessed</c> queries: each schema's branch INNER JOINs its
    /// activity / user-activity satellite so the merged feed orders by
    /// activity recency across partitions.
    /// </summary>
    IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        string? userId,
        string? activityUserId,
        CancellationToken ct = default);
}
