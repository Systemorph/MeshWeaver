using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Adds the node-authorship columns (<c>created_by</c>, <c>last_modified_by</c>, <c>created_date</c>)
/// to every same-schema partition's <c>mesh_nodes</c> and makes the history trigger record the
/// modifier.
///
/// <para><b>Background.</b> <c>MeshNode</c> carries <c>CreatedBy</c> / <c>LastModifiedBy</c> /
/// <c>CreatedDate</c> (stamped by the create/update handlers), but the Postgres <c>mesh_nodes</c>
/// table never had columns for them and the adapter never wrote/read them — so on every PG-backed
/// mesh each node round-tripped with NULL authorship (the "Created by / Updated by" line in the node
/// header rendered blank). This adds the columns; the adapter (same change set) now persists and
/// hydrates them.</para>
///
/// <para><b>Steps per same-schema partition (a schema owning both <c>mesh_nodes</c> and its own
/// <c>mesh_node_history</c>), all idempotent:</b></para>
/// <list type="number">
///   <item><c>ALTER TABLE … ADD COLUMN IF NOT EXISTS</c> the three authorship columns.</item>
///   <item><c>CREATE OR REPLACE</c> <c>{schema}.trg_mesh_node_to_history()</c> so the history
///     snapshot's <c>changed_by</c> is populated from <c>NEW.last_modified_by</c> (schema-qualified
///     insert via <c>TG_TABLE_SCHEMA</c>); ensure the trigger exists (belt-and-braces with V44).</item>
/// </list>
///
/// <para><b>No backfill.</b> Authorship for rows written before this change was never captured and
/// cannot be recovered; identities populate on the first write after the fix. Frozen inline SQL —
/// matches <c>PostgreSqlSchemaInitializer.GetVersionedPartitionDdl</c> as of V45.</para>
///
/// <para><b>Scope.</b> Same-schema partitions only; cross-schema <c>{schema}_versions</c> layouts are
/// left untouched (their history lives elsewhere and their trigger already schema-qualifies).</para>
/// </summary>
public sealed class V45_AddNodeAuthorshipColumns : IMigration
{
    public int Version => 45;
    public string Description => "Add created_by/last_modified_by/created_date to mesh_nodes on every partition schema (PG dropped node authorship on write) and record changed_by in the history trigger";

    public async Task RunAsync(MigrationContext ctx)
    {
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

            // 1. Authorship columns (idempotent).
            await using (var alter = ctx.DataSource.CreateCommand($"""
                ALTER TABLE {quotedSchema}.mesh_nodes
                    ADD COLUMN IF NOT EXISTS created_by       TEXT,
                    ADD COLUMN IF NOT EXISTS last_modified_by TEXT,
                    ADD COLUMN IF NOT EXISTS created_date     TIMESTAMPTZ
                """))
            {
                await alter.ExecuteNonQueryAsync();
            }

            // 2. History trigger records changed_by = NEW.last_modified_by (schema-qualified insert).
            await using (var trg = ctx.DataSource.CreateCommand($"""
                CREATE OR REPLACE FUNCTION {quotedSchema}.trg_mesh_node_to_history() RETURNS TRIGGER AS $trg_history$
                BEGIN
                    EXECUTE format(
                        'INSERT INTO %I.mesh_node_history (
                            namespace, id, name, node_type, description, category, icon,
                            display_order, last_modified, version, state, content, desired_id, main_node, changed_by
                        ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
                        ON CONFLICT (namespace, id, version) DO NOTHING',
                        TG_TABLE_SCHEMA
                    ) USING
                        NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.description,
                        NEW.category, NEW.icon, NEW.display_order, NEW.last_modified,
                        NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node, NEW.last_modified_by;
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
                await trg.ExecuteNonQueryAsync();
            }

            ctx.Logger.LogInformation(
                "Repair v45: '{Schema}' — authorship columns added; history trigger records changed_by", schema);
        }

        ctx.Logger.LogInformation(
            "Repair v45: done — {Count} partition schema(s) now persist node authorship", schemas.Count);
    }
}
