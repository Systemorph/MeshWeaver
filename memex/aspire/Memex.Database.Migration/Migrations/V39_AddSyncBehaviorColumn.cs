using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Adds the <c>sync_behavior</c> column to every existing <c>mesh_nodes</c> table.
///
/// <para>Before this, <c>MeshNode.SyncBehavior</c> — the static-repo "Not synced" decouple
/// claim — had no Postgres column: <c>PostgreSqlStorageAdapter</c> dropped it on write and
/// defaulted it to <see cref="MeshWeaver.Mesh.SyncBehavior.Include"/> on read. So a decoupled
/// partition silently re-coupled on the next restart and the static-repo import re-clobbered
/// admin-managed content — e.g. a customer's <c>Provider/Anthropic</c> API key reverting to the
/// shipped default. The schema initializer now defines the column on fresh schemas; this repair
/// adds it to schemas that already exist (public + auth + every partition).</para>
///
/// <para>Idempotent: <c>ADD COLUMN IF NOT EXISTS</c> across every schema that has a
/// <c>mesh_nodes</c> table, in one dynamic-SQL pass on the runner connection.</para>
/// </summary>
public sealed class V39_AddSyncBehaviorColumn : IMigration
{
    public int Version => 39;
    public string Description => "Add sync_behavior column to mesh_nodes (persist the partition decouple claim)";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT table_schema
                    FROM information_schema.tables
                    WHERE table_name = 'mesh_nodes'
                LOOP
                    EXECUTE format(
                        'ALTER TABLE %I.mesh_nodes ADD COLUMN IF NOT EXISTS sync_behavior SMALLINT NOT NULL DEFAULT 0',
                        r.table_schema);
                END LOOP;
            END $$;
            """);
        await cmd.ExecuteNonQueryAsync();
        ctx.Logger.LogInformation(
            "Repair v39: ensured sync_behavior column on every mesh_nodes table (decouple claim now persists).");
    }
}
