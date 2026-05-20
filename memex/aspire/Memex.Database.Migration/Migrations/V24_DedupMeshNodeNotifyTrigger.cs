using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Replace <c>{schema}.notify_mesh_node_changes()</c> in every partition schema
/// with a version that suppresses pg_notify on no-op UPDATEs (same content,
/// name, node_type, state, version, desired_id, main_node).
///
/// <para>Why this exists: the old function fired NOTIFY for every UPDATE,
/// including idempotent writes (a workspace.Update lambda that returns the
/// same node value, or a same-value upsert). Every NOTIFY wakes every
/// synced-query subscriber, which re-reads its result set, which can in
/// turn write more rows — an amplification feedback loop. Prod incident
/// 2026-05-20: opening a chat thread fanned out to ~5 cross-schema queries
/// + per-message access checks; each access read triggered a no-op upsert
/// touching last_modified, which fired NOTIFY, which woke the synced queries
/// again. Per-partition single-Npgsql connection (MaxPoolSize=1) couldn't
/// keep up → /authorize hung.</para>
///
/// <para><b>Idempotent</b>: <c>CREATE OR REPLACE FUNCTION</c>. Safe to re-run.
/// Schema initializer creates fresh partitions with the new function already
/// in place; this migration covers existing partitions.</para>
/// </summary>
public sealed class V24_DedupMeshNodeNotifyTrigger : IMigration
{
    public int Version => 24;
    public string Description =>
        "Replace notify_mesh_node_changes() per schema with no-op-UPDATE dedup";

    private const string DedupedFunction = """
        CREATE OR REPLACE FUNCTION notify_mesh_node_changes() RETURNS TRIGGER AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                PERFORM pg_notify('mesh_node_changes',
                    json_build_object('path', CASE WHEN OLD.namespace = '' THEN OLD.id ELSE OLD.namespace || '/' || OLD.id END, 'op', 'DELETE')::text);
                RETURN OLD;
            ELSIF TG_OP = 'UPDATE'
                  AND OLD.content IS NOT DISTINCT FROM NEW.content
                  AND OLD.name IS NOT DISTINCT FROM NEW.name
                  AND OLD.node_type IS NOT DISTINCT FROM NEW.node_type
                  AND OLD.state IS NOT DISTINCT FROM NEW.state
                  AND OLD.version IS NOT DISTINCT FROM NEW.version
                  AND OLD.desired_id IS NOT DISTINCT FROM NEW.desired_id
                  AND OLD.main_node IS NOT DISTINCT FROM NEW.main_node THEN
                RETURN NEW;
            ELSE
                PERFORM pg_notify('mesh_node_changes',
                    json_build_object('path', CASE WHEN NEW.namespace = '' THEN NEW.id ELSE NEW.namespace || '/' || NEW.id END, 'op', TG_OP)::text);
                RETURN NEW;
            END IF;
        END;
        $$ LANGUAGE plpgsql;
        """;

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            ctx.Logger.LogInformation(
                "Repair v24: replacing notify_mesh_node_changes() in schema {Schema}…", schema);

            // The function lives in each partition schema (and is invoked by
            // the per-schema mesh_node_notify trigger). search_path needs to
            // point at the schema so CREATE FUNCTION lands there, not in
            // public.
            await using var cmd = ctx.DataSource.CreateCommand(
                $"SET LOCAL search_path TO \"{schema}\"; {DedupedFunction}");
            await cmd.ExecuteNonQueryAsync();
        }

        ctx.Logger.LogInformation(
            "Repair v24: deduped notify_mesh_node_changes() across {Count} partition schema(s)",
            schemas.Count);
    }
}
