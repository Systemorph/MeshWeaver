using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Adds a Postgres trigger that fires <c>NOTIFY partition_changes</c> when
/// an <c>Admin/Partition/*</c> row is inserted, updated, or deleted in
/// <c>admin.mesh_nodes</c>. The payload is a small JSON document
/// (<c>{"op":"INSERT","namespace":"acme"}</c>) consumed by
/// <see cref="MeshWeaver.Hosting.PostgreSql.PgPartitionNotifyListener"/>
/// on every silo. Each listener invalidates its
/// <see cref="MeshWeaver.Hosting.PostgreSql.PgPartitionCache"/> entry for the
/// affected namespace so the next access re-probes
/// <c>information_schema.schemata</c> and picks up the new/dropped state.
///
/// <para>Why pg_notify and not the existing <c>mesh_node_changes</c> channel:
/// that channel carries every row write — every silo would burn cycles
/// filtering it for the small subset of <c>Admin/Partition</c> events.
/// A dedicated channel is one extra trigger, near-zero throughput, and
/// makes the partition-routing invariant explicit at the schema level.</para>
///
/// <para><b>Idempotent</b>: <c>CREATE OR REPLACE FUNCTION</c> and
/// <c>DROP TRIGGER IF EXISTS</c> + <c>CREATE TRIGGER</c> are safe to re-run.</para>
/// </summary>
public sealed class V23_PartitionChangesNotify : IMigration
{
    public int Version => 23;
    public string Description =>
        "Add pg_notify('partition_changes') trigger on Admin/Partition row writes";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "admin"))
        {
            ctx.Logger.LogInformation(
                "Repair v23: 'admin' schema missing — skipping partition_changes trigger setup");
            return;
        }

        await using (var cmd = ctx.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION admin.notify_partition_change()
            RETURNS trigger AS $$
            DECLARE
                payload_ns TEXT;
            BEGIN
                payload_ns := COALESCE(NEW.id, OLD.id);
                PERFORM pg_notify('partition_changes',
                    json_build_object(
                        'op', TG_OP,
                        'namespace', payload_ns
                    )::text);
                RETURN COALESCE(NEW, OLD);
            END $$ LANGUAGE plpgsql;
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = ctx.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS trg_partition_notify ON admin.mesh_nodes;
            CREATE TRIGGER trg_partition_notify
            AFTER INSERT OR UPDATE OR DELETE ON admin.mesh_nodes
            FOR EACH ROW
            WHEN (
                (TG_OP = 'DELETE' AND OLD.namespace = 'Admin/Partition') OR
                (TG_OP <> 'DELETE' AND NEW.namespace = 'Admin/Partition')
            )
            EXECUTE FUNCTION admin.notify_partition_change();
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        ctx.Logger.LogInformation(
            "Repair v23: partition_changes trigger installed on admin.mesh_nodes");
    }
}
