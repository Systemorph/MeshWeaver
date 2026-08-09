using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Drop schemas whose names could never be valid partition segments (#714).
///
/// <para>Bug: before the partition router learned to reject malformed first segments and
/// before schema creation was gated to partition-owning creates, navigating to (or probing)
/// an arbitrary top-level URL lazily provisioned a real schema + tables. memex and
/// the cloud database accumulated querystring-shaped junk like
/// <c>search?q=query%20syntax&amp;hq=scope%3adescendants</c> (nine variants) and
/// <c>login?returnurl=https%3a%2f%2f…</c>. <see cref="V03_DropRogueSchemas"/> only dropped a
/// hardcoded bare-word list, which cannot match these.</para>
///
/// <para>This migration is pattern-based, not name-list based: it enumerates every
/// non-system schema and drops exactly those that FAIL the shared segment-validity rule
/// (<see cref="PartitionDefinition.IsValidPartitionSegment"/> — the SAME predicate the
/// router and the provisioning boundary enforce), i.e. names that do not start with a
/// letter/digit, contain anything other than a letter/digit/<c>.</c>/<c>-</c>/<c>_</c>, or
/// exceed 63 UTF-8 bytes. <b>The rule is Unicode-aware, NOT ASCII-only</b>
/// (<c>char.IsLetterOrDigit</c>): an accented schema such as <c>müller</c> PASSES and is
/// never dropped — do not read this as an <c>[A-Za-z0-9._-]</c> whitelist. Every legitimate schema —
/// partitions, their <c>*_versions</c> twins, <c>admin</c>/<c>auth</c>/<c>apitoken</c>/
/// <c>system_access</c>/<c>event_log</c> — passes the rule and is untouched; a schema that
/// fails it can never be routed to (the router's validity check precedes even its
/// registered-partition lookup), so it can hold nothing reachable — this also covers
/// legacy email-named user schemas (<c>…@…</c>), stranded-unreachable since the router
/// hardening. Drops are best-effort CASCADE with per-schema logging and a pre-drop
/// row-count audit so a surprise is visible in the log (mirrors
/// <see cref="V03_DropRogueSchemas"/> / <see cref="V38_DropLegacyProviderSchema"/>), plus a
/// <c>searchable_schemas</c> de-registration so queries stop fanning out to dropped schemas.</para>
/// </summary>
public sealed class V51_DropInvalidPartitionSchemas : IMigration
{
    public int Version => 51;
    public string Description => "Drop junk schemas whose names can never be valid partition segments (#714)";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Enumerate candidates: every schema except the engine's own (pg_catalog, pg_toast,
        // pg_temp_*, …) and the SQL-standard information_schema / public. Validity is decided
        // in C# by the ONE shared segment rule — never a duplicated regex or a name list.
        var invalid = new List<string>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg%'
              AND nspname NOT IN ('information_schema', 'public')
            ORDER BY nspname
            """))
        await using (var reader = await listCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (!PartitionDefinition.IsValidPartitionSegment(name))
                    invalid.Add(name);
            }

        if (invalid.Count == 0)
        {
            ctx.Logger.LogInformation("Repair v51: no invalid-named schemas found — nothing to drop.");
            return;
        }

        ctx.Logger.LogInformation(
            "Repair v51: found {Count} schema(s) failing the partition-segment rule: {Schemas}",
            invalid.Count, string.Join(", ", invalid.Select(s => $"\"{s}\"")));

        foreach (var schema in invalid)
        {
            // Audit what we're removing before the CASCADE so a surprise is visible in the log
            // (mirrors V38). Tolerant — a junk schema may not even have a mesh_nodes table.
            try
            {
                await using var countCmd = ctx.DataSource.CreateCommand(
                    "SELECT count(*) FROM \"" + schema.Replace("\"", "\"\"") + "\".mesh_nodes");
                var rows = await countCmd.ExecuteScalarAsync();
                ctx.Logger.LogInformation(
                    "Repair v51: dropping invalid-named schema \"{Schema}\" ({Rows} mesh_nodes rows).",
                    schema, rows);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogInformation(
                    "Repair v51: dropping invalid-named schema \"{Schema}\" (no readable mesh_nodes table: {Reason}).",
                    schema, ex.Message);
            }

            // Best-effort, log-and-continue (mirrors V03): a failed DROP (lock contention, an
            // unexpected dependency) must NOT abort the whole migration run and block the deploy.
            try
            {
                var quoted = schema.Replace("\"", "\"\"");
                await using (var dropCmd = ctx.DataSource.CreateCommand(
                    $"DROP SCHEMA IF EXISTS \"{quoted}\" CASCADE"))
                    await dropCmd.ExecuteNonQueryAsync();
                // The versions twin of a junk schema fails the rule itself and is dropped by its
                // own iteration; this explicit twin drop (V03/V38 parity) is an idempotent no-op
                // when it is already gone.
                await using (var dropVCmd = ctx.DataSource.CreateCommand(
                    $"DROP SCHEMA IF EXISTS \"{quoted}_versions\" CASCADE"))
                    await dropVCmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation("Repair v51: dropped invalid-named schema \"{Schema}\"", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v51: failed to drop schema \"{Schema}\" (continuing)", schema);
            }

            // De-register from the searchable-schemas index (mirrors V38): a dangling row keeps
            // cross-schema queries fanning out to the dropped schema. Tolerant — the table may
            // not exist on every deployment shape, and a missing row is a no-op.
            try
            {
                await using var deregCmd = ctx.DataSource.CreateCommand(
                    "DELETE FROM public.searchable_schemas WHERE lower(schema_name) = lower($1)");
                deregCmd.Parameters.AddWithValue(schema);
                var removed = await deregCmd.ExecuteNonQueryAsync();
                if (removed > 0)
                    ctx.Logger.LogInformation(
                        "Repair v51: de-registered \"{Schema}\" from searchable_schemas.", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v51: could not de-register \"{Schema}\" from searchable_schemas (continuing).", schema);
            }
        }
    }
}
