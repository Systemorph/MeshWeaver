using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Installs the <c>mesh_node_copy_to_history</c> trigger on <b>every</b> same-schema partition so
/// version history is actually recorded.
///
/// <para><b>Background.</b> Each versioned partition schema is meant to carry an AFTER INSERT/UPDATE
/// trigger on its <c>mesh_nodes</c> that snapshots every row into that schema's
/// <c>mesh_node_history</c>. The deployed <c>GetVersionedPartitionDdl</c> created the trigger under a
/// GLOBALLY-scoped guard — <c>IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname =
/// 'mesh_node_copy_to_history')</c> — but trigger names are unique PER TABLE, not per database. Once
/// the first schema provisioned (<c>public</c>) had the trigger, the guard was satisfied for every
/// database, so <b>no partition schema ever got the trigger</b> and <b>no version history was ever
/// recorded</b> for any Space/User node. The portal "Version History" panel showed nothing not only
/// because the reader was a no-op (fixed alongside) but because there were no rows to read.</para>
///
/// <para><b>Fix (frozen inline SQL — a migration must not depend on live code that keeps evolving;
/// this body matches <c>PostgreSqlSchemaInitializer.GetVersionedPartitionDdl</c> as of V44):</b></para>
/// <list type="number">
///   <item><b>Re-create <c>{schema}.trg_mesh_node_to_history()</c></b> with the INSERT
///     schema-qualified via <c>TG_TABLE_SCHEMA</c> (the schema of the <c>mesh_nodes</c> that fired
///     the trigger), so the snapshot lands in the RIGHT schema's <c>mesh_node_history</c> regardless
///     of the writing session's <c>search_path</c> (writes on the shared base pool run on
///     <c>public</c>). <c>CREATE OR REPLACE</c> preserves the OID, so where the trigger already
///     exists it starts using the fixed body immediately.</item>
///   <item><b>Create the trigger where the global guard skipped it</b> — every real partition — under
///     a properly schema-scoped <c>IF NOT EXISTS</c>.</item>
/// </list>
///
/// <para><b>No backfill.</b> Versions before this repair were never written anywhere and cannot be
/// reconstructed; history accrues from the first write after the fix. New partitions get the fixed
/// trigger from the updated <c>ensure_partition_schema</c> proc.</para>
///
/// <para><b>Scope.</b> Only <i>same-schema</i> versioned partitions (a schema owning both
/// <c>mesh_nodes</c> and its own <c>mesh_node_history</c>). Cross-schema <c>{schema}_versions</c>
/// layouts are intentionally left untouched — <c>TG_TABLE_SCHEMA</c> there would target a
/// non-existent <c>{schema}.mesh_node_history</c> and break writes.</para>
/// </summary>
public sealed class V44_FixMeshNodeHistoryTriggerPerSchema : IMigration
{
    public int Version => 44;
    public string Description => "Install mesh_node_copy_to_history on every same-schema partition (the global trigger-name guard installed it only on public, so partitions recorded no version history) and schema-qualify its insert via TG_TABLE_SCHEMA";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Same-schema versioned partitions: a schema with BOTH mesh_nodes and its own
        // mesh_node_history. Excludes information_schema / pg_* and the cross-schema
        // {schema}_versions history schemas (which have no mesh_nodes of their own).
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT n.table_schema
            FROM information_schema.tables n
            WHERE n.table_name = 'mesh_nodes'
              AND n.table_schema NOT IN ('information_schema','pg_catalog','pg_toast')
              AND n.table_schema NOT LIKE '%\_versions'
              AND EXISTS (
                  SELECT 1 FROM information_schema.tables h
                  WHERE h.table_name = 'mesh_node_history'
                    AND h.table_schema = n.table_schema)
            ORDER BY n.table_schema
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";

            await using (var cmd = ctx.DataSource.CreateCommand($"""
                CREATE OR REPLACE FUNCTION {quotedSchema}.trg_mesh_node_to_history() RETURNS TRIGGER AS $trg_history$
                BEGIN
                    EXECUTE format(
                        'INSERT INTO %I.mesh_node_history (
                            namespace, id, name, node_type, description, category, icon,
                            display_order, last_modified, version, state, content, desired_id, main_node
                        ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                        ON CONFLICT (namespace, id, version) DO NOTHING',
                        TG_TABLE_SCHEMA
                    ) USING
                        NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                        NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                        NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node;
                    RETURN NEW;
                END;
                $trg_history$ LANGUAGE plpgsql;

                DO $ensure_trigger$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_trigger tg
                        JOIN pg_class c ON c.oid = tg.tgrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE tg.tgname = 'mesh_node_copy_to_history'
                          AND c.relname = 'mesh_nodes' AND n.nspname = '{schema.Replace("'", "''")}')
                    THEN
                        CREATE TRIGGER mesh_node_copy_to_history
                            AFTER INSERT OR UPDATE ON {quotedSchema}.mesh_nodes
                            FOR EACH ROW EXECUTE FUNCTION {quotedSchema}.trg_mesh_node_to_history();
                    END IF;
                END;
                $ensure_trigger$;
                """))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            ctx.Logger.LogInformation(
                "Repair v44: '{Schema}' — mesh_node_copy_to_history installed/updated; version history now recorded per-schema", schema);
        }

        ctx.Logger.LogInformation(
            "Repair v44: done — {Count} partition schema(s) now record version history", schemas.Count);
    }
}
