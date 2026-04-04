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
}
