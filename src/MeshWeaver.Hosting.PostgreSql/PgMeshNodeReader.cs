using MeshWeaver.Mesh;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Shared row-reading helpers for materializing a <see cref="MeshNode"/> from a
/// Postgres result set across the storage adapter and the cross-schema query provider.
/// </summary>
internal static class PgMeshNodeReader
{
    /// <summary>
    /// Reads the node-level <see cref="MeshNode.SyncBehavior"/> (the static-repo "Not synced"
    /// decouple claim) from a result row. The <c>sync_behavior</c> column lives only on
    /// <c>mesh_nodes</c> — the sole decouplable table — so reads from satellite tables and
    /// from rows written before the column existed won't surface it; those default to
    /// <see cref="SyncBehavior.Include"/> (fully synced), the column's <c>DEFAULT 0</c>.
    /// Defensive lookup (scan field names rather than <c>GetOrdinal</c>) keeps every existing
    /// SELECT that omits the column working unchanged.
    /// </summary>
    internal static SyncBehavior ReadSyncBehavior(NpgsqlDataReader reader)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), "sync_behavior", StringComparison.Ordinal))
                continue;
            return reader.IsDBNull(i) ? SyncBehavior.Include : (SyncBehavior)reader.GetInt16(i);
        }
        return SyncBehavior.Include;
    }
}
