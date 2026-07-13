using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repairs the <b>wrong-schema resolution in <c>trg_access_changed()</c></b> and backfills the
/// <c>user_effective_permissions</c> tables it left empty.
///
/// <para><b>Background.</b> Every partition schema carries an <c>access_changed</c> trigger on its
/// <c>access</c> satellite table whose function re-materializes the denormalized
/// <c>user_effective_permissions</c> whenever an AccessAssignment is written. The deployed function
/// body called the rebuild functions UNQUALIFIED (<c>PERFORM rebuild_user_permissions_for(…)</c>),
/// which plpgsql resolves through the CALLING SESSION's <c>search_path</c>. Production writes flow
/// through the shared base connection pool (default <c>search_path</c> = <c>public</c>) with
/// schema-qualified statements, so the trigger silently executed PUBLIC's rebuild functions against
/// public's empty <c>access</c> table. The partition's <c>user_effective_permissions</c> stayed
/// EMPTY after every grant write — partition-scoped queries failed closed (count 0) for every user
/// while direct reads worked. Live incident: memex 2026-07-13, freshly recreated partitions
/// (AgenticEngineering / DataModeling / RiskTransfer); older partitions only had rows because the
/// per-boot self-heal re-materializes them schema-QUALIFIED.</para>
///
/// <para>Two steps per partition schema, both idempotent (frozen inline SQL — a migration must not
/// depend on live code that keeps evolving; this body matches
/// <c>PostgreSqlSchemaInitializer.AccessChangedTriggerFunctionBody</c> as of V43):</para>
/// <list type="number">
///   <item><b>Re-create <c>{schema}.trg_access_changed()</c></b> with every rebuild call qualified
///     via <c>TG_TABLE_SCHEMA</c> (the schema of the <c>access</c> table that fired the trigger —
///     i.e. the partition schema, independent of the caller's <c>search_path</c>).
///     <c>CREATE OR REPLACE</c> keeps the function OID, so the existing trigger picks the fixed
///     body up immediately; the trigger itself is re-created only where missing.</item>
///   <item><b>Backfill</b>: run <c>{schema}.rebuild_user_effective_permissions()</c> so tables the
///     broken trigger left empty are materialized from the intact <c>_Access</c> grants (and
///     <c>public.partition_access</c> re-synced).</item>
/// </list>
///
/// <para><b>Belt and braces.</b> The same change set makes this SELF-HEALING: the always-run schema
/// init (<c>PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript</c>, step 5 of
/// <c>InitializeAsync</c>) re-installs the fixed function + trigger and re-runs the rebuild on
/// every boot. This migration remains as the recorded, ordered repair for deployed databases (and
/// heals DBs whose next boot predates the new initializer).</para>
/// </summary>
public sealed class V43_FixAccessTriggerSchemaResolutionAndBackfill : IMigration
{
    public int Version => 43;
    public string Description => "Schema-qualify trg_access_changed's rebuild calls via TG_TABLE_SCHEMA (grant writes on the shared pool rebuilt public instead of the partition) and backfill empty user_effective_permissions";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Partition schemas that carry the permission machinery: an access satellite table AND
        // the per-user rebuild function. Very old / bare schemas without them are skipped —
        // installing the trigger there would break access writes at runtime.
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'access'
              AND t.table_schema NOT IN
                  ('information_schema','pg_catalog','pg_toast','admin','doc')
              AND t.table_schema NOT LIKE '%\_versions'
              AND EXISTS (
                  SELECT 1 FROM pg_proc p
                  JOIN pg_namespace pn ON pn.oid = p.pronamespace
                  WHERE p.proname = 'rebuild_user_permissions_for'
                    AND pn.nspname = t.table_schema)
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";

            // 1. Re-create the trigger function with TG_TABLE_SCHEMA-qualified rebuild calls.
            //    CREATE OR REPLACE preserves the OID — the existing access_changed trigger
            //    starts using the fixed body with no trigger re-creation needed.
            await using (var fn = ctx.DataSource.CreateCommand($"""
                CREATE OR REPLACE FUNCTION {quotedSchema}.trg_access_changed() RETURNS TRIGGER AS $trg_access$
                DECLARE
                    affected_user TEXT;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        affected_user := OLD.content->>'accessObject';
                    ELSE
                        affected_user := NEW.content->>'accessObject';
                    END IF;

                    -- Schema-qualified via TG_TABLE_SCHEMA (= the partition schema): an unqualified
                    -- PERFORM resolves through the WRITING SESSION's search_path, which on the shared
                    -- base connection pool is public — silently rebuilding the WRONG schema's
                    -- permissions and leaving this partition's user_effective_permissions empty
                    -- (memex prod incident 2026-07-13).
                    IF affected_user IS NOT NULL THEN
                        EXECUTE format('SELECT %I.rebuild_user_permissions_for($1)', TG_TABLE_SCHEMA)
                            USING affected_user;
                    ELSE
                        EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', TG_TABLE_SCHEMA);
                    END IF;

                    IF TG_OP = 'UPDATE' AND OLD.content->>'accessObject' IS DISTINCT FROM NEW.content->>'accessObject' THEN
                        IF OLD.content->>'accessObject' IS NOT NULL THEN
                            EXECUTE format('SELECT %I.rebuild_user_permissions_for($1)', TG_TABLE_SCHEMA)
                                USING OLD.content->>'accessObject';
                        END IF;
                    END IF;

                    RETURN NULL;
                END;
                $trg_access$ LANGUAGE plpgsql;

                DO $ensure_trigger$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_trigger tg
                        JOIN pg_class c ON c.oid = tg.tgrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE tg.tgname = 'access_changed'
                          AND c.relname = 'access' AND n.nspname = '{schema.Replace("'", "''")}')
                    THEN
                        CREATE TRIGGER access_changed
                            AFTER INSERT OR UPDATE OR DELETE ON {quotedSchema}.access
                            FOR EACH ROW EXECUTE FUNCTION {quotedSchema}.trg_access_changed();
                    END IF;
                END;
                $ensure_trigger$;
                """))
            {
                await fn.ExecuteNonQueryAsync();
            }

            // 2. Backfill: materialize user_effective_permissions (+ sync public.partition_access)
            //    from the intact _Access grants the broken trigger never projected.
            await using (var rebuild = ctx.DataSource.CreateCommand(
                $"SELECT {quotedSchema}.rebuild_user_effective_permissions()"))
            {
                await rebuild.ExecuteNonQueryAsync();
            }

            ctx.Logger.LogInformation(
                "Repair v43: '{Schema}' — trg_access_changed re-created (TG_TABLE_SCHEMA-qualified) and user_effective_permissions rebuilt", schema);
        }

        ctx.Logger.LogInformation("Repair v43: done — {Count} schema(s) repaired and backfilled", schemas.Count);
    }
}
