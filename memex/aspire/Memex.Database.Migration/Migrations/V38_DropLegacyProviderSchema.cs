using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Remove the orphaned top-level <c>_provider</c> partition schema.
///
/// <para>History: the built-in AI model catalog briefly lived under a top-level <c>_Provider</c>
/// namespace (commit 04c71b17a) before it was relocated to <c>Provider</c> / <c>Admin/Provider</c>
/// (f7c467222). The relocation left a stale <c>_provider</c> schema carrying a single
/// <c>_Provider/_Policy</c> PartitionAccessPolicy — written by a hub that lacked the type registration,
/// so its <c>$type</c> is the namespace-qualified FULL name. That orphan keeps a phantom partition
/// alive: PathResolutionService synthesises a bare <c>_Provider</c> root on every probe, and the
/// access-control read path re-reads <c>_Provider/_Policy</c>, which never type-resolves → "stayed an
/// untyped JsonElement" logged thousands of times per hour (the atioz storm + sglauser flake).</para>
///
/// <para>No current code path writes a top-level <c>_provider</c> schema — the live
/// <c>{user}/_Provider/…</c> credential namespace lives INSIDE each user's own partition schema, NOT
/// here — so dropping it is safe and idempotent (<c>IF EXISTS</c> → no-op on portals that never had
/// it). Also removes the stale <c>searchable_schemas</c> registration so queries stop fanning out to
/// the dropped schema. Mirrors <see cref="V03_DropRogueSchemas"/>.</para>
/// </summary>
public sealed class V38_DropLegacyProviderSchema : IMigration
{
    private const string Schema = "_provider";

    public int Version => 38;
    public string Description => "Drop the orphaned legacy top-level _provider partition schema";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Always best-effort de-register from the searchable-schemas index — the row can outlive the
        // schema, and a dangling entry keeps cross-schema queries fanning out to a non-existent schema.
        await TryDeregisterSearchableSchema(ctx);

        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, Schema))
        {
            ctx.Logger.LogInformation("Repair v38: schema \"{Schema}\" absent — nothing to drop.", Schema);
            return;
        }

        // Audit what we're removing before the CASCADE so a surprise is visible in the log.
        try
        {
            await using var countCmd = ctx.DataSource.CreateCommand(
                $"SELECT (SELECT count(*) FROM \"{Schema}\".mesh_nodes), (SELECT count(*) FROM \"{Schema}\".access)");
            await using var reader = await countCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                ctx.Logger.LogInformation(
                    "Repair v38: dropping orphan schema \"{Schema}\" ({Nodes} mesh_nodes, {Access} access rows).",
                    Schema, reader.GetInt64(0), reader.GetInt64(1));
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Repair v38: pre-drop audit of \"{Schema}\" failed (continuing to drop).", Schema);
        }

        // Best-effort, log-and-continue (mirrors V03_DropRogueSchemas): a failed DROP (lock contention,
        // an unexpected dependency) must NOT abort the whole migration run and block the deploy — and it
        // must not leave the run aborted between the two drops (the top_level_index matview UNIONs the
        // searchable schemas, and Phase 3's SearchableSchemasUpdater rebuilds it AFTER all migrations).
        try
        {
            await using (var dropCmd = ctx.DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE"))
                await dropCmd.ExecuteNonQueryAsync();
            await using (var dropVCmd = ctx.DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}_versions\" CASCADE"))
                await dropVCmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation("Repair v38: dropped orphan schema \"{Schema}\" (+ _versions).", Schema);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Repair v38: failed to drop orphan schema \"{Schema}\" (continuing).", Schema);
        }
    }

    /// <summary>
    /// Remove <c>_provider</c> from <c>public.searchable_schemas</c> if that registry table exists.
    /// Tolerant: the table may not exist on every deployment shape, and a missing row is a no-op.
    /// </summary>
    private static async Task TryDeregisterSearchableSchema(MigrationContext ctx)
    {
        try
        {
            await using var cmd = ctx.DataSource.CreateCommand(
                "DELETE FROM public.searchable_schemas WHERE lower(schema_name) = $1");
            cmd.Parameters.AddWithValue(Schema);
            var removed = await cmd.ExecuteNonQueryAsync();
            if (removed > 0)
                ctx.Logger.LogInformation("Repair v38: de-registered \"{Schema}\" from searchable_schemas.", Schema);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Repair v38: could not de-register \"{Schema}\" from searchable_schemas (continuing).", Schema);
        }
    }
}
