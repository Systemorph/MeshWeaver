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
    /// for a pg_notify cycle.
    /// </summary>
    Task SyncSearchableSchemasAsync(CancellationToken ct = default);

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
