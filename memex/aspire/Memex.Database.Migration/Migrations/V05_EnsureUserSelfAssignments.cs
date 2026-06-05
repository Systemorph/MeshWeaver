namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Ensure every user has an Admin self-assignment in their OWN partition, then rebuild
/// effective permissions across all content partitions.
///
/// <para>Users are partition roots, so they are discovered from the central index
/// (<c>public.top_level_index</c> — the #16 partition-root materialized view), NOT the
/// legacy per-schema <c>"user"</c> access-object schema (which no longer exists — it was the
/// pre-V27 schema, since removed). Each User's self-Admin grant lands in that user's own
/// partition's <c>access</c> table at <c>{id}/_Access</c>.</para>
///
/// <para>On a fresh DB the index has no User rows yet, so this is a clean no-op. The backfill
/// fixes missing AccessAssignment nodes for users onboarded before <c>UserScopeGrantHandler</c>
/// existed and propagates role-permission changes into <c>user_effective_permissions</c>.</para>
/// </summary>
public sealed class V05_EnsureUserSelfAssignments : IMigration
{
    public int Version => 5;
    public string Description => "Ensure user self-assignments (from the central index) and rebuild permissions";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
            DO $$
            DECLARE
                user_rec RECORD;
            BEGIN
                -- Backfill each User's self-Admin AccessAssignment IN THE USER'S OWN PARTITION.
                -- Users are partition roots → discovered from the central index
                -- (public.top_level_index, the #16 materialized view) and own a schema named
                -- after their id; the grant lands at {id}/_Access in that partition's `access`
                -- table. There is NO legacy `user` schema (the pre-V27 access-object schema is
                -- gone). On a fresh DB the index has no Users, so this is a clean no-op. Per-user
                -- EXCEPTION so one missing/half-provisioned partition can't abort the migration.
                FOR user_rec IN
                    SELECT id FROM public.top_level_index WHERE node_type = 'User'
                LOOP
                    BEGIN
                        EXECUTE format($ins$
                            INSERT INTO %1$I.access
                                (id, namespace, name, node_type, content, main_node, last_modified, version, state)
                            SELECT %2$L || '_Access', %2$L || '/_Access', %2$L || ' Access', 'AccessAssignment',
                                   jsonb_build_object('accessObject', %2$L, 'displayName', %2$L,
                                       'roles', jsonb_build_array(jsonb_build_object('role', 'Admin'))),
                                   %2$L, NOW(), 1, 2
                            WHERE NOT EXISTS (
                                SELECT 1 FROM %1$I.access
                                WHERE namespace = %2$L || '/_Access'
                                  AND content->>'accessObject' = %2$L)
                        $ins$, lower(user_rec.id), user_rec.id);
                    EXCEPTION WHEN OTHERS THEN
                        RAISE NOTICE 'Self-assignment backfill for user % failed: %', user_rec.id, SQLERRM;
                    END;
                END LOOP;

                -- Rebuild effective permissions for every content partition (each per-schema
                -- `access` table). `user` is no longer special-cased — it does not exist.
                FOR user_rec IN
                    SELECT schema_name FROM information_schema.schemata s
                    WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                                  WHERE t.table_schema = s.schema_name AND t.table_name = 'access')
                    AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
                    AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
                LOOP
                    BEGIN
                        EXECUTE format('SELECT %I.rebuild_user_effective_permissions()', user_rec.schema_name);
                    EXCEPTION WHEN OTHERS THEN
                        RAISE NOTICE 'Schema % rebuild failed: %', user_rec.schema_name, SQLERRM;
                    END;
                END LOOP;
            END $$;
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
