using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// The partition-access reconcile: re-applies the current per-partition permission functions and
/// rebuilds <c>{schema}.user_effective_permissions</c> + <c>public.partition_access</c> from the
/// <c>access</c> satellites — the single implementation behind BOTH
/// <see cref="V35_ReconcilePartitionAccessIndex"/> (the one-shot versioned heal) and the
/// ALWAYS-RUNS migration phase in <c>Program.cs</c>.
///
/// <para><b>Why always-runs.</b> V35 healed this drift once, but it RECURRED on a database
/// already past v35 (memex.local, 2026-07-03: every partition's
/// <c>user_effective_permissions</c> empty while the <c>access</c> tables held valid Admin
/// assignments — grants invisible to the permission evaluator because the synced queries run
/// under RLS, which depends on <c>partition_access</c>; menus silently degrade and the user's
/// own spaces vanish from search, with no error anywhere). A versioned one-shot cannot heal a
/// recurrence, so the reconcile now runs on EVERY migration pass, like the doc-mirror phase:
/// idempotent, one function call per schema, and any future drift self-heals on the next roll
/// instead of wedging permissions until someone hand-runs SQL.</para>
///
/// <para><b>Drift detection.</b> Before rebuilding, each schema with AccessAssignment rows but an
/// EMPTY materialization is logged at Warning — the recurrence's root cause (what wipes the
/// tables) is still unidentified, and these log lines are the evidence trail that will pin it.</para>
/// </summary>
public static class PartitionAccessReconcile
{
    /// <summary>Runs the reconcile across every partition schema (those with an <c>access</c> table).</summary>
    /// <param name="dataSource">The database to reconcile.</param>
    /// <param name="vectorDimensions">Embedding dimension for the provisioning proc script.</param>
    /// <param name="logger">Migration logger.</param>
    /// <param name="phase">Log prefix identifying the caller ("v35" or "always-on").</param>
    public static async Task RunAsync(
        NpgsqlDataSource dataSource, int vectorDimensions, ILogger logger, string phase)
    {
        // 1. (Re)install the single-source-of-truth provisioning proc so its body carries the
        //    CURRENT versioned DDL (the rebuild functions with the partition_access sync). Pure
        //    CREATE OR REPLACE FUNCTION — idempotent.
        await using (var procCmd = dataSource.CreateCommand(
            PostgreSqlSchemaInitializer.GetEnsurePartitionSchemaProcScript(vectorDimensions)))
        {
            await procCmd.ExecuteNonQueryAsync();
        }

        var schemas = await SchemaHelpers.DiscoverAccessSchemasAsync(dataSource);

        foreach (var schema in schemas)
        {
            // Quoted-identifier escaping: schema names come from information_schema but can
            // legally contain double quotes (and prod carries junk schemas from URL fragments)
            // — double any embedded quote so the interpolated identifier stays valid SQL.
            var ident = schema.Replace("\"", "\"\"");
            // Drift detection BEFORE the heal: assignments present but materialization empty is
            // the silent permission wedge — surface it loudly so recurrences are diagnosable.
            try
            {
                await using var driftCmd = dataSource.CreateCommand($"""
                    SELECT (SELECT count(*) FROM "{ident}".access),
                           (SELECT count(*) FROM "{ident}".user_effective_permissions)
                    """);
                await using var rdr = await driftCmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    var accessRows = rdr.GetInt64(0);
                    var uepRows = rdr.GetInt64(1);
                    if (accessRows > 0 && uepRows == 0)
                        logger.LogWarning(
                            "Reconcile {Phase}: DRIFT in \"{Schema}\" — {AccessRows} access assignment(s) but an EMPTY "
                            + "user_effective_permissions (grants invisible to RLS/permissions until this heal). "
                            + "Something wiped or bypassed the materialization — investigate what ran before this.",
                            phase, schema, accessRows);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconcile {Phase}: drift probe failed for \"{Schema}\"", phase, schema);
            }

            // 2a. Re-apply the current per-partition DDL (CREATE OR REPLACE the rebuild functions
            //     etc.) via the single-source-of-truth proc — cures any stale function body.
            try
            {
                await using var ensureCmd = dataSource.CreateCommand(
                    "SELECT public.ensure_partition_schema(@p)");
                ensureCmd.Parameters.AddWithValue("@p", schema);
                await ensureCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Reconcile {Phase}: \"{Schema}\" — ensure_partition_schema failed; skipping", phase, schema);
                continue;
            }

            // 2b. Full schema-level reconcile: rebuild user_effective_permissions AND re-sync
            //     public.partition_access (upserts every user with Read, deletes the revoked) —
            //     heals the drift whether it came from a stale function, a trigger-bypassing
            //     write, or a wiped table.
            try
            {
                await using var rebuildCmd = dataSource.CreateCommand(
                    $"SELECT \"{ident}\".rebuild_user_effective_permissions()");
                await rebuildCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Reconcile {Phase}: rebuild_user_effective_permissions failed for \"{Schema}\"", phase, schema);
            }
        }

        logger.LogInformation(
            "Reconcile {Phase}: partition-access reconciled across {Count} schema(s)", phase, schemas.Count);
    }
}
