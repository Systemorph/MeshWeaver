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

    /// <summary>
    /// Reads a nullable text column by name, or null when the column is absent from the
    /// result set. Defensive (scan field names, not <c>GetOrdinal</c>) so SELECTs predating
    /// the authorship columns keep working — used for <c>created_by</c> / <c>last_modified_by</c>.
    /// </summary>
    internal static string? ReadNullableString(NpgsqlDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), column, StringComparison.Ordinal))
                continue;
            return reader.IsDBNull(i) ? null : reader.GetString(i);
        }
        return null;
    }

    /// <summary>
    /// Reads a nullable <c>text[]</c> column by name, or null when the column is absent from
    /// the result set or NULL/empty. Defensive like <see cref="ReadNullableString"/> — used for
    /// <c>exclude_from_context</c> so SELECTs predating the column keep working.
    /// </summary>
    internal static string[]? ReadStringArray(NpgsqlDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), column, StringComparison.Ordinal))
                continue;
            if (reader.IsDBNull(i))
                return null;
            var value = reader.GetFieldValue<string[]>(i);
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    /// <summary>
    /// Reads a nullable timestamptz column by name as a UTC <see cref="DateTimeOffset"/>, or
    /// null when absent/NULL. Used for <c>created_date</c>; mirrors how the adapter reads
    /// <c>last_modified</c> (<c>new DateTimeOffset(GetDateTime(), TimeSpan.Zero)</c>).
    /// </summary>
    internal static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), column, StringComparison.Ordinal))
                continue;
            return reader.IsDBNull(i) ? null : new DateTimeOffset(reader.GetDateTime(i), TimeSpan.Zero);
        }
        return null;
    }
}
