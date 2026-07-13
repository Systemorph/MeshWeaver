using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// SQL features the connected Snowflake endpoint actually supports. Real Snowflake supports
/// everything; the LocalStack emulator (which transpiles to another engine) may not. Probed
/// once at schema initialization by <see cref="SnowflakeCapabilityProbe"/> and injected into
/// the SQL generator / query layer so affected constructs switch to fallback shapes.
/// </summary>
public sealed record SnowflakeCapabilities(
    bool SupportsVector,
    bool SupportsMerge,
    bool SupportsIlike,
    bool SupportsMaxBy,
    bool SupportsQualify,
    bool SupportsLikeEscape)
{
    /// <summary>Everything on — the real-Snowflake profile, and the default before probing.</summary>
    public static SnowflakeCapabilities AllOn { get; } = new(true, true, true, true, true, true);
}

/// <summary>
/// Mesh-scoped singleton holding the probed <see cref="SnowflakeCapabilities"/>. Starts at
/// <see cref="SnowflakeCapabilities.AllOn"/>; schema initialization overwrites it with the
/// probe result before the mesh serves traffic. Components read <see cref="Current"/> lazily
/// (per statement generation), so construction order relative to the probe doesn't matter.
/// </summary>
public sealed class SnowflakeCapabilityHolder
{
    private volatile SnowflakeCapabilities _current = SnowflakeCapabilities.AllOn;

    /// <summary>The capabilities in effect (probe result once initialization has run).</summary>
    public SnowflakeCapabilities Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>
/// Probes the endpoint's SQL feature support by executing one representative statement per
/// capability. Async I/O leaf — callers run it inside an <c>IIoPool</c> invoke (schema
/// initialization / test fixture), never on a hub scheduler.
/// </summary>
public static class SnowflakeCapabilityProbe
{
    /// <summary>Runs all capability probes against <paramref name="source"/>.</summary>
    public static async Task<SnowflakeCapabilities> ProbeAsync(
        SnowflakeConnectionSource source, ILogger? logger, CancellationToken ct)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        return new SnowflakeCapabilities(
            SupportsVector: await ProbeAsync(connection, logger, "VECTOR",
                "SELECT PARSE_JSON('[1,2]')::VECTOR(FLOAT, 2)", ct).ConfigureAwait(false),
            SupportsMerge: await ProbeMergeAsync(connection, logger, ct).ConfigureAwait(false),
            SupportsIlike: await ProbeAsync(connection, logger, "ILIKE",
                "SELECT 'a' ILIKE 'A'", ct).ConfigureAwait(false),
            SupportsMaxBy: await ProbeAsync(connection, logger, "MAX_BY",
                "SELECT MAX_BY(c1, c2) FROM (SELECT true AS c1, 1 AS c2)", ct).ConfigureAwait(false),
            SupportsQualify: await ProbeAsync(connection, logger, "QUALIFY",
                "SELECT c1 FROM (SELECT 1 AS c1) QUALIFY ROW_NUMBER() OVER (ORDER BY c1) = 1", ct).ConfigureAwait(false),
            SupportsLikeEscape: await ProbeAsync(connection, logger, "LIKE ESCAPE",
                @"SELECT 'a_b' LIKE 'a\\_b' ESCAPE '\\'", ct).ConfigureAwait(false));
    }

    private static async Task<bool> ProbeAsync(
        DbConnection connection, ILogger? logger, string capability, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex)
        {
            logger?.LogInformation(
                "Snowflake capability {Capability} unsupported by endpoint: {Message}",
                capability, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// MERGE can't be probed with a bare SELECT — it needs a target relation. A scratch
    /// temporary table keeps the probe side-effect-free (temp tables are session-scoped).
    /// </summary>
    private static async Task<bool> ProbeMergeAsync(
        DbConnection connection, ILogger? logger, CancellationToken ct)
    {
        try
        {
            await Execute(connection,
                "CREATE TEMPORARY TABLE IF NOT EXISTS \"cap_probe_merge\" (\"id\" TEXT)", ct)
                .ConfigureAwait(false);
            await Execute(connection,
                """
                MERGE INTO "cap_probe_merge" t USING (SELECT 'x' AS "id") s ON t."id" = s."id"
                WHEN MATCHED THEN UPDATE SET "id" = s."id"
                WHEN NOT MATCHED THEN INSERT ("id") VALUES (s."id")
                """, ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex)
        {
            logger?.LogInformation(
                "Snowflake capability MERGE unsupported by endpoint: {Message}", ex.Message);
            return false;
        }
    }

    private static async Task Execute(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
